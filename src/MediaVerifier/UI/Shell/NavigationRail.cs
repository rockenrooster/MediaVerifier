using MediaVerifier.UI.Theme;
using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.UI.Shell;

internal enum RailItemKind
{
    Page,
    Update,
}

internal sealed class RailItem
{
    public RailItem(RailItemKind kind, PageId page, IconName icon, string label, bool bottom)
    {
        Kind = kind;
        Page = page;
        Icon = icon;
        Label = label;
        Bottom = bottom;
    }

    public RailItemKind Kind { get; }

    public PageId Page { get; }

    public IconName Icon { get; }

    public string Label { get; }

    public bool Bottom { get; }

    public bool Visible { get; set; } = true;
}

/// <summary>
/// The navigation rail (subplan 08, Window and shell): 200 px wide, or 56 px below a 1,100 px window.
/// Custom-painted; keyboard: Up/Down/Home/End move, Enter or Space opens.
/// </summary>
internal sealed class NavigationRail : KitControl
{
    public const int ExpandedWidth = 200;
    public const int CollapsedWidth = 56;
    public const int CollapseBelowWindowWidth = 1100;

    private readonly List<RailItem> _items;
    private readonly RailItem _updateItem;
    private int _hover = -1;
    private int _pressed = -1;
    private int _focus;
    private bool _collapsed;
    private PageId _selected = PageId.Dashboard;
    private int _issuesCount;
    private Severity _issuesTier;
    private string _issuesText = "";

