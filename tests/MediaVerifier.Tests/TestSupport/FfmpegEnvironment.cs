using System.Diagnostics;
using MediaVerifier.Core.Ffmpeg;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// The two FFmpeg builds tests may use (subplan 15), never mixed up: the engine (the bundle fetched into
/// deps/ffmpeg/bin, or MEDIAVERIFIER_FFMPEG_DIR before the first bundle exists) runs everything the app
/// runs; the tooling build (a full build on PATH) only generates fixtures.
/// </summary>
public static class FfmpegEnvironment
{
    private static readonly Lazy<FfmpegLocation?> EngineLocation = new(FindEngine);
    private static readonly Lazy<string?> ToolingPath = new(FindTooling);

    public static FfmpegLocation? Engine => EngineLocation.Value;

    /// <summary>Full path of the tooling ffmpeg.exe on PATH, or null.</summary>
    public static string? Tooling => ToolingPath.Value;

    public static FfRunner CreateRunner() =>
        new(Engine ?? throw new InvalidOperationException("No FFmpeg engine; the test should have been skipped."));

    private static FfmpegLocation? FindEngine()
    {
        string bundle = RepositoryPaths.Combine("deps", "ffmpeg", "bin");
        FfmpegLocation fetched = new(bundle, IsOverride: false);
        if (fetched.ExecutablesExist)
        {
            return fetched;
        }

        string? overrideDir = Environment.GetEnvironmentVariable(FfmpegLocator.OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            FfmpegLocation location = new(Path.GetFullPath(overrideDir), IsOverride: true);
            if (location.ExecutablesExist)
            {
                return location;
            }
        }

        return null;
    }

    private static string? FindTooling()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate) && HasEncoders(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The tooling build must be able to encode (the bundle cannot).</summary>
    private static bool HasEncoders(string ffmpeg)
    {
        try
        {
            ProcessStartInfo start = new(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-encoders");
            using Process process = Process.Start(start)!;
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return text.Contains("libx264", StringComparison.Ordinal) && text.Contains("flac", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>An integration test that needs the FFmpeg engine; skipped, never failed, without one.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegEnvironment.Engine is null)
        {
            Skip = "No FFmpeg engine: run scripts/Get-FfmpegBundle.ps1 or set MEDIAVERIFIER_FFMPEG_DIR.";
        }
    }
}

/// <summary>An integration test that needs generated media: the engine and the tooling FFmpeg.</summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute()
    {
        if (FfmpegEnvironment.Tooling is null)
        {
            Skip = "No tooling FFmpeg (a full build with encoders) on PATH to generate fixtures.";
        }
        else if (FfmpegEnvironment.Engine is null)
        {
            Skip = "No FFmpeg engine: run scripts/Get-FfmpegBundle.ps1 or set MEDIAVERIFIER_FFMPEG_DIR.";
        }
    }
}

public sealed class FixtureTheoryAttribute : TheoryAttribute
{
    public FixtureTheoryAttribute()
    {
        if (FfmpegEnvironment.Tooling is null || FfmpegEnvironment.Engine is null)
        {
            Skip = "Needs the tooling FFmpeg on PATH and the FFmpeg engine.";
        }
    }
}
