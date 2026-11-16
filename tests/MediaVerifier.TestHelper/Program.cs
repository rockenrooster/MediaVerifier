using System.Diagnostics;
using System.Globalization;
using System.Text;

// Usage:
//   sleep <ms>                  sleep, writing nothing
//   lines <count>               write <count> FFmpeg-style error lines to stderr
//   binary <bytes>              write <bytes> bytes to stdout
//   progress <blocks>           write -progress style blocks to stdout
//   exit <code>                 exit with <code>
//   spawn <pidFile> <ms>        start a child that sleeps <ms>, write its PID to <pidFile>, then sleep
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
            File.WriteAllText(args[1], child.Id.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep((int)Arg(2));
            return 0;
        }

    default:
        Console.Error.WriteLine("unknown mode");
        return 2;
}
