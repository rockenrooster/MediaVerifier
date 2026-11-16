using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// Hosts the active page in the content area (subplan 08): 24 px side margins, the page's top at 16 px
/// below the header (y 72 in the window), and 24 px (16 above the status strip) at the bottom. A page
/// taller than the area scrolls; rail, header and strip stay put.
/// </summary>
internal sealed class PageViewport : ScrollableControl
{
    private PageBase? _page;
    private bool _stripVisible;

    public PageViewport()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AutoScroll = true;
        TabStop = false;
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

        AutoScrollPosition = Point.Empty;
        page.Visible = true;
        LayoutPage();
        ResumeLayout(performLayout: true);
    }

    /// <summary>Content-area size in device pixels for a page at the current viewport size.</summary>
    public Rectangle ContentBounds()
    {
        int side = Dpi.Scale(24, DeviceDpi);
        int top = Dpi.Scale(16, DeviceDpi);
        int bottom = Dpi.Scale(_stripVisible ? 16 : 24, DeviceDpi);
        return new Rectangle(side, top, Math.Max(0, ClientSize.Width - (side * 2)), Math.Max(0, ClientSize.Height - top - bottom));
    }

    public void LayoutPage()
    {
        if (_page is null)
        {
            return;
        }

        Rectangle content = ContentBounds();
        int width = content.Width;
        int needed = _page.MeasureContentHeight(width);
        int height = needed > content.Height ? needed : content.Height;
        bool scrolls = needed > content.Height;
        if (scrolls)
        {
            width = Math.Max(0, ClientSize.Width - (content.X * 2) - (VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth));
            needed = _page.MeasureContentHeight(width);
            height = Math.Max(needed, content.Height);
        }

        Point scroll = AutoScrollPosition;
        _page.Bounds = new Rectangle(content.X + scroll.X, content.Y + scroll.Y, width, height);
        AutoScrollMinSize = scrolls ? new Size(0, content.Y + height + Dpi.Scale(_stripVisible ? 16 : 24, DeviceDpi)) : Size.Empty;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutPage();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Theme.Tokens.Bg);
    }
}
