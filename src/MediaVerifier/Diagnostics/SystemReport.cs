using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// Settings → Advanced → Copy system report: what a bug report needs, one fact a line, in English like the logs. It
/// names no folder or file of the user's: watched folders are counted, and a location under the profile shows as
/// <c>%LOCALAPPDATA%</c>. Reads the database, so it is built off the UI thread. (It copied only the version lines.)
/// </summary>
internal static class SystemReport
{
    public static string Build(UiServices services, AppState state)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        void Line(string label, string value) => text.Append(label).Append(": ").AppendLine(value);

        Line("App", AppInfo.WindowTitle + (services.IsDemo ? " (demo data)" : string.Empty));
        Line("Windows", Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit"));
        Line(".NET", RuntimeInformation.FrameworkDescription + ", " + RuntimeInformation.ProcessArchitecture);
        Line("Processors", Environment.ProcessorCount.ToString(c));
        MediaEngineInfo engine = services.MediaEngine;
        Line("Media engine", string.Create(c, $"FFmpeg {engine.Version} ({engine.Build}), {engine.Check.State}{(engine.Location.IsOverride ? ", override" : string.Empty)}"));

        if (!services.IsDemo)
        {
            List<RootRecord> roots = services.Database.Roots.List();
            Line("Watched folders", string.Create(c, $"{roots.Count(r => r.Role == RootRole.Library)} library, {roots.Count(r => r.Role == RootRole.Backup)} backup, {roots.Count(r => !r.Enabled)} switched off"));
            HealthSnapshot health = services.Database.Queries.Health(TimeSpan.FromDays(state.Settings.Scanning.ScrubPeriodDays), services.Clock.UtcNow);
            Line("Files", string.Create(c, $"{health.FilesIndexed} indexed, {health.FilesScanned} scanned"));
            SeverityCounts open = health.Open;
            Line("Open problems", string.Create(c, $"{open.Critical} Critical, {open.High} High, {open.Medium} Medium, {open.Low} Low"));
            RunRecord? last = services.Database.Runs.List(1).FirstOrDefault();
            Line("Last scan", last is null ? "none" : string.Create(c, $"{new DateTimeOffset(last.StartedUtc, TimeSpan.Zero):yyyy-MM-dd HH:mm} UTC, {last.Status}, {last.Depth}, {last.FilesSeen} files, {last.Errors} errors"));
            Line("Database", string.Create(c, $"{Shorten(services.Database.FilePath)}, {services.Database.Maintenance.SizeBytes() / (1024.0 * 1024.0):0.0} MB, SQLite {services.Database.SqliteVersion}"));
        }

        Core.Settings.ScanSettings scan = state.Settings.Scanning;
        Line("Scanning", string.Create(c, $"default depth {scan.DefaultDepth}, speed {scan.Speed}, re-read every {scan.ScrubPeriodDays} days"));
        Line("Window", string.Create(c, $"theme {state.Settings.Theme}, start with Windows {OnOff(state.Settings.StartWithWindows)}, keep running {OnOff(state.Settings.MinimizeToTray)}, update check {OnOff(state.Settings.CheckForUpdatesOnStartup)}"));
        Line("Logs", Shorten(state.Paths.LogsDirectory));
        return text.ToString();
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>A path under the local application data folder with that part replaced (no user name in a report).</summary>
    internal static string Shorten(string path)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 && path.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + path[local.Length..] : "(custom location)";
    }
}
