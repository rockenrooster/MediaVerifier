using System.Text.Json;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

public sealed class BundleTests
{
    [Fact]
    public void LocatorPrefersTheOverrideAndWarns()
    {
        List<string> warnings = [];
        FfmpegLocation located = FfmpegLocator.Locate(@"C:\App\ffmpeg", @"D:\dev ffmpeg\bin", warnings.Add);
        Assert.True(located.IsOverride);
        Assert.Equal(@"D:\dev ffmpeg\bin\ffmpeg.exe", located.FfmpegExe);
        Assert.Single(warnings);
        Assert.Contains("MEDIAVERIFIER_FFMPEG_DIR", warnings[0], StringComparison.Ordinal);

        FfmpegLocation bundle = FfmpegLocator.Locate(@"C:\App\ffmpeg", "  ", warnings.Add);
        Assert.False(bundle.IsOverride);
        Assert.Equal(@"C:\App\ffmpeg\ffprobe.exe", bundle.FfprobeExe);
        Assert.Single(warnings);
    }

    [Fact]
    public void IntactBundleIsOk()
    {
        using TempDirectory temp = new("bundle");
        FfmpegLocation location = FakeBundle(temp.Path);
        BundleCheck check = BundleIntegrity.Check(location);
        Assert.Equal(BundleState.Ok, check.State);
        Assert.Equal("9.0.2", check.Info!.FfmpegVersion);
    }

    [Fact]
    public void AChangedDllGivesChangedAndScansContinue()
    {
        using TempDirectory temp = new("bundle");
        FfmpegLocation location = FakeBundle(temp.Path);
        File.AppendAllText(Path.Combine(temp.Path, "avcodec-62.dll"), "patched");

        BundleCheck check = BundleIntegrity.Check(location);

        Assert.Equal(BundleState.Changed, check.State);
        Assert.Equal(["avcodec-62.dll"], check.ChangedFiles);
        Assert.True(check.ScanningAllowed);
    }

    [Fact]
    public void ADeletedDllGivesMissingAndScansAreRefused()
    {
        using TempDirectory temp = new("bundle");
        FfmpegLocation location = FakeBundle(temp.Path);
        File.Delete(Path.Combine(temp.Path, "avformat-62.dll"));

        BundleCheck check = BundleIntegrity.Check(location);

        Assert.Equal(BundleState.Missing, check.State);
        Assert.Equal(["avformat-62.dll"], check.MissingFiles);
        Assert.False(check.ScanningAllowed);
    }

    [Fact]
    public void NoManifestMeansMissing()
    {
        using TempDirectory temp = new("bundle");
        FfmpegLocation location = FakeBundle(temp.Path);
        File.Delete(location.BundleManifest);
        Assert.Equal(BundleState.Missing, BundleIntegrity.Check(location).State);
        Assert.Equal(BundleState.Missing, BundleIntegrity.Check(new FfmpegLocation(temp.Combine("nowhere"), IsOverride: false)).State);
    }

    [Fact]
    public void TheOverrideIsNotIntegrityChecked()
    {
        using TempDirectory temp = new("bundle");
        FakeBundle(temp.Path);
        BundleCheck check = BundleIntegrity.Check(new FfmpegLocation(temp.Path, IsOverride: true));
        Assert.Equal(BundleState.Override, check.State);
        Assert.True(check.ScanningAllowed);
    }

    [FfmpegFact]
    public async Task EngineCheckRejectsABuildThatIsNotTheBundle()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        string? problem = await BundleIntegrity.VerifyEngineAsync(runner, "9.0.2");
        if (FfmpegEnvironment.Engine!.IsOverride)
        {
            // A developer build (8.0 or newer, usually a full build with network protocols) must fail.
            Assert.NotNull(problem);
        }
        else
        {
            Assert.Null(problem);
        }
    }

    [FixtureFact]
    public async Task PhotoNamedLikeASequencePatternProbesAndDecodes()
    {
        using TempDirectory temp = new("percent");
        string path = FixtureFactory.CopyTo("img.jpg", temp.Path, "a%03d.jpg");
        using FfRunner runner = FfmpegEnvironment.CreateRunner();

        // The probe exactly as ProbeArtifactProvider runs it.
        List<string> probeArgs = ["-show_format", "-show_streams", "-show_error", "-of", "json", .. FfmpegArgs.Input(path, MediaKind.Photo, "jpeg")];
        FfResult probed = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffprobe, Arguments = probeArgs, Stdout = StdoutMode.Text });
        ProbeResult probe = ProbeParser.Parse(probed.StdoutText, probed.Log);
        Assert.True(probe.Opened);
        Assert.Equal(1920, probe.Streams[0].Width);

        List<string> args = [.. FfmpegArgs.Decode()];
        args.AddRange(FfmpegArgs.Input(path, MediaKind.Photo, "jpeg"));
        args.AddRange(["-f", "null", "-"]);
        FfResult decode = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = args });
        Assert.Equal(ProcessOutcome.Completed, decode.Outcome);
        Assert.Equal(0, decode.Log.Count(LogSignalKind.OpenFailure));
        Assert.Equal(0, decode.Log.Count(LogSignalKind.DecodeError));
    }

    private static FfmpegLocation FakeBundle(string folder)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe", "avcodec-62.dll", "avformat-62.dll", "avutil-60.dll", "LICENSE.txt" })
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, "content of " + name);
            files[name] = BundleIntegrity.Sha256Hex(path);
        }

        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            ffmpegVersion = "9.0.2",
            extraVersion = "mediaverifier-1",
            licence = "LGPL-2.1-or-later",
            files,
        });
        File.WriteAllText(Path.Combine(folder, "bundle.json"), json);
        return new FfmpegLocation(folder, IsOverride: false);
    }
}
