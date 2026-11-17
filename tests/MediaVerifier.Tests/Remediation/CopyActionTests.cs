using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>Preview extraction, the de-clipped copy and the Recycle Bin entry (subplan 12, tasks R-08, R-09).</summary>
public sealed class CopyActionTests
{
    [Fact]
    public void Embedded_jpeg_streams_are_found_largest_first_and_never_at_offset_zero()
    {
        byte[] small = Jpeg(5000);
        byte[] large = Jpeg(9000);
        byte[] data = [.. Jpeg(6000), .. new byte[100], .. small, .. new byte[50], .. large, 1, 2, 3];
        List<(long Offset, int Length)> found = ExtractPreviewAction.Candidates(data);
        Assert.Equal(2, found.Count);
        Assert.Equal(large.Length, found[0].Length);
        Assert.Equal(6000 + 100 + small.Length + 50, found[0].Offset);
        Assert.Equal(small.Length, found[1].Length);
        Assert.Empty(ExtractPreviewAction.Candidates(Jpeg(100)));
    }

    [Fact]
    public void The_catalogue_offers_the_copy_actions_where_they_apply()
    {
        RootRecord library = new() { Id = 1, Path = @"D:\Media", Role = RootRole.Library };
        RootRecord share = new() { Id = 2, Path = @"\\nas\media", Role = RootRole.Library };
        FileRecord photo = new() { Id = 1, RootId = 1, Name = "a.jpg", Ext = "jpg", MediaKind = MediaKind.Photo };
        FileRecord flac = new() { Id = 2, RootId = 1, Name = "a.flac", Ext = "flac", MediaKind = MediaKind.Audio };
        FileRecord mp3 = flac with { Name = "a.mp3", Ext = "mp3" };
        Assert.Contains(ActionIds.ExtractPreview, ActionCatalog.ForFinding("COR-PARTIAL-IMAGE", photo, library, false, false));
        Assert.Contains(ActionIds.DeclipCopy, ActionCatalog.ForFinding("AUD-CLIPPING", flac, library, false, true));
        Assert.DoesNotContain(ActionIds.DeclipCopy, ActionCatalog.ForFinding("AUD-CLIPPING", mp3, library, false, true));
        Assert.Null(ActionCatalog.Recommended(ActionCatalog.ForFinding("AUD-CLIPPING", flac, library, false, true)));
        Assert.IsType<RecycleAction>(ActionCatalog.Create(ActionIds.Recycle, WindowsFileSystem.Instance));
        Assert.Equal(CoreStrings.Remediation.NoRecycleBin, new OperationRunner(null!, null!, WindowsFileSystem.Instance, null!).CheckPreconditions(mp3 with { RootId = 2 }, share, new RecycleAction(), 0));
    }

    [FixtureFact]
    public async Task A_truncated_camera_jpeg_gives_up_its_embedded_preview()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        byte[] main = await File.ReadAllBytesAsync(FixtureFactory.Get("img.jpg"));
        string thumbPath = Path.Combine(harness.Library, "thumb.tmp");
        FfResult made = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["-f", "jpeg_pipe", "-i", FixtureFactory.Get("img.jpg"), "-vf", "scale=320:180", "-q:v", "4", "-f", "image2", "-update", "1", thumbPath] });
        Assert.True(made.Completed);
        byte[] thumb = await File.ReadAllBytesAsync(thumbPath);
        File.Delete(thumbPath);
        Assert.True(thumb.Length < 65000);

        // SOI, an APP1 segment holding the preview (as cameras store it), then half of the main image.
        int segment = thumb.Length + 2;
        byte[] camera = [0xFF, 0xD8, 0xFF, 0xE1, (byte)(segment >> 8), (byte)segment, .. thumb, .. main.AsSpan(2, (main.Length / 2) - 2)];
        string path = Path.Combine(harness.Library, "DSC_0412.jpg");
        await File.WriteAllBytesAsync(path, camera);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Quick);
        FileRecord file = harness.File1("DSC_0412.jpg");

        OperationOutcome done = await new OperationRunner(harness.Database, runner, WindowsFileSystem.Instance, harness.Database.Clock).RunAsync(file.Id, new ExtractPreviewAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.Equal(Path.Combine(harness.Library, "DSC_0412.preview.jpg"), done.ResultPath);
        Assert.Equal(thumb, await File.ReadAllBytesAsync(done.ResultPath!));
        Assert.Equal(camera, await File.ReadAllBytesAsync(path));
    }

    [FixtureFact]
    public async Task The_declipped_copy_has_no_sample_above_full_scale_and_leaves_the_original()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string original = FixtureFactory.CopyTo("clip.flac", harness.Library);
        byte[] before = await File.ReadAllBytesAsync(original);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Quick);

        OperationOutcome done = await new OperationRunner(harness.Database, runner, WindowsFileSystem.Instance, harness.Database.Clock).RunAsync(harness.File1("clip.flac").Id, new DeclipCopyAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.Equal(Path.Combine(harness.Library, "clip.declipped.flac"), done.ResultPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(original));
        Assert.Empty(Directory.GetFiles(harness.Library, "*.mvtmp-*"));
    }

    private static byte[] Jpeg(int length)
    {
        byte[] data = new byte[length];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[^2] = 0xFF;
        data[^1] = 0xD9;
        return data;
    }
}