    public NavigationRail()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Navigation";
        _items =
        [
            Page(PageId.Dashboard),
            Page(PageId.Issues),
            Page(PageId.Library),
            Page(PageId.Scans),
            Page(PageId.Schedule),
            Page(PageId.Tools),
            new RailItem(RailItemKind.Update, PageId.Settings, IconName.Update, Strings.Pages.UpdateAvailable, bottom: true) { Visible = false },
            new RailItem(RailItemKind.Page, PageId.Settings, IconName.Settings, Strings.Pages.Settings, bottom: true),
        ];
        _updateItem = _items[6];
    }

    /// <summary>Raised when an item is clicked or chosen with Enter or Space.</summary>
    public event EventHandler<RailItem>? ItemInvoked;

    public IReadOnlyList<RailItem> Items => _items;

    public PageId SelectedPage
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                Invalidate();
            }
        }
    }

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed != value)
            {
                _collapsed = value;
                Invalidate();
            }
        }
    }

    public bool UpdateAvailable
    {
        get => _updateItem.Visible;
        set
        {
            if (_updateItem.Visible != value)
            {
                _updateItem.Visible = value;
                Invalidate();
            }
        }
    }

    /// <summary>Open findings at Medium or above and the worst open tier; 0 hides the badge.</summary>
    public void SetIssuesBadge(int count, Severity worst)
    {
        _issuesCount = Math.Max(0, count);
        _issuesTier = worst;
        _issuesText = count > 999 ? "999+" : count.ToString("N0", CultureInfo.CurrentCulture);
        Invalidate();
    }

    public int LogicalWidth => _collapsed ? CollapsedWidth : ExpandedWidth;

    /// <summary>Item rectangle in device pixels.</summary>
    public Rectangle ItemBounds(int index)
    {
        RailItem item = _items[index];
        int width = _collapsed ? 40 : 184;
        int top;
        if (!item.Bottom)
        {
            int position = 0;
            for (int i = 0; i < index; i++)
            {
                if (!_items[i].Bottom && _items[i].Visible)
                {
                    position++;
                }
            }

            top = S(64 + (position * 44));
        }
        else if (item.Kind == RailItemKind.Update)
        {
            top = Height - S(8 + 40 + 44);
        }
        else
        {
            top = Height - S(8 + 40);
        }

        return new Rectangle(S(8), top, S(width), S(40));
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnGotFocus(EventArgs e)
    {
        int selectedIndex = _items.FindIndex(i => i.Kind == RailItemKind.Page && i.Page == _selected);
        if (selectedIndex >= 0)
        {
            _focus = selectedIndex;
        }

        base.OnGotFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up:
                MoveFocus(-1);
                e.Handled = true;
                break;
            case Keys.Down:
                MoveFocus(1);
                e.Handled = true;
                break;
            case Keys.Home:
                _focus = NextVisible(-1, 1);
                Invalidate();
                e.Handled = true;
                break;
            case Keys.End:
                _focus = NextVisible(_items.Count, -1);
                Invalidate();
                e.Handled = true;
                break;
            case Keys.Enter:
            case Keys.Space:
                if (_focus >= 0 && _focus < _items.Count && _items[_focus].Visible)
                {
                    ItemInvoked?.Invoke(this, _items[_focus]);
                }

                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = HitTest(e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0 && index == _pressed)
        {
            _focus = index;
            ItemInvoked?.Invoke(this, _items[index]);
        }

        _pressed = -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Tokens.Bg);
        KitPaint.VerticalLine(g, Tokens.Stroke, Width - S(1), 0, Height, S(1));

        // App mark: the icon at 24 px, then the wordmark when expanded.
        int markX = _collapsed ? 16 : 18;
        IconRenderer.DrawAppIcon(g, new RectangleF(S(markX), S(16), S(24), S(24)), small: false, smallTriangleOnGradient: true);
        if (!_collapsed)
        {
            KitPaint.DrawText(g, Strings.ProductName, Type.BodyStrong, Tokens.Text, S(50), S(28), Width - S(58));
        }

        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Visible)
            {
                PaintItem(g, i);
            }
        }
    }

    /// <summary>
    /// Paints an expanded navigation item (subplan 08): 40 px, radius 4; hover a Hover fill and a StrokeStrong
    /// bar, selected a Selected fill, an Accent bar and the label in BodyStrong; icon at +14, label at +42,
    /// an optional count badge 10 px from the right. The render harness draws the reference items with it.
    /// </summary>
    public static void DrawItem(Graphics g, Rectangle bounds, IconName icon, string label, bool selected, bool hover, string? badge, Severity badgeTier, int dpi, Color? iconColor = null)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(4, dpi);
        if (selected || hover)
        {
            KitPaint.FillRoundedRect(g, bounds, radius, selected ? t.Selected : t.Hover);
            RectangleF bar = new(bounds.X, bounds.Y + ((bounds.Height - Dpi.Scale(16, dpi)) / 2f), Dpi.ScaleF(3, dpi), Dpi.Scale(16, dpi));
            KitPaint.FillRoundedRect(g, bar, Dpi.ScaleF(1.5f, dpi), selected ? t.Accent : t.StrokeStrong);
        }

        IconRenderer.Draw(g, icon, bounds.X + Dpi.Scale(14, dpi), bounds.Y + Dpi.Scale(12, dpi), 16, dpi, iconColor ?? t.Text);
        int labelX = bounds.X + Dpi.Scale(42, dpi);
        int badgeSpace = 0;
        if (!string.IsNullOrEmpty(badge))
        {
            Font font = type.CaptionStrong;
            int width = Math.Max(Dpi.Scale(20, dpi), KitPaint.MeasureWidth(badge, font) + Dpi.Scale(14, dpi));
            int height = Dpi.Scale(20, dpi);
            Rectangle pill = new(bounds.Right - Dpi.Scale(10, dpi) - width, bounds.Y + ((bounds.Height - height) / 2), width, height);
            TierColors colors = t.ForSeverity(badgeTier);
            KitPaint.FillRoundedRect(g, pill, height / 2f, colors.Pill);
            KitPaint.DrawTextCentred(g, badge, font, colors.Text, pill.X + (pill.Width / 2), pill.Y + (pill.Height / 2));
            badgeSpace = width + Dpi.Scale(10, dpi);
        }

        KitPaint.DrawText(g, label, selected ? type.BodyStrong : type.Body, t.Text, labelX, bounds.Y + (bounds.Height / 2), bounds.Right - labelX - badgeSpace - Dpi.Scale(8, dpi));
    }

    private void PaintItem(Graphics g, int index)
    {
        RailItem item = _items[index];
        Rectangle bounds = ItemBounds(index);
        bool selected = item.Kind == RailItemKind.Page && item.Page == _selected;
        bool hover = index == _hover;
        float radius = SF(4);

        if (selected)
        {
            KitPaint.FillRoundedRect(g, bounds, radius, Tokens.Selected);
            PaintLeadingBar(g, bounds, Tokens.Accent);
        }
        else if (hover)
        {
            KitPaint.FillRoundedRect(g, bounds, radius, Tokens.Hover);
            PaintLeadingBar(g, bounds, Tokens.StrokeStrong);
        }

        int iconX = bounds.X + S(_collapsed ? 12 : 14);
        int iconY = bounds.Y + S(12);
        Color iconColor = item.Kind == RailItemKind.Update ? Tokens.Accent : Tokens.Text;
        IconRenderer.Draw(g, item.Icon, iconX, iconY, 16, DeviceDpi, iconColor);

        if (!_collapsed)
        {
            int labelX = bounds.X + S(42);
            int badgeSpace = 0;
            if (item.Kind == RailItemKind.Page && item.Page == PageId.Issues && _issuesCount > 0)
            {
                badgeSpace = PaintBadge(g, bounds);
            }

            KitPaint.DrawText(g, item.Label, selected ? Type.BodyStrong : Type.Body, Tokens.Text, labelX, bounds.Y + (bounds.Height / 2), bounds.Right - labelX - badgeSpace - S(8));
        }
        else if (item.Kind == RailItemKind.Page && item.Page == PageId.Issues && _issuesCount > 0)
        {
            // Collapsed: a dot in the tier's fill with a 1.5 px Bg outline at the icon's top right.
            float cx = iconX + SF(18);
            float cy = iconY - SF(1);
            float r = SF(4.5f);
            float outline = SF(1.5f);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(GdiCache.Brush(Tokens.Bg), cx - r - (outline / 2), cy - r - (outline / 2), (r * 2) + outline, (r * 2) + outline);
            g.FillEllipse(GdiCache.Brush(Tokens.ForSeverity(_issuesTier).Fill), cx - r + (outline / 2), cy - r + (outline / 2), (r * 2) - outline, (r * 2) - outline);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }

        if (index == _focus && KeyboardFocused)
        {
            KitPaint.DrawFocusRing(g, bounds, radius, DeviceDpi);
        }
    }

    private int PaintBadge(Graphics g, Rectangle item)
    {
        Font font = Type.CaptionStrong;
        int textWidth = KitPaint.MeasureWidth(g, _issuesText, font);
        int width = Math.Max(S(20), textWidth + S(14));
        int height = S(20);
        Rectangle pill = new(item.Right - S(10) - width, item.Y + ((item.Height - height) / 2), width, height);
        Theme.TierColors colors = Tokens.ForSeverity(_issuesTier);
        KitPaint.FillRoundedRect(g, pill, height / 2f, colors.Pill);
        KitPaint.DrawTextCentred(g, _issuesText, font, colors.Text, pill.X + (pill.Width / 2), pill.Y + (pill.Height / 2));
        return width + S(10);
    }

    private void PaintLeadingBar(Graphics g, Rectangle bounds, Color color)
    {
        RectangleF bar = new(bounds.X, bounds.Y + ((bounds.Height - S(16)) / 2f), SF(3), S(16));
        KitPaint.FillRoundedRect(g, bar, SF(1.5f), color);
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Visible && ItemBounds(i).Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private void SetHover(int index)
    {
        if (_hover != index)
        {
            _hover = index;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    private void MoveFocus(int direction)
    {
        _focus = NextVisible(_focus, direction);
        Invalidate();
    }

    private int NextVisible(int from, int direction)
    {
        int index = from;
        for (int step = 0; step < _items.Count; step++)
        {
            index += direction;
            if (index < 0)
            {
                index = _items.Count - 1;
            }
            else if (index >= _items.Count)
            {
                index = 0;
            }

            if (_items[index].Visible)
            {
                return index;
            }
        }

        return from;
    }

    private static RailItem Page(PageId id) => new(RailItemKind.Page, id, PageInfo.Icon(id), PageInfo.Title(id), bottom: false);
}
