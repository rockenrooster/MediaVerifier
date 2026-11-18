using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.UI;

/// <summary>
/// Hand-offs to Windows Explorer, the default programs and the clipboard. Opening runs on a short-lived thread of its
/// own: the existence checks and the shell wait for a disconnected network share or a sleeping disk, which froze the
/// window when they ran on the UI thread. Files, folders and pages open through ShellExecute: explorer.exe splits its
/// command line at commas, so a path such as <c>D:\Music\A,B</c> opened Documents instead.
/// </summary>
internal static class ShellActions
{
    /// <summary>
    /// Where the hand-offs report back: the main window's toasts (set by the main window, called on the UI thread). A
    /// menu item that silently did nothing was the most common complaint.
    /// </summary>
    public static Action<Kit.Popups.ToastContent>? Toast { get; set; }

    /// <summary>
    /// Opens Explorer on the file's folder with the file selected, through the shell API. A command line
    /// <c>explorer /select,…</c> fails for paths with spaces once the whole argument is quoted. A file that is
    /// gone opens its folder.
    /// </summary>
    public static void ShowInFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            OffUiThread("Show in folder", () => ShowInFolderNow(path));
        }
    }

    /// <summary>
    /// Opens a file in the program Windows has for its type. A file that is gone (moved, deleted, or on a drive that is
    /// not connected) is reported in a toast that offers its folder; it used to do nothing at all.
    /// </summary>
    public static void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        SynchronizationContext? ui = SynchronizationContext.Current;
        OffUiThread("Open file", () =>
        {
            if (File.Exists(path))
            {
                Open(path);
            }
            else
            {
                Report(ui, FileNotFound(path));
            }
        });
    }

    /// <summary>The toast for a file that is not there, with Show in folder.</summary>
    internal static Kit.Popups.ToastContent FileNotFound(string path) =>
        new(Core.Model.Severity.Medium, Strings.Shell.FileNotFound, Path.GetFileName(path), Strings.Menu.ShowInFolder, () => ShowInFolder(path));

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            OffUiThread("Open folder", () => Open(path));
        }
    }

    /// <summary>Opens a web page in the default browser.</summary>
    public static void OpenUrl(string url) => OffUiThread("Open page", () => Open(url));

    /// <summary>Opens <paramref name="path"/> when it exists, otherwise <paramref name="fallback"/>; the check runs off the UI thread.</summary>
    public static void OpenFileOr(string path, string fallback) => OffUiThread("Open", () => Open(File.Exists(path) ? path : fallback));

    public const string WebsiteUrl = "https://github.com/rockenrooster/MediaVerifier";
    public const string ReleasesUrl = "https://github.com/rockenrooster/MediaVerifier/releases";
    public const string NoticesUrl = "https://github.com/rockenrooster/MediaVerifier/blob/master/THIRD-PARTY-NOTICES.md";

    /// <summary>The help page for a rule (its section of docs/rules.md), or the help index.</summary>
    public static string HelpUrl(string? ruleId)
    {
        const string Rules = "https://github.com/rockenrooster/MediaVerifier/blob/master/docs/rules.md";
        if (ruleId is null || !Core.Rules.RuleCatalog.TryGet(ruleId, out Core.Rules.RuleDefinition rule))
        {
            return "https://github.com/rockenrooster/MediaVerifier#readme";
        }

        // GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens.
        string heading = (rule.Id + " " + rule.Title).ToLowerInvariant();
        System.Text.StringBuilder slug = new(heading.Length);
        foreach (char c in heading)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return Rules + "#" + slug;
    }

    /// <summary>
    /// Copies the lines to the clipboard (UI thread) and says so in a one-line toast. When another program holds the
    /// clipboard, Windows Forms retries for about a second and then throws: that is a toast too, not the error dialog.
    /// </summary>
    public static void CopyText(IEnumerable<string> lines)
    {
        string text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            AppLog.Shared?.Warning("Copy to the clipboard failed", ex);
            Toast?.Invoke(new Kit.Popups.ToastContent(Core.Model.Severity.Medium, Strings.Shell.CopyFailed, Strings.Shell.ClipboardBusy));
            return;
        }

        Toast?.Invoke(new Kit.Popups.ToastContent(Core.Model.Severity.None, Strings.Shell.Copied, string.Empty));
    }

    /// <summary>Shows a toast on the UI thread the hand-off was started from (nothing when there is none).</summary>
    private static void Report(SynchronizationContext? ui, Kit.Popups.ToastContent content) =>
        ui?.Post(_ => Toast?.Invoke(content), null);

    private static void ShowInFolderNow(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            OpenParent(path);
            return;
        }

        if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr item, 0, out _) < 0 || item == IntPtr.Zero)
        {
            OpenParent(path);
            return;
        }

        try
        {
            // With no child items, the one item list names the item to select in its parent folder.
            if (SHOpenFolderAndSelectItems(item, 0, null, 0) < 0)
            {
                OpenParent(path);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    private static void OpenParent(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            Open(parent);
        }
    }

    /// <summary>
    /// ShellExecute's default verb: the file's program, Explorer for a folder, the browser for a page. Windows shows its
    /// own Open with dialog for a type without a program, as Explorer does.
    /// </summary>
    private static void Open(string target)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, ErrorDialog = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No program, or Windows refused (and said so): nothing more to show.
        }
    }

    /// <summary>Runs a shell hand-off on its own STA thread; a failure is logged, the window never waits for it.</summary>
    private static void OffUiThread(string what, Action action)
    {
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ErrorLog.Write(what, ex);
            }
        })
        {
            IsBackground = true,
            Name = what,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[]? items, uint flags);
}
