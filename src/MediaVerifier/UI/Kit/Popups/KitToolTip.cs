using System.Runtime.CompilerServices;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Tooltips (subplan 08, Tooltip): 26 px high, radius 4, Raised with a Stroke border and shadow, 8 px
/// padding, Caption text. Appears 500 ms after the pointer rests on a control, 8 px below the pointer,
/// and hides on leave, click or key press.
/// <list type="bullet">
/// <item>A disabled control gets no mouse input (Windows gives it to the parent), so the tip that says why it is
/// disabled is armed from the parent's mouse moves.</item>
/// <item><see cref="Track"/> serves controls whose tip depends on what is under the pointer: a cell cut with an
/// ellipsis (subplan 08: the full value shows in a tooltip), a severity-bar segment.</item>
/// <item>One tip window serves the app. It has no owner and stays above every window (as Disk Monitor 2.0's): owned by
/// a dialog, it was disposed with the dialog, and every later tip threw.</item>
/// </list>
/// </summary>
internal static class KitToolTip
{
    public const int Height = 26;
    private static readonly ConditionalWeakTable<Control, Entry> Entries = [];
    private static readonly ConditionalWeakTable<Control, object> HookedParents = [];
    private static readonly object Hooked = new();
    private static readonly System.Windows.Forms.Timer Delay = new() { Interval = 500 };
    private static TipWindow? s_window;
    private static Control? s_pending;

    static KitToolTip()
    {
        Delay.Tick += (_, _) =>
        {
            Delay.Stop();
            if (s_pending is { IsDisposed: false } control && Entries.TryGetValue(control, out Entry? entry) && entry.Text.Length > 0)
            {
                ShowAt(control, entry.Text, Cursor.Position);
            }
        };
    }

    /// <summary>The control whose tip is waiting for its delay or showing (tests).</summary>
    internal static Control? Pending => s_pending;

    /// <summary>The shared tip window, once a tip has shown (tests).</summary>
    internal static Form? Window => s_window;

    /// <summary>Sets (or clears, with an empty string) the tooltip of <paramref name="control"/>.</summary>
    public static void Set(Control control, string text)
    {
        ArgumentNullException.ThrowIfNull(control);
        EntryFor(control).Text = text ?? string.Empty;
    }

    /// <summary>Text currently set for a control (empty when none).</summary>
    public static string Get(Control control) => Entries.TryGetValue(control, out Entry? entry) ? entry.Text : string.Empty;

    /// <summary>
    /// For a control whose tip depends on what is under the pointer: call it from the control's MouseMove with the text
    /// for that spot. The same text again changes nothing (a waiting or shown tip stays); new text hides the tip and
    /// starts the 500 ms delay again; an empty text hides it.
    /// </summary>
    public static void Track(Control control, string text)
    {
        ArgumentNullException.ThrowIfNull(control);
        Entry entry = EntryFor(control);
        entry.Tracked = true;
        text ??= string.Empty;
        if (text == entry.Text && (text.Length == 0 || s_pending == control))
        {
            return;
        }

        entry.Text = text;
        if (text.Length == 0)
        {
            if (s_pending == control)
            {
                Hide();
            }

            return;
        }

        Hide();
        Arm(control);
    }

    /// <summary>
    /// <see cref="Track"/> with the full <paramref name="text"/> when it is wider than <paramref name="width"/> device
    /// pixels in <paramref name="font"/> (it is drawn cut with an ellipsis), and nothing otherwise.
    /// </summary>
    public static void TrackCut(Control control, string text, Font font, int width) =>
        Track(control, !string.IsNullOrEmpty(text) && KitPaint.MeasureWidth(text, font) > width ? text : string.Empty);

    /// <summary>Logical width of a tooltip: 8 px padding around Caption text.</summary>
    public static int MeasureWidth(string text, int dpi) =>
        (int)Math.Round(16 + (KitPaint.MeasureWidth(text, TypeRamp.For(dpi).Caption) / (dpi / 96f)), MidpointRounding.AwayFromZero);

