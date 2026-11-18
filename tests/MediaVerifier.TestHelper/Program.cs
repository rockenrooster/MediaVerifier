using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

// Usage:
//   sleep <ms>                  sleep, writing nothing
//   lines <count>               write <count> FFmpeg-style error lines to stderr
//   binary <bytes>              write <bytes> bytes to stdout
//   progress <blocks>           write -progress style blocks to stdout
//   exit <code>                 exit with <code>
//   spawn <pidFile> <ms>        start a child that sleeps <ms>, write its PID to <pidFile>, then sleep
//   scan <base> <library> <ffmpegDir> <mutex>
//                               kill tests (task T-08): a Standard scan of <library> with the real engine, its
//                               database and data under <base>; prints "started" once the run exists
//   -<anything> ...             called like ffmpeg or ffprobe: stall until killed (a hung decoder)
// A copy named crash-*.exe exits at once with an access violation's code, like an FFmpeg that crashes on a file.
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty).StartsWith("crash", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Segmentation fault");
    return unchecked((int)0xC0000005);
}

string mode = args.Length > 0 ? args[0] : "";
long Arg(int i) => long.Parse(args[i], CultureInfo.InvariantCulture);
switch (mode)
{
    case "sleep":
        Thread.Sleep((int)Arg(1));
        return 0;
    case "lines":
        {
            using StreamWriter error = new(Console.OpenStandardError(), new UTF8Encoding(false), 1 << 16);
            long count = Arg(1);
            for (long i = 0; i < count; i++)
            {
                error.Write("[h264 @ 000001d5ee3a8380] [error] Invalid NAL unit size (");
                error.Write(i % 9973);
                error.Write(" > 1024).\n");
            }

            return 0;
        }

    case "binary":
        {
            using Stream output = Console.OpenStandardOutput();
            byte[] block = new byte[64 * 1024];
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = (byte)i;
            }

            long remaining = Arg(1);
            while (remaining > 0)
            {
                int n = (int)Math.Min(block.Length, remaining);
                output.Write(block, 0, n);
                remaining -= n;
            }

            return 0;
        }

    case "progress":
        {
            using StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false));
            long blocks = Arg(1);
            for (long b = 1; b <= blocks; b++)
            {
                output.Write($"frame={b * 24}\nout_time_us={b * 1_000_000}\ndup_frames=0\ndrop_frames=0\nspeed=48.2x\nprogress={(b == blocks ? "end" : "continue")}\n");
                output.Flush();
                Thread.Sleep(20);
            }

            return 0;
        }

    case "exit":
        return (int)Arg(1);
    case "spawn":
        {
            string self = Environment.ProcessPath!;
            ProcessStartInfo start = new(self) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("sleep");
            start.ArgumentList.Add(args[2]);
            using Process child = Process.Start(start)!;
            // Written under another name and renamed, so a test waiting for the file never opens it half written.
            File.WriteAllText(args[1] + ".tmp", child.Id.ToString(CultureInfo.InvariantCulture));
            File.Move(args[1] + ".tmp", args[1]);
            Thread.Sleep((int)Arg(2));
            return 0;
        }

    case "scan":
        {
            AppPaths paths = AppPaths.UnderBase(args[1]);
            paths.EnsureCreated();
            using Database database = Database.Open(paths.DefaultDatabaseFile);
            string library = Path.GetFullPath(args[2]);
            if (!database.Roots.List().Any(r => string.Equals(r.Path, library, StringComparison.OrdinalIgnoreCase)))
            {
                _ = database.Roots.AddAsync(new RootRecord { Path = library, Label = "Library" }).GetAwaiter().GetResult();
            }

            using FfRunner runner = new(new FfmpegLocation(args[3], IsOverride: true));
            ScanSettings settings = new();
            RuleSettings rules = database.RuleSettings.Load();
            using ScanEngine engine = new(EngineFactory.CreateServices(database, runner, paths, () => settings, () => rules), args[4]);
            ScanRequest request = new([.. database.Roots.List().Select(r => new ScanScope(r.Id))], ScanDepth.Standard, RunTrigger.Manual);
            Task<(int ExitCode, RunSummary? Summary)> run = HeadlessScan.RunAsync(engine, request);
            Console.Out.WriteLine("started");
            Console.Out.Flush();
            (int exit, RunSummary? summary) = run.GetAwaiter().GetResult();
            Console.Out.WriteLine(summary?.Status.ToString() ?? "none");
            return exit;
        }

    case not null when mode.StartsWith('-'):
        Thread.Sleep(Timeout.Infinite);
        return 0;
    default:
        Console.Error.WriteLine("unknown mode");
        return 2;
}
