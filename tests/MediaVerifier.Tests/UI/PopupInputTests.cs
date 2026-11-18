using System.Runtime.InteropServices;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Menus and drop-down lists must take a click without being activated: WinForms activated the popup on
/// WM_MOUSEACTIVATE, the owner window's Deactivate closed the list on the mouse-down, and the click fell through to the
/// window beneath, so no drop-down choice and no menu item ever ran (owner report, v0.1.0.14).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PopupInputTests
{
    private const int WmMouseActivate = 0x0021;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmKeyDown = 0x0100;
    private const int WmMouseWheel = 0x020A;
    private const int MaNoActivate = 3;
    private const int HtClient = 1;

    [Fact]
    public void A_click_on_a_popup_does_not_activate_it()
    {
        IntPtr answer = IntPtr.Zero;
        Sta.Run(() =>
        {
            using Form owner = OffscreenOwner(out Control anchor);
            PopupList list = new([new PopupItem("One"), new PopupItem("Two")], PopupStyle.DropDown, 200, 0);
            list.Open(anchor, anchor.PointToScreen(new Point(0, anchor.Height)));
            Application.DoEvents();
            answer = SendMessage(list.Handle, WmMouseActivate, owner.Handle, (IntPtr)(HtClient | (WmLButtonDown << 16)));
            list.Dismiss();
            Application.DoEvents();
        });

        Assert.Equal(MaNoActivate, (int)answer);
    }

    [Fact]
    public void Clicking_an_item_chooses_it()
    {
        int chosen = -1;
        bool dismissed = false;
        Sta.Run(() =>
        {
            using Form owner = OffscreenOwner(out Control anchor);
            PopupItem[] items = [new("Category"), new("Damage"), new("Integrity")];
            PopupList list = new(items, PopupStyle.DropDown, 200, 0);
            list.Chosen += (_, index) => chosen = index;
            list.Dismissed += (_, _) => dismissed = true;
            list.Open(anchor, anchor.PointToScreen(new Point(0, anchor.Height)));
            Application.DoEvents();

            // The second item's centre, in the popup window's client coordinates (content starts after the shadow pad).
            Point content = list.PointToClient(list.ContentScreenBounds.Location);
            int y = content.Y + Dpi.Scale(4 + 32 + 16, list.PaintDpi);
            int x = content.X + Dpi.Scale(40, list.PaintDpi);
            IntPtr point = (IntPtr)((y << 16) | (x & 0xFFFF));
            _ = SendMessage(list.Handle, WmMouseActivate, owner.Handle, (IntPtr)(HtClient | (WmLButtonDown << 16)));
            _ = SendMessage(list.Handle, WmLButtonDown, (IntPtr)1, point);
            _ = SendMessage(list.Handle, WmLButtonUp, IntPtr.Zero, point);
            Application.DoEvents();
        });

        Assert.True(dismissed, "The list did not close after the click.");
        Assert.Equal(1, chosen);
    }

    /// <summary>
    /// The Issues Rule list has one item per rule with findings (about forty on a real library): taller than a 1080p
    /// screen, its lower items could not be reached. It now shows what fits and scrolls with the keys and the wheel.
    /// </summary>
    [Fact]
    public void A_list_taller_than_the_screen_scrolls()
    {
        bool scrolls = false;
        int contentHeight = 0;
        int firstAfterEnd = -1;
        int hoverAfterEnd = -1;
        int firstAfterHome = -1;
        int firstAfterWheel = -1;
        int firstOpenedOnCurrent = -1;
        Sta.Run(() =>
        {
            using Form owner = OffscreenOwner(out Control anchor);
            PopupItem[] items = [.. Enumerable.Range(0, 40).Select(i => new PopupItem($"Rule {i}"))];
            Point below = anchor.PointToScreen(new Point(0, anchor.Height + 4));
            Rectangle screen = new(below.X - 100, below.Y - 200, 1000, 800);

            PopupList list = new(items, PopupStyle.DropDown, 200, 0);
            list.Open(anchor, below, workingArea: screen);
            Application.DoEvents();
            scrolls = list.Scrolls;
            contentHeight = list.ContentScreenBounds.Height;
            Message end = Message.Create(list.Handle, WmKeyDown, (IntPtr)(int)Keys.End, IntPtr.Zero);
            _ = list.PreFilterMessage(ref end);
            firstAfterEnd = list.FirstVisible;
            hoverAfterEnd = list.HoverIndex;
            Message home = Message.Create(list.Handle, WmKeyDown, (IntPtr)(int)Keys.Home, IntPtr.Zero);
            _ = list.PreFilterMessage(ref home);
            firstAfterHome = list.FirstVisible;
            Point inside = new(list.ContentScreenBounds.X + 20, list.ContentScreenBounds.Y + 20);
            Message wheel = Message.Create(list.Handle, WmMouseWheel, (IntPtr)(-120 << 16), (IntPtr)((inside.Y << 16) | (inside.X & 0xFFFF)));
            _ = list.PreFilterMessage(ref wheel);
            firstAfterWheel = list.FirstVisible;
            list.Dismiss();

            // A drop-down opens with its current item in view.
            PopupList again = new(items, PopupStyle.DropDown, 200, 35);
            again.Open(anchor, below, workingArea: screen);
            Application.DoEvents();
            firstOpenedOnCurrent = again.FirstVisible;
            again.Dismiss();
            Application.DoEvents();
        });

        Assert.True(scrolls, "Forty items in 600 px must scroll.");
        Assert.True(contentHeight <= 600 - 8, $"The list ({contentHeight} px) runs past the bottom of the screen.");
        Assert.Equal(39, hoverAfterEnd);
        Assert.True(firstAfterEnd > 0, "End did not bring the last item into view.");
        Assert.Equal(0, firstAfterHome);
        Assert.Equal(3, firstAfterWheel);
        Assert.True(firstOpenedOnCurrent > 0 && firstOpenedOnCurrent <= 35, $"The current item was not shown (first {firstOpenedOnCurrent}).");
    }

    /// <summary>A scrolled list paints whole items from its first shown one and a scroll thumb at its right edge.</summary>
    [Fact]
    public void A_scrolled_list_paints_its_thumb()
    {
        PopupItem[] items = [.. Enumerable.Range(0, 40).Select(i => new PopupItem($"Rule {i}"))];
        using Bitmap surface = new(220, 8 + (10 * 32));
        using (Graphics g = Graphics.FromImage(surface))
        {
            g.Clear(Color.Magenta);
            PopupList.PaintList(g, new Rectangle(0, 0, surface.Width, surface.Height), items, PopupStyle.DropDown, 12, 13, -1, 10, 96);
        }

        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            surface.Save(Path.Combine(folder, "popup-scrolled.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        // The thumb: 6 px wide, 3 px from the right edge; for item 10 of 30 scrollable positions it sits in the upper half.
        int x = surface.Width - 3 - 3;
        Color background = surface.GetPixel(x, surface.Height - 6);
        bool thumb = Enumerable.Range(4, surface.Height / 2).Any(y => surface.GetPixel(x, y).ToArgb() != background.ToArgb());
        Assert.True(thumb, "No scroll thumb was painted.");
    }

    /// <summary>An owner far off-screen that is shown without taking activation from whatever the user is doing.</summary>
    private static Form OffscreenOwner(out Control anchor)
    {
        Form owner = new QuietForm { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(600, 400), ShowInTaskbar = false };
        anchor = new Panel { Bounds = new Rectangle(20, 20, 160, 32) };
        owner.Controls.Add(anchor);
        owner.Show();
        return owner;
    }

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
