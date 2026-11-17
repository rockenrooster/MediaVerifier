using MediaVerifier.UI.Theme;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// Hosts the active page in the content area (subplan 08): 24 px side margins, the page's top at 16 px
/// below the header (y 72 in the window), and 24 px (16 above the status strip) at the bottom. A page
/// taller than the area scrolls under the kit's overlay scrollbar, which sits in the right margin;
/// rail, header and strip stay put.
/// </summary>
internal sealed class PageViewport : Control, IScrollHost
{
    private readonly ScrollThumb _thumb;
    private PageBase? _page;
    private bool _stripVisible;
    private bool _drawerOpen;
    private int _offset;
    private int _pageHeight;

    public PageViewport()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        _thumb = new ScrollThumb(this);
        Controls.Add(_thumb);
    }

    public PageBase? Page => _page;

    public bool StripVisible
    {
        get => _stripVisible;
        set
        {
            if (_stripVisible != value)
            {
                _stripVisible = value;
                LayoutPage();
            }
        }
    }

    /// <summary>
    /// True while the detail drawer is open beside the content: the right margin is then 16 px, as in
    /// the issues-detail reference.
    /// </summary>
    public bool DrawerOpen
    {
        get => _drawerOpen;
        set
        {
            if (_drawerOpen != value)
            {
                _drawerOpen = value;
                LayoutPage();
            }
        }
    }

    public int ScrollOffset
    {
        get => _offset;
        set
        {
            int clamped = Math.Clamp(value, 0, ScrollMax);
            if (clamped != _offset)
            {
                _offset = clamped;
                PlacePage();
            }
        }
    }

    public int ScrollMax
    {
        get
        {
            Rectangle content = ContentBounds();
            return Math.Max(0, content.Y + _pageHeight + BottomMargin - ClientSize.Height);
        }
    }

    public int ViewportHeight => ClientSize.Height;

    public int ContentHeight => ContentBounds().Y + _pageHeight + BottomMargin;

    private int BottomMargin => Dpi.Scale(_stripVisible ? 16 : 24, DeviceDpi);

    public void Show(PageBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (ReferenceEquals(_page, page))
        {
            return;
        }

        SuspendLayout();
        if (_page is not null)
        {
            _page.Visible = false;
        }

        _page = page;
        if (page.Parent != this)
        {
            Controls.Add(page);
        }

        _offset = 0;
        page.Visible = true;
        LayoutPage();
        ResumeLayout(performLayout: true);
    }

    /// <summary>Content-area size in device pixels for a page at the current viewport size.</summary>
    public Rectangle ContentBounds()
    {
        int side = Dpi.Scale(24, DeviceDpi);
        int right = Dpi.Scale(_drawerOpen ? 16 : 24, DeviceDpi);
        int top = Dpi.Scale(16, DeviceDpi);
        return new Rectangle(side, top, Math.Max(0, ClientSize.Width - side - right), Math.Max(0, ClientSize.Height - top - BottomMargin));
    }

    /// <summary>Sizes the page to the content area, or to its measured height when that is taller.</summary>
    public void LayoutPage()
    {
        if (_page is null)
        {
            _thumb.Place(ClientRectangle);
            return;
        }

        Rectangle content = ContentBounds();
        _pageHeight = Math.Max(content.Height, _page.MeasureContentHeight(content.Width));
        _offset = Math.Clamp(_offset, 0, ScrollMax);
        PlacePage();
    }

    public void EnsureVisible(Control descendant)
    {
        if (_page is null || descendant == _thumb || !_page.Contains(descendant))
        {
            return;
        }

        Rectangle bounds = RectangleToClient(descendant.RectangleToScreen(descendant.ClientRectangle));
        int margin = Dpi.Scale(16, DeviceDpi);
        ScrollOffset = KitScrolling.OffsetToShow(_offset, bounds.Top + _offset - margin, bounds.Bottom + _offset + margin, ClientSize.Height);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
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

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(ThemeService.Tokens.Bg);
    }

    private void PlacePage()
    {
        if (_page is not null)
        {
            Rectangle content = ContentBounds();
            Rectangle bounds = new(content.X, content.Y - _offset, content.Width, _pageHeight);
            if (_page.Bounds != bounds)
            {
                _page.Bounds = bounds;
            }
        }

        _thumb.Place(ClientRectangle);
    }
}
