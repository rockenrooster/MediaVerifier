using System.Runtime.InteropServices;
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
/// 5 s (paused while hovered), fades out over 150 ms; with Windows' Animation effects off it appears and goes
/// at once.
/// <para>
/// A toast belongs to the window: it is not topmost, so it never shows over another program. While the window is
/// hidden in the notification area or minimized, toasts wait and show when it is back; and the 5 s run only while the
/// user is in the window, so a result with Undo is not gone before anyone saw it. Text cut with an ellipsis shows in
/// full in a tooltip, and a failure whose text is cut gets the <c>Show</c> button of subplan 09 D.
/// </para>
/// </summary>
internal sealed class ToastHost : IDisposable
{
    public const int Width = 360;
    public const int Height = 64;
    private const int MaxToasts = 3;
    private readonly Form _owner;
    private readonly Func<int> _bottomInset;
    private readonly List<ToastWindow> _toasts = [];
    private bool _ownerActive;

    /// <param name="owner">The main window.</param>
    /// <param name="bottomInset">Logical pixels kept free at the bottom (the status strip while it shows).</param>
    public ToastHost(Form owner, Func<int> bottomInset)
    {
        _owner = owner;
        _bottomInset = bottomInset;
        _ownerActive = Form.ActiveForm == owner;
        owner.Move += OnOwnerMoved;
        owner.Resize += OnOwnerShowingChanged;
        owner.VisibleChanged += OnOwnerShowingChanged;
        owner.Activated += OnOwnerActivated;
        owner.Deactivate += OnOwnerDeactivated;
    }

    /// <summary>The window is on the screen: shown and not minimized.</summary>
    private bool OwnerShowing => !_owner.IsDisposed && _owner.Visible && _owner.WindowState != FormWindowState.Minimized;

    /// <summary>The user is in the window (a toast's 5 s run only then).</summary>
    private bool OwnerActive => _ownerActive;

