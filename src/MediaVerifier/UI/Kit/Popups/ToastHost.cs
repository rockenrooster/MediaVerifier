using System.Drawing.Drawing2D;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit.Popups;

/// <summary>One toast (subplan 08, Toast; subplan 09, Dialogs D).</summary>
internal sealed record ToastContent(Severity Level, string Title, string Text, string? ActionText = null, Action? Action = null);

/// <summary>
/// Toasts: 360 × 64, radius 8, Raised, Stroke, shadow, 16 px from the window's bottom-right corner (above
/// the status strip), stacked upward 8 px apart, at most three. A 3 × 48 bar in the level's Fill, a 16 px
/// shape icon at (16, 14), title in BodyStrong at +44 centred on y 22, text in Caption Text2 centred on
/// y 43, an optional compact button and a compact close button at the right. Fades in over 150 ms, stays
/// 5 s (paused while hovered), fades out over 150 ms; without animation effects it appears and goes at once.
/// </summary>
internal sealed class ToastHost : IDisposable
{
    public const int Width = 360;
    public const int Height = 64;
    private const int MaxToasts = 3;
    private readonly Form _owner;
    private readonly Func<int> _bottomInset;
    private readonly List<ToastWindow> _toasts = [];

    /// <param name="owner">The main window.</param>
    /// <param name="bottomInset">Logical pixels kept free at the bottom (the status strip while it shows).</param>
    public ToastHost(Form owner, Func<int> bottomInset)
    {
        _owner = owner;
        _bottomInset = bottomInset;
        owner.Move += (_, _) => Reposition();
        owner.Resize += (_, _) => Reposition();
    }

    public void Show(ToastContent content)
    {
        if (_toasts.Count >= MaxToasts)
        {
            _toasts[0].Dismiss();
        }

        ToastWindow toast = new(content, this) { PaintDpi = _owner.DeviceDpi };
        _toasts.Add(toast);
        Reposition();
        toast.Start(_owner);
    }

    public void Dispose()
    {
        foreach (ToastWindow toast in _toasts.ToArray())
        {
            toast.Dispose();
        }

        _toasts.Clear();
    }

    /// <summary>Paints a toast's surface and contents (harness and window share it).</summary>
    public static void Draw(Graphics g, Rectangle bounds, ToastContent content, bool actionHover, bool closeHover, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        TierColors tier = t.ForSeverity(content.Level);
        float radius = Dpi.ScaleF(8, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        KitPaint.FillRoundedRect(g, new RectangleF(bounds.X, bounds.Y + Dpi.ScaleF(8, dpi), Dpi.ScaleF(3, dpi), Dpi.ScaleF(48, dpi)), Dpi.ScaleF(1.5f, dpi), tier.Fill);
        IconRenderer.DrawSeverity(g, content.Level, bounds.X + Dpi.ScaleF(16, dpi), bounds.Y + Dpi.ScaleF(14, dpi), 16, dpi, tier.Text, t.Raised);
        (Rectangle action, Rectangle close) = ButtonBounds(bounds, content, dpi);
        int textRight = (content.ActionText is null ? close.X : action.X) - Dpi.Scale(8, dpi);
        int x = bounds.X + Dpi.Scale(44, dpi);
        KitPaint.DrawText(g, content.Title, type.BodyStrong, t.Text, x, bounds.Y + Dpi.Scale(22, dpi), textRight - x);
        KitPaint.DrawText(g, content.Text, type.Caption, t.Text2, x, bounds.Y + Dpi.Scale(43, dpi), textRight - x);
        if (content.ActionText is not null)
        {
            ButtonPainter.Paint(g, action, ButtonKind.Secondary, actionHover ? VisualState.Hover : VisualState.Rest, content.ActionText, null, false, dpi);
        }

        IconButton.Draw(g, close, IconName.Close, closeHover ? VisualState.Hover : VisualState.Rest, 0, dpi);
    }

    /// <summary>The action button and close button rectangles inside a toast.</summary>
    public static (Rectangle Action, Rectangle Close) ButtonBounds(Rectangle bounds, ToastContent content, int dpi)
    {
        int size = Dpi.Scale(28, dpi);
        int y = bounds.Y + ((bounds.Height - size) / 2);
        Rectangle close = new(bounds.Right - Dpi.Scale(12, dpi) - size, y, size, size);
        if (content.ActionText is null)
        {
            return (Rectangle.Empty, close);
        }

        int width = Dpi.Scale(ButtonPainter.MeasureWidth(content.ActionText, null, false, ButtonKind.Secondary, dpi), dpi);
        width = Math.Max(Dpi.Scale(52, dpi), width - Dpi.Scale(12, dpi));
        return (new Rectangle(close.X - Dpi.Scale(4, dpi) - width, y, width, size), close);
    }

    private void Remove(ToastWindow toast)
    {
        _toasts.Remove(toast);
        Reposition();
    }

    private void Reposition()
    {
        if (_owner.IsDisposed)
        {
            return;
        }

        int dpi = _owner.DeviceDpi;
        Rectangle client = _owner.RectangleToScreen(_owner.ClientRectangle);
        int x = client.Right - Dpi.Scale(16 + Width, dpi);
        int y = client.Bottom - Dpi.Scale(16 + _bottomInset() + Height, dpi);
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            _toasts[i].MoveTo(new Point(x, y));
            y -= Dpi.Scale(Height + 8, dpi);
        }
    }

