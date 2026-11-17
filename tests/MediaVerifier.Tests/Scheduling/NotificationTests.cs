using System.Xml.Linq;
using MediaVerifier.App;
using MediaVerifier.Core.Ipc;

namespace MediaVerifier.Tests.Scheduling;

/// <summary>Activation links, per-user registration and the toast payload (subplan 13; tasks N-02, N-03).</summary>
public sealed class NotificationTests
{
    [Theory]
    [InlineData("mediaverifier://run/123", ActivationTarget.Run, 123L)]
    [InlineData("MEDIAVERIFIER://RUN/7/", ActivationTarget.Run, 7L)]
    [InlineData("mediaverifier://finding/9", ActivationTarget.Finding, 9L)]
    [InlineData("mediaverifier://finding/999999999999999999", ActivationTarget.Finding, 999_999_999_999_999_999L)]
    [InlineData("mediaverifier://issues", ActivationTarget.Issues, 0L)]
    [InlineData("mediaverifier://Issues/", ActivationTarget.Issues, 0L)]
    public void The_three_link_forms_parse_and_round_trip(string text, ActivationTarget target, long id)
    {
        ActivationUri? link = ActivationUri.Parse(text);
        Assert.NotNull(link);
        Assert.Equal(target, link.Target);
        Assert.Equal(id, link.Id);
        Assert.Equal(link, ActivationUri.Parse(link.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mediaverifier://")]
    [InlineData("mediaverifier://run")]
    [InlineData("mediaverifier://run/")]
    [InlineData("mediaverifier://run/0")]
    [InlineData("mediaverifier://run/007")]
    [InlineData("mediaverifier://run/-1")]
    [InlineData("mediaverifier://run/+1")]
    [InlineData("mediaverifier://run/ 1")]
    [InlineData("mediaverifier://run/1 ")]
    [InlineData("mediaverifier://run/1/2")]
    [InlineData("mediaverifier://run//1")]
    [InlineData("mediaverifier://run/1?x=y")]
    [InlineData("mediaverifier://run/1#top")]
    [InlineData("mediaverifier://run/12a")]
    [InlineData("mediaverifier://run/\u0661\u0662")]
    [InlineData("mediaverifier://run/1234567890123456789")]
    [InlineData("mediaverifier://run/99999999999999999999")]
    [InlineData("mediaverifier://finding/1%2F..%2F")]
    [InlineData("mediaverifier://open/C:/Windows/System32")]
    [InlineData("mediaverifier://file/C:\\Music\\a.flac")]
    [InlineData("mediaverifier://issues?delete=all")]
    [InlineData("mediaverifier://issues/1")]
    [InlineData("mediaverifier://scan/1")]
    [InlineData("mediaverifier:run/1")]
    [InlineData(" mediaverifier://run/1")]
    [InlineData("https://run/1")]
    public void Anything_else_is_rejected(string? text) => Assert.Null(ActivationUri.Parse(text));

    [Fact]
    public void Fuzzed_links_only_ever_parse_to_their_canonical_form()
    {
        Random random = new(20261117);
        const string alphabet = "runfidgsse/0123456789:%.?&=#\\-_ +\u0661";
        string[] stems = ["mediaverifier://", "mediaverifier://run/", "mediaverifier://finding/", "mediaverifier://issues", "MediaVerifier://", string.Empty];
        int parsed = 0;
        for (int i = 0; i < 50_000; i++)
        {
            char[] tail = new char[random.Next(0, 24)];
            for (int c = 0; c < tail.Length; c++)
            {
                tail[c] = alphabet[random.Next(alphabet.Length)];
            }

            string text = stems[random.Next(stems.Length)] + new string(tail);
            if (ActivationUri.Parse(text) is not ActivationUri link)
            {
                continue;
            }

            parsed++;
            Assert.Equal(link.ToString(), text.EndsWith('/') ? text[..^1] : text, ignoreCase: true);
            Assert.True(link.Target == ActivationTarget.Issues ? link.Id == 0 : link.Id > 0, text);
        }

        Assert.True(parsed > 0);
    }

    [Fact]
    public void Registration_writes_the_aumid_and_protocol_and_is_idempotent()
    {
        FakeRegistry registry = new();
        ToastRegistration.Register(registry, @"C:\Program Files\MediaVerifier\MediaVerifier.exe", @"C:\Users\A\AppData\Local\MediaVerifier\notification-icon.png");
        Dictionary<(string, string), string> first = new(registry.Values);
        Assert.Equal(5, registry.Writes);
        Assert.Equal("MediaVerifier", registry.Values[(ToastRegistration.AumidKey, "DisplayName")]);
        Assert.Equal(@"C:\Users\A\AppData\Local\MediaVerifier\notification-icon.png", registry.Values[(ToastRegistration.AumidKey, "IconUri")]);
        Assert.Equal("URL:MediaVerifier", registry.Values[(@"Software\Classes\mediaverifier", string.Empty)]);
        Assert.Equal(string.Empty, registry.Values[(@"Software\Classes\mediaverifier", "URL Protocol")]);
        Assert.Equal("\"C:\\Program Files\\MediaVerifier\\MediaVerifier.exe\" \"%1\"", registry.Values[(@"Software\Classes\mediaverifier\shell\open\command", string.Empty)]);

        // A second start writes nothing new and leaves the same state.
        ToastRegistration.Register(registry, @"C:\Program Files\MediaVerifier\MediaVerifier.exe", @"C:\Users\A\AppData\Local\MediaVerifier\notification-icon.png");
        Assert.Equal(5, registry.Writes);
        Assert.Equal(first, registry.Values);

        // A moved portable copy repairs the command on its next start.
        ToastRegistration.Register(registry, @"D:\Tools\MediaVerifier.exe", @"C:\Users\A\AppData\Local\MediaVerifier\notification-icon.png");
        Assert.Equal(6, registry.Writes);
        Assert.Equal("\"D:\\Tools\\MediaVerifier.exe\" \"%1\"", registry.Values[(@"Software\Classes\mediaverifier\shell\open\command", string.Empty)]);

        ToastRegistration.Remove(registry);
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void The_toast_payload_matches_the_plan_and_escapes_text()
    {
        string xml = ToastXml.Build("3 new problems found", "1 critical, 2 high in Music. Scan finished 03:12.", new ActivationUri(ActivationTarget.Run, 123), "Review");
        Assert.Equal(
            "<toast launch=\"mediaverifier://run/123\" activationType=\"protocol\"><visual><binding template=\"ToastGeneric\"><text>3 new problems found</text>"
            + "<text>1 critical, 2 high in Music. Scan finished 03:12.</text></binding></visual><actions><action content=\"Review\" activationType=\"protocol\" "
            + "arguments=\"mediaverifier://run/123\" /></actions></toast>",
            xml);

        string hostile = ToastXml.Build("<b>\"&'</b>", string.Empty, new ActivationUri(ActivationTarget.Issues, 0), "Open & go");
        XElement toast = XElement.Parse(hostile);
        Assert.Equal("<b>\"&'</b>", toast.Descendants("text").Single().Value);
        Assert.Equal("Open & go", toast.Descendants("action").Single().Attribute("content")!.Value);
    }

    [Fact]
    public void The_notification_manager_can_be_activated_without_showing_anything() => Assert.True(ToastInterop.CanActivate());

    [Fact]
    public void A_toast_goes_through_every_interop_call_under_an_unregistered_id()
    {
        // Windows drops toasts of an id with no registration, so this exercises every v-table call and shows nothing.
        string xml = ToastXml.Build("Test", "Body", new ActivationUri(ActivationTarget.Issues, 0), "Review");
        Assert.True(ToastInterop.Show(xml, "test", "tests", silent: true, "rockenrooster.MediaVerifier.Tests.Unregistered"));
        Assert.False(ToastInterop.Show("<toast><not-closed>", "test", "tests", silent: true, "rockenrooster.MediaVerifier.Tests.Unregistered"));
    }

    [Fact]
    public void The_notification_icon_is_a_256_pixel_png()
    {
        using Tests.TestSupport.TempDirectory temp = new("icon");
        string path = Path.Combine(temp.Path, ToastRegistration.IconFileName);
        ToastRegistration.WriteIcon(path);
        using Bitmap icon = new(path);
        Assert.Equal(new Size(256, 256), icon.Size);
        Assert.NotEqual(0, icon.GetPixel(128, 128).A);
        Assert.Equal(0, icon.GetPixel(0, 0).A);
    }

    private sealed class FakeRegistry : IRegistryWriter
    {
        public Dictionary<(string Key, string Name), string> Values { get; } = [];

        public int Writes { get; private set; }

        public void SetValue(string subKey, string? name, string value)
        {
            (string, string) id = (subKey, name ?? string.Empty);
            if (!Values.TryGetValue(id, out string? current) || current != value)
            {
                Values[id] = value;
                Writes++;
            }
        }

        public void DeleteTree(string subKey)
        {
            foreach ((string Key, string Name) id in Values.Keys.Where(k => k.Key.Equals(subKey, StringComparison.OrdinalIgnoreCase) || k.Key.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Values.Remove(id);
            }
        }
    }
}

/// <summary>The open window while a scheduled scan runs in another process (subplan 13; task SCH-06).</summary>
[Collection(Tests.TestSupport.UiCollection.Name)]
public sealed class BackgroundRunTests
{
    [Fact]
    public void A_background_run_disables_scanning_with_its_reason()
    {
        using Tests.TestSupport.TempDirectory temp = new("background");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        bool running = true;
        using UiServices demo = Diagnostics.DemoUiData.CreateServices(state, temp.Path);
        Diagnostics.DemoScanControl scans = new() { Status = Core.Engine.EngineStatus.Idle };
        using UiServices services = new()
        {
            State = state,
            Database = demo.Database,
            Clock = demo.Clock,
            Scans = scans,
            MediaEngine = demo.MediaEngine,
            Data = demo.Data,
            BackgroundProbe = () => running,
        };

        Assert.True(services.BackgroundRunActive());
        Assert.Equal(MediaVerifier.UI.Strings.Shell.ScheduledRunning, services.CannotScanReason(hasFolders: true));

        running = false;
        Assert.False(services.BackgroundRunActive());
        Assert.Null(services.CannotScanReason(hasFolders: true));

        // The window's own scan is reported as its own, whatever runs elsewhere.
        running = true;
        scans.Status = Diagnostics.DemoScanControl.Running;
        Assert.False(services.BackgroundRunActive());
        Assert.Equal(MediaVerifier.UI.Strings.Shell.AlreadyRunning, services.CannotScanReason(hasFolders: true));
    }

    [Fact]
    public void Stop_signals_the_headless_runs_event()
    {
        // A private name: the real event belongs to a real scheduled run, which a test must never stop.
        string name = @"Local\MediaVerifier.Tests.StopRun." + Guid.NewGuid().ToString("N");
        Assert.False(PlanHost.StopBackgroundRun(name));
        Assert.False(PlanHost.BackgroundRunActive(name));
        using (EventWaitHandle stop = new(false, EventResetMode.ManualReset, name))
        {
            Assert.True(PlanHost.BackgroundRunActive(name));
            Assert.True(PlanHost.StopBackgroundRun(name));
            Assert.True(stop.WaitOne(0));
        }

        Assert.False(PlanHost.BackgroundRunActive(name));
    }
}

/// <summary>Settings → Notifications (subplan 09, Settings; task N-05).</summary>
[Collection(Tests.TestSupport.UiCollection.Name)]
public sealed class NotificationSettingsTests
{
    [Fact]
    public void Quiet_hour_times_appear_only_while_quiet_hours_are_on()
    {
        using Tests.TestSupport.TempDirectory temp = new("settings");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        using UiServices services = Diagnostics.DemoUiData.CreateServices(state, temp.Path);
        using MediaVerifier.UI.Pages.SettingsPage page = new(services) { Size = new Size(1000, 640) };
        page.ShowSection(4);
        page.PerformLayout();
        List<MediaVerifier.UI.Kit.KitTextBox> times = [.. Descendants<MediaVerifier.UI.Kit.KitTextBox>(page).Where(b => b.AccessibleName is "From" or "To")];
        Assert.Equal(2, times.Count);
        Assert.All(times, t => Assert.False(t.Parent!.Visible));
        Assert.Contains(Descendants<MediaVerifier.UI.Kit.KitButton>(page), b => b.Text == MediaVerifier.UI.Strings.Settings.SendTestNotification);

        MediaVerifier.UI.Kit.ToggleSwitch quiet = Descendants<MediaVerifier.UI.Kit.ToggleSwitch>(page).Single(t => t.AccessibleName == MediaVerifier.UI.Strings.Settings.QuietHours);
        quiet.Checked = true;
        Assert.All(times, t => Assert.True(t.Parent!.Visible));
        Assert.True(state.Settings.Scanning.QuietHours);
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            using Bitmap bitmap = new(page.Width, page.Height);
            page.DrawToBitmap(bitmap, new Rectangle(Point.Empty, page.Size));
            bitmap.Save(Path.Combine(folder, "settings-notifications.png"));
        }

        quiet.Checked = false;
        Assert.All(times, t => Assert.False(t.Parent!.Visible));
    }

    private static IEnumerable<T> Descendants<T>(Control root)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }
}
