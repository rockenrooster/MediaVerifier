using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
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

    /// <summary>
    /// Every card opens from the keyboard: the chevrons are buttons (findings after the first, History and Technical
    /// details were mouse only), named for screen readers, and Technical details has its Copy details button.
    /// </summary>
    [Fact]
    public void The_cards_open_from_the_keyboard_and_technical_details_can_be_copied()
    {
        FileDetail detail = new(
            1,
            "Night Drive - 07 - Overpass.flac",
            @"D:\Music\Night Drive - 07 - Overpass.flac",
            ["D:", "Music"],
            MediaKind.Audio,
            "FLAC",
            FingerprintState.Unchanged,
            DateTimeOffset.UtcNow,
            1,
            DateTimeOffset.UtcNow,
            [Finding(1, "AUD-FAKE-LOSSLESS", EvidenceView.None, new Dictionary<string, object>()), Finding(2, "AUD-DC", EvidenceView.None, new Dictionary<string, object>())],
            [],
            "codec flac\nsample rate 44100",
            1000,
            []);
        using FileDetailView view = new() { Width = 440 };
        view.Bind(detail, DateTimeOffset.UtcNow, string.Empty);
        List<IconButton> chevrons = [.. view.Controls.OfType<IconButton>().Where(b => b.Icon is IconName.ChevronDown or IconName.ChevronUp)];
        Assert.Equal(4, chevrons.Count);
        Assert.All(chevrons, c => Assert.False(string.IsNullOrEmpty(c.AccessibleName)));
        KitButton copy = view.Controls.OfType<KitButton>().Single(b => b.Text == Strings.Drawer.CopyDetails);
        Assert.False(copy.Visible);

        int closed = view.Height;
        IconButton second = chevrons.Single(c => c.AccessibleName == "AUD-DC");
        Assert.Equal(IconName.ChevronDown, second.Icon);
        PressEnter(second);
        Assert.Equal(IconName.ChevronUp, second.Icon);
        Assert.True(view.Height > closed, "the second finding did not open");

        PressEnter(chevrons.Single(c => c.AccessibleName == Strings.Drawer.TechnicalDetails));
        Assert.True(copy.Visible, "Copy details is not shown with the technical details");
        PressEnter(chevrons.Single(c => c.AccessibleName == Strings.Drawer.History));
        Assert.Equal(IconName.ChevronUp, chevrons.Single(c => c.AccessibleName == Strings.Drawer.History).Icon);
    }

    /// <summary>Enter on a focused control, through its own key handling (the keyboard's path, not a click).</summary>
    private static void PressEnter(Control control) =>
        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(control, [new KeyEventArgs(Keys.Enter)]);

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
        using Form owner = DialogStage.Show(dialog);
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            MediaVerifier.Diagnostics.ScreenRenderer.Save(dialog, Path.Combine(folder, "dialog-a.png"));
        }

        Assert.True(dialog.TotalLogicalHeight() >= 380, $"{dialog.TotalLogicalHeight()}");
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
        using Form owner = DialogStage.Show(dialog);
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            MediaVerifier.Diagnostics.ScreenRenderer.Save(dialog, Path.Combine(folder, "schedule-edit.png"));
        }

        Assert.Equal("Weekly check", dialog.PlanName);
        Assert.Equal(weekly.ToJson(), dialog.Result.ToJson());
        dialog.Close();
    }

    /// <summary>
    /// Save is disabled only with a reason the summary box shows (subplan 09's wording), and a time typed without its
    /// leading zero counts: before, "3:00" disabled Save without a word.
    /// </summary>
    [Fact]
    public void The_editor_says_why_Save_is_disabled_and_reads_a_short_time()
    {
        MediaVerifier.Core.Scheduling.PlanDefinition weekly = MediaVerifier.Core.Scheduling.PlanDefinition.Templates[1].Definition;
        using ScheduleEditorDialog dialog = ScheduleEditorDialog.Create("Weekly check", weekly, [], new DateOnly(2026, 11, 1));
        using Form owner = DialogStage.Show(dialog);
        KitTextBox name = Field<KitTextBox>(dialog, "_name");
        KitTextBox time = Field<KitTextBox>(dialog, "_time");
        KitButton save = Field<KitButton>(dialog, "_save");

        time.Text = "3:00";
        Assert.True(save.Enabled);
        Assert.Null(Field<string>(dialog, "_problem"));
        Assert.Equal("03:00", dialog.Result.Trigger.Time);

        time.Text = "25:00";
        Assert.False(save.Enabled);
        Assert.Equal(Strings.Schedule.ErrorTime, Field<string>(dialog, "_problem"));

        time.Text = "21:30";
        name.Text = "  ";
        Assert.False(save.Enabled);
        Assert.Equal(Strings.Schedule.ErrorName, Field<string>(dialog, "_problem"));
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            MediaVerifier.Diagnostics.ScreenRenderer.Save(dialog, Path.Combine(folder, "schedule-edit-error.png"));
        }

        name.Text = "Nightly";
        Assert.True(save.Enabled);
        Assert.Equal("21:30", dialog.Result.Trigger.Time);
        dialog.Close();
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
}
