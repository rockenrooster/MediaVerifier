using System.Diagnostics;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Pages;
using Xunit.Abstractions;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Task H-02 (subplan 16): with the UI watchdog running, the flows of subplan 09 on the demo library never block the
/// UI thread for more than 50 ms after the first visit of each page (which creates it). In the Perf category because
/// a busy machine stretches every step; scripts/Measure-Performance.ps1 records the longest block.
/// </summary>
[Collection(UiCollection.Name)]
[Trait("Category", "Perf")]
public sealed class UiStallTests(ITestOutputHelper output)
{
    [Fact]
    public void Page_flows_never_block_the_ui_thread_for_more_than_50_ms()
    {
        List<string> stalls = [];
        TimeSpan longest = TimeSpan.Zero;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("stall");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);

            // First visits create the pages and warm the caches.
            Walk(form);

            using UiWatchdog watchdog = new(SynchronizationContext.Current!, (waited, marks) => stalls.Add($"{waited.TotalMilliseconds:0} ms after {marks}"));
            for (int round = 0; round < 2; round++)
            {
                Walk(form);
            }

            Pump(TimeSpan.FromMilliseconds(300));
            longest = watchdog.Longest;
            form.Close();
        });
        output.WriteLine($"longest UI block: {longest.TotalMilliseconds:0} ms");
        PerfReport.Add("UI thread", "every page twice, drawer, browse and selection on the demo library", $"longest block {longest.TotalMilliseconds:0} ms", "never over 50 ms");
        Assert.True(stalls.Count == 0, string.Join(Environment.NewLine, stalls));
    }

    /// <summary>Every page, then the Issues drawer and selection, Library browse and the Settings sections.</summary>
    private static void Walk(MainForm form)
    {
        foreach (PageId page in PageInfo.All)
        {
            form.Navigate(page);
            Settle(form.CurrentPageLoading);
            if (form.CurrentPageControl is IssuesPage issues)
            {
                issues.ShowDemoSelection();
                Pump(TimeSpan.FromMilliseconds(50));
                Settle(issues.ShowDemoDetailAsync());
                form.SetDrawerContent(null);
            }
            else if (form.CurrentPageControl is LibraryPage library)
            {
                library.ShowDemoBrowse();
                Settle(form.CurrentPageLoading);
            }
            else if (form.CurrentPageControl is SettingsPage settings)
            {
                for (int section = 0; section < 8; section++)
                {
                    settings.ShowSection(section);
                    Pump(TimeSpan.FromMilliseconds(20));
                }
            }

            Pump(TimeSpan.FromMilliseconds(100));
        }
    }
}
