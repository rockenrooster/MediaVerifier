using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// How menus, drop-down lists and the notifications flyout close, and what runs after: the chosen action runs from a
/// posted message once the list has closed, so a dialog it opens gets its keys (it ran inside the app-wide message
/// filter, where the closed list kept swallowing Esc, Enter and Space, and where an exception was dropped silently).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PopupDismissTests
{
    private const int WmKeyDown = 0x0100;

    /// <summary>Schedule's Delete schedule (and Library's Remove…) chosen with the keys opened a confirmation that Esc could not answer.</summary>
    [Fact]
    public void A_menu_item_chosen_with_Enter_opens_a_dialog_that_Esc_answers()
    {
        bool? confirmed = null;
        bool forced = false;
        Sta.Run(() =>
        {
            ScreenRenderer.Offscreen = true;
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            PopupItem[] items = [new("Delete schedule", IconName.Trash)];
            KitMenu.ShowBelow(anchor, items, _ => confirmed = MessageDialog.Confirm(owner, "Delete Weekly?", "The schedule is removed.", "Cancel", "Delete", destructive: true));
            Application.DoEvents();

            using System.Windows.Forms.Timer answer = new() { Interval = 50 };
            Stopwatch? since = null;
            answer.Tick += (_, _) =>
            {
                if (Application.OpenForms.OfType<MessageDialog>().FirstOrDefault(d => d.Visible) is not MessageDialog dialog)
                {
                    return;
                }

                if (since is null)
                {
                    since = Stopwatch.StartNew();
                    _ = PostMessage(dialog.Handle, WmKeyDown, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                }
                else if (since.Elapsed > TimeSpan.FromSeconds(2))
                {
                    forced = true;
                    dialog.DialogResult = DialogResult.Abort;
                }
            };
            answer.Start();

            // Through the thread's message queue, as typed: the list's filter sees them first.
            _ = PostMessage(anchor.Handle, WmKeyDown, (IntPtr)(int)Keys.Down, IntPtr.Zero);
            _ = PostMessage(anchor.Handle, WmKeyDown, (IntPtr)(int)Keys.Enter, IntPtr.Zero);
            Stopwatch limit = Stopwatch.StartNew();
            while (confirmed is null && limit.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }

            answer.Stop();
        });

        Assert.False(forced, "Esc did not reach the dialog the menu item opened.");
        Assert.Equal(false, confirmed);
    }

    /// <summary>A failing keyboard-chosen action (Copy path when the clipboard is busy) was dropped without a log entry.</summary>
    [Fact]
    public void An_exception_from_a_keyboard_chosen_item_reaches_the_error_report()
    {
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            KitMenu.ShowBelow(anchor, [new PopupItem("Copy path", IconName.Copy)], _ => throw new InvalidOperationException("clipboard busy"));
            Application.DoEvents();
            _ = PostMessage(anchor.Handle, WmKeyDown, (IntPtr)(int)Keys.Down, IntPtr.Zero);
            _ = PostMessage(anchor.Handle, WmKeyDown, (IntPtr)(int)Keys.Enter, IntPtr.Zero);
            Pump(TimeSpan.FromMilliseconds(300));
        }));

        Assert.Equal("clipboard busy", thrown.Message);
    }

    /// <summary>
    /// Clicking the drop-down, the Scan chevron or a <c>more</c> button again closed the list on the press and opened a
    /// new one on the release. The press on the anchor now only closes it, and its release is swallowed as well.
    /// </summary>
    [Fact]
    public void Pressing_the_button_that_opened_a_list_only_closes_it()
    {
        bool dismissed = false;
        bool anchorSawPress = false;
        bool anchorSawRelease = false;
        bool openAfter = true;
        bool laterClickReached = false;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out RecordingPanel anchor, new RecordingPanel());
            PopupList list = new([new PopupItem("Category"), new PopupItem("Damage")], PopupStyle.DropDown, 200, 0);
            list.Dismissed += (_, _) => dismissed = true;
            list.Open(anchor, anchor.PointToScreen(new Point(0, anchor.Height + 4)));
            Application.DoEvents();

            IntPtr inside = (IntPtr)((10 << 16) | 20);
            _ = PostMessage(anchor.Handle, WmLButtonDown, (IntPtr)1, inside);
            Application.DoEvents();
            _ = PostMessage(anchor.Handle, WmLButtonUp, IntPtr.Zero, inside);
            Application.DoEvents();
            openAfter = list is { IsDisposed: false, Visible: true };
            anchorSawPress = anchor.Downs > 0;
            anchorSawRelease = anchor.Ups > 0;

            // The next click is the user's again.
            _ = PostMessage(anchor.Handle, WmLButtonDown, (IntPtr)1, inside);
            _ = PostMessage(anchor.Handle, WmLButtonUp, IntPtr.Zero, inside);
            Application.DoEvents();
            laterClickReached = anchor.Downs == 1 && anchor.Ups == 1;
        });

        Assert.True(dismissed, "The list stayed open.");
        Assert.False(openAfter, "A list was open after the click.");
        Assert.False(anchorSawPress, "The press that closed the list reached the drop-down.");
        Assert.False(anchorSawRelease, "The release of the press that closed the list reached the drop-down (it opens on the release).");
        Assert.True(laterClickReached, "A later click did not reach the button.");
    }

    /// <summary>A press anywhere else closes the list and still reaches what it was aimed at.</summary>
    [Fact]
    public void A_press_elsewhere_closes_the_list_and_goes_on()
    {
        bool swallowed = true;
        bool dismissed = false;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            PopupList list = new([new PopupItem("One"), new PopupItem("Two")], PopupStyle.DropDown, 200, 0);
            list.Dismissed += (_, _) => dismissed = true;
            list.Open(anchor, anchor.PointToScreen(new Point(0, anchor.Height + 4)));
            Application.DoEvents();
            Message press = Message.Create(owner.Handle, WmLButtonDown, (IntPtr)1, (IntPtr)((300 << 16) | 400));
            swallowed = list.PreFilterMessage(ref press);
            Application.DoEvents();
        });

        Assert.True(dismissed);
        Assert.False(swallowed, "A press outside the list must go on to its target.");
    }

    /// <summary>
    /// The shadow is part of the popup window: it took clicks meant for the lower part of a drop-down and for Pause and
    /// Stop under a toast. Outside the content the window is transparent to the mouse.
    /// </summary>
    [Fact]
    public void The_shadow_lets_clicks_through()
    {
        int onShadow = 0;
        int onContent = 0;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            PopupList list = new([new PopupItem("One"), new PopupItem("Two")], PopupStyle.DropDown, 200, 0);
            list.Open(anchor, anchor.PointToScreen(new Point(0, anchor.Height + 4)));
            Application.DoEvents();
            Rectangle content = list.ContentScreenBounds;
            onShadow = (int)SendMessage(list.Handle, WmNcHitTest, IntPtr.Zero, Pack(new Point(content.X + 20, content.Y - 6)));
            onContent = (int)SendMessage(list.Handle, WmNcHitTest, IntPtr.Zero, Pack(new Point(content.X + 20, content.Y + 10)));
            list.Dismiss();
            Application.DoEvents();
        });

        Assert.Equal(-1, onShadow);
        Assert.Equal(1, onContent);
    }

    /// <summary>F4 and Alt+Down close an open drop-down list (Alt alone no longer closed it before Down reopened it).</summary>
    [Fact]
    public void F4_and_Alt_Down_close_the_list_and_Alt_alone_waits()
    {
        bool f4Swallowed = false;
        bool f4Closed = false;
        bool altPassed = false;
        bool openAfterAlt = false;
        bool altDownSwallowed = false;
        bool altDownClosed = false;
        bool altF4Passed = false;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            PopupItem[] items = [new("One"), new("Two")];
            Point below = anchor.PointToScreen(new Point(0, anchor.Height + 4));

            PopupList first = new(items, PopupStyle.DropDown, 200, 0);
            first.Open(anchor, below);
            Message f4 = Message.Create(anchor.Handle, WmKeyDown, (IntPtr)(int)Keys.F4, IntPtr.Zero);
            f4Swallowed = first.PreFilterMessage(ref f4);
            f4Closed = !first.Visible;

            PopupList second = new(items, PopupStyle.DropDown, 200, 0);
            second.Open(anchor, below);
            Message alt = Message.Create(anchor.Handle, WmSysKeyDown, (IntPtr)(int)Keys.Menu, IntPtr.Zero);
            altPassed = !second.PreFilterMessage(ref alt);
            openAfterAlt = second.Visible;
            Message altDown = Message.Create(anchor.Handle, WmSysKeyDown, (IntPtr)(int)Keys.Down, IntPtr.Zero);
            altDownSwallowed = second.PreFilterMessage(ref altDown);
            altDownClosed = !second.Visible;

            PopupList third = new(items, PopupStyle.DropDown, 200, 0);
            third.Open(anchor, below);
            Message altF4 = Message.Create(anchor.Handle, WmSysKeyDown, (IntPtr)(int)Keys.F4, IntPtr.Zero);
            altF4Passed = !third.PreFilterMessage(ref altF4) && !third.Visible;
            Application.DoEvents();
        });

        Assert.True(f4Swallowed && f4Closed, "F4 did not close the list.");
        Assert.True(altPassed && openAfterAlt, "Alt alone closed the list.");
        Assert.True(altDownSwallowed && altDownClosed, "Alt+Down did not close the list (or went on to reopen it).");
        Assert.True(altF4Passed, "Alt+F4 must close the list and still reach the window.");
    }

    /// <summary>WinForms keeps asking the filters it started a message with: a closed list must let everything through.</summary>
    [Fact]
    public void A_closed_list_lets_every_message_through()
    {
        bool[] swallowed = [];
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            PopupList list = new([new PopupItem("One")], PopupStyle.DropDown, 200, 0);
            list.Open(anchor, anchor.PointToScreen(new Point(0, anchor.Height + 4)));
            list.Dismiss();
            int[] keys = [(int)Keys.Escape, (int)Keys.Enter, (int)Keys.Space, (int)Keys.Down, (int)Keys.A];
            swallowed = [.. keys.Select(key =>
            {
                Message m = Message.Create(anchor.Handle, WmKeyDown, (IntPtr)key, IntPtr.Zero);
                return list.PreFilterMessage(ref m);
            })];
            Application.DoEvents();
        });

        Assert.All(swallowed, Assert.False);
    }

    /// <summary>After a wheel scroll the item under the pointer is highlighted, not the one that was there before.</summary>
    [Fact]
    public void After_a_wheel_scroll_the_item_under_the_pointer_is_highlighted()
    {
        int hoverBefore = -1;
        int hoverAfter = -1;
        int firstAfter = -1;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            PopupItem[] items = [.. Enumerable.Range(0, 40).Select(i => new PopupItem($"Rule {i}"))];
            Point below = anchor.PointToScreen(new Point(0, anchor.Height + 4));
            PopupList list = new(items, PopupStyle.DropDown, 200, 0);
            list.Open(anchor, below, workingArea: new Rectangle(below.X - 100, below.Y - 200, 1000, 800));
            Application.DoEvents();
            hoverBefore = list.HoverIndex;

            // The pointer rests on the third shown row (4 px padding, 32 px rows).
            Rectangle content = list.ContentScreenBounds;
            Point pointer = new(content.X + 30, content.Y + Dpi.Scale(4 + (2 * 32) + 16, list.PaintDpi));
            Message wheel = Message.Create(list.Handle, WmMouseWheel, (IntPtr)(-120 << 16), Pack(pointer));
            _ = list.PreFilterMessage(ref wheel);
            hoverAfter = list.HoverIndex;
            firstAfter = list.FirstVisible;
            list.Dismiss();
            Application.DoEvents();
        });

        Assert.Equal(0, hoverBefore);
        Assert.Equal(firstAfter + 2, hoverAfter);
    }

    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMouseWheel = 0x020A;
    private const int WmNcHitTest = 0x0084;

    private static IntPtr Pack(Point p) => (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>An anchor that counts the presses and releases that reach it.</summary>
    private sealed class RecordingPanel : Panel
    {
        public int Downs { get; private set; }

        public int Ups { get; private set; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Downs++;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Ups++;
        }
    }
}

/// <summary>Off-screen windows for pop-up tests: shown without taking activation from the user's window.</summary>
internal static class PopupTestWindows
{
    /// <summary>An owner far off-screen with one anchor control (a drop-down or menu button stand-in).</summary>
    public static Form Owner(out Control anchor) => Owner(out anchor, new Panel());

    public static Form Owner<T>(out T anchor, T control)
        where T : Control
    {
        Form owner = new QuietForm { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(700, 500), ShowInTaskbar = false };
        control.Bounds = new Rectangle(20, 20, 160, 32);
        owner.Controls.Add(control);
        owner.Show();
        anchor = control;
        return owner;
    }

    internal sealed class QuietForm : Form
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
}
