using System.Text.Json;
using System.Text.Json.Nodes;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

/// <summary>Golden-file tests over captured ffprobe JSON (task FF-05).</summary>
public sealed class ProbeTests
{
    private static readonly string Folder = RepositoryPaths.Combine("tests", "MediaVerifier.Tests", "Data", "probe");

    private static ProbeResult Load(string name, params string[] logLines) =>
        ProbeParser.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")), LogSummary.FromLines(logLines));

    [Fact]
    public void HealthyFlac()
    {
        ProbeResult probe = Load("src16.flac");
        Assert.True(probe.Opened);
        Assert.False(probe.Unrecognised);
        Assert.Equal("flac", probe.FormatName);
        ProbeStream audio = Assert.Single(probe.Streams);
        Assert.Equal("flac", audio.CodecName);
        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.InRange(probe.DurationSeconds!.Value, 19.9, 20.1);
        MediaSummary summary = probe.ToSummary();
        Assert.Equal(16, summary.BitDepth);
        Assert.Equal("flac", summary.AudioCodec);
        Assert.Null(summary.VideoCodec);
    }

    [Fact]
    public void PaddedFlacReportsItsRawBits()
    {
        ProbeResult probe = Load("pad24.flac");
        Assert.Equal("s32", probe.Streams[0].SampleFmt);
        Assert.Equal(24, probe.Streams[0].BitsPerRawSample);
        Assert.Equal(24, probe.ToSummary().BitDepth);
    }

    [Fact]
    public void VideoWithAudio()
    {
        ProbeResult probe = Load("av.mp4");
        Assert.True(probe.Opened);
        Assert.Equal("h264", probe.Video!.CodecName);
        Assert.Equal(1920, probe.Video.Width);
        Assert.Equal(1080, probe.Video.Height);
        Assert.Equal(24.0, probe.Video.AvgFrameRate!.Value.Value, 3);
        Assert.Equal("aac", probe.Audio!.CodecName);
        Assert.StartsWith("mov,mp4", probe.FormatName, StringComparison.Ordinal);
        Assert.Equal(24000, probe.ToSummary().FpsMilli);
    }

    [Fact]
    public void PhotosHaveOneVideoStream()
    {
        ProbeResult png = Load("img.png");
        Assert.Equal("png_pipe", png.FormatName);
        Assert.Equal("png", png.Streams[0].CodecName);
        ProbeResult jpeg = Load("img.jpg");
        Assert.Equal("mjpeg", jpeg.Streams[0].CodecName);
        Assert.Equal(1080, jpeg.ToSummary().Height);
    }

    [Fact]
    public void CoverArtIsNotVideo()
    {
        ProbeResult probe = Load("cover.mp3");
        Assert.Equal(2, probe.Streams.Count);
        Assert.Null(probe.Video);
        Assert.True(probe.Streams.Single(s => s.CodecType == "video").IsAttachedPicture);
        Assert.Equal("mp3", probe.Audio!.CodecName);
        Assert.Null(probe.ToSummary().VideoCodec);
    }

    [Fact]
    public void UnopenableFileIsNotOpenedAndUnrecognised()
    {
        ProbeResult probe = Load(
            "text.mp3",
            "[mp3 @ 000002cc99001120] [warning] Format mp3 detected only with low score of 1, misdetection possible!",
            "[mp3 @ 000002cc99001120] [error] Failed to find two consecutive MPEG audio frames.",
            "[error] text.mp3: Invalid data found when processing input");
        Assert.False(probe.Opened);
        Assert.True(probe.Unrecognised);
    }

    [Fact]
    public void MissingFieldsAndNotAvailableValuesAreTolerated()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "png", "width": "N/A", "nb_frames": "N/A", "r_frame_rate": "0/0", "avg_frame_rate": "N/A", "disposition": { "default": 1 } },
                { "index": 1, "codec_type": "audio", "sample_rate": "", "channels": 2 }
              ],
              "format": { "format_name": "image2", "probe_score": "25", "duration": "N/A", "size": "1234", "bit_rate": "N/A" }
            }
            """;
        ProbeResult probe = ProbeParser.Parse(json, LogSummary.Empty);
        Assert.True(probe.Opened);
        Assert.Null(probe.Streams[0].Width);
        Assert.Null(probe.Streams[0].NbFrames);
        Assert.Null(probe.Streams[0].RFrameRate);
        Assert.Null(probe.Streams[1].SampleRate);
        Assert.Equal(2, probe.Streams[1].Channels);
        Assert.Null(probe.DurationSeconds);
        Assert.Equal(1234, probe.SizeBytes);
        Assert.Equal(25, probe.ProbeScore);
        Assert.False(ProbeParser.Parse("{}", LogSummary.Empty).Opened);
        Assert.False(ProbeParser.Parse("not json", LogSummary.Empty).Opened);
    }

    [Fact]
    public void LowScoreWithoutStreamsIsUnrecognisedEvenWithAFormatName()
    {
        const string json = """{ "format": { "format_name": "flac", "probe_score": "1", "size": "0" } }""";
        ProbeResult probe = ProbeParser.Parse(json, LogSummary.Empty);
        Assert.False(probe.Opened);
        Assert.True(probe.Unrecognised);
    }

    /// <summary>Re-captures the golden files from generated fixtures (run with MEDIAVERIFIER_UPDATE_DOCS=1).</summary>
    [FixtureFact]
    public async Task CaptureGoldenFiles()
    {
        if (!RepositoryPaths.UpdateDocs)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new("probe");
        string text = temp.Combine("text.mp3");
        File.WriteAllText(text, "This is not an MP3 file, just text with a misleading name.\n");
        Dictionary<string, string> inputs = new()
        {
            ["src16.flac"] = FixtureFactory.Get("src16.flac"),
            ["pad24.flac"] = FixtureFactory.Get("pad24.flac"),
            ["av.mp4"] = FixtureFactory.Get("av.mp4"),
            ["img.png"] = FixtureFactory.Get("img.png"),
            ["img.jpg"] = FixtureFactory.Get("img.jpg"),
            ["cover.mp3"] = FixtureFactory.Get("cover.mp3"),
            ["text.mp3"] = text,
        };
        foreach ((string name, string path) in inputs)
        {
            FfResult result = await runner.RunAsync(new FfRequest
            {
                Tool = FfTool.Ffprobe,
                Arguments = ["-show_format", "-show_streams", "-show_error", "-of", "json", "-i", path],
                Stdout = StdoutMode.Text,
            });
            JsonNode root = JsonNode.Parse(result.StdoutText ?? "{}") ?? new JsonObject();
            if (root["format"] is JsonObject format)
            {
                // Never commit a local path.
                format["filename"] = name;
            }

            File.WriteAllText(Path.Combine(Folder, name + ".json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }
}
