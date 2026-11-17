using System.Runtime.CompilerServices;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Tooltips (subplan 08, Tooltip): 26 px high, radius 4, Raised with a Stroke border and shadow, 8 px
/// padding, Caption text. Appears 500 ms after the pointer rests on a control, 8 px below the pointer,
/// and hides on leave, click or key press.
/// </summary>
internal static class KitToolTip
{
    public const int Height = 26;
    private static readonly ConditionalWeakTable<Control, Entry> Entries = [];
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
                ShowFor(control, entry.Text);
            }
        };
    }

    /// <summary>Sets (or clears, with an empty string) the tooltip of <paramref name="control"/>.</summary>
    public static void Set(Control control, string text)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!Entries.TryGetValue(control, out Entry? entry))
        {
            entry = new Entry();
            Entries.Add(control, entry);
            control.MouseEnter += OnEnter;
            control.MouseLeave += OnHide;
            control.MouseDown += OnHide;
            control.KeyDown += OnHide;
            control.Disposed += OnHide;
        }

        entry.Text = text ?? string.Empty;
    }

    /// <summary>Text currently set for a control (empty when none).</summary>
    public static string Get(Control control) => Entries.TryGetValue(control, out Entry? entry) ? entry.Text : string.Empty;

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
        s_window?.Hide();
    }

    private static void OnEnter(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            s_pending = control;
            Delay.Stop();
            Delay.Start();
        }
    }

    private static void OnHide(object? sender, EventArgs e) => Hide();

    private static void ShowFor(Control control, string text)
    {
        if (!control.IsHandleCreated || !control.Visible)
        {
            return;
        }

        s_window ??= new TipWindow();
        s_window.Text = text;
        s_window.PaintDpi = control.DeviceDpi;
        Point cursor = Cursor.Position;
        int below = Dpi.Scale(16 + 8, control.DeviceDpi);
        Rectangle screen = Screen.FromPoint(cursor).WorkingArea;
        Size size = s_window.Measure();
        int x = Math.Min(cursor.X, screen.Right - size.Width);
        int y = cursor.Y + below;
        if (y + size.Height > screen.Bottom)
        {
            y = cursor.Y - size.Height - Dpi.Scale(8, control.DeviceDpi);
        }

        s_window.ShowAt(new Point(Math.Max(screen.Left, x), y), control.FindForm());
    }

    private sealed class Entry
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class TipWindow : LayeredPopup
    {
        protected override int Radius => 4;

        protected override Size ContentSize => Measure();

        // KitToolTip.Height: unqualified, Height is this window's own height, which grew each time the tip was painted.
        public Size Measure() => new(Dpi.Scale(MeasureWidth(Text, PaintDpi), PaintDpi), Dpi.Scale(KitToolTip.Height, PaintDpi));

        protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => KitToolTip.Paint(g, bounds, Text, dpi);
    }
}