    /// <summary>
    /// Windows' Animation effects setting (Settings, Accessibility, Visual effects; SPI_GETCLIENTAREAANIMATION, which Disk
    /// Monitor 2.0 reads too). SystemInformation.UIEffectsEnabled is a different setting.
    /// </summary>
    private static bool AnimationsEnabled
    {
        get
        {
            int enabled = 1;
            return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
        }
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    public void Show(ToastContent content)
    {
        if (_owner.IsDisposed)
        {
            return;
        }

        if (_toasts.Count >= MaxToasts)
        {
            _toasts[0].Dismiss(null);
        }

        ToastWindow toast = new(Prepare(content), this) { PaintDpi = _owner.DeviceDpi };
        _toasts.Add(toast);
        Reposition();
        if (OwnerShowing)
        {
            toast.Start();
        }
    }

    /// <summary>Takes the toast that shows <paramref name="content"/> (that same instance) off the screen; nothing if it has gone.</summary>
    public void Dismiss(ToastContent content)
    {
        for (int i = 0; i < _toasts.Count; i++)
        {
            if (_toasts[i].Shows(content))
            {
                _toasts[i].Dismiss(null);
                return;
            }
        }
    }

    /// <summary>Places the toasts again: call it when the space kept free at the bottom changes (the status strip).</summary>
    public void Reposition()
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

    public void Dispose()
    {
        _owner.Move -= OnOwnerMoved;
        _owner.Resize -= OnOwnerShowingChanged;
        _owner.VisibleChanged -= OnOwnerShowingChanged;
        _owner.Activated -= OnOwnerActivated;
        _owner.Deactivate -= OnOwnerDeactivated;
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
        (int x, int width) = TextColumn(bounds, content, dpi);
        KitPaint.DrawText(g, content.Title, type.BodyStrong, t.Text, x, bounds.Y + Dpi.Scale(22, dpi), width);
        KitPaint.DrawText(g, content.Text, type.Caption, t.Text2, x, bounds.Y + Dpi.Scale(43, dpi), width);
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

    /// <summary>
    /// The toast as it will show. A failure (any tier level) without a button of its own whose title or text does not
    /// fit gets <c>Show</c> (subplan 09 D), which opens the whole text in a message: the reason a repair, an update or a
    /// database action failed was cut to a third.
    /// </summary>
    internal ToastContent Prepare(ToastContent content)
    {
        if (content.Level == Severity.None || content.ActionText is not null)
        {
            return content;
        }

        int dpi = _owner.DeviceDpi;
        TypeRamp type = TypeRamp.For(dpi);
        (_, int width) = TextColumn(new Rectangle(0, 0, Dpi.Scale(Width, dpi), Dpi.Scale(Height, dpi)), content, dpi);
        bool cut = KitPaint.MeasureWidth(content.Title, type.BodyStrong) > width || KitPaint.MeasureWidth(content.Text, type.Caption) > width;
        return cut ? content with { ActionText = Strings.Dialogs.Show, Action = () => ShowInFull(content) } : content;
    }

    /// <summary>The title and text column: from 44 px to 8 px before the action button (or the close button).</summary>
    private static (int X, int Width) TextColumn(Rectangle bounds, ToastContent content, int dpi)
    {
        (Rectangle action, Rectangle close) = ButtonBounds(bounds, content, dpi);
        int right = (content.ActionText is null ? close.X : action.X) - Dpi.Scale(8, dpi);
        int x = bounds.X + Dpi.Scale(44, dpi);
        return (x, right - x);
    }

    private void ShowInFull(ToastContent content)
    {
        if (!_owner.IsDisposed)
        {
            Form over = Form.ActiveForm is KitDialog dialog ? dialog : _owner;
            MessageDialog.Show(over, content.Title, content.Text, Strings.Kit.Close);
        }
    }

    private void Remove(ToastWindow toast)
    {
        _toasts.Remove(toast);
        Reposition();
    }

    private void OnOwnerMoved(object? sender, EventArgs e) => Reposition();

    private void OnOwnerShowingChanged(object? sender, EventArgs e)
    {
        Reposition();
        bool showing = OwnerShowing;
        foreach (ToastWindow toast in _toasts.ToArray())
        {
            toast.SetOwnerShowing(showing);
        }
    }

    private void OnOwnerActivated(object? sender, EventArgs e) => SetOwnerActive(true);

    private void OnOwnerDeactivated(object? sender, EventArgs e) => SetOwnerActive(false);

    private void SetOwnerActive(bool active)
    {
        _ownerActive = active;
        foreach (ToastWindow toast in _toasts)
        {
            toast.UpdateHold();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    private sealed class ToastWindow : LayeredPopup
    {
        private const int FadeMs = 150;
        private const int HoldMs = 5000;
        private const int FrameMs = 15;
        private readonly ToastContent _content;
        private readonly ToastHost _host;
        private readonly System.Windows.Forms.Timer _timer = new();
        private Phase _phase = Phase.Waiting;
        private long _phaseStart;
        private bool _hovered;
        private bool _hoverAction;
        private bool _hoverClose;
        private bool _away;
        private bool _dismissed;
        private Point _location;

        public ToastWindow(ToastContent content, ToastHost host)
        {
            _content = content;
            _host = host;
            _timer.Tick += (_, _) => Tick();
        }

        private enum Phase
        {
            Waiting,
            FadeIn,
            Hold,
            FadeOut,
        }

        // ToastHost's constants: unqualified, Width and Height are this window's own size, which grew by the shadow's
        // margin each time the toast was painted (the toast that kept expanding).
        protected override Size ContentSize => new(Dpi.Scale(ToastHost.Width, PaintDpi), Dpi.Scale(ToastHost.Height, PaintDpi));

        protected override bool TopMostPopup => false;

        /// <summary>Shows the toast for the first time (its window is on the screen).</summary>
        public void Start()
        {
            bool animate = AnimationsEnabled;
            PopupOpacity = animate ? (byte)0 : (byte)255;
            ShowAt(_location, _host._owner);
            Begin(animate ? Phase.FadeIn : Phase.Hold);
        }

        /// <summary>The window went to the notification area or the taskbar, or came back.</summary>
        public void SetOwnerShowing(bool showing)
        {
            if (_dismissed)
            {
                return;
            }

            if (!showing)
            {
                if (_phase != Phase.Waiting)
                {
                    // It waits with the window and gets its full time back.
                    _timer.Stop();
                    _away = true;
                    Hide();
                }

                return;
            }

            if (_phase == Phase.Waiting)
            {
                Start();
            }
            else if (_away)
            {
                _away = false;
                PopupOpacity = 255;
                ShowAt(_location, _host._owner);
                Begin(Phase.Hold);
            }
        }

        public void MoveTo(Point location)
        {
            _location = location;
            if (Visible)
            {
                MoveContentTo(location);
            }
        }

        /// <summary>Starts or stops the 5 s: they run while the user is in the window and the pointer is not on the toast.</summary>
        public void UpdateHold()
        {
            if (_phase != Phase.Hold || _dismissed || _away)
            {
                return;
            }

            if (_hovered || !_host.OwnerActive)
            {
                _timer.Stop();
            }
            else if (!_timer.Enabled)
            {
                _timer.Interval = HoldMs;
                _timer.Start();
            }
        }

        public bool Shows(ToastContent content) => ReferenceEquals(_content, content);

        /// <summary>Closes the toast; <paramref name="then"/> (its button's action) runs afterwards from a posted message.</summary>
        public void Dismiss(Action? then)
        {
            if (_dismissed)
            {
                return;
            }

            _dismissed = true;
            _timer.Stop();
            Hide();
            _host.Remove(this);
            if (IsHandleCreated)
            {
                BeginInvoke(() => Finish(then));
            }
            else
            {
                Finish(then);
            }
        }

        protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => Draw(g, bounds, _content, _hoverAction, _hoverClose, dpi);

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            UpdateHold();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle bounds = new(Point.Empty, ContentSize);
            (Rectangle action, Rectangle close) = ButtonBounds(bounds, _content, PaintDpi);
            Point p = ToContent(e.Location);
            bool hoverAction = action.Contains(p);
            bool hoverClose = close.Contains(p);
            if (hoverAction != _hoverAction || hoverClose != _hoverClose)
            {
                _hoverAction = hoverAction;
                _hoverClose = hoverClose;
                Render();
            }

            // Subplan 09 E: the full text of anything cut short, in a tooltip.
            (int x, int width) = TextColumn(bounds, _content, PaintDpi);
            bool titleLine = p.Y < Dpi.Scale(33, PaintDpi);
            TypeRamp type = TypeRamp.For(PaintDpi);
            KitToolTip.TrackCut(this, p.X >= x && p.X < x + width ? (titleLine ? _content.Title : _content.Text) : string.Empty, titleLine ? type.BodyStrong : type.Caption, width);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            _hoverAction = false;
            _hoverClose = false;
            Render();
            UpdateHold();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            (Rectangle action, Rectangle close) = ButtonBounds(new Rectangle(Point.Empty, ContentSize), _content, PaintDpi);
            Point p = ToContent(e.Location);
            if (action.Contains(p))
            {
                Dismiss(_content.Action);
            }
            else if (close.Contains(p))
            {
                Dismiss(null);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dismissed = true;
                _timer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Begin(Phase phase)
        {
            _phase = phase;
            _phaseStart = Environment.TickCount64;
            _timer.Stop();
            if (phase == Phase.Hold)
            {
                // One timer for the whole hold, started and stopped by hover and the window's activation.
                UpdateHold();
                return;
            }

            _timer.Interval = FrameMs;
            _timer.Start();
        }

        private void Tick()
        {
            long elapsed = Environment.TickCount64 - _phaseStart;
            switch (_phase)
            {
                case Phase.FadeIn:
                    PopupOpacity = (byte)Math.Min(255, elapsed * 255 / FadeMs);
                    if (elapsed >= FadeMs)
                    {
                        Begin(Phase.Hold);
                    }

                    break;
                case Phase.Hold:
                    if (AnimationsEnabled)
                    {
                        Begin(Phase.FadeOut);
                    }
                    else
                    {
                        Dismiss(null);
                    }

                    break;
                case Phase.FadeOut:
                    if (elapsed >= FadeMs)
                    {
                        Dismiss(null);
                        return;
                    }

                    PopupOpacity = (byte)Math.Max(0, 255 - (elapsed * 255 / FadeMs));
                    break;
            }
        }

        private void Finish(Action? then)
        {
            try
            {
                then?.Invoke();
            }
            finally
            {
                Dispose();
            }
        }
    }
}
