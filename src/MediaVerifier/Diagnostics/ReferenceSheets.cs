using System.Text;
using MediaVerifier.Core.Model;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Shell;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// Rebuilds the reference sheets of docs/design (components, dialogs, icons) from the kit's painters, so
/// the render harness can place each beside its reference (subplan 08, Visual QA harness; task UK-13).
/// Coordinates are the SVGs' logical pixels; texts are the mockups' demo content.
/// </summary>
internal static class ReferenceSheets
{
    public static readonly Size ComponentsSize = new(1280, 1434);
    public static readonly Size DialogsSize = new(1280, 1396);
    public static readonly Size IconsSize = new(1280, 852);

    private static readonly VisualState[] ButtonStates = [VisualState.Rest, VisualState.Hover, VisualState.Pressed | VisualState.Hover, VisualState.Disabled, VisualState.Focused];

    public static void Components(Graphics g, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        g.Clear(t.Bg);
        Text(g, "Components and states", type.Title, t.Text, 24, 28, dpi);

        Caption(g, "BUTTONS", 24, 64, dpi);
        string[] columns = ["REST", "HOVER", "PRESSED", "DISABLED", "FOCUS"];
        for (int i = 0; i < columns.Length; i++)
        {
            Caption(g, columns[i], 200 + (i * 160), 64, dpi);
        }

        (string Label, ButtonKind Kind)[] rows = [("Primary", ButtonKind.Primary), ("Secondary", ButtonKind.Secondary), ("Subtle", ButtonKind.Subtle), ("Danger", ButtonKind.Danger)];
        for (int r = 0; r < rows.Length; r++)
        {
            int y = 100 + (r * 48);
            Text(g, rows[r].Label, type.Body, t.Text2, 24, y, dpi);
            int width = ButtonPainter.MeasureWidth("Scan folder", IconName.Play, false, rows[r].Kind, dpi);
            for (int c = 0; c < ButtonStates.Length; c++)
            {
                ButtonPainter.Paint(g, Rect(200 + (c * 160), y - 16, width, 32, dpi), rows[r].Kind, ButtonStates[c], "Scan folder", IconName.Play, false, dpi);
            }
        }

        Text(g, "Icon button", type.Body, t.Text2, 24, 292, dpi);
        for (int c = 0; c < ButtonStates.Length; c++)
        {
            IconButton.Draw(g, Rect(200 + (c * 160), 276, 32, 32, dpi), IconName.Refresh, ButtonStates[c], 0, dpi);
        }

        Text(g, "Compact (28)", type.Body, t.Text2, 24, 340, dpi);
        int details = ButtonPainter.MeasureWidth("Details", null, false, ButtonKind.Secondary, dpi);
        for (int c = 0; c < 4; c++)
        {
            ButtonPainter.Paint(g, Rect(200 + (c * 160), 326, details, 28, dpi), ButtonKind.Secondary, ButtonStates[c], "Details", null, false, dpi);
        }

        Text(g, "Split button", type.Body, t.Text2, 24, 388, dpi);
        VisualState[] split = [VisualState.Rest, VisualState.Hover, VisualState.Pressed];
        for (int c = 0; c < split.Length; c++)
        {
            SplitButton.Draw(g, Rect(200 + (c * 160), 372, 107, 32, dpi), split[c], split[c], "Scan", IconName.Play, dpi);
        }

        Caption(g, "SEVERITY", 24, 428, dpi);
        int x = 24;
        foreach (Severity tier in Tiers)
        {
            x += Unscale(BadgePainter.PaintSeverityBadge(g, S(x, dpi), S(444, dpi), tier, dpi), dpi) + 12;
        }

        DrawShapeRow(g, 393, 448, 12, 22, dpi);
        DrawShapeRow(g, 515, 446, 16, 28, dpi);
        DrawShapeRow(g, 667, 442, 24, 36, dpi);
        (IconName Icon, string Label)[] states = [(IconName.StatePending, "Pending"), (IconName.StateSkipped, "Skipped"), (IconName.StateMissing, "Missing"), (IconName.StateError, "Error")];
        x = 863;
        foreach ((IconName icon, string label) in states)
        {
            IconRenderer.Draw(g, icon, S(x, dpi), S(446, dpi), 16, dpi, t.Text3);
            Text(g, label, type.Caption, t.Text3, x + 22, 454, dpi);
            x += 22 + Unscale(KitPaint.MeasureWidth(label, type.Caption), dpi) + 18;
        }

        Caption(g, "CHIPS AND PILLS", 24, 488, dpi);
        x = 24;
        (string Text, Severity? Tier, IconName? Icon, int? Count, bool On, VisualState State)[] chips =
        [
            ("All", null, null, 325, true, VisualState.Rest),
            ("Critical", Severity.Critical, null, 3, false, VisualState.Rest),
            ("High", Severity.High, null, 14, false, VisualState.Hover),
            ("Medium", Severity.Medium, null, 96, true, VisualState.Rest),
            ("Photos", null, IconName.Photo, null, false, VisualState.Rest),
            ("Music", null, IconName.Music, null, true, VisualState.Rest),
        ];
        foreach (var chip in chips)
        {
            int width = Chip.MeasureWidth(chip.Text, chip.Tier, chip.Icon, chip.Count, chip.On, dpi);
            Chip.Draw(g, Rect(x, 502, width, 28, dpi), chip.Text, chip.Tier, chip.Icon, chip.Count, chip.On, chip.State, dpi);
            x += width + 8;
        }

        x = 614;
        x += Unscale(BadgePainter.PaintPill(g, S(x, dpi), S(506, dpi), "Library", dpi), dpi) + 8;
        x += Unscale(BadgePainter.PaintPill(g, S(x, dpi), S(506, dpi), "Likely", dpi), dpi) + 8;
        x += Unscale(BadgePainter.PaintStatusPill(g, S(x, dpi), S(506, dpi), "Completed", Severity.None, dpi), dpi) + 8;
        x += Unscale(BadgePainter.PaintStatusPill(g, S(x, dpi), S(506, dpi), "Stopped", Severity.Medium, dpi), dpi) + 8;
        BadgePainter.PaintStatusPill(g, S(x, dpi), S(506, dpi), "Could not run", Severity.Critical, dpi);

        Caption(g, "INPUTS", 24, 554, dpi);
        string[] inputColumns = ["REST", "HOVER", "FOCUS", "DISABLED"];
        for (int i = 0; i < inputColumns.Length; i++)
        {
            Caption(g, inputColumns[i], 200 + (i * 220), 554, dpi);
        }

        VisualState[] inputStates = [VisualState.Rest, VisualState.Hover, VisualState.Rest, VisualState.Disabled];
        Text(g, "Text box", type.Body, t.Text2, 24, 586, dpi);
        Text(g, "Search box", type.Body, t.Text2, 24, 630, dpi);
        Text(g, "Drop-down", type.Body, t.Text2, 24, 674, dpi);
        for (int c = 0; c < 4; c++)
        {
            InputPainter.PaintTextBox(g, Rect(200 + (c * 220), 570, 200, 32, dpi), "Weekly check", null, inputStates[c], c == 2, false, dpi);
            if (c < 3)
            {
                SearchBox.Draw(g, Rect(200 + (c * 220), 614, 200, 32, dpi), c == 2 ? "overpass" : string.Empty, "Search files", inputStates[c], c == 2, dpi);
                DropDown.Draw(g, Rect(200 + (c * 220), 658, 200, 32, dpi), "All folders", c == 2 ? VisualState.Pressed : inputStates[c], dpi);
            }
        }

        Text(g, "Segmented", type.Body, t.Text2, 24, 718, dpi);
        string[] depth = ["Quick", "Standard", "Deep"];
        SegmentedControl.Draw(g, Rect(200, 702, SegmentedControl.MeasureWidth(depth, 1, dpi), 32, dpi), depth, 1, -1, false, VisualState.Rest, dpi);
        string[] grouping = ["Severity", "Folder", "Rule"];
        SegmentedControl.Draw(g, Rect(480, 702, SegmentedControl.MeasureWidth(grouping, 0, dpi), 32, dpi), grouping, 0, -1, false, VisualState.Rest, dpi);

        Text(g, "Switch, check, radio", type.Body, t.Text2, 24, 756, dpi);
        ToggleSwitch.Draw(g, Rect(200, 746, 40, 20, dpi), false, VisualState.Rest, dpi);
        ToggleSwitch.Draw(g, Rect(256, 746, 40, 20, dpi), false, VisualState.Hover, dpi);
        ToggleSwitch.Draw(g, Rect(312, 746, 40, 20, dpi), true, VisualState.Rest, dpi);
        ToggleSwitch.Draw(g, Rect(368, 746, 40, 20, dpi), true, VisualState.Hover, dpi);
        KitCheckBox.Draw(g, Rect(440, 747, 18, 18, dpi), false, VisualState.Rest, dpi);
        KitCheckBox.Draw(g, Rect(472, 747, 18, 18, dpi), false, VisualState.Hover, dpi);
        KitCheckBox.Draw(g, Rect(504, 747, 18, 18, dpi), true, VisualState.Rest, dpi);
        KitCheckBox.Draw(g, Rect(536, 747, 18, 18, dpi), true, VisualState.Hover, dpi);
        KitRadioButton.Draw(g, Rect(600, 747, 18, 18, dpi), false, VisualState.Rest, dpi);
        KitRadioButton.Draw(g, Rect(632, 747, 18, 18, dpi), true, VisualState.Rest, dpi);
        ToggleSwitch.Draw(g, Rect(700, 746, 40, 20, dpi), true, VisualState.Focused, dpi);
        KitCheckBox.Draw(g, Rect(772, 747, 18, 18, dpi), true, VisualState.Focused, dpi);

        Caption(g, "PROGRESS AND DATA", 24, 798, dpi);
        DataPaint.ProgressBar(g, RectF(24, 818, 280, 8, dpi), 0.42);
        DataPaint.ProgressBar(g, RectF(24, 836, 280, 4, dpi), 0.42);
        DataPaint.ProgressRing(g, new PointF(S(336, dpi), S(826, dpi)), Dpi.ScaleF(8, dpi), -60, dpi);
        DataPaint.ProgressRing(g, new PointF(S(368, dpi), S(826, dpi)), Dpi.ScaleF(10, dpi), -60, dpi);
        Span<RectangleF> segments = stackalloc RectangleF[5];
        DataPaint.SeverityBar(g, RectF(408, 820, 320, 8, dpi), [1, 2, 14, 31, 88], segments, dpi);
        DataPaint.Donut(g, new PointF(S(784, dpi), S(828, dpi)), Dpi.ScaleF(22, dpi), Dpi.ScaleF(6, dpi), 0.82, t.Accent);
        DataPaint.Donut(g, new PointF(S(844, dpi), S(828, dpi)), Dpi.ScaleF(22, dpi), Dpi.ScaleF(6, dpi), 0.987, t.Ok.Fill);
        DataPaint.Sparkline(g, RectF(896, 808, 200, 40, dpi), [11, 22, 34, 30, 42, 53, 50, 41, 45, 52, 48, 43, 39, 41, 37], dpi);

        Caption(g, "INFO BARS", 24, 874, dpi);
        InfoBar.Draw(g, Rect(24, 888, 600, 40, dpi), Severity.Medium, "2 changed videos will be skipped.", "Show", false, VisualState.Rest, dpi);
        InfoBar.Draw(g, Rect(640, 888, 600, 40, dpi), Severity.Critical, "Drive D: returned read errors on 4 files.", "What to do", false, VisualState.Rest, dpi);
        InfoBar.Draw(g, Rect(24, 936, 600, 40, dpi), Severity.Low, "Keep your library checked automatically.", "Create schedule", false, VisualState.Rest, dpi);
        InfoBar.Draw(g, Rect(640, 936, 600, 40, dpi), Severity.None, "Restored 3 files from Backup (E:).", "Undo", false, VisualState.Rest, dpi);

        Caption(g, "LIST ROWS: GROUP HEADER 32 PX, TWO-LINE ROW 44 PX (REST, HOVER, SELECTED)", 24, 996, dpi);
        Rectangle list = Rect(24, 1010, 1232, 164, dpi);
        KitPaint.FillRoundedRect(g, list, Dpi.ScaleF(8, dpi), t.Surface);
        ListRow row = new() { IsGroup = true, Line1 = "High", Count = "14", Severity = Severity.High };
        VirtualList.DrawGroupHeader(g, Rect(24, 1010, 1232, 32, dpi), row, false, dpi);
        (string Name, string Summary, string Folder, string Size)[] files =
        [
            ("Night Drive - 07 - Overpass.flac", "Likely made from a lossy source  ·  hard cutoff at 16.6 kHz", @"D:\Music\Night Drive\Overpass", "31.2 MB"),
            ("Coastline 4K.mkv", "Likely upscaled  ·  2160p file with detail equivalent to 1080p", @"D:\Videos\Travel", "8.1 GB"),
            ("Sunday Session - 02.flac", "Embedded checksum mismatch  ·  audio differs from the stored MD5", @"D:\Music\Sunday Session", "41.0 MB"),
        ];
        for (int i = 0; i < files.Length; i++)
        {
            ListRow item = new() { Severity = Severity.High, Line1 = files[i].Name, Line2 = files[i].Summary, Folder = files[i].Folder, Size = files[i].Size };
            VirtualList.DrawTwoLineRow(g, Rect(24, 1042 + (i * 44), 1232, 44, dpi), item, i == 2, i == 1, false, dpi);
        }

        KitPaint.StrokeRoundedRect(g, list, Dpi.ScaleF(8, dpi), t.Stroke, Dpi.ScaleF(1, dpi));

        Caption(g, "NAVIGATION ITEMS 40 PX (REST, HOVER, SELECTED)   ·   SCROLLBAR (REST, HOVER)   ·   BREADCRUMB", 24, 1198, dpi);
        NavigationRail.DrawItem(g, Rect(24, 1212, 184, 40, dpi), IconName.Library, "Rest", false, false, null, Severity.None, dpi);
        NavigationRail.DrawItem(g, Rect(224, 1212, 184, 40, dpi), IconName.Library, "Hover", false, true, null, Severity.None, dpi);
        NavigationRail.DrawItem(g, Rect(424, 1212, 184, 40, dpi), IconName.Library, "Selected", true, false, "128", Severity.Critical, dpi);
        VirtualList.DrawScrollThumb(g, Rect(660, 1212, 6, 40, dpi), false, dpi);
        VirtualList.DrawScrollThumb(g, Rect(684, 1212, 10, 40, dpi), true, dpi);
        Breadcrumb.Draw(g, Rect(740, 1216, 400, 32, dpi), ["Music", "Night Drive", "Overpass"], -1, -1, dpi);

        Caption(g, "CARD   ·   LOADING SKELETON   ·   EMPTY STATE", 24, 1276, dpi);
        Card.Draw(g, Rect(24, 1290, 300, 120, dpi), "CARD TITLE", VisualState.Rest, false, dpi);
        Text(g, "Card body text", type.Body, t.Text, 40, 1342, dpi);
        Text(g, "Secondary line", type.Caption, t.Text2, 40, 1364, dpi);
        Card.Draw(g, Rect(340, 1290, 300, 120, dpi), null, VisualState.Rest, false, dpi);
        for (int i = 0; i < 3; i++)
        {
            VirtualList.DrawSkeleton(g, Rect(340, 1298 + (i * 34), 300, 34, dpi), dpi, t);
        }

        Card.Draw(g, Rect(656, 1290, 600, 120, dpi), null, VisualState.Rest, false, dpi);
        EmptyState.Draw(g, Rect(656, 1290, 600, 120, dpi), IconName.Check, true, "No problems found", "12,507 files checked. Last scan today 03:12.", null, false, dpi);
    }

