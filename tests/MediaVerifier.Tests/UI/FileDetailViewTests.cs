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

/// <summary>Dialog A lays out its facts, the skipped-files bar and the pause check box (subplan 09, A).</summary>
[Collection(UiCollection.Name)]
public sealed class RepairDialogTests
{
    [Fact]
    public void Dialog_a_paints_with_every_optional_part()
    {
        RepairPreview preview = new(
            "Rebuild the index of 38 videos?",
            "Each video is rewritten into a new file with a working index. The picture and sound are copied bit for bit and checked before anything is replaced.",
            MediaVerifier.UI.Kit.IconName.Video,
            "38 videos, 61.4 GB in total",
            "About 4 minutes",
            "Originals go to quarantine for 30 days",
            true,
            2,
            ["a.mp4", "b.mp4"],
            true,
            "Rebuild index");
        using RepairDialog dialog = RepairDialog.Create(preview);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Size = new Size(dialog.LogicalWidth, dialog.TotalLogicalHeight());
        dialog.Show();
        dialog.PerformLayout();
        Application.DoEvents();
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            MediaVerifier.Diagnostics.ScreenRenderer.Save(dialog, Path.Combine(folder, "dialog-a.png"));
        }

        Assert.True(dialog.Height >= 380, $"{dialog.Height}");
        dialog.Close();
    }
}

/// <summary>The plan editor shows a plan and builds a valid definition from its controls (task SCH-07).</summary>
[Collection(UiCollection.Name)]
public sealed class ScheduleEditorTests
{
    [Fact]
    public void The_editor_round_trips_a_weekly_plan()
    {
        MediaVerifier.Core.Scheduling.PlanDefinition weekly = MediaVerifier.Core.Scheduling.PlanDefinition.Templates[1].Definition;
        using ScheduleEditorDialog dialog = ScheduleEditorDialog.Create("Weekly check", weekly, [new MediaVerifier.Core.Storage.RootRecord { Id = 1, Path = @"D:\Music", Label = "Music" }], new DateOnly(2026, 11, 1));
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Size = new Size(dialog.LogicalWidth, dialog.TotalLogicalHeight());
        dialog.Show();
        dialog.PerformLayout();
        Application.DoEvents();
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            MediaVerifier.Diagnostics.ScreenRenderer.Save(dialog, Path.Combine(folder, "schedule-edit.png"));
        }

        Assert.Equal("Weekly check", dialog.PlanName);
        Assert.Equal(weekly.ToJson(), dialog.Result.ToJson());
        dialog.Close();
    }
}
