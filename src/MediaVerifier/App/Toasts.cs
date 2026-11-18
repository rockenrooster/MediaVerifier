using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;
using MediaVerifier.Core;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Logging;
using Microsoft.Win32;

namespace MediaVerifier.App;

/// <summary>The writes that per-user registration needs; tests use an in-memory fake, never the real registry.</summary>
internal interface IRegistryWriter
{
    /// <summary>Sets a string value; <paramref name="name"/> null is the key's default value. Writes only when it differs.</summary>
    void SetValue(string subKey, string? name, string value);

    void DeleteTree(string subKey);
}

/// <summary>Keys under HKEY_CURRENT_USER through <see cref="Registry.CurrentUser"/>.</summary>
internal sealed class CurrentUserRegistry : IRegistryWriter
{
    public void SetValue(string subKey, string? name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        if (key.GetValue(name) is not string current || !string.Equals(current, value, StringComparison.Ordinal))
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    public void DeleteTree(string subKey) => Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
}

/// <summary>
/// Per-user registration for toasts and activation links (subplan 13, Registration; task N-02): the app user model
/// id with its display name and icon, and the <c>mediaverifier:</c> protocol. Idempotent; <c>--cleanup</c> removes it.
/// </summary>
internal static class ToastRegistration
{
    public const string AppUserModelId = "rockenrooster.MediaVerifier";
    public const string AumidKey = @"Software\Classes\AppUserModelId\" + AppUserModelId;
    public const string ProtocolKey = @"Software\Classes\" + ActivationUriScheme.Scheme;
    public const string IconFileName = "notification-icon.png";

    /// <summary>The registry entries; writing them twice leaves the same state.</summary>
    public static void Register(IRegistryWriter registry, string exePath, string iconPath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.SetValue(AumidKey, "DisplayName", AppInfo.ProductName);
        registry.SetValue(AumidKey, "IconUri", iconPath);
        registry.SetValue(ProtocolKey, null, "URL:" + AppInfo.ProductName);
        registry.SetValue(ProtocolKey, "URL Protocol", string.Empty);
        registry.SetValue(ProtocolKey + @"\shell\open\command", null, CommandFor(exePath));
    }

    /// <summary>What Windows runs for a link: the executable with the link as its only argument.</summary>
    public static string CommandFor(string exePath) => "\"" + exePath + "\" \"%1\"";

    public static void Remove(IRegistryWriter registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.DeleteTree(AumidKey);
        registry.DeleteTree(ProtocolKey);
    }