    public static void Dialogs(Graphics g, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        g.Clear(t.Bg);
        Text(g, "Dialogs, menus and transient surfaces", type.Title, t.Text, 24, 28, dpi);

        // A: confirm a repair.
        CaptionMixed(g, "A  Confirm a repair", 24, 60, dpi);
        Dialog(g, Rect(24, 76, 480, 400, dpi), "Rebuild the index of 38 videos?", dpi);
        Text(g, "Each video is rewritten into a new file with a working index. The picture and", type.Body, t.Text, 48, 146, dpi);
        Text(g, "sound are copied bit for bit and checked before anything is replaced.", type.Body, t.Text, 48, 166, dpi);
        (IconName Icon, string Line)[] facts = [(IconName.Video, "38 videos, 61.4 GB in total"), (IconName.Clock, "About 4 minutes"), (IconName.Quarantine, "Originals go to quarantine for 30 days"), (IconName.Undo, "You can undo this")];
        for (int i = 0; i < facts.Length; i++)
        {
            IconRenderer.Draw(g, facts[i].Icon, S(48, dpi), S(192 + (i * 28), dpi), 16, dpi, t.Text2);
            Text(g, facts[i].Line, type.Body, t.Text, 76, 200 + (i * 28), dpi);
        }

        InfoBar.Draw(g, Rect(48, 308, 432, 40, dpi), Severity.Medium, "2 changed videos will be skipped.", "Show", false, VisualState.Rest, dpi);
        KitCheckBox.Draw(g, Rect(48, 364, 18, 18, dpi), true, VisualState.Rest, dpi);
        Text(g, "Pause the running scan while this happens", type.Body, t.Text, 76, 373, dpi);
        ButtonRow(g, 504, 420, [("Cancel", ButtonKind.Secondary, false), ("Rebuild index", ButtonKind.Primary, true)], dpi);

        // B: permanent deletion; the safe button holds the focus.
        CaptionMixed(g, "B  Confirm a permanent deletion", 536, 60, dpi);
        Dialog(g, Rect(536, 76, 400, 204, dpi), "Delete 41 files for good?", dpi);
        Text(g, "These are the originals kept in quarantine, 12.8 GB. This", type.Body, t.Text, 560, 146, dpi);
        Text(g, "cannot be undone.", type.Body, t.Text, 560, 166, dpi);
        ButtonRow(g, 936, 224, [("Keep them", ButtonKind.Secondary, true), ("Delete for good", ButtonKind.Danger, false)], dpi, hoverLast: true);

        // C: context menu.
        CaptionMixed(g, "C  Context menu", 968, 60, dpi);
        PopupItem[] menu =
        [
            new("Details", IconName.Info, "Enter"),
            new("Export replacement list", IconName.Report, "Ctrl+Enter"),
            new("Scan again", IconName.Refresh, "Shift+F5"),
            PopupItem.Separator,
            new("Ignore", IconName.Ignore, "Ctrl+I"),
            new("Ignore this rule for this folder", IconName.Ignore),
            PopupItem.Separator,
            new("Show in folder", IconName.Folder, "Alt+Enter"),
            new("Copy path", IconName.Copy, "Ctrl+C"),
            PopupItem.Separator,
            new("Move to quarantine", IconName.Quarantine, "Del"),
        ];
        Floating(g, Rect(968, 76, 280, PopupList.MeasureHeight(menu), dpi), 8, dpi);
        PopupList.PaintList(g, Rect(968, 76, 280, PopupList.MeasureHeight(menu), dpi), menu, PopupStyle.Menu, -1, 1, dpi);

        // D: toast; E: tooltip.
        CaptionMixed(g, "D  Toast", 536, 316, dpi);
        Floating(g, Rect(536, 332, 360, 64, dpi), 8, dpi);
        ToastHost.Draw(g, Rect(536, 332, 360, 64, dpi), new ToastContent(Severity.None, "Index rebuilt for 36 videos", "2 skipped  ·  originals are in quarantine", "Undo"), false, false, dpi);
        CaptionMixed(g, "E  Tooltip", 536, 424, dpi);
        string tip = @"D:\Music\Night Drive\Overpass\Night Drive - 07 - Overpass.flac";
        Rectangle tipRect = Rect(536, 440, KitToolTip.MeasureWidth(tip, dpi), 26, dpi);
        Floating(g, tipRect, 4, dpi);
        KitToolTip.Paint(g, tipRect, tip, dpi);

        // F: before a large scan.
        CaptionMixed(g, "F  Before a large scan", 24, 516, dpi);
        Dialog(g, Rect(24, 532, 480, 420, dpi), "Scan 58,210 files?", dpi);
        Text(g, "Music, Photos and Videos  ·  3.5 TB", type.Body, t.Text2, 48, 598, dpi);
        OptionRow.Draw(g, Rect(48, 620, 432, 56, dpi), "Quick", "Checks structure and fingerprints new files", "About 25 min", false, VisualState.Rest, dpi);
        OptionRow.Draw(g, Rect(48, 684, 432, 56, dpi), "Standard", "Decodes audio and photos fully, samples video", "About 3 h 20 min", true, VisualState.Rest, dpi);
        OptionRow.Draw(g, Rect(48, 748, 432, 56, dpi), "Deep", "Also decodes every video frame", "About 19 h", false, VisualState.Rest, dpi);
        Text(g, "Estimates come from earlier scans on these drives. You can pause or stop at any time,", type.Caption, t.Text3, 48, 824, dpi);
        Text(g, "and a stopped scan continues where it left off.", type.Caption, t.Text3, 48, 840, dpi);
        ButtonRow(g, 504, 896, [("Cancel", ButtonKind.Secondary, false), ("Start scan", ButtonKind.Primary, true)], dpi);

        // G: add or edit a folder.
        CaptionMixed(g, "G  Add or edit a folder", 536, 516, dpi);
        Dialog(g, Rect(536, 532, 560, 492, dpi), "Add folder", dpi);
        IconButton.Draw(g, Rect(1052, 552, 28, 28, dpi), IconName.Close, VisualState.Rest, 0, dpi);
        Text(g, "Folder", type.Body, t.Text2, 560, 608, dpi);
        InputPainter.PaintTextBox(g, Rect(664, 592, 323, 32, dpi), @"D:\Music", null, VisualState.Rest, false, true, dpi);
        ButtonPainter.Paint(g, Rect(995, 592, 77, 32, dpi), ButtonKind.Secondary, VisualState.Rest, Strings.Kit.Browse, null, false, dpi);
        Text(g, "Name", type.Body, t.Text2, 560, 652, dpi);
        InputPainter.PaintTextBox(g, Rect(664, 636, 408, 32, dpi), "Music", null, VisualState.Rest, false, true, dpi);
        Text(g, "Use as", type.Body, t.Text2, 560, 704, dpi);
        OptionRow.Draw(g, Rect(664, 684, 408, 52, dpi), "Library", "Scan everything here and report problems", null, true, VisualState.Rest, dpi);
        OptionRow.Draw(g, Rect(664, 744, 408, 52, dpi), "Backup", "Only record fingerprints, so damaged files can be restored from here", null, false, VisualState.Rest, dpi);
        Text(g, "Contains", type.Body, t.Text2, 560, 818, dpi);
        x0(g, 664, "Photos", dpi, out int next);
        x0(g, next + 30, "Videos", dpi, out next);
        x0(g, next + 30, "Music", dpi, out _);
        Text(g, "Skip", type.Body, t.Text2, 560, 860, dpi);
        InputPainter.PaintTextBox(g, Rect(664, 844, 408, 32, dpi), @"*\Samples\*; *.part", null, VisualState.Rest, false, true, dpi);
        Text(g, "Folder or file patterns, separated by semicolons", type.Caption, t.Text3, 664, 888, dpi);
        InfoBar.Draw(g, Rect(560, 908, 512, 36, dpi), Severity.Low, "3,201 media files found  ·  96 GB", null, false, VisualState.Rest, dpi);
        ButtonRow(g, 1096, 968, [("Cancel", ButtonKind.Secondary, false), ("Add folder", ButtonKind.Primary, true)], dpi);

        // H: notifications flyout.
        CaptionMixed(g, "H  Notifications", 24, 976, dpi);
        Rectangle flyout = Rect(24, 992, 380, 372, dpi);
        Floating(g, flyout, 8, dpi);
        KitPaint.FillRoundedRect(g, flyout, Dpi.ScaleF(8, dpi), t.Raised);
        KitPaint.StrokeRoundedRect(g, flyout, Dpi.ScaleF(8, dpi), t.Stroke, Dpi.ScaleF(1, dpi));
        Text(g, "Notifications", type.Subtitle, t.Text, 40, 1020, dpi);
        int markWidth = Unscale(KitPaint.MeasureWidth("Mark all read", type.Body), dpi);
        Text(g, "Mark all read", type.Body, t.Accent, 388 - markWidth, 1020, dpi);
        (Severity Tier, string Title, string Time, string Body, bool Unread)[] notes =
        [
            (Severity.Critical, "3 new problems found", "03:12", "1 critical, 2 high in Music. Scan finished 03:12.", true),
            (Severity.None, "Index rebuilt for 36 videos", "Yesterday", "2 skipped. Originals are in quarantine.", true),
            (Severity.Low, "Update available", "Mon", "Version 0.2.0.4 is ready to install.", false),
            (Severity.Medium, "Folder not connected", "12 Nov", "Backup (E:) was not found. Its files are untouched.", false),
        ];
        for (int i = 0; i < notes.Length; i++)
        {
            int top = 1048 + (i * 68);
            if (i == 1)
            {
                g.FillRectangle(GdiCache.Brush(t.Hover), Rect(25, top, 378, 68, dpi));
                KitPaint.FillRoundedRect(g, RectF(25, top + 8, 3, 52, dpi), Dpi.ScaleF(1.5f, dpi), t.StrokeStrong);
            }

            if (notes[i].Unread)
            {
                float r = Dpi.ScaleF(3, dpi);
                g.FillEllipse(GdiCache.Brush(t.Accent), S(34, dpi) - r, S(top + 24, dpi) - r, 2 * r, 2 * r);
            }

            IconRenderer.DrawSeverity(g, notes[i].Tier, S(46, dpi), S(top + 16, dpi), 16, dpi, t.ForSeverity(notes[i].Tier).Text, i == 1 ? t.Hover : t.Raised);
            Text(g, notes[i].Title, type.BodyStrong, t.Text, 74, top + 24, dpi);
            int timeWidth = Unscale(KitPaint.MeasureWidth(notes[i].Time, type.Caption), dpi);
            Text(g, notes[i].Time, type.Caption, t.Text3, 388 - timeWidth, top + 24, dpi);
            Text(g, notes[i].Body, type.Caption, t.Text2, 74, top + 45, dpi);
            KitPaint.HorizontalLine(g, t.Stroke, S(40, dpi), S(388, dpi), S(top + 67, dpi), Dpi.ScaleF(1, dpi));
        }

        Text(g, "Clear all", type.Body, t.Text, 333, 1342, dpi);

        // I: drop-down list.
        CaptionMixed(g, "I  Drop-down list", 536, 1112, dpi);
        DropDown.Draw(g, Rect(536, 1128, 180, 32, dpi), "Up to High", VisualState.Pressed, dpi);
        PopupItem[] levels = [new("Up to Critical"), new("Up to High"), new("Up to Medium"), new("Up to Low")];
        Rectangle listRect = Rect(536, 1164, 180, PopupList.MeasureHeight(levels), dpi);
        Floating(g, listRect, 8, dpi);
        PopupList.PaintList(g, listRect, levels, PopupStyle.DropDown, 1, 2, dpi);
    }

