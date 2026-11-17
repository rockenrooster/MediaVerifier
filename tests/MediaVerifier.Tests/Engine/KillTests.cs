using System.Diagnostics;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Tests.Engine;

/// <summary>
/// Task T-08 (subplan 15, Robustness tests): a helper process scans a temporary library with the real engine and is
/// killed at 20 random moments. After every kill the database opens cleanly and its invariants hold; a final scan
/// then completes and covers every file.
/// </summary>
[Trait("Category", "System")]
public sealed class KillTests
{
    private const int Kills = 20;
    private static readonly string Helper = Path.Combine(AppContext.BaseDirectory, "MediaVerifier.TestHelper.exe");
    private static readonly string[] Sources = ["src16.flac", "m128.mp3", "a128.m4a", "img.jpg", "img.png", "av.mp4"];

    [FixtureFact]
    public void Twenty_kills_leave_a_consistent_library_that_a_new_scan_completes()
    {
        using TempDirectory temp = new("kill");
        string library = Path.Combine(temp.Path, "Library");
        int files = 0;
        for (int copy = 0; copy < 8; copy++)
        {
            string folder = Directory.CreateDirectory(Path.Combine(library, "Set " + copy)).FullName;
            foreach (string source in Sources)
            {
                FixtureFactory.CopyTo(source, folder);
                files++;
            }
        }

        string data = Path.Combine(temp.Path, "Data");
        string mutex = @"Local\MediaVerifier.Tests.Kill." + Guid.NewGuid().ToString("N");
        Random random = new(15_08);
        int interrupted = 0;
        for (int kill = 0; kill < Kills; kill++)
        {
            // New modified times make every file due again, so each run has work to be killed in.
            DateTime touched = DateTime.UtcNow.AddMinutes(-kill);
            foreach (string file in Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, touched);
            }

            using Process scan = Start(data, library, mutex);
            Assert.True(WaitForLine(scan, "started", TimeSpan.FromSeconds(30)), $"kill {kill}: the helper did not start");
            Thread.Sleep(random.Next(50, 2500));
            if (!scan.HasExited)
            {
                scan.Kill(entireProcessTree: true);
                interrupted++;
            }

            Assert.True(scan.WaitForExit(TimeSpan.FromSeconds(30)));
            CheckInvariants(data, kill);
        }

        Assert.True(interrupted >= Kills / 2, $"only {interrupted} of {Kills} runs were still going when killed");

        // Resume: a new scan finishes and leaves every file analysed at Standard depth.
        using (Process final = Start(data, library, mutex))
        {
            Assert.True(final.WaitForExit(TimeSpan.FromMinutes(5)), "the final scan did not finish");
            Assert.Equal(0, final.ExitCode);
        }

        CheckInvariants(data, Kills);
        using Database database = Database.Open(Core.AppPaths.UnderBase(data).DefaultDatabaseFile);
        Assert.Equal(files, Scalar(database, "SELECT COUNT(*) FROM files WHERE scanned_depth >= " + (int)ScanDepth.Standard));
        Assert.Equal((long)RunStatus.Completed, Scalar(database, "SELECT status FROM runs ORDER BY id DESC LIMIT 1"));
        Assert.True(Scalar(database, "SELECT COUNT(*) FROM runs WHERE status = " + (int)RunStatus.Interrupted) >= 1);
    }

    private static Process Start(string data, string library, string mutex)
    {
        ProcessStartInfo start = new(Helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "scan", data, library, FfmpegEnvironment.Engine!.Directory, mutex })
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static bool WaitForLine(Process process, string expected, TimeSpan timeout)
    {
        Task<string?> line = process.StandardOutput.ReadLineAsync();
        return line.Wait(timeout) && line.Result == expected;
    }

    /// <summary>
    /// Opening runs crash recovery; then: the file is intact, nothing is left Running, no finding or artifact lost its
    /// file, every file's open-finding count and worst tier match its findings, and run counters are sane.
    /// </summary>
    private static void CheckInvariants(string data, int kill)
    {
        using Database database = Database.Open(Core.AppPaths.UnderBase(data).DefaultDatabaseFile);
        string context = $"after kill {kill}";
        Assert.Equal("ok", database.Read(c => Query(c, "PRAGMA integrity_check")));
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM runs WHERE status = " + (int)RunStatus.Running));
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM findings WHERE file_id NOT IN (SELECT id FROM files)"));
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM artifacts WHERE file_id NOT IN (SELECT id FROM files)"));
        long mismatched = Scalar(database, """
            SELECT COUNT(*) FROM files f
            WHERE f.open_findings <> (SELECT COUNT(*) FROM findings g WHERE g.file_id = f.id AND g.status = 0)
               OR f.worst_severity <> COALESCE((SELECT MAX(g.severity) FROM findings g WHERE g.file_id = f.id AND g.status = 0), 0)
            """);
        Assert.True(mismatched == 0, $"{context}: {mismatched} files have summaries that disagree with their findings");
        Assert.Equal(0, Scalar(database, "SELECT COUNT(*) FROM runs WHERE files_analysed > files_seen OR files_seen < 0 OR new_findings < 0 OR errors < 0"));
    }

    private static long Scalar(Database database, string sql) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    private static string Query(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
