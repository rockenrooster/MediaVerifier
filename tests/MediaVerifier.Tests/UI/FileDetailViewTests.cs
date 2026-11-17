using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>The drawer paints every evidence view, with and without its picture (tasks UI-05, A-09, V-05).</summary>
[Collection(UiCollection.Name)]
public sealed class FileDetailViewTests
{
    [Fact]
    public void Every_evidence_view_paints_while_its_picture_is_missing()
    {
        Dictionary<string, object> upscale = new()
        {
            ["effectiveHeight"] = 720,
            ["containerHeight"] = 1080,
            ["containerWidth"] = 1920,
            ["frameTimes"] = new object[] { 0.3, 2.5, 4.6 },
            ["frameSsim"] = new object[] { 0.993, 0.991, 0.95 },
            ["frameUsable"] = new object[] { true, true, false },
        };
        Dictionary<string, object> bitrate = new()
        {
            ["bitrateKbps"] = 446.0,
            ["equivalentBpp"] = 0.009,
            ["width"] = 1920,
            ["height"] = 1080,
            ["fps"] = 24.0,
            ["codec"] = "h264",
        };
        Dictionary<string, object> waveform = new() { ["positionsSeconds"] = new object[] { 1.5, 3.0 } };
        FileDetail detail = new(
            1,
            "Coastline 4K.mkv",
            @"D:\Videos\Coastline 4K.mkv",
            ["D:", "Videos"],
            MediaKind.Video,
            "MKV",
            FingerprintState.Unchanged,
            DateTimeOffset.UtcNow,
            1,
            DateTimeOffset.UtcNow,
            [
                Finding(1, "VID-UPSCALED", EvidenceView.FrameStrip, upscale),
                Finding(2, "VID-STARVED", EvidenceView.Bitrate, bitrate),
                Finding(3, "AUD-CLIPPING", EvidenceView.Waveform, waveform),
                Finding(4, "AUD-FAKE-LOSSLESS", EvidenceView.Spectrogram, new Dictionary<string, object> { ["kneeHz"] = 16600.0 }),
            ],
            [],
            string.Empty,
            1000,
            []);

        using FileDetailView view = new() { Width = 440 };
        view.Bind(detail, DateTimeOffset.UtcNow, string.Empty);
        for (int i = 1; i < 4; i++)
        {
            view.ToggleFinding(i);
        }

        using Bitmap bitmap = new(440, Math.Max(1, view.Height));
        view.DrawToBitmap(bitmap, new Rectangle(0, 0, 440, view.Height));
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            bitmap.Save(Path.Combine(folder, "drawer-evidence.png"));
        }
        Assert.True(view.Height > 900, $"all four findings open: {view.Height}");
    }

    private static FindingDetail Finding(long id, string rule, EvidenceView view, IReadOnlyDictionary<string, object> evidence) =>
        new(id, rule, Severity.Medium, 0.85f, rule, "summary", "what", "why", "do", view, evidence, "caption");
}
