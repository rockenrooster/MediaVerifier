using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>Buttons, inputs, choices and containers paint in every state and behave (tasks UK-05, UK-06, UK-09).</summary>
[Collection(UiCollection.Name)]
public sealed class KitControlTests
{
    private static readonly VisualState[] States =
    [
        VisualState.Rest, VisualState.Hover, VisualState.Pressed | VisualState.Hover, VisualState.Disabled, VisualState.Focused,
    ];

    public static TheoryData<int, string> DpiAndTheme => new() { { 96, "Dark" }, { 96, "Light" }, { 144, "Dark" }, { 96, "HighContrast" } };

    [Theory]
    [MemberData(nameof(DpiAndTheme))]
    public void Every_painter_draws_every_state(int dpi, string theme)
    {
        ThemeTokens original = ThemeService.Tokens;
        ThemeService.Set(theme switch { "Light" => ThemeTokens.Light, "HighContrast" => ThemeTokens.HighContrast(), _ => ThemeTokens.Dark });
        try
        {
            using Bitmap bitmap = new(900, 200);
            using Graphics g = Graphics.FromImage(bitmap);
            Rectangle button = new(10, 10, Dpi.Scale(116, dpi), Dpi.Scale(32, dpi));
            foreach (VisualState state in States)
            {
                foreach (ButtonKind kind in Enum.GetValues<ButtonKind>())
                {
                    ButtonPainter.Paint(g, button, kind, state, "Scan folder", IconName.Play, false, dpi);
                }

                SplitButton.Draw(g, button, state, state, "Scan", IconName.Play, dpi);
                IconButton.Draw(g, new Rectangle(10, 10, 32, 32), IconName.Refresh, state, 3, dpi);
                Chip.Draw(g, new Rectangle(10, 60, 100, 28), "Critical", Severity.Critical, null, 3, true, state, dpi);
                Chip.Draw(g, new Rectangle(10, 60, 100, 28), "Photos", null, IconName.Photo, null, false, state, dpi);
                InputPainter.PaintTextBox(g, new Rectangle(10, 100, 200, 32), "Weekly check", "Placeholder", state, state.HasFlag(VisualState.Focused), false, dpi);
                SearchBox.Draw(g, new Rectangle(10, 100, 200, 32), "overpass", "Search files", state, true, dpi);
                DropDown.Draw(g, new Rectangle(220, 100, 200, 32), "All folders", state, dpi);
                SegmentedControl.Draw(g, new Rectangle(430, 100, 200, 32), ["Quick", "Standard", "Deep"], 1, 2, false, state, dpi);
                ToggleSwitch.Draw(g, new Rectangle(10, 150, 40, 20), true, state, dpi);
                ToggleSwitch.Draw(g, new Rectangle(60, 150, 40, 20), false, state, dpi);
                KitCheckBox.Draw(g, new Rectangle(110, 150, 18, 18), true, state, dpi);
                KitRadioButton.Draw(g, new Rectangle(140, 150, 18, 18), false, state, dpi);
                OptionRow.Draw(g, new Rectangle(200, 140, 400, 56), "Standard", "Decodes audio and photos fully", "About 3 h 20 min", true, state, dpi);
                Card.Draw(g, new Rectangle(620, 10, 260, 120), "CARD TITLE", state, true, dpi);
            }

            foreach (Severity tier in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.None })
            {
                BadgePainter.PaintSeverityBadge(g, 10, 10, tier, dpi);
                BadgePainter.PaintStatusPill(g, 100, 10, "Completed", tier, dpi);
                InfoBar.Draw(g, new Rectangle(10, 40, 600, 40), tier, "2 changed videos will be skipped.", "Show", true, VisualState.Focused, dpi);
            }

            BadgePainter.PaintPill(g, 10, 10, "Library", dpi);
            KitToolTip.Paint(g, new Rectangle(10, 10, 320, 26), @"D:\Music\Night Drive\Overpass.flac", dpi);
            PopupList.PaintList(g, new Rectangle(10, 10, 280, 300), [new PopupItem("Details", IconName.Info, "Enter"), PopupItem.Separator, new PopupItem("Copy path", IconName.Copy, "Ctrl+C")], PopupStyle.Menu, -1, 0, dpi);
            PopupList.PaintList(g, new Rectangle(10, 10, 180, 140), [new PopupItem("Up to Critical"), new PopupItem("Up to High")], PopupStyle.DropDown, 1, 0, dpi);
            ToastHost.Draw(g, new Rectangle(10, 10, 360, 64), new ToastContent(Severity.None, "Index rebuilt for 36 videos", "2 skipped", "Undo"), true, false, dpi);
            SelectionBar.Draw(g, new Rectangle(10, 10, 560, 48), "3 selected", dpi);
            KitDialog.DrawFrame(g, new Rectangle(10, 10, 480, 200), "Rebuild the index of 38 videos?", dpi);
            EmptyState.Draw(g, new Rectangle(0, 0, 600, 200), IconName.Check, false, "No problems found", "12,507 files checked.", "Last scan today 03:12.", true, dpi);
        }
        finally
        {
            ThemeService.Set(original);
        }
    }

    [Fact]
    public void Button_width_follows_the_content_with_minimums()
    {
        int scan = ButtonPainter.MeasureWidth("Scan folder", IconName.Play, false, ButtonKind.Secondary, 96);
        Assert.InRange(scan, 110, 116);
        Assert.Equal(64, ButtonPainter.MeasureWidth("OK", null, false, ButtonKind.Secondary, 96));
        Assert.Equal(40, ButtonPainter.MeasureWidth(null, IconName.Refresh, false, ButtonKind.Subtle, 96));
    }

    [Fact]
    public void Chip_toggles_with_space_and_reports_checked_state()
    {
        using Chip chip = new() { Text = "Photos" };
        int toggled = 0;
        chip.Toggled += (_, _) => toggled++;
        Send(chip, Keys.Space);
        Assert.True(chip.On);
        Assert.Equal(1, toggled);
        Assert.True(chip.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
    }

    [Fact]
    public void Segmented_control_moves_with_arrows_and_stops_at_the_ends()
    {
        using SegmentedControl segmented = new();
        segmented.Segments.Add("Quick");
        segmented.Segments.Add("Standard");
        segmented.Segments.Add("Deep");
        Send(segmented, Keys.Right);
        Send(segmented, Keys.Right);
        Send(segmented, Keys.Right);
        Assert.Equal(2, segmented.SelectedIndex);
        Send(segmented, Keys.Home);
        Assert.Equal(0, segmented.SelectedIndex);
    }

    [Fact]
    public void Drop_down_changes_value_with_up_and_down_while_closed()
    {
        using DropDown dropDown = new();
        dropDown.Items.Add("Up to Critical");
        dropDown.Items.Add("Up to High");
        dropDown.SelectedIndex = 0;
        Send(dropDown, Keys.Down);
        Assert.Equal("Up to High", dropDown.SelectedItem);
        Send(dropDown, Keys.Up);
        Assert.Equal(0, dropDown.SelectedIndex);
        dropDown.DisplayFormat = "Group: {0}";
        Assert.Equal("Group: Up to Critical", dropDown.ShownText);
    }

    [Fact]
    public void Radio_buttons_and_option_rows_exclude_their_group()
    {
        using Panel panel = new();
        KitRadioButton a = new() { GroupName = "g", Checked = true };
        KitRadioButton b = new() { GroupName = "g" };
        OptionRow quick = new() { GroupName = "depth", Checked = true };
        OptionRow deep = new() { GroupName = "depth" };
        panel.Controls.AddRange([a, b, quick, deep]);
        Send(b, Keys.Space);
        Assert.True(b.Checked);
        Assert.False(a.Checked);
        Send(deep, Keys.Space);
        Assert.True(deep.Checked);
        Assert.False(quick.Checked);
    }

    [Fact]
    public void Number_box_clamps_and_steps()
    {
        using NumberBox box = new() { Minimum = 1, Maximum = 90, Value = 89 };
        box.Value = 120;
        Assert.Equal(90, box.Value);
        box.Text = "12";
        Assert.Equal(12, box.Value);
    }

    [Fact]
    public void Popup_list_height_counts_items_separators_and_padding()
    {
        Assert.Equal(8 + 32 + 9 + 32, PopupList.MeasureHeight([new PopupItem("A"), PopupItem.Separator, new PopupItem("B")]));
    }

    private static void Send(Control control, Keys key)
    {
        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, [new KeyEventArgs(key)]);
    }
}
