namespace MediaVerifier.UI.Kit;

/// <summary>
/// A vertically scrolling area drawn with the kit's overlay scrollbar (subplan 08, Scrollbar) instead
/// of a native one. Keyboard focus that lands on a hidden descendant scrolls it into view.
/// </summary>
internal interface IScrollHost
{
    /// <summary>Scroll offset in device pixels, 0 to <see cref="ScrollMax"/>; setting it clamps and moves the content.</summary>
    int ScrollOffset { get; set; }

    int ScrollMax { get; }

    int ViewportHeight { get; }

    int ContentHeight { get; }

    /// <summary>Scrolls so that <paramref name="descendant"/> is fully visible when it fits.</summary>
    void EnsureVisible(Control descendant);
}

/// <summary>Shared scrolling arithmetic for <see cref="IScrollHost"/> implementations.</summary>
internal static class KitScrolling
{
    /// <summary>The wheel step for <paramref name="delta"/> in device pixels (16 px per line).</summary>
    public static int WheelStep(int delta, int dpi) => -delta * Math.Max(1, SystemInformation.MouseWheelScrollLines) * Dpi.Scale(16, dpi) / 120;

    /// <summary>The offset that brings <paramref name="top"/>..<paramref name="bottom"/> (content coordinates) into view.</summary>
    public static int OffsetToShow(int offset, int top, int bottom, int viewport)
    {
        if (bottom - top > viewport || top < offset)
        {
            return top;
        }

        return bottom > offset + viewport ? bottom - viewport : offset;
    }

    /// <summary>Marks a wheel event as consumed so it does not also scroll the containers around it.</summary>
    public static void MarkHandled(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    /// <summary>Walks up from a control that received keyboard focus and scrolls every host around it.</summary>
    public static void RevealFocused(Control control)
    {
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IScrollHost host)
            {
                host.EnsureVisible(control);
            }
        }
    }
}

/// <summary>
/// The kit's scrollbar thumb as a child of an <see cref="IScrollHost"/>: 6 px wide (10 on hover or
/// drag), 3 px in from the right, 4 px from the top and bottom, at least 32 px long, no track. Hidden
/// when the content fits.
/// </summary>
internal sealed class ScrollThumb : KitControl
{
    private readonly IScrollHost _host;
    private Rectangle _track;
    private bool _dragging;
    private int _dragStart;
    private int _dragStartOffset;

    public ScrollThumb(IScrollHost host)
    {
        _host = host;
        TabStop = false;
        Visible = false;
    }

    protected override bool Interactive => true;

    private bool Wide => IsHovered || _dragging;

    /// <summary>Shows or hides the thumb and places it along <paramref name="track"/> (the host's visible area).</summary>
    public void Place(Rectangle track)
    {
        _track = track;
        int max = _host.ScrollMax;
        if (max <= 0 || track.Height <= S(48))
        {
            Visible = false;
            return;
        }

        int length = Math.Max(S(32), (track.Height - S(8)) * _host.ViewportHeight / Math.Max(1, _host.ContentHeight));
        int travel = track.Height - S(8) - length;
        int top = track.Top + S(4) + (int)((long)travel * Math.Clamp(_host.ScrollOffset, 0, max) / max);
        int width = S(Wide ? 10 : 6);
        Bounds = new Rectangle(track.Right - S(3) - width, top, width, length);
        if (!Visible)
        {
            Visible = true;
        }

        BringToFront();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);
    }

    protected override void OnPaint(PaintEventArgs e) => Lists.VirtualList.DrawScrollThumb(e.Graphics, ClientRectangle, Wide, DeviceDpi);

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Place(_track);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Place(_track);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStart = Cursor.Position.Y;
            _dragStartOffset = _host.ScrollOffset;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            int travel = Math.Max(1, _track.Height - S(8) - Height);
            _host.ScrollOffset = _dragStartOffset + (int)((long)(Cursor.Position.Y - _dragStart) * _host.ScrollMax / travel);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Place(_track);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _host.ScrollOffset += KitScrolling.WheelStep(e.Delta, DeviceDpi);
        KitScrolling.MarkHandled(e);
    }
}

/// <summary>
/// A vertical scroll container for one <see cref="Content"/> control (the detail drawer): the content
/// takes the panel's width and its own height, and moves with the wheel and the kit scrollbar.
/// </summary>
internal sealed class ScrollPanel : KitControl, IScrollHost
{
    private readonly ScrollThumb _thumb;
    private int _offset;

    public ScrollPanel(Control content)
    {
        Content = content;
        TabStop = false;
        _thumb = new ScrollThumb(this);
        Controls.Add(content);
        Controls.Add(_thumb);
        content.SizeChanged += (_, _) => Apply();
    }

    public Control Content { get; }

    public int ScrollOffset
    {
        get => _offset;
        set
        {
            _offset = value;
            Apply();
        }
    }

    public int ScrollMax => Math.Max(0, Content.Height - Height);

    public int ViewportHeight => Height;

    public int ContentHeight => Content.Height;

    public void ScrollToTop() => ScrollOffset = 0;

    /// <summary>Scrolls so that the given rectangle of the content is visible.</summary>
    public void EnsureVisible(Rectangle contentRect) => ScrollOffset = KitScrolling.OffsetToShow(_offset, contentRect.Top, contentRect.Bottom, Height);

    public void EnsureVisible(Control descendant)
    {
        if (descendant == _thumb || !Content.Contains(descendant))
        {
            return;
        }

        Rectangle bounds = Content.RectangleToClient(descendant.RectangleToScreen(descendant.ClientRectangle));
        EnsureVisible(bounds);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ScrollMax > 0)
        {
            ScrollOffset += KitScrolling.WheelStep(e.Delta, DeviceDpi);
            KitScrolling.MarkHandled(e);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (Content.Width != Width)
        {
            Content.Width = Width;
        }

        Apply();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Surface);

    private void Apply()
    {
        _offset = Math.Clamp(_offset, 0, ScrollMax);
        if (Content.Top != -_offset)
        {
            Content.Top = -_offset;
        }

        _thumb.Place(ClientRectangle);
    }
}
