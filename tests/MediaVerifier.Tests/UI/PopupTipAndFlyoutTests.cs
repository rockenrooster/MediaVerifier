using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Shell;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>Tooltips (the shared tip window, disabled controls, tips that follow the pointer) and the notifications flyout.</summary>
[Collection(UiCollection.Name)]
public sealed class PopupTipAndFlyoutTests
{
    /// <summary>Tips are shown here as if the pointer rested far off-screen, so nothing appears on the user's screen.</summary>
    private static readonly Point Offscreen = new(-32000, -32000);

    /// <summary>
    /// The tip window was owned by the form of the control it explained: a dialog's Close (Esc) tip was disposed with the
    /// dialog, and every later tip in the app threw ObjectDisposedException ("Something went wrong").
    /// </summary>
    [Fact]
    public void The_shared_tip_outlives_the_dialog_it_was_shown_over()
    {
        Form? tipOwner = null;
        bool aliveAfterDialog = false;
        bool shownAfterDialog = false;
        bool shownAfterDispose = false;
        Sta.Run(() =>
        {
            using Form main = PopupTestWindows.Owner(out Control bell);
            KitToolTip.Set(bell, "Notifications");
            Form dialog = PopupTestWindows.Owner(out Control close);
            KitToolTip.Set(close, "Close (Esc)");
            KitToolTip.ShowNow(close, Offscreen);
            Form tip = KitToolTip.Window!;
            tipOwner = tip.Owner;
            KitToolTip.Hide();
            dialog.Dispose();
            aliveAfterDialog = !tip.IsDisposed;

            KitToolTip.ShowNow(bell, Offscreen);
            shownAfterDialog = KitToolTip.Window is { IsDisposed: false, Visible: true };
            KitToolTip.Hide();

            // Even a tip window that is gone some other way is made again.
            KitToolTip.Window!.Dispose();
            KitToolTip.ShowNow(bell, Offscreen);
            shownAfterDispose = KitToolTip.Window is { IsDisposed: false, Visible: true };
            KitToolTip.Hide();
        });

        Assert.Null(tipOwner);
        Assert.True(aliveAfterDialog, "Closing the dialog disposed the shared tip window.");
        Assert.True(shownAfterDialog, "No tip showed after the dialog closed.");
        Assert.True(shownAfterDispose, "A disposed tip window was not made again.");
    }

    /// <summary>
    /// A disabled control gets no mouse input, so its MouseEnter never fired and the reason it is disabled (Scan: "Add a
    /// folder first"; Fix…: "These problems need different fixes") never showed. The parent's mouse moves arm it.
    /// </summary>
    [Fact]
    public void The_reason_a_button_is_disabled_is_armed_from_its_parent()
    {
        Control? overButton = null;
        Control? besideButton = null;
        Control? afterLeave = null;
        Control? button = null;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out TestParent parent, new TestParent());
            Button scan = new() { Bounds = new Rectangle(10, 4, 60, 24), Enabled = false };
            button = scan;

