using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Generates test media with the tooling FFmpeg's built-in sources (subplan 15, task T-02) into a cache
/// keyed by generator version and tooling version, so the cost is paid once per machine. Recipes are
/// the verified ones from subplan 15; nothing is committed to the repository.
/// </summary>
public static class FixtureFactory
{
    /// <summary>Bump when a recipe changes so stale caches are not reused.</summary>
    public const int GeneratorVersion = 1;

    private const string Enc = "-c:v libx264 -crf 20 -preset veryfast -g 48";
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Recipe> Recipes = BuildRecipes();
    private static readonly Lazy<string> RootPath = new(ComputeRoot);

    public static string Root => RootPath.Value;

    public static IReadOnlyCollection<string> Names => Recipes.Keys;

    /// <summary>Returns the fixture's path, generating it (and what it is made from) first if needed.</summary>
    public static string Get(string name)
    {
        lock (Gate)
        {
            return Ensure(name);
        }
    }

    /// <summary>A copy in <paramref name="folder"/> under <paramref name="newName"/>, for tests that damage or rename.</summary>
    public static string CopyTo(string name, string folder, string? newName = null)
    {
        string source = Get(name);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, newName ?? name);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    private static string Ensure(string name)
    {
        if (!Recipes.TryGetValue(name, out Recipe? recipe))
        {
            throw new ArgumentException($"No recipe for fixture '{name}'.", nameof(name));
        }

        string path = Path.Combine(Root, name);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        foreach (string input in recipe.Inputs)
        {
            Ensure(input);
        }

        // Unique per process: several test runs (worktrees) may share the cache and generate at once.
        string tempName = $".tmp-{Environment.ProcessId}-{name}";
        string temp = Path.Combine(Root, tempName);
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        RunTooling(recipe.Arguments(name).Replace("{out}", Quote(tempName), StringComparison.Ordinal));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private static void RunTooling(string arguments)
    {
        string ffmpeg = FfmpegEnvironment.Tooling ?? throw new InvalidOperationException("No tooling FFmpeg on PATH; the test should have been skipped.");
        ProcessStartInfo start = new(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Root,
        };
        foreach (string argument in SplitArguments("-hide_banner -nostdin -v error -y " + arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture generation failed: ffmpeg {arguments}{Environment.NewLine}{error}");
        }
    }

    /// <summary>Splits a recipe's command line; double quotes group, as in the subplan's listings.</summary>
    internal static List<string> SplitArguments(string text)
    {
        List<string> parts = [];
        StringBuilder current = new();
        bool quoted = false;
        bool any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string Quote(string name) => "\"" + name + "\"";

    private static string ComputeRoot()
    {
        string tooling = FfmpegEnvironment.Tooling ?? "none";
        string version = ToolingVersion(tooling);
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..12];
        string root = Path.Combine(Path.GetTempPath(), "MediaVerifier.Fixtures", $"v{GeneratorVersion}-{key}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ToolingVersion(string ffmpeg)
    {
        if (!File.Exists(ffmpeg))
        {
            return "none";
        }

        ProcessStartInfo start = new(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-version");
        using Process process = Process.Start(start)!;
        string first = process.StandardOutput.ReadLine() ?? "unknown";
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return first;
    }

    private static Dictionary<string, Recipe> BuildRecipes()
    {
        Dictionary<string, Recipe> r = new(StringComparer.Ordinal);
        void Add(string name, string args, params string[] inputs) => r[name] = new Recipe(inputs, _ => args);

        // Audio (subplan 15, Fixture generator).
        Add("src16.flac", "-f lavfi -i \"anoisesrc=d=20:c=pink:r=44100:a=0.5:seed=7\" -ac 2 -sample_fmt s16 -f flac {out}");
        Add("m128.mp3", "-i src16.flac -c:a libmp3lame -b:a 128k -f mp3 {out}", "src16.flac");
        Add("m320.mp3", "-i src16.flac -c:a libmp3lame -b:a 320k -f mp3 {out}", "src16.flac");
        Add("mV0.mp3", "-i src16.flac -c:a libmp3lame -q:a 0 -f mp3 {out}", "src16.flac");
        Add("a128.m4a", "-i src16.flac -c:a aac -b:a 128k -f ipod {out}", "src16.flac");
        Add("o128.opus", "-i src16.flac -c:a libopus -b:a 128k -f opus {out}", "src16.flac");
        Add("fake128.flac", "-i m128.mp3 -sample_fmt s16 -f flac {out}", "m128.mp3");
        Add("fake320.flac", "-i m320.mp3 -sample_fmt s16 -f flac {out}", "m320.mp3");
        Add("fakeV0.flac", "-i mV0.mp3 -sample_fmt s16 -f flac {out}", "mV0.mp3");
        Add("fakeAac.flac", "-i a128.m4a -sample_fmt s16 -f flac {out}", "a128.m4a");
        Add("fakeOpus.flac", "-i o128.opus -sample_fmt s16 -f flac {out}", "o128.opus");
        Add("infl320.mp3", "-i m128.mp3 -c:a libmp3lame -b:a 320k -f mp3 {out}", "m128.mp3");
        Add("up96.flac", "-i src16.flac -ar 96000 -sample_fmt s32 -f flac {out}", "src16.flac");
        Add("pad24.flac", "-i src16.flac -sample_fmt s32 -f flac {out}", "src16.flac");
        Add("true96.flac", "-f lavfi -i \"anoisesrc=d=20:c=pink:r=96000:a=0.5:seed=9\" -ac 2 -sample_fmt s32 -f flac {out}");
        Add("clip.flac", "-i src16.flac -af \"volume=14dB\" -sample_fmt s16 -f flac {out}", "src16.flac");
        Add("clipatt.flac", "-i clip.flac -af \"volume=-3dB\" -sample_fmt s16 -f flac {out}", "clip.flac");
        Add("clip320.mp3", "-i clip.flac -c:a libmp3lame -b:a 320k -f mp3 {out}", "clip.flac");
        Add("natural.flac", "-i src16.flac -af \"lowpass=f=9000:poles=1\" -sample_fmt s16 -f flac {out}", "src16.flac");
        Add("clicks.flac", "-i src16.flac -af \"volume=0.25,aeval='val(ch)+0.5*(eq(n,88200)+eq(n,264600)+eq(n,441000)+eq(n,617400)+eq(n,793800))':c=same\" -sample_fmt s16 -f flac {out}", "src16.flac");
        Add("drums.flac", "-f lavfi -i \"aevalsrc='0.8*sin(2*PI*55*t)*exp(-mod(t,0.5)*12)+0.5*(random(0)*2-1)*exp(-mod(t+0.25,0.5)*40)':s=44100:d=20\" -ac 2 -sample_fmt s16 -f flac {out}");
        Add("long5m.flac", "-f lavfi -i \"anoisesrc=d=300:c=pink:r=44100:a=0.3:seed=3\" -ac 2 -sample_fmt s16 -f flac {out}");
        Add("dull.flac", "-i src16.flac -af \"highshelf=f=6000:g=-30,lowpass=f=15000:poles=2\" -sample_fmt s16 -f flac {out}", "src16.flac");

        Add("cover.mp3", "-i m128.mp3 -i img.jpg -map 0:a -map 1:v -c copy -id3v2_version 3 -disposition:v:0 attached_pic -f mp3 {out}", "m128.mp3", "img.jpg");

        // Video.
        Add("native1080.mp4", $"-f lavfi -i \"mandelbrot=size=1920x1080:rate=24\" -t 6 -vf \"noise=c0s=6:c0f=t+u,format=yuv420p\" {Enc} -f mp4 {{out}}");
        Add("up720.mp4", $"-f lavfi -i \"mandelbrot=size=1280x720:rate=24\" -t 6 -vf \"noise=c0s=6:c0f=t+u,scale=1920:1080:flags=lanczos,format=yuv420p\" {Enc} -f mp4 {{out}}");
        Add("up540.mp4", $"-f lavfi -i \"mandelbrot=size=960x540:rate=24\" -t 6 -vf \"noise=c0s=6:c0f=t+u,scale=1920:1080:flags=bicubic,format=yuv420p\" {Enc} -f mp4 {{out}}");
        Add("fs.mp4", "-i native1080.mp4 -c copy -movflags +faststart -f mp4 {out}", "native1080.mp4");
        Add("t.mkv", "-i fs.mp4 -c copy -f matroska {out}", "fs.mp4");
        Add("av.mp4", "-i fs.mp4 -i a128.m4a -map 0:v:0 -map 1:a:0 -c copy -shortest -movflags +faststart -f mp4 {out}", "fs.mp4", "a128.m4a");
        Add("starved.mp4", "-i native1080.mp4 -c:v libx264 -b:v 300k -preset veryfast -f mp4 {out}", "native1080.mp4");

        // Photos.
        Add("img.jpg", "-ss 2 -i native1080.mp4 -frames:v 1 -q:v 3 -f image2 -update 1 {out}", "native1080.mp4");
        Add("img.png", "-ss 2 -i native1080.mp4 -frames:v 1 -c:v png -f image2 -update 1 {out}", "native1080.mp4");
        Add("img.webp", "-ss 2 -i native1080.mp4 -frames:v 1 -c:v libwebp -q:v 80 -f webp {out}", "native1080.mp4");
        return r;
    }

    private sealed record Recipe(string[] Inputs, Func<string, string> Arguments);
}

/// <summary>Deterministic damage for corruption tests (subplan 15, Corruption mutators; task T-03).</summary>
public static class Mutate
{
    /// <summary>Keeps the first <paramref name="fraction"/> of the file.</summary>
    public static void Truncate(string path, double fraction)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        stream.SetLength((long)(stream.Length * fraction));
    }

    /// <summary>XORs one byte at a fraction of the length.</summary>
    public static long FlipByte(string path, double position)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        long offset = Clamp((long)(stream.Length * position), stream.Length);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
        return offset;
    }

    /// <summary>Overwrites a 4 KiB-aligned block with zeros.</summary>
    public static long ZeroBlock(string path, double position, int length = 4096)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        long offset = Clamp((long)(stream.Length * position) / 4096 * 4096, stream.Length);
        stream.Position = offset;
        stream.Write(new byte[(int)Math.Min(length, stream.Length - offset)]);
        return offset;
    }

    /// <summary>Overwrites <paramref name="count"/> bytes with seeded random data.</summary>
    public static long RandomBlock(string path, double position, int count, int seed)
    {
        byte[] data = new byte[count];
        new Random(seed).NextBytes(data);
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        long offset = Clamp((long)(stream.Length * position), stream.Length);
        stream.Position = offset;
        stream.Write(data, 0, (int)Math.Min(count, stream.Length - offset));
        return offset;
    }

    /// <summary>Runs a size-neutral mutation and restores the modified time, to simulate rot.</summary>
    public static void PreserveStat(string path, Action<string> mutation)
    {
        DateTime modified = File.GetLastWriteTimeUtc(path);
        mutation(path);
        File.SetLastWriteTimeUtc(path, modified);
    }

    /// <summary>A position in 0.30–0.70 drawn from <paramref name="seed"/>, so headers are not hit.</summary>
    public static double Position(int seed) => 0.30 + (new Random(seed).NextDouble() * 0.40);

    private static long Clamp(long offset, long length) => Math.Max(0, Math.Min(offset, length - 1));
}
