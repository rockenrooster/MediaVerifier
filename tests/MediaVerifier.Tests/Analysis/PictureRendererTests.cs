using System.Diagnostics;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Analysis;

/// <summary>Drawer thumbnails, frame strips and compare images (subplan 09 header; subplan 11, task V-05).</summary>
[Trait("Category", "Integration")]
public sealed class PictureRendererTests
{
    [FixtureFact]
    public async Task Thumbnails_come_from_the_photo_a_video_frame_or_cover_art()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new();
        PictureRenderer pictures = new(runner, temp.Combine("cache"));
        Assert.Equal((128, 128), Size(await pictures.ThumbnailAsync(FixtureFactory.Get("img.jpg"), "1-a", MediaKind.Photo, 0, CancellationToken.None)));
        Assert.Equal((128, 128), Size(await pictures.ThumbnailAsync(FixtureFactory.Get("native1080.mp4"), "2-a", MediaKind.Video, 6, CancellationToken.None)));
        Assert.Equal((128, 128), Size(await pictures.ThumbnailAsync(FixtureFactory.Get("cover.mp3"), "3-a", MediaKind.Audio, 20, CancellationToken.None)));
        Assert.Null(await pictures.ThumbnailAsync(FixtureFactory.Get("src16.flac"), "4-a", MediaKind.Audio, 20, CancellationToken.None));
    }

    [FixtureFact]
    public async Task The_strip_tiles_the_frames_and_the_compare_image_is_quick_and_cached()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new();
        PictureRenderer pictures = new(runner, temp.Combine("cache"));
        string video = FixtureFactory.Get("up720.mp4");
        Assert.Equal((3 * PictureRenderer.StripWidth, PictureRenderer.StripHeight), Size(await pictures.FrameStripAsync(video, "5-a", [0.3, 2.5, 4.6], CancellationToken.None)));

        byte[]? compare = await pictures.CompareAsync(video, "5-a", MediaKind.Video, 2.5, 1920, 1080, 720, CancellationToken.None);
        Assert.Equal((2 * PictureRenderer.CompareWidth, PictureRenderer.CompareHeight), Size(compare));
        Assert.True(pictures.LastCompareTime < TimeSpan.FromSeconds(2), $"{pictures.LastCompareTime.TotalMilliseconds:0} ms");

        Stopwatch watch = Stopwatch.StartNew();
        Assert.Equal(compare, await pictures.CompareAsync(video, "5-a", MediaKind.Video, 2.5, 1920, 1080, 720, CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200), "the second request should come from the cache");
    }

    private static (int Width, int Height)? Size(byte[]? png)
    {
        if (png is null)
        {
            return null;
        }

        using MemoryStream stream = new(png);
        using Bitmap image = new(stream);
        return (image.Width, image.Height);
    }
}