    public static void Icons(Graphics g, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        g.Clear(t.Bg);
        Text(g, "Icons", type.Title, t.Text, 24, 28, dpi);
        Text(g, "16 × 16 grid, 1.5 px stroke, round caps and joins. Shown at 16 px, at 32 px, and on the accent colour.", type.Body, t.Text2, 24, 56, dpi);
        IconName[] icons = [.. Enum.GetValues<IconName>().Where(i => i < IconName.StatePending)];
        for (int i = 0; i < icons.Length; i++)
        {
            int cx = 24 + (i % 8 * 154);
            int cy = 84 + (i / 8 * 92);
            Card.Draw(g, Rect(cx, cy, 142, 80, dpi), null, VisualState.Rest, false, dpi);
            IconRenderer.Draw(g, icons[i], S(cx + 20, dpi), S(cy + 18, dpi), 16, dpi, t.Text);
            IconRenderer.Draw(g, icons[i], S(cx + 56, dpi), S(cy + 10, dpi), 32, dpi, t.Text);
            KitPaint.FillRoundedRect(g, RectF(cx + 106, cy + 14, 24, 24, dpi), Dpi.ScaleF(4, dpi), t.Accent);
            IconRenderer.Draw(g, icons[i], S(cx + 110, dpi), S(cy + 18, dpi), 16, dpi, t.OnAccent);
            KitPaint.DrawTextCentred(g, Kebab(icons[i]), type.Caption, t.Text2, S(cx + 71, dpi), S(cy + 62, dpi));
        }

        Text(g, "App icon", type.Subtitle, t.Text, 24, 652, dpi);
        Text(g, "Gradient version at 32 px and larger; flat version with a larger triangle at 24 px and below.", type.Body, t.Text2, 24, 678, dpi);
        (int X, int Y, int Size)[] sizes = [(24, 700, 128), (180, 732, 64), (272, 740, 48), (348, 748, 32), (408, 752, 24), (460, 756, 16)];
        foreach ((int x, int y, int size) in sizes)
        {
            IconRenderer.DrawAppIcon(g, RectF(x, y, size, size, dpi), small: size <= 24);
        }
    }

