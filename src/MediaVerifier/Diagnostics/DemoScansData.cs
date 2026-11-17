using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Diagnostics;

/// <summary>The scan history of the mockup (docs/design/scans-dark.svg).</summary>
internal sealed partial class DemoUiData
{
    private static readonly RunInfo[] History =
    [
        Run(1, At(16, 3, 12), TimeSpan.FromMinutes(108), RunTrigger.Scheduled, ScanDepth.Standard, "All folders", 12_507, 2, 1, RunStatus.Completed),
        Run(2, At(15, 3, 0), TimeSpan.FromMinutes(120), RunTrigger.Scheduled, ScanDepth.Quick, "All folders", 9_214, 0, 0, RunStatus.BudgetReached),
        Run(3, At(10, 21, 40), TimeSpan.FromMinutes(37), RunTrigger.Manual, ScanDepth.Deep, "Videos", 212, 5, 0, RunStatus.Cancelled),
        Run(4, At(9, 3, 0), TimeSpan.FromMinutes(112), RunTrigger.Scheduled, ScanDepth.Standard, "All folders", 12_498, 1, 3, RunStatus.Completed),
        Run(5, At(8, 3, 0), null, RunTrigger.Scheduled, ScanDepth.Standard, "All folders", 0, 0, 0, RunStatus.Failed),
        Run(6, At(1, 3, 0), TimeSpan.FromMinutes(104), RunTrigger.Scheduled, ScanDepth.Standard, "All folders", 12_455, 0, 2, RunStatus.Completed),
    ];

    public ScansData LoadScans() => new(History, null, At(16, 5, 0), true, null);

    public RunDetail? LoadRunDetail(long runId) =>
        History.FirstOrDefault(r => r.Id == runId) is RunInfo run ? new RunDetail(run, (long)(1.4 * 1024 * 1024 * 1024 * 1024), run.Files, run.Files, [], 0) : null;

    public ScanEstimate LoadScanEstimate(IReadOnlyList<long> rootIds) =>
        new(58_210, (long)(3.5 * 1024 * 1024 * 1024 * 1024), "Music, Photos and Videos", TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(200), TimeSpan.FromHours(19));

    private static RunInfo Run(long id, DateTimeOffset started, TimeSpan? duration, RunTrigger trigger, ScanDepth depth, string folders, long files, long found, long resolved, RunStatus status) =>
        new(id, started, duration is TimeSpan d ? started + d : null, trigger, trigger == RunTrigger.Scheduled ? "Nightly verification" : null, depth, folders, files, found, resolved, status);
}