    /// <summary>Paints the tooltip's surface and text (the shadow is the caller's).</summary>
    public static void Paint(Graphics g, Rectangle bounds, string text, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(4, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        KitPaint.DrawText(g, text, TypeRamp.For(dpi).Caption, t.Text, bounds.X + Dpi.Scale(8, dpi), bounds.Y + (bounds.Height / 2), bounds.Width - Dpi.Scale(16, dpi));
    }

    public static void Hide()
    {
        Delay.Stop();
        s_pending = null;
        if (UsableWindow() is TipWindow window)
        {
            window.Hide();
        }
    }

    /// <summary>Shows the tip of <paramref name="control"/> as if the pointer rested at <paramref name="pointer"/> (tests pass a point off-screen).</summary>
    internal static void ShowNow(Control control, Point pointer)
    {
        if (Entries.TryGetValue(control, out Entry? entry) && entry.Text.Length > 0)
        {
            s_pending = control;
            ShowAt(control, entry.Text, pointer);
        }
    }

    private static Entry EntryFor(Control control)
    {
        if (!Entries.TryGetValue(control, out Entry? entry))
        {
            entry = new Entry();
            Entries.Add(control, entry);
            control.MouseEnter += OnEnter;
            control.MouseLeave += OnHide;
            control.MouseDown += OnHide;
            control.KeyDown += OnHide;
            control.Disposed += OnHide;
            control.ParentChanged += OnParentChanged;
            HookParent(control.Parent);
        }

        return entry;
    }

    private static void Arm(Control control)
    {
        s_pending = control;
        Delay.Stop();
        Delay.Start();
    }

    private static void OnEnter(object? sender, EventArgs e)
    {
        // A tracked control arms from its MouseMove, with the text for the spot under the pointer.
        if (sender is Control control && Entries.TryGetValue(control, out Entry? entry) && !entry.Tracked)
        {
            Arm(control);
        }
    }

    private static void OnHide(object? sender, EventArgs e) => Hide();

    private static void OnParentChanged(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            HookParent(control.Parent);
        }
    }

    /// <summary>Watches a parent's mouse once, for the disabled children among its controls with a tip.</summary>
    private static void HookParent(Control? parent)
    {
        if (parent is null || HookedParents.TryGetValue(parent, out _))
        {
            return;
        }

        HookedParents.Add(parent, Hooked);
        parent.MouseMove += OnParentMouseMove;
        parent.MouseLeave += OnParentLeaveOrPress;
        parent.MouseDown += OnParentLeaveOrPress;
    }

    private static void OnParentMouseMove(object? sender, MouseEventArgs e)
    {
        if (sender is not Control parent)
        {
            return;
        }

        if (DisabledChildAt(parent, e.Location) is Control disabled)
        {
            if (s_pending != disabled)
            {
                Hide();
                Arm(disabled);
            }
        }
        else if (s_pending is { Enabled: false } pending && pending.Parent == parent)
        {
            Hide();
        }
    }

    private static void OnParentLeaveOrPress(object? sender, EventArgs e)
    {
        if (sender is Control parent && s_pending is { Enabled: false } pending && pending.Parent == parent)
        {
            Hide();
        }
    }

    private static Control? DisabledChildAt(Control parent, Point location)
    {
        Control.ControlCollection children = parent.Controls;
        for (int i = 0; i < children.Count; i++)
        {
            Control child = children[i];
            if (!child.Enabled && child.Visible && child.Bounds.Contains(location) && Entries.TryGetValue(child, out Entry? entry) && entry.Text.Length > 0)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>The tip window when it can be used on this thread (the tests run UI on several threads).</summary>
    private static TipWindow? UsableWindow() =>
        s_window is { IsDisposed: false } window && !(window.IsHandleCreated && window.InvokeRequired) ? window : null;

    private static void ShowAt(Control control, string text, Point pointer)
    {
        if (!control.IsHandleCreated || !control.Visible)
        {
            return;
        }

        TipWindow window = UsableWindow() ?? (s_window = new TipWindow());
        window.Text = text;
        window.PaintDpi = control.DeviceDpi;
        int below = Dpi.Scale(16 + 8, control.DeviceDpi);
        Rectangle screen = Screen.FromPoint(pointer).WorkingArea;
        Size size = window.Measure();
        int x = Math.Min(pointer.X, screen.Right - size.Width);
        int y = pointer.Y + below;
        if (y + size.Height > screen.Bottom)
        {
            y = pointer.Y - size.Height - Dpi.Scale(8, control.DeviceDpi);
        }

        window.ShowAt(new Point(Math.Max(screen.Left, x), y));
    }

    private sealed class Entry
    {
        public string Text { get; set; } = string.Empty;

        /// <summary>Set through <see cref="Track"/>: the text follows the pointer.</summary>
        public bool Tracked { get; set; }
    }

    private sealed class TipWindow : LayeredPopup
    {
        protected override int Radius => 4;

        protected override Size ContentSize => Measure();

        /// <summary>The pointer stays on the control the tip explains, even where the tip covers it.</summary>
        protected override bool ClickThrough => true;

        // KitToolTip.Height: unqualified, Height is this window's own height, which grew each time the tip was painted.
        public Size Measure() => new(Dpi.Scale(MeasureWidth(Text, PaintDpi), PaintDpi), Dpi.Scale(KitToolTip.Height, PaintDpi));

        protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => KitToolTip.Paint(g, bounds, Text, dpi);
    }
}
