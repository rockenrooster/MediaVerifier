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
