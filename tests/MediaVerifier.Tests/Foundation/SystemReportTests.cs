using MediaVerifier.App;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

/// <summary>Copy system report gives the facts a bug report needs and nothing that names the user or their folders.</summary>
public sealed class SystemReportTests
{
    [Fact]
    public async Task The_report_has_the_facts_and_no_user_paths()
    {
        using TempDirectory temp = new("report");
        string library = Directory.CreateDirectory(temp.Combine("Private Music")).FullName;
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        state.Settings.CheckForUpdatesOnStartup = false;
        using UiServices services = UiServices.CreateLive(state, null, startupMaintenance: false, engineMutexName: "Local\\MediaVerifier.Tests.Report." + Guid.NewGuid().ToString("N"));
        await services.Database.Roots.AddAsync(new RootRecord { Path = library, Label = "Private Music", Role = RootRole.Library });

        string report = await Task.Run(() => SystemReport.Build(services, state));

        foreach (string label in new[] { "App:", "Windows:", ".NET:", "Media engine:", "Watched folders: 1 library", "Files:", "Open problems:", "Last scan: none", "Database:", "Scanning:", "Window:", "Logs:" })
        {
            Assert.Contains(label, report, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Private Music", report, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, report, StringComparison.OrdinalIgnoreCase);
    }
}
