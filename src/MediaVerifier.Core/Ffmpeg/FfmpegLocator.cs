using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>The FFmpeg version the app ships (subplan 17).</summary>
public static class FfmpegVersions
{
    public const string Bundled = "9.0.2";
}

/// <summary>Where ffmpeg.exe and ffprobe.exe live, and whether that is the developer override.</summary>
public sealed record FfmpegLocation(string Directory, bool IsOverride)
{
    public string FfmpegExe => Path.Combine(Directory, "ffmpeg.exe");

    public string FfprobeExe => Path.Combine(Directory, "ffprobe.exe");

    public string BundleManifest => Path.Combine(Directory, "bundle.json");

    public bool ExecutablesExist => File.Exists(FfmpegExe) && File.Exists(FfprobeExe);
}

/// <summary>
/// Resolves exactly one FFmpeg folder (subplan 04): the developer override
/// <c>MEDIAVERIFIER_FFMPEG_DIR</c> when set (logged as a warning, never offered in the UI), otherwise
/// the bundle in <c>&lt;folder of MediaVerifier.exe&gt;\ffmpeg\</c>. No other copy is ever used.
/// </summary>
public static class FfmpegLocator
{
    public const string OverrideVariable = "MEDIAVERIFIER_FFMPEG_DIR";

    public static FfmpegLocation Locate(AppPaths paths) => Locate(paths.BundledFfmpegDirectory, Environment.GetEnvironmentVariable(OverrideVariable));

    public static FfmpegLocation Locate(string bundleDirectory, string? overrideDirectory, Action<string>? warn = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            string full = Path.GetFullPath(overrideDirectory.Trim().Trim('"'));
            string message = $"{OverrideVariable} is set: using the FFmpeg in {full} instead of the bundle (development only)";
            if (warn is not null)
            {
                warn(message);
            }
            else
            {
                AppLog.Shared?.Warning(message);
            }

            return new FfmpegLocation(full, IsOverride: true);
        }

        return new FfmpegLocation(Path.GetFullPath(bundleDirectory), IsOverride: false);
    }
}