    /// <summary>The kebab-case icon name used in docs/design/icons.md (SortDown → sort-down).</summary>
    public static string Kebab(IconName icon)
    {
        string name = icon.ToString();
        StringBuilder builder = new(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    private static readonly Severity[] Tiers = [Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.None];

    private static void DrawShapeRow(Graphics g, int x, int y, int size, int pitch, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        for (int i = 0; i < Tiers.Length; i++)
        {
            IconRenderer.DrawSeverity(g, Tiers[i], S(x + (i * pitch), dpi), S(y, dpi), size, dpi, t.ForSeverity(Tiers[i]).Text, t.Bg);
        }
    }

    private static void x0(Graphics g, int x, string label, int dpi, out int next)
    {
        KitCheckBox.Draw(g, Rect(x, 809, 18, 18, dpi), true, VisualState.Rest, dpi);
        Text(g, label, TypeRamp.For(dpi).Body, ThemeService.Tokens.Text, x + 28, 818, dpi);
        next = x + 28 + Unscale(KitPaint.MeasureWidth(label, TypeRamp.For(dpi).Body), dpi);
    }

    private static void Dialog(Graphics g, Rectangle bounds, string title, int dpi)
    {
        Floating(g, bounds, 8, dpi);
        KitDialog.DrawFrame(g, bounds, title, dpi);
    }

    private static void Floating(Graphics g, Rectangle bounds, int radius, int dpi) => ShadowCache.Draw(g, bounds, radius, dpi, ThemeService.Tokens.Shadow);

    /// <summary>Buttons right-aligned 24 px inside a dialog whose right edge is <paramref name="right"/>.</summary>
    private static void ButtonRow(Graphics g, int right, int y, (string Text, ButtonKind Kind, bool Focused)[] buttons, int dpi, bool hoverLast = false)
    {
        int x = right - 24;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int width = ButtonPainter.MeasureWidth(buttons[i].Text, null, false, buttons[i].Kind, dpi);
            x -= width;
            VisualState state = buttons[i].Focused ? VisualState.Focused : (hoverLast && i == buttons.Length - 1 ? VisualState.Hover : VisualState.Rest);
            ButtonPainter.Paint(g, Rect(x, y, width, 32, dpi), buttons[i].Kind, state, buttons[i].Text, null, false, dpi);
            x -= 8;
        }
    }

    private static void Caption(Graphics g, string text, int x, int y, int dpi) =>
        Text(g, text, TypeRamp.For(dpi).CaptionStrong, ThemeService.Tokens.Text3, x, y, dpi);

    private static void CaptionMixed(Graphics g, string text, int x, int y, int dpi) => Caption(g, text, x, y, dpi);

    private static void Text(Graphics g, string text, Font font, Color color, int x, int centreY, int dpi) =>
        KitPaint.DrawText(g, text, font, color, S(x, dpi), S(centreY, dpi), KitPaint.MeasureWidth(text, font) + 2);

    private static int S(int logical, int dpi) => Dpi.Scale(logical, dpi);

    private static int Unscale(int device, int dpi) => (int)Math.Round(device * 96.0 / dpi);

    private static Rectangle Rect(int x, int y, int width, int height, int dpi) => new(S(x, dpi), S(y, dpi), S(width, dpi), S(height, dpi));

    private static RectangleF RectF(float x, float y, float width, float height, int dpi) =>
        new(Dpi.ScaleF(x, dpi), Dpi.ScaleF(y, dpi), Dpi.ScaleF(width, dpi), Dpi.ScaleF(height, dpi));
}
