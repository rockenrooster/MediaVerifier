using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MediaVerifier.App;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Pages;
using Xunit.Abstractions;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The Issues page on about 4,000 open findings stays inside the watchdog's 50 ms (subplan 16, H-02): the owner's log
/// showed "UI thread blocked for 160 ms" with about 3,900 open findings. Applying a load, Select all with its Fix…
/// state, and browsing files in the drawer are timed. In the Perf category because a busy machine stretches timings.
/// </summary>
[Collection(UiCollection.Name)]
[Trait("Category", "Perf")]
public sealed class IssuesApplyPerfTests(ITestOutputHelper output)
{
    [Fact]
    public void Four_thousand_findings_never_block_the_ui_thread_for_50_ms()
    {
        List<double> apply = [];
        List<double> withPaint = [];
        List<string> stalls = [];
        TimeSpan longest = TimeSpan.Zero;
        int ids = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("issues-apply");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));
            Task.Run(() => SyntheticLibrary.GenerateAsync(services.Database, new SyntheticLibraryOptions { Files = 10_000, FindingRate = 0.4 })).GetAwaiter().GetResult();
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.ShowToast = _ => { };
            form.Navigate(PageId.Issues);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            IssuesPage page = Assert.IsType<IssuesPage>(form.CurrentPageControl);
            MethodInfo load = typeof(IssuesPage).GetMethod("LoadData", BindingFlags.NonPublic | BindingFlags.Instance)!;
            MethodInfo applyData = typeof(IssuesPage).GetMethod("ApplyData", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int round = 0; round < 5; round++)
            {
                object data = Task.Run(() => load.Invoke(page, null)!).GetAwaiter().GetResult();
                Stopwatch watch = Stopwatch.StartNew();
                applyData.Invoke(page, [data]);
                apply.Add(watch.Elapsed.TotalMilliseconds);
                page.PerformLayout();
                form.Refresh();
                withPaint.Add(watch.Elapsed.TotalMilliseconds);
            }

            ids = Field<MediaVerifier.UI.Data.IssuesData>(page, "_data").Ids.Length;

            // The first drawer open creates its controls; then the timed flow.
            VirtualList list = Field<VirtualList>(page, "_list");
            FileDetailView view = Field<FileDetailView>(page, "_detail");
            object source = Field<object>(page, "_source");
            MethodInfo rowAt = source.GetType().GetMethod("RowAt")!;
            int first = Enumerable.Range(0, list.Count).First(i => rowAt.Invoke(source, [i]) is not null);
            OpenAndWait(form, page, view, first);
            form.SetDrawerContent(null);
            Pump(TimeSpan.FromMilliseconds(300));

            using UiWatchdog watchdog = new(SynchronizationContext.Current!, (waited, marks) => stalls.Add($"{waited.TotalMilliseconds:0} ms after {marks}"));
            list.SelectAll();
            Pump(TimeSpan.FromMilliseconds(1500));
            list.ClearSelection();
            Pump(TimeSpan.FromMilliseconds(100));
            list.FocusRow(first);
            OpenAndWait(form, page, view, first);
            for (int i = 0; i < 5; i++)
            {
                long shown = view.Detail!.FileId;
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(list, [new KeyEventArgs(Keys.Down)]);
                Settle(() => view.Detail!.FileId != shown, TimeSpan.FromSeconds(3));
                Pump(TimeSpan.FromMilliseconds(150));
            }

            form.SetDrawerContent(null);
            Pump(TimeSpan.FromMilliseconds(300));
            longest = watchdog.Longest;
            form.Close();
        });

        output.WriteLine($"{ids} ids; apply ms: {Join(apply)}; with layout and paint: {Join(withPaint)}; longest block in Select all and drawer browsing: {longest.TotalMilliseconds:0} ms");
        Assert.True(ids >= 3_000, $"the library has only {ids} findings");

        // The first round warms the caches; the rest are what a reload costs.
        Assert.True(apply.Skip(1).Max() < 50, $"ApplyData took {apply.Skip(1).Max():0} ms");
        Assert.True(withPaint.Skip(1).Max() < 50, $"ApplyData with layout and paint took {withPaint.Skip(1).Max():0} ms");
        Assert.True(stalls.Count == 0, string.Join(Environment.NewLine, stalls));
    }

    private static void OpenAndWait(MainForm form, IssuesPage page, FileDetailView view, int index)
    {
        page.GetType().GetMethod("OpenDetail", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [index]);
        Settle(() => form.Shell.Drawer.IsOpen && view.Detail is not null, TimeSpan.FromSeconds(10));
        Pump(TimeSpan.FromMilliseconds(300));
    }

    private static string Join(IEnumerable<double> values) => string.Join(", ", values.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)));

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
}