    /// <summary>First thing in <c>Main</c>: the process's app user model id, so toasts and the taskbar group under it.</summary>
    public static void SetProcessId()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("App user model id", ex);
        }
    }

    /// <summary>
    /// Before the first toast (GUI start, headless run): the icon PNG in the data folder and the registry entries.
    /// Returns false when registration failed (logged); toasts then fall back to the next channel.
    /// </summary>
    public static bool Ensure(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            string icon = Path.Combine(state.Paths.LocalDirectory, IconFileName);
            if (!File.Exists(icon))
            {
                WriteIcon(icon);
            }

            // The installed copy handles the links when there is one, not whichever copy started last.
            Register(new CurrentUserRegistry(), RegistrationTarget.ExePath(), icon);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ExternalException or ArgumentException)
        {
            state.Log.Warning("Notification registration failed; notifications use the next channel", ex);
            return false;
        }
    }

    /// <summary>The app icon (subplan 08, App icon) as a 256 px PNG for the notification header.</summary>
    internal static void WriteIcon(string path)
    {
        using Bitmap bitmap = new(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            UI.Kit.IconRenderer.DrawAppIcon(g, new RectangleF(0, 0, 256, 256), small: false);
        }

        string temporary = path + ".tmp";
        bitmap.Save(temporary, System.Drawing.Imaging.ImageFormat.Png);
        File.Move(temporary, path, overwrite: true);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

/// <summary>The toast payload of subplan 13 (Raising a toast); text is escaped by the XML writer.</summary>
internal static class ToastXml
{
    public static string Build(string title, string body, ActivationUri link, string buttonText)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(link);
        string uri = link.ToString();
        XElement binding = new("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", title));
        if (body.Length > 0)
        {
            binding.Add(new XElement("text", body));
        }

        XElement toast = new(
            "toast",
            new XAttribute("launch", uri),
            new XAttribute("activationType", "protocol"),
            new XElement("visual", binding),
            new XElement("actions", new XElement("action", new XAttribute("content", buttonText), new XAttribute("activationType", "protocol"), new XAttribute("arguments", uri))));
        return toast.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>
/// Windows toasts through the WinRT notification API called as COM, without the Windows SDK projection (subplan 13,
/// Raising a toast; task N-03). Interface ids and method order come from the Windows SDK 10.0.26100 IDL files
/// (windows.ui.notifications.idl, windows.data.xml.dom.idl). Methods are called through their v-table slots:
/// 0–2 are IUnknown and 3–5 IInspectable, so an interface's first own method is slot 6.
/// </summary>
internal static class ToastInterop
{
    private const string ManagerClass = "Windows.UI.Notifications.ToastNotificationManager";
    private const string NotificationClass = "Windows.UI.Notifications.ToastNotification";
    private const string XmlDocumentClass = "Windows.Data.Xml.Dom.XmlDocument";
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    // IToastNotificationManagerStatics: CreateToastNotifier 6, CreateToastNotifierWithId 7, GetTemplateContent 8.
    private static readonly Guid ManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

    // IToastNotificationFactory: CreateToastNotification 6.
    private static readonly Guid NotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");

    // IToastNotification2: put_Tag 6, get_Tag 7, put_Group 8, get_Group 9, put_SuppressPopup 10.
    private static readonly Guid Notification2 = new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");

    // IToastNotifier: Show 6.
    private static readonly Guid Notifier = new("75927B93-03F3-41EC-91D3-6E5BAC1B38E7");

    // IXmlDocumentIO: LoadXml 6.
    private static readonly Guid XmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");

    // IXmlDocument: passed to CreateToastNotification.
    private static readonly Guid XmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");

    private static int s_failureLogged;

    private delegate int HStringOutFn(IntPtr self, IntPtr text, out IntPtr result);

    private delegate int HStringFn(IntPtr self, IntPtr text);

    private delegate int PointerOutFn(IntPtr self, IntPtr argument, out IntPtr result);

    private delegate int PointerFn(IntPtr self, IntPtr argument);

    private delegate int BooleanFn(IntPtr self, byte value);

    /// <summary>
    /// Shows a toast under the registered app user model id; a toast with the same <paramref name="tag"/> and
    /// <paramref name="group"/> replaces an older one. <paramref name="silent"/> skips the pop-up (quiet hours).
    /// False when any step fails; the first failure in a process is logged.
    /// </summary>
    /// <summary>The id notifications go out under; the test run sets one that is never registered, so nothing shows.</summary>
    internal static string AppUserModelId { get; set; } = ToastRegistration.AppUserModelId;

    public static bool Show(string xml, string tag, string group, bool silent) => Show(xml, tag, group, silent, AppUserModelId);

    /// <summary>The same under another app user model id (tests use one that is never registered, so nothing shows).</summary>
    internal static bool Show(string xml, string tag, string group, bool silent, string appUserModelId)
    {
        using WinRtScope scope = new();
        try
        {
            IntPtr manager = scope.Factory(ManagerClass, ManagerStatics);
            IntPtr factory = scope.Factory(NotificationClass, NotificationFactory);
            IntPtr document = scope.Activate(XmlDocumentClass);
            IntPtr io = scope.Query(document, XmlDocumentIO);
            Check(Slot<HStringFn>(io, 6)(io, scope.String(xml)));
            IntPtr content = scope.Query(document, XmlDocument);
            Check(Slot<HStringOutFn>(manager, 7)(manager, scope.String(appUserModelId), out IntPtr notifierUnknown));
            scope.Own(notifierUnknown);
            IntPtr notifier = scope.Query(notifierUnknown, Notifier);
            Check(Slot<PointerOutFn>(factory, 6)(factory, content, out IntPtr toast));
            scope.Own(toast);
            IntPtr toast2 = scope.Query(toast, Notification2);
            Check(Slot<HStringFn>(toast2, 6)(toast2, scope.String(tag)));
            Check(Slot<HStringFn>(toast2, 8)(toast2, scope.String(group)));
            Check(Slot<BooleanFn>(toast2, 10)(toast2, silent ? (byte)1 : (byte)0));
            Check(Slot<PointerFn>(notifier, 6)(notifier, toast));
            return true;
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException or MarshalDirectiveException)
        {
            if (Interlocked.Exchange(ref s_failureLogged, 1) == 0)
            {
                ErrorLog.Write("Windows notification", ex);
            }

            return false;
        }
    }

    /// <summary>The self-check: the notification manager's activation factory can be acquired. Shows nothing.</summary>
    public static bool CanActivate()
    {
        using WinRtScope scope = new();
        try
        {
            _ = scope.Factory(ManagerClass, ManagerStatics);
            return true;
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static T Slot<T>(IntPtr instance, int slot)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            throw new COMException("A Windows notification call failed", hresult);
        }
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern void RoUninitialize();

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>
    /// The interface pointers and strings of one call, released in reverse order. Initialises the Windows Runtime on
    /// threads that have no apartment yet (pool threads) and leaves threads that have one (the UI thread) alone.
    /// </summary>
    private sealed class WinRtScope : IDisposable
    {
        private readonly List<IntPtr> _pointers = [];
        private readonly List<IntPtr> _strings = [];
        private readonly bool _initialised;

        public WinRtScope()
        {
            int result = RoInitialize(1);
            _initialised = result >= 0 && result != RpcEChangedMode;
        }

        public IntPtr Factory(string className, Guid iid)
        {
            Guid id = iid;
            Check(RoGetActivationFactory(String(className), ref id, out IntPtr factory));
            return Own(factory);
        }

        public IntPtr Activate(string className)
        {
            Check(RoActivateInstance(String(className), out IntPtr instance));
            return Own(instance);
        }

        public IntPtr Query(IntPtr unknown, Guid iid)
        {
            Check(Marshal.QueryInterface(unknown, in iid, out IntPtr result));
            return Own(result);
        }

        public IntPtr String(string text)
        {
            Check(WindowsCreateString(text, text.Length, out IntPtr hstring));
            _strings.Add(hstring);
            return hstring;
        }

        public IntPtr Own(IntPtr pointer)
        {
            if (pointer != IntPtr.Zero)
            {
                _pointers.Add(pointer);
            }

            return pointer;
        }

        public void Dispose()
        {
            for (int i = _pointers.Count - 1; i >= 0; i--)
            {
                _ = Marshal.Release(_pointers[i]);
            }

            foreach (IntPtr text in _strings)
            {
                _ = WindowsDeleteString(text);
            }

            if (_initialised)
            {
                RoUninitialize();
            }
        }
    }
}

/// <summary>
/// The system notification channel (subplan 13, System toasts): a Windows toast, else the tray balloon. The caller
/// writes the in-app entry (the bell) before calling, so every system notification has one.
/// </summary>
internal static class SystemToast
{
    /// <summary>Tag and group of run notifications: a newer run's toast replaces an unread older one.</summary>
    public const string RunTag = "scan";
    public const string RunGroup = "runs";

    /// <summary>
    /// Shows a notification; returns false when no system channel worked (the bell still has it). The balloon is for
    /// headless runs; the open window falls back to its in-app toast instead.
    /// </summary>
    public static bool Show(string title, string body, ActivationUri link, bool silent, bool allowBalloon = true, string tag = RunTag, string group = RunGroup)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (ToastInterop.Show(ToastXml.Build(title, body, link, CoreStrings.Notify.Review), tag, group, silent))
        {
            return true;
        }

        return allowBalloon && !silent && ShowBalloon(title, body);
    }

    /// <summary>The fallback: a temporary tray icon whose balloon Windows shows as a toast for a few seconds.</summary>
    private static bool ShowBalloon(string title, string body)
    {
        try
        {
            using NotifyIcon icon = new() { Icon = SystemIcons.Information, Text = AppInfo.ProductName, Visible = true };
            icon.ShowBalloonTip(10_000, title, body.Length == 0 ? title : body, ToolTipIcon.Info);
            Thread.Sleep(TimeSpan.FromSeconds(8));
            icon.Visible = false;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
        {
            ErrorLog.Write("Tray notification", ex);
            return false;
        }
    }
}
