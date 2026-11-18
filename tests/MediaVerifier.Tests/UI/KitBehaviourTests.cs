using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// How the kit controls behave under the mouse and the keyboard (audit of v0.1.0.14): focus rings that show, clicks
/// that only the left button makes, keys that act once, text boxes that keep the focus and the values they show.
/// Windows are off-screen and never activated; input arrives as messages sent to their handles.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class KitBehaviourTests
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;

    /// <summary>
    /// A focus ring lies 1 px outside its control, so the container paints it: a 2 px Accent ring with a 1 px gap,
    /// here for a button inside a card and for the Browse button inside a path box.
    /// </summary>
    [Fact]
    public void The_container_paints_the_focus_ring_outside_the_control()
    {
        using Card card = new() { Size = new Size(300, 120) };
        KitButton button = new() { Text = "Verify now", Bounds = new Rectangle(40, 40, 120, 32) };
        card.Controls.Add(button);
        using Bitmap bitmap = new(300, 120);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            KitPaint.PaintFocusRing(g, card, button);
        }

        // The middle of the 2 px stroke, 1 px out from the control (scaled with the control's DPI). GDI+ centres pixels on
        // whole coordinates, so on the right and bottom the fully covered pixel is one further out.
        Color accent = ThemeService.Tokens.Accent;
        int out2 = (int)Math.Round(Dpi.ScaleF(1, button.DeviceDpi) + Dpi.ScaleF(1, button.DeviceDpi), MidpointRounding.AwayFromZero);
        int y = button.Top + (button.Height / 2);
        Assert.True(Near(bitmap.GetPixel(button.Left - out2, y), accent), "no ring left of the button");
        Assert.True(Near(bitmap.GetPixel(button.Right + out2, y), accent), "no ring right of the button");
        Assert.True(Near(bitmap.GetPixel(button.Left + 60, button.Top - out2), accent), "no ring above the button");
        Assert.True(Near(bitmap.GetPixel(button.Left + 60, button.Bottom + out2), accent), "no ring below the button");
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(button.Left + 2, y).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(button.Left - 12, y).ToArgb());

        // A control that is not inside the host paints nothing there.
        using KitButton elsewhere = new() { Bounds = new Rectangle(40, 40, 120, 32) };
        using Bitmap empty = new(300, 120);
        using (Graphics g = Graphics.FromImage(empty))
        {
            g.Clear(Color.Black);
            KitPaint.PaintFocusRing(g, card, elsewhere);
        }

        Assert.Equal(Color.Black.ToArgb(), empty.GetPixel(37, 56).ToArgb());
    }

    [Fact]
    public void Ring_shapes_follow_each_control()
    {
        using KitButton button = new() { Size = new Size(100, 32) };
        Assert.Equal(new Rectangle(0, 0, 100, 32), button.FocusRingBounds);
        using ToggleSwitch toggle = new() { Text = "Start with Windows", Size = new Size(200, 24) };
        Rectangle mark = toggle.FocusRingBounds;
        Assert.Equal(new Size(Dpi.Scale(40, toggle.DeviceDpi), Dpi.Scale(20, toggle.DeviceDpi)), mark.Size);
        Assert.Equal(mark.Height / 2f, toggle.FocusRingRadius);
        using KitCheckBox check = new() { Size = new Size(18, 18) };
        Assert.Equal(Dpi.ScaleF(3, check.DeviceDpi), check.FocusRingRadius);
        using Card passive = new() { Size = new Size(200, 100) };
        Assert.True(passive.FocusRingBounds.IsEmpty);
        passive.Clickable = true;
        Assert.Equal(passive.ClientRectangle, passive.FocusRingBounds);
        using KitTextBox box = new() { Size = new Size(200, 32) };
        Assert.True(box.FocusRingBounds.IsEmpty, "a text box shows focus with its border");
    }

    /// <summary>Right and middle clicks used to press buttons, flip switches and chips and confirm dialogs.</summary>
    [Fact]
    public void Only_the_left_button_clicks()
    {
        int clicks = 0;
        int toggles = 0;
        bool switched = false;
        Sta.Run(() =>
        {
            using Form owner = OffscreenForm();
            KitButton button = new() { Text = "Delete for good", Bounds = new Rectangle(10, 10, 140, 32) };
            Chip chip = new() { Text = "Photos", Bounds = new Rectangle(10, 60, 100, 28) };
            ToggleSwitch toggle = new() { Bounds = new Rectangle(10, 100, 40, 20) };
            button.Click += (_, _) => clicks++;
            chip.Toggled += (_, _) => toggles++;
            owner.Controls.AddRange([button, chip, toggle]);
            owner.Show();
            Application.DoEvents();
            foreach ((int down, int up) in new[] { (WmRButtonDown, WmRButtonUp), (WmMButtonDown, WmMButtonUp) })
            {
                Click(button, down, up);
                Click(chip, down, up);
                Click(toggle, down, up);
            }

            Assert.Equal(0, clicks);
            Assert.Equal(0, toggles);
            Assert.False(toggle.Checked);
            Click(button, WmLButtonDown, WmLButtonUp);
            Click(chip, WmLButtonDown, WmLButtonUp);
            Click(toggle, WmLButtonDown, WmLButtonUp);
            switched = toggle.Checked;
            owner.Close();
        });

        Assert.Equal(1, clicks);
        Assert.Equal(1, toggles);
        Assert.True(switched);
    }

    /// <summary>Two quick clicks on a chip or switch are two toggles, not one click and a double-click.</summary>
    [Fact]
    public void A_quick_second_click_toggles_again()
    {
        int toggles = 0;
        bool on = true;
        Sta.Run(() =>
        {
            using Form owner = OffscreenForm();
            Chip chip = new() { Text = "Photos", Bounds = new Rectangle(10, 60, 100, 28) };
            ToggleSwitch toggle = new() { Bounds = new Rectangle(10, 100, 40, 20) };
            chip.Toggled += (_, _) => toggles++;
            owner.Controls.AddRange([chip, toggle]);
            owner.Show();
            Application.DoEvents();
            DoubleClick(chip);
            DoubleClick(toggle);
            on = toggle.Checked;
            owner.Close();
        });

        Assert.Equal(2, toggles);
        Assert.False(on);
    }

    /// <summary>A double-click on Scan started a scan and then warned that it could not run.</summary>
    [Fact]
    public void A_double_click_on_the_split_button_clicks_once()
    {
        int clicks = 0;
        Sta.Run(() =>
        {
            using Form owner = OffscreenForm();
            SplitButton scan = new() { Text = "Scan", Bounds = new Rectangle(10, 10, 120, 32) };
            scan.Click += (_, _) => clicks++;
            owner.Controls.Add(scan);
            owner.Show();
            Application.DoEvents();
            DoubleClick(scan);
            owner.Close();
        });

        Assert.Equal(1, clicks);
    }

    /// <summary>Space presses while held and acts on release; holding a key never repeats the action.</summary>
    [Fact]
    public void Space_acts_on_release_and_keys_do_not_repeat()
    {
        using ToggleSwitch toggle = new();
        KeyDown(toggle, Keys.Space);
        Assert.False(toggle.Checked);
        KeyDown(toggle, Keys.Space);
        KeyDown(toggle, Keys.Space);
        KeyUp(toggle, Keys.Space);
        Assert.True(toggle.Checked);

        using KitButton button = new() { Text = "OK" };
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        KeyDown(button, Keys.Enter);
        KeyDown(button, Keys.Enter);
        KeyDown(button, Keys.Enter);
        Assert.Equal(1, clicks);
        KeyUp(button, Keys.Enter);
        KeyDown(button, Keys.Enter);
        Assert.Equal(2, clicks);
        KeyDown(button, Keys.Space);
        Assert.Equal(2, clicks);
        KeyUp(button, Keys.Space);
        Assert.Equal(3, clicks);
    }

    /// <summary>
    /// A held Enter that opened a dialog auto-repeats into the dialog's focused button, and into the invoker again
    /// when the dialog closes: a repeated key-down never acts.
    /// </summary>
    [Fact]
    public void A_repeated_key_down_from_a_press_elsewhere_does_not_act()
    {
        int clicks = 0;
        bool toggled = true;
        Sta.Run(() =>
        {
            using Form owner = OffscreenForm();
            KitButton button = new() { Text = "Keep scanning", Bounds = new Rectangle(10, 10, 140, 32) };
            ToggleSwitch toggle = new() { Bounds = new Rectangle(10, 60, 40, 20) };
            button.Click += (_, _) => clicks++;
            owner.Controls.AddRange([button, toggle]);
            owner.Show();
            Application.DoEvents();
            Key(button, WmKeyDown, Keys.Enter, repeat: true);
            Key(button, WmKeyUp, Keys.Enter, repeat: true);
            Key(toggle, WmKeyDown, Keys.Space, repeat: true);
            Key(toggle, WmKeyUp, Keys.Space, repeat: true);
            Assert.Equal(0, clicks);
            Assert.False(toggle.Checked);

            Key(button, WmKeyDown, Keys.Enter, repeat: false);
            Key(button, WmKeyDown, Keys.Enter, repeat: true);
            Key(button, WmKeyUp, Keys.Enter, repeat: true);
            Key(toggle, WmKeyDown, Keys.Space, repeat: false);
            Key(toggle, WmKeyDown, Keys.Space, repeat: true);
            Key(toggle, WmKeyUp, Keys.Space, repeat: true);
            toggled = toggle.Checked;
            owner.Close();
        });

        Assert.Equal(1, clicks);
        Assert.True(toggled);
    }

    /// <summary>
    /// The render harness prints the edit: the placeholder of an empty box and the text of a filled one are part of
    /// the picture.
    /// </summary>
    [Fact]
    public void A_printed_box_shows_its_placeholder_or_text()
    {
        int placeholder = 0;
        int text = 0;
        Sta.Run(() =>
        {
            using Form owner = OffscreenForm();
            SearchBox named = new() { Placeholder = "Search files", Bounds = new Rectangle(10, 10, 240, 32) };
            SearchBox blank = new() { Bounds = new Rectangle(10, 60, 240, 32) };
            SearchBox filled = new() { Text = "overpass", Bounds = new Rectangle(10, 110, 240, 32) };
            owner.Controls.AddRange([named, blank, filled]);
            owner.Show();
            Application.DoEvents();
            using Bitmap without = Print(Edit(blank));
            using (Bitmap withPlaceholder = Print(Edit(named)))
            {
                placeholder = Differing(withPlaceholder, without);
            }

            using (Bitmap withText = Print(Edit(filled)))
            {
                text = Differing(withText, without);
            }

            owner.Close();
        });

        Assert.True(placeholder > 20, $"only {placeholder} pixels show the placeholder");
        Assert.True(text > 20, $"only {text} pixels show the text");
    }

    /// <summary>Arrow keys move between controls that can be selected: passive cards and pages are not among them.</summary>
    [Fact]
    public void Only_tab_stops_are_selectable()
    {
        using PassiveView view = new();
        Assert.False(view.TabStop, "a kit control that never asks to be a tab stop is one");
        Assert.False(view.CanSelect);
        using Card card = new();
        Assert.False(card.CanSelect);
        card.Clickable = true;
        Assert.True(card.CanSelect);
        using InfoBar bar = new();
        Assert.False(bar.CanSelect);
        bar.Action = "Show";
        Assert.True(bar.CanSelect);
        using KitButton button = new();
        Assert.True(button.CanSelect);
    }

    /// <summary>Only the checked option of a group is a tab stop: Tab leaves the group, Up and Down move inside it.</summary>
    [Fact]
    public void Only_the_checked_option_is_a_tab_stop()
    {
        using Panel panel = new();
        OptionRow quick = new() { GroupName = "depth" };
        OptionRow standard = new() { GroupName = "depth", Checked = true };
        OptionRow deep = new() { GroupName = "depth" };
        panel.Controls.AddRange([quick, standard, deep]);
        Assert.Equal(new[] { false, true, false }, new[] { quick.TabStop, standard.TabStop, deep.TabStop });
        deep.Checked = true;
        Assert.Equal(new[] { false, false, true }, new[] { quick.TabStop, standard.TabStop, deep.TabStop });
        deep.Checked = false;
        Assert.Equal(new[] { true, true, true }, new[] { quick.TabStop, standard.TabStop, deep.TabStop });
    }

    /// <summary>
    /// The edit inside a text box is the tab stop and stays visible (Shift+Tab used to snap back into the same box, the
    /// first click lost its position, and switching windows lost the focus); the box's name is on the edit.
    /// </summary>
    [Fact]
    public void A_text_box_hands_tab_stop_and_name_to_its_edit()
    {
        using KitTextBox box = new() { AccessibleName = "Name", Size = new Size(200, 32) };
        TextBox edit = Edit(box);
        Assert.False(box.TabStop);
        Assert.False(box.CanSelect);
        Assert.True(edit.TabStop);
        Assert.True(edit.Visible);
        Assert.Equal("Name", edit.AccessibleName);

        using SearchBox search = new() { Placeholder = "Filter rules", Size = new Size(240, 32) };
        Assert.Equal("Filter rules", Edit(search).AccessibleName);

        using PathBox path = new() { AccessibleName = "Folder", Size = new Size(400, 32) };
        Assert.Equal("Folder", Edit(path.Controls.OfType<KitTextBox>().Single()).AccessibleName);

        box.Enabled = false;
        Assert.False(edit.Visible);
        box.Enabled = true;
        Assert.True(edit.Visible);
    }

    /// <summary>The clear icon's 36 px stay free of the edit while typing, so the icon is never covered.</summary>
    [Fact]
    public void The_search_edit_never_covers_the_clear_icon()
    {
        using SearchBox search = new() { Size = new Size(240, 32) };
        search.PerformLayout();
        TextBox edit = Edit(search);
        int right = edit.Right;
        search.Text = "overpass";
        search.PerformLayout();
        Assert.Equal(right, edit.Right);
        Assert.True(edit.Right <= search.Width - Dpi.Scale(36, search.DeviceDpi));
    }

    /// <summary>
    /// A number box shows the value in use once the edit loses the focus: "20" in a box limited to 16 was shown while
    /// 2 (its first digit) was saved; an emptied box shows the value again.
    /// </summary>
    [Fact]
    public void A_number_box_applies_its_limits_when_the_edit_loses_focus()
    {
        using NumberBox box = new() { Minimum = 1, Maximum = 16, Value = 4 };
        List<int> saved = [];
        box.ValueChanged += (_, _) => saved.Add(box.Value);
        TextBox edit = Edit(box);
        edit.Text = "2";
        edit.Text = "20";
        Assert.Equal(2, box.Value);
        Raise(edit, "OnLostFocus", EventArgs.Empty);
        Assert.Equal(16, box.Value);
        Assert.Equal("16", box.Text);
        Assert.Equal(new[] { 2, 16 }, saved);

        edit.Text = string.Empty;
        Raise(edit, "OnLostFocus", EventArgs.Empty);
        Assert.Equal(16, box.Value);
        Assert.Equal("16", box.Text);
    }

    /// <summary>The five dBFS rule limits are negative: the minus sign is accepted at the start of the number.</summary>
    [Fact]
    public void A_number_box_with_negative_limits_accepts_a_minus_sign()
    {
        char minus = CultureInfo.CurrentCulture.NumberFormat.NegativeSign[0];
        using NumberBox db = new() { Minimum = -150, Maximum = -20, Number = -80 };
        TextBox edit = Edit(db);
        edit.SelectAll();
        Assert.False(Press(edit, minus), "a minus sign replacing the whole value was rejected");
        edit.Select(edit.TextLength, 0);
        Assert.True(Press(edit, minus), "a second minus sign was accepted");
        Assert.True(Press(edit, 'x'));
        edit.Text = "-90";
        Assert.Equal(-90, db.Value);

        using NumberBox count = new() { Minimum = 1, Maximum = 16 };
        Assert.True(Press(Edit(count), minus), "a minus sign was accepted where no negative value is allowed");
    }

    /// <summary>Clicking an open drop-down closes its list; it used to close it and open it again at once.</summary>
    [Fact]
    public void Clicking_an_open_drop_down_closes_it()
    {
        bool closedAfterClick = false;
        bool opensAgain = false;
        Sta.Run(() =>
        {
            using Form owner = OffscreenForm();
            DropDown drop = new() { Bounds = new Rectangle(20, 20, 200, 32) };
            drop.Items.Add("Severity");
            drop.Items.Add("Folder");
            drop.SelectedIndex = 0;
            owner.Controls.Add(drop);
            owner.Show();
            Application.DoEvents();
            drop.OpenList();
            Application.DoEvents();
            PopupList list = (PopupList)typeof(DropDown).GetField("_open", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(drop)!;

            // The list's message filter sees the press on the drop-down first and closes the list; then the press
            // and the release reach the drop-down.
            Message press = Message.Create(drop.Handle, WmLButtonDown, (IntPtr)1, LParam(20, 16));
            _ = list.PreFilterMessage(ref press);
            Click(drop, WmLButtonDown, WmLButtonUp);
            closedAfterClick = !drop.IsOpen;
            Application.DoEvents();
            Click(drop, WmLButtonDown, WmLButtonUp);
            opensAgain = drop.IsOpen;
            drop.Dispose();
            Application.DoEvents();
            owner.Close();
        });

        Assert.True(closedAfterClick, "the click that closed the list opened it again");
        Assert.True(opensAgain, "the next click did not open the list");
    }

    [Fact]
    public void A_busy_button_ignores_clicks_and_says_it_is_busy()
    {
        using KitButton button = new() { Text = "Compact" };
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        int width = button.PreferredLogicalWidth;
        button.Busy = true;
        button.PerformClick();
        KeyDown(button, Keys.Enter);
        Assert.Equal(0, clicks);
        Assert.True(button.AccessibilityObject.State.HasFlag(AccessibleStates.Busy));
        Assert.Equal(width + 16 + 8, button.PreferredLogicalWidth);
        using (Bitmap bitmap = new(200, 40))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            ButtonPainter.Paint(g, new Rectangle(0, 0, 120, 32), ButtonKind.Primary, VisualState.Disabled, "Compact", null, false, 96, busyAngle: 30);
        }

        button.Busy = false;
        KeyUp(button, Keys.Enter);
        button.PerformClick();
        Assert.Equal(1, clicks);
    }

    /// <summary>"Stop ignoring" and a long count used to push Quarantine under the close button.</summary>
    [Fact]
    public void Selection_bar_buttons_never_run_under_the_close_button()
    {
        using SelectionBar bar = new() { Size = new Size(Dpi.Scale(470, 96), Dpi.Scale(48, 96)) };
        bar.SetButtons(
        [
            new KitButton { Text = "Fix…", Kind = ButtonKind.Primary, Icon = IconName.Tools },
            new KitButton { Text = "Stop ignoring", Icon = IconName.Ignore },
            new KitButton { Text = "Scan again", Icon = IconName.Refresh },
            new KitButton { Text = "Quarantine", Icon = IconName.Quarantine },
        ]);
        bar.CountText = "3,902 selected";
        bar.PerformLayout();
        IconButton close = bar.Controls.OfType<IconButton>().Single();
        Assert.All(bar.Buttons, b => Assert.True(b.Right <= close.Left, $"{b.Text} ends at {b.Right}, the close button starts at {close.Left}"));
        Assert.All(bar.Buttons, b => Assert.True(b.IconOnly));
        Assert.Equal("Quarantine", KitToolTip.Get(bar.Buttons[3]));
        Assert.Equal(bar.Buttons.Count, close.TabIndex);
        Assert.Equal(Enumerable.Range(0, 4), bar.Buttons.Select(b => b.TabIndex));

        // At full width the labels fit and come back.
        bar.Size = new Size(Dpi.Scale(560, 96), Dpi.Scale(48, 96));
        bar.CountText = "3 selected";
        bar.Buttons[1].Text = "Ignore";
        bar.PerformLayout();
        Assert.All(bar.Buttons, b => Assert.False(b.IconOnly));
        Assert.Equal(string.Empty, KitToolTip.Get(bar.Buttons[3]));
    }

    [Fact]
    public void Screen_readers_hear_the_segment_and_the_info_bar_action()
    {
        using SegmentedControl theme = new() { AccessibleName = "Theme" };
        theme.Segments.Add("System");
        theme.Segments.Add("Light");
        theme.Segments.Add("Dark");
        theme.SelectedIndex = 2;
        Assert.Equal("Dark", theme.AccessibilityObject.Value);

        using InfoBar bar = new() { Text = "2 changed videos will be skipped.", Action = "Show" };
        Assert.Equal("Show", bar.AccessibilityObject.Description);
    }

    /// <summary>The empty state places its button in layout, not as a side effect of painting.</summary>
    [Fact]
    public void An_empty_state_places_its_button_without_painting()
    {
        using EmptyState empty = new() { Size = new Size(600, 400), Headline = "No folders yet", Body = "Add a folder to start." };
        KitButton add = empty.SetButton("Add folder");
        Assert.True(add.Top > 200 && add.Top < 300, $"button at {add.Top}");
        Assert.Equal((600 - add.Width) / 2, add.Left);
    }

    /// <summary>Text drawn through GDI keeps to the Graphics' clip (rows scrolled under a table's header).</summary>
    [Fact]
    public void Native_text_respects_the_graphics_clip()
    {
        using Bitmap bitmap = new(200, 40);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.SetClip(new Rectangle(0, 0, 200, 10));
            KitPaint.DrawText(g, "Overpass", TypeRamp.For(96).Body, Color.Black, 4, 26, 190);
        }

        bool inked = false;
        for (int x = 0; x < 200 && !inked; x++)
        {
            for (int y = 10; y < 40 && !inked; y++)
            {
                inked = bitmap.GetPixel(x, y).ToArgb() != Color.White.ToArgb();
            }
        }

        Assert.False(inked, "text was drawn outside the clip");
    }

    [Fact]
    public void Painting_the_same_paragraph_twice_wraps_it_once()
    {
        Font font = TypeRamp.For(96).Body;
        string text = "Each file is rewritten with a fresh index, and the original goes to quarantine until you empty it.";
        IReadOnlyList<string> first = TextWrap.Lines(text, font, 200);
        Assert.Same(first, TextWrap.Lines(text, font, 200));
        Assert.True(first.Count > 1);
        Assert.Equal(2, TextWrap.Lines(text, font, 200, maxLines: 2).Count);
    }

    [Fact]
    public void A_drop_down_formats_its_text_once_per_change()
    {
        using DropDown drop = new() { DisplayFormat = "Group: {0}" };
        drop.Items.Add("Severity");
        drop.Items.Add("Folder");
        drop.SelectedIndex = 0;
        string shown = drop.ShownText;
        Assert.Equal("Group: Severity", shown);
        Assert.Same(shown, drop.ShownText);
        drop.SelectedIndex = 1;
        Assert.Equal("Group: Folder", drop.ShownText);
    }

    private static int Differing(Bitmap a, Bitmap b)
    {
        int count = 0;
        for (int x = 0; x < a.Width; x++)
        {
            for (int y = 0; y < a.Height; y++)
            {
                count += a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb() ? 1 : 0;
            }
        }

        return count;
    }

    private static bool Near(Color actual, Color expected) =>
        Math.Abs(actual.R - expected.R) + Math.Abs(actual.G - expected.G) + Math.Abs(actual.B - expected.B) < 60;

    private static TextBox Edit(Control box) => box.Controls.OfType<TextBox>().Single();

    private static void KeyDown(Control control, Keys key) => Raise(control, "OnKeyDown", new KeyEventArgs(key));

    private static void KeyUp(Control control, Keys key) => Raise(control, "OnKeyUp", new KeyEventArgs(key));

    /// <summary>Types <paramref name="character"/> into the edit; true when the box rejected it.</summary>
    private static bool Press(TextBox edit, char character)
    {
        KeyPressEventArgs args = new(character);
        Raise(edit, "OnKeyPress", args);
        return args.Handled;
    }

    private static void Raise(Control control, string method, EventArgs args) =>
        typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [args]);

    private static IntPtr LParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    private static void Click(Control control, int down, int up)
    {
        IntPtr point = LParam(control.Width / 2, control.Height / 2);
        _ = SendMessage(control.Handle, down, (IntPtr)(down == WmLButtonDown ? 1 : down == WmRButtonDown ? 2 : 0x10), point);
        _ = SendMessage(control.Handle, up, IntPtr.Zero, point);
        Application.DoEvents();
    }

    /// <summary>A key message as Windows sends it; bit 30 of lParam says the key was already down (auto-repeat).</summary>
    private static void Key(Control control, int message, Keys key, bool repeat)
    {
        long lParam = 1 | (repeat || message == WmKeyUp ? 0x40000000L : 0) | (message == WmKeyUp ? 0x80000000L : 0);
        _ = SendMessage(control.Handle, message, (IntPtr)(int)key, (IntPtr)lParam);
        Application.DoEvents();
    }

    /// <summary>Prints a control as the render harness does (WM_PRINT: client, background and frame).</summary>
    private static Bitmap Print(Control control)
    {
        Bitmap bitmap = new(control.Width, control.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using Graphics g = Graphics.FromImage(bitmap);
        IntPtr hdc = g.GetHdc();
        try
        {
            _ = SendMessage(control.Handle, 0x0317, hdc, (IntPtr)(0x04 | 0x08 | 0x02));
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    private static void DoubleClick(Control control)
    {
        IntPtr point = LParam(control.Width / 4, control.Height / 2);
        _ = SendMessage(control.Handle, WmLButtonDown, (IntPtr)1, point);
        _ = SendMessage(control.Handle, WmLButtonUp, IntPtr.Zero, point);
        _ = SendMessage(control.Handle, WmLButtonDblClk, (IntPtr)1, point);
        _ = SendMessage(control.Handle, WmLButtonUp, IntPtr.Zero, point);
        Application.DoEvents();
    }

    /// <summary>A window far off-screen that is shown without taking activation from whatever the user is doing.</summary>
    private static Form OffscreenForm() =>
        new QuietForm { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(600, 400), ShowInTaskbar = false };

    /// <summary>A kit control that never sets TabStop, like a page's passive view.</summary>
    private sealed class PassiveView : KitControl;

    private sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000;
                return parameters;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