    private sealed class ToastWindow : LayeredPopup
    {
        private readonly ToastContent _content;
        private readonly ToastHost _host;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private long _phaseStart;
        private int _phase;
        private bool _hoverAction;
        private bool _hoverClose;
        private Point _location;

        public ToastWindow(ToastContent content, ToastHost host)
        {
            _content = content;
            _host = host;
            _timer.Tick += (_, _) => Tick();
        }

        // ToastHost's constants: unqualified, Width and Height are this window's own size, which grew by the shadow's
        // margin each time the toast was painted (the toast that kept expanding).
        protected override Size ContentSize => new(Dpi.Scale(ToastHost.Width, PaintDpi), Dpi.Scale(ToastHost.Height, PaintDpi));

        public void Start(Form owner)
        {
            Owner = owner;
            PopupOpacity = SystemInformation.UIEffectsEnabled ? (byte)0 : (byte)255;
            ShowAt(_location, owner);
            _phase = 0;
            _phaseStart = Environment.TickCount64;
            _timer.Start();
        }

        public void MoveTo(Point location)
        {
            _location = location;
            if (Visible)
            {
                ShowAt(location, Owner);
            }
        }

        public void Dismiss()
        {
            _timer.Stop();
            Hide();
            _host.Remove(this);
            BeginInvoke(Dispose);
        }

        protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => Draw(g, bounds, _content, _hoverAction, _hoverClose, dpi);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            (Rectangle action, Rectangle close) = ButtonBounds(new Rectangle(Point.Empty, ContentSize), _content, PaintDpi);
            Point p = ToContent(e.Location);
            bool hoverAction = action.Contains(p);
            bool hoverClose = close.Contains(p);
            if (hoverAction != _hoverAction || hoverClose != _hoverClose)
            {
                _hoverAction = hoverAction;
                _hoverClose = hoverClose;
                Render();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverAction = false;
            _hoverClose = false;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            (Rectangle action, Rectangle close) = ButtonBounds(new Rectangle(Point.Empty, ContentSize), _content, PaintDpi);
            Point p = ToContent(e.Location);
            if (action.Contains(p))
            {
                _content.Action?.Invoke();
                Dismiss();
            }
            else if (close.Contains(p))
            {
                Dismiss();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Tick()
        {
            long elapsed = Environment.TickCount64 - _phaseStart;
            bool hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
            switch (_phase)
            {
                case 0:
                    PopupOpacity = SystemInformation.UIEffectsEnabled ? (byte)Math.Min(255, elapsed * 255 / 150) : (byte)255;
                    if (elapsed >= 150)
                    {
                        Next(1);
                    }

                    break;
                case 1:
                    if (hovered)
                    {
                        _phaseStart = Environment.TickCount64;
                    }
                    else if (elapsed >= 5000)
                    {
                        Next(2);
                    }

                    break;
                default:
                    if (!SystemInformation.UIEffectsEnabled || elapsed >= 150)
                    {
                        Dismiss();
                        return;
                    }

                    PopupOpacity = (byte)Math.Max(0, 255 - (elapsed * 255 / 150));
                    break;
            }
        }

        private void Next(int phase)
        {
            _phase = phase;
            _phaseStart = Environment.TickCount64;
        }
    }
}
