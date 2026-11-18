using System.Diagnostics;
using MediaVerifier.App;
using MediaVerifier.Core.Model;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>Settings (subplan 09, Settings): what each row does when used, off-screen and without taking the focus.</summary>
[Collection(UiCollection.Name)]
public sealed class SettingsBehaviourTests
{
    /// <summary>A change that cannot be written says so once (it used to look applied and revert at the next start).</summary>
    [Fact]
    public void A_setting_that_cannot_be_saved_shows_one_critical_toast()
    {
        List<ToastContent> toasts = [];
        bool savedLater = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("settings-save-fails");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            services.ShowToast = toasts.Add;
            using SettingsPage page = new(services) { Size = new Size(1000, 700) };
            Directory.CreateDirectory(state.SettingsStore.TempPath);

            ToggleSwitch ask = Find<ToggleSwitch>(page, t => t.AccessibleName == Strings.Settings.AskLarge);
            ask.Checked = !ask.Checked;
            Settle(() => toasts.Count > 0);
            ask.Checked = !ask.Checked;
            Pump(TimeSpan.FromMilliseconds(300));

            Directory.Delete(state.SettingsStore.TempPath);
            ask.Checked = !ask.Checked;
            Settle(() => File.Exists(state.Paths.SettingsFile));
            savedLater = File.Exists(state.Paths.SettingsFile);
        });

        ToastContent toast = Assert.Single(toasts);
        Assert.Equal(Severity.Critical, toast.Level);
        Assert.Equal(Strings.Settings.SaveFailed, toast.Text);
        Assert.Equal(Strings.Settings.OpenLogs, toast.ActionText);
        Assert.True(savedLater, "The next change was not saved once the file could be written again.");
    }

    /// <summary>Integrity offers no choice of where originals go: the choice was saved and ignored (they go to quarantine).</summary>
    [Fact]
    public void Integrity_has_no_keep_originals_choice()
    {
        int dropDowns = -1;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("settings-integrity");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using SettingsPage page = new(services) { Size = new Size(1000, 700) };
            page.ShowSection(3);
            Control integrity = Descendants(page).First(c => c.Visible && c.Parent == page && c.Left > 0);
            dropDowns = Descendants(integrity).OfType<DropDown>().Count();
        });

        Assert.Equal(0, dropDowns);
    }

    /// <summary>
    /// In a window of the minimum height the Scanning section is taller than the content area: the page scrolls, its last
    /// row can be brought into view, and another section starts at the top again (the lower rows were cut off).
    /// </summary>
    [Fact]
    public void A_tall_section_scrolls_to_its_last_row()
    {
        int scrollMax = -1;
        bool lastRowShown = false;
        int afterSwitch = -1;
        int generalMax = -1;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("settings-scroll");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.MinimumClientSize);
            form.Navigate(PageId.Settings);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            settings.ShowSection(1);
            Pump(TimeSpan.FromMilliseconds(100));

            MediaVerifier.UI.Shell.PageViewport viewport = form.Viewport;
            scrollMax = viewport.ScrollMax;
            ToggleSwitch strict = Find<ToggleSwitch>(settings, t => t.AccessibleName == Strings.Settings.Strict);
            viewport.EnsureVisible(strict);
            Pump(TimeSpan.FromMilliseconds(50));
            Rectangle shown = viewport.RectangleToClient(strict.RectangleToScreen(strict.ClientRectangle));
            lastRowShown = shown.Top >= 0 && shown.Bottom <= viewport.ClientSize.Height;

            settings.ShowSection(0);
            Pump(TimeSpan.FromMilliseconds(50));
            afterSwitch = viewport.ScrollOffset;
            generalMax = viewport.ScrollMax;
            form.Close();
        });

        Assert.True(scrollMax > 0, "Scanning does not scroll at the minimum window height.");
        Assert.True(lastRowShown, "Strict decoding could not be brought into view.");
        Assert.Equal(0, afterSwitch);
        Assert.Equal(0, generalMax);
    }

    /// <summary>
    /// Send test takes the address just typed: a click on a kit button leaves the focus in the box, so the address was
    /// never committed and the test called the https address invalid.
    /// </summary>
    [Fact]
    public void Send_test_takes_the_address_just_typed()
    {
        string? saved = null;
        string? afterInvalid = null;
        string boxAfterInvalid = "unset";
        List<ToastContent> toasts = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("settings-webhook");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            services.ShowToast = toasts.Add;
            using SettingsPage page = new(services) { Size = new Size(1000, 700) };
            page.ShowSection(4);
            KitTextBox box = Find<KitTextBox>(page, b => b.AccessibleName == Strings.Settings.Webhook);
            KitButton send = Find<KitButton>(page, b => b.Text == Strings.Settings.SendTest);

            box.Text = "https://example.org/hook";
            send.PerformClick();
            Pump(TimeSpan.FromMilliseconds(50));
            saved = state.Settings.Scanning.WebhookUrl;

            box.Text = "ftp://example.org/hook";
            send.PerformClick();
            Pump(TimeSpan.FromMilliseconds(300));
            afterInvalid = state.Settings.Scanning.WebhookUrl;
            boxAfterInvalid = box.Text;
        });

        Assert.Equal("https://example.org/hook", saved);
        Assert.Equal("https://example.org/hook", afterInvalid);
        Assert.Equal("https://example.org/hook", boxAfterInvalid);
        ToastContent invalid = Assert.Single(toasts);
        Assert.Equal(Strings.Settings.WebhookInvalid, invalid.Text);
    }

    /// <summary>An https address is kept once typing pauses, without leaving the box (closing to the tray kept the old one).</summary>
    [Fact]
    public void An_https_webhook_is_kept_when_typing_pauses()
    {
        string? saved = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("settings-webhook-pause");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using SettingsPage page = new(services) { Size = new Size(1000, 700) };
            KitTextBox box = Find<KitTextBox>(page, b => b.AccessibleName == Strings.Settings.Webhook);
            box.CreateControl();
            box.Text = "https://example.org/pause";
            Settle(() => state.Settings.Scanning.WebhookUrl is not null, TimeSpan.FromSeconds(2));
            saved = state.Settings.Scanning.WebhookUrl;
        });

        Assert.Equal("https://example.org/pause", saved);
    }

    /// <summary>The self-check and Send test take seconds: the button is disabled, says why, and ignores a second click.</summary>
    [Fact]
    public void A_slow_button_is_disabled_with_a_reason_until_its_work_ends()
    {
        bool enabledWhileBusy = true;
        string tipWhileBusy = string.Empty;
        bool enabledAfter = false;
        string tipAfter = "unset";
        int runs = 0;
        Sta.Run(() =>
        {
            using KitButton button = new() { Text = Strings.Settings.RunSelfCheck };
            TaskCompletionSource done = new();
            Task first = SettingsPage.WhileBusyAsync(button, Strings.Settings.SelfCheckRunning, () =>
            {
                runs++;
                return done.Task;
            });
            enabledWhileBusy = button.Enabled;
            tipWhileBusy = KitToolTip.Get(button);
            Task second = SettingsPage.WhileBusyAsync(button, Strings.Settings.SelfCheckRunning, () =>
            {
                runs++;
                return Task.CompletedTask;
            });
            done.SetResult();
            Settle(() => first.IsCompleted && second.IsCompleted);
            enabledAfter = button.Enabled;
            tipAfter = KitToolTip.Get(button);
        });

        Assert.False(enabledWhileBusy);
        Assert.Equal(Strings.Settings.SelfCheckRunning, tipWhileBusy);
        Assert.Equal(1, runs);
        Assert.True(enabledAfter);
        Assert.Equal(string.Empty, tipAfter);
    }

    internal static T Find<T>(Control root, Func<T, bool> match)
        where T : Control => Descendants(root).OfType<T>().First(match);

    internal static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control inner in Descendants(child))
            {
                yield return inner;
            }
        }
    }

    internal static void Settle(Func<bool> done, TimeSpan? limit = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < (limit ?? TimeSpan.FromSeconds(10)))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    internal static void Pump(TimeSpan time) => Settle(() => false, time);
}