            // Set before the button has a parent, as icon buttons set theirs in their initialisers.
            KitToolTip.Set(scan, "Add a folder first");
            parent.Controls.Add(scan);
            parent.PointerAt(new Point(20, 10));
            overButton = KitToolTip.Pending;
            parent.PointerAt(new Point(120, 10));
            besideButton = KitToolTip.Pending;
            parent.PointerAt(new Point(20, 10));
            parent.PointerLeft();
            afterLeave = KitToolTip.Pending;
            KitToolTip.Hide();
        });

        Assert.Same(button, overButton);
        Assert.Null(besideButton);
        Assert.Null(afterLeave);
    }

    /// <summary>Track serves tips that depend on the spot under the pointer (cut text, bar segments).</summary>
    [Fact]
    public void A_tracked_tip_follows_the_text_under_the_pointer()
    {
        Control? cut = null;
        Control? notCut = null;
        Control? cleared = null;
        Control? sameAgain = null;
        Control? cell = null;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control list);
            cell = list;
            Font font = TypeRamp.For(96).Body;
            const string path = @"D:\Music\Night Drive\Overpass\Night Drive - 07 - Overpass.flac";
            KitToolTip.TrackCut(list, path, font, 40);
            cut = KitToolTip.Pending;
            KitToolTip.Track(list, path);
            sameAgain = KitToolTip.Pending;
            KitToolTip.TrackCut(list, "Overpass", font, 400);
            notCut = KitToolTip.Pending;
            KitToolTip.Track(list, "Medium: 3");
            KitToolTip.Track(list, string.Empty);
            cleared = KitToolTip.Pending;
            KitToolTip.Hide();
        });

        Assert.Same(cell, cut);
        Assert.Same(cell, sameAgain);
        Assert.Null(notCut);
        Assert.Null(cleared);
    }

    /// <summary>The flyout could only be used with the mouse: its items, Mark all read and Clear all now take the keys.</summary>
    [Fact]
    public void The_flyout_is_used_from_the_keyboard()
    {
        NotificationRecord? clicked = null;
        bool markedRead = false;
        int hotAfterDown = int.MinValue;
        int hotAfterHome = int.MinValue;
        int hotAfterEnd = int.MinValue;
        bool enterSwallowed = false;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control bell);
            NotificationRecord[] items = Notices(3);
            NotificationsFlyout flyout = NotificationsFlyout.Show(bell, items, DateTimeOffset.UtcNow);
            flyout.ItemClicked += (_, item) => clicked = item;
            Key(flyout, bell, Keys.Down);
            Key(flyout, bell, Keys.Down);
            hotAfterDown = flyout.Hot;
            Key(flyout, bell, Keys.Home);
            hotAfterHome = flyout.Hot;
            Key(flyout, bell, Keys.End);
            hotAfterEnd = flyout.Hot;
            Key(flyout, bell, Keys.Up);
            enterSwallowed = Key(flyout, bell, Keys.Enter);
            Pump();

            NotificationsFlyout again = NotificationsFlyout.Show(bell, items, DateTimeOffset.UtcNow);
            again.MarkAllRead += (_, _) => markedRead = true;
            Key(again, bell, Keys.Home);
            Key(again, bell, Keys.Space);
            Pump();
        });

        Assert.Equal(1, hotAfterDown);
        Assert.Equal(NotificationsFlyout.HotMark, hotAfterHome);
        Assert.Equal(NotificationsFlyout.HotClear, hotAfterEnd);
        Assert.True(enterSwallowed, "Enter went on to the bell, which opened a second flyout.");
        Assert.Equal(3, clicked?.Id);
        Assert.True(markedRead, "Space on Mark all read did nothing.");
    }

    /// <summary>
    /// Clicking the bell again closed the flyout on the press and opened a new one on the release (after another database
    /// read); and a second flyout could open over the first.
    /// </summary>
    [Fact]
    public void The_bell_closes_its_flyout_and_only_one_is_open()
    {
        bool pressSwallowed = false;
        bool firstOpenAfterSecond = true;
        bool secondOpenAfterBell = true;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control bell);
            NotificationRecord[] items = Notices(2);
            NotificationsFlyout first = NotificationsFlyout.Show(bell, items, DateTimeOffset.UtcNow);
            NotificationsFlyout second = NotificationsFlyout.Show(bell, items, DateTimeOffset.UtcNow);
            firstOpenAfterSecond = first is { IsDisposed: false, Visible: true };
            Message press = Message.Create(bell.Handle, 0x0201, (IntPtr)1, (IntPtr)((10 << 16) | 10));
            pressSwallowed = second.PreFilterMessage(ref press);
            secondOpenAfterBell = second.Visible;
            Pump();
        });

        Assert.False(firstOpenAfterSecond, "Two flyouts were open at once.");
        Assert.True(pressSwallowed, "The press on the bell went on and would open the flyout again.");
        Assert.False(secondOpenAfterBell);
    }

    /// <summary>Only six of up to fifty notices showed, with no way to reach the others; the list now scrolls.</summary>
    [Fact]
    public void Older_notices_scroll_into_view()
    {
        int firstAfterEnd = -1;
        int firstAfterWheel = -1;
        int hotAfterUp = int.MinValue;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control bell);
            NotificationsFlyout flyout = NotificationsFlyout.Show(bell, Notices(20), DateTimeOffset.UtcNow);
            Key(flyout, bell, Keys.End);
            Key(flyout, bell, Keys.Up);
            hotAfterUp = flyout.Hot;
            firstAfterEnd = flyout.FirstVisible;
            Rectangle content = flyout.ContentScreenBounds;
            Point inside = new(content.X + 40, content.Y + 120);
            Message wheel = Message.Create(flyout.Handle, 0x020A, (IntPtr)(120 << 16), (IntPtr)((inside.Y << 16) | (inside.X & 0xFFFF)));
            _ = flyout.PreFilterMessage(ref wheel);
            firstAfterWheel = flyout.FirstVisible;
            flyout.Dismiss();
            Pump();
        });

        Assert.Equal(19, hotAfterUp);
        Assert.Equal(20 - NotificationsFlyout.MaxItems, firstAfterEnd);
        Assert.Equal(20 - NotificationsFlyout.MaxItems - 1, firstAfterWheel);
    }

    /// <summary>The flyout with more notices than fit paints a scroll thumb (saved with MEDIAVERIFIER_SAVE_RENDERS).</summary>
    [Fact]
    public void A_scrolled_flyout_paints_its_thumb()
    {
        NotificationRecord[] items = Notices(9);
        Size size = new(NotificationsFlyout.LogicalWidth, NotificationsFlyout.LogicalHeight(items.Length));
        using Bitmap surface = new(size.Width, size.Height);
        ThemeTokens original = ThemeService.Tokens;
        try
        {
            ThemeService.Set(ThemeTokens.Dark);
            using (Graphics g = Graphics.FromImage(surface))
            {
                g.Clear(ThemeTokens.Dark.Raised);
                NotificationsFlyout.Draw(g, new Rectangle(Point.Empty, size), items, DateTimeOffset.UtcNow, 3, 4, keyboard: false, 96);
            }
        }
        finally
        {
            ThemeService.Set(original);
        }

        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            surface.Save(Path.Combine(folder, "notifications-scrolled.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        // The thumb sits 3 px from the right edge, in the item area (56 px header, six 68 px rows).
        int x = size.Width - 3 - 3;
        int background = surface.GetPixel(x, 50).ToArgb();
        Assert.Contains(Enumerable.Range(60, 6 * 68 - 8), y => surface.GetPixel(x, y).ToArgb() != background);
    }

    private static NotificationRecord[] Notices(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new NotificationRecord
        {
            Id = i + 1,
            Utc = DateTime.UtcNow.AddMinutes(-i).Ticks,
            Level = i % 2 == 0 ? Severity.High : Severity.None,
            Title = $"Notice {i + 1}",
            Body = "Scan finished.",
        })];

    private static bool Key(TransientPopup popup, Control target, Keys key)
    {
        Message m = Message.Create(target.Handle, 0x0100, (IntPtr)(int)key, IntPtr.Zero);
        return popup.PreFilterMessage(ref m);
    }

    private static void Pump()
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 100)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>A parent whose mouse moves a test can play (a disabled child's mouse input goes to its parent).</summary>
    private sealed class TestParent : Panel
    {
        public void PointerAt(Point location) => OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, location.X, location.Y, 0));

        public void PointerLeft() => OnMouseLeave(EventArgs.Empty);
    }
}
