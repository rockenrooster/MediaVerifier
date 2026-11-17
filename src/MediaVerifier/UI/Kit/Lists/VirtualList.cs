using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit.Lists;

/// <summary>How a list draws its rows.</summary>
internal enum ListMode
{
    /// <summary>Two-line rows, 44 px (Issues).</summary>
    TwoLine,

    /// <summary>One-line rows, 36 px, with a table header (Library browse, Scans history).</summary>
    Table,
}

/// <summary>A table column: header text, fixed logical width (0 = absorbs the remaining width), alignment.</summary>
internal sealed record ListColumn(string Header, int Width, bool RightAligned = false, bool Sortable = true, int Pad = 16);

/// <summary>
/// One row's content, filled by the source into a reused instance (painting allocates nothing). Strings
/// must be existing objects held by the page's model, not built per call.
/// </summary>
internal sealed class ListRow
{
    public bool IsGroup;

    /// <summary>The filter summary row ("Showing {n} of {total}" and a link); positioned like a group header.</summary>
    public bool IsSummary;

    /// <summary>The link text of a summary row.</summary>
    public string? Link;
    public bool Collapsed;
    public bool Loaded = true;
    public Severity? Severity;
    public IconName? StateIcon;

    /// <summary>The OK shape in OkText (a scanned file without problems).</summary>
    public bool OkIcon;

    /// <summary>A table cell drawn in Text3 (a skip reason); -1 for none.</summary>
    public int MutedColumn = -1;

    /// <summary>A table cell drawn as a pill (a run result); -1 for none.</summary>
    public int PillColumn = -1;

    /// <summary>The pill's colours: a tier, OK colours, or neutral when null.</summary>
    public Severity? PillTier;
    public string Line1 = string.Empty;
    public string? Line2;
    public string? Size;
    public string? Folder;
    public string? Count;

    /// <summary>Table cells after the first (which is <see cref="Line1"/>); index = column.</summary>
    public readonly string?[] Cells = new string?[12];

    public void Clear()
    {
        IsGroup = false;
        IsSummary = false;
        Link = null;
        Collapsed = false;
        Loaded = true;
        Severity = null;
        StateIcon = null;
        OkIcon = false;
        MutedColumn = -1;
        PillColumn = -1;
        PillTier = null;
        Line1 = string.Empty;
        Line2 = null;
        Size = null;
        Folder = null;
        Count = null;
        Array.Clear(Cells);
    }
}

/// <summary>The data behind a <see cref="VirtualList"/>. Rows are identified by index and a stable id.</summary>
internal interface IListSource
{
    int Count { get; }

    /// <summary>A stable id per row (selection survives refreshes); group headers use negative ids.</summary>
    long IdOf(int index);

    void Fill(int index, ListRow row);

    /// <summary>Expands or collapses a group header row.</summary>
    void SetCollapsed(int index, bool collapsed);

    /// <summary>
    /// Number of group header rows before <paramref name="index"/>. Group headers are shorter than items,
    /// so the list positions rows from this; sources with groups answer it in O(log groups).
    /// </summary>
    int GroupsBefore(int index) => 0;
}

/// <summary>
/// The virtual list of subplan 08 (task UK-07): only visible rows are drawn, the row height is fixed,
/// and scrolling allocates nothing. Two-line rows with group headers, or one-line table rows under a
/// sortable header; multi-select with Ctrl, Shift and Ctrl+A; hover actions (Details and More) on two-line
/// rows; skeleton rows while data loads; a custom scrollbar.
/// </summary>
internal sealed class VirtualList : KitControl
{
    private const int GroupHeight = 32;
    private const int HeaderHeight = 32;
    private readonly ListRow _row = new();
    private readonly HashSet<long> _selected = [];
    private IListSource? _source;
    private IReadOnlyList<ListColumn> _columns = [];
    private int _scroll;
    private int _hover = -1;
    private int _focus = -1;
    private int _anchor = -1;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private bool _framed = true;
    private bool _scrollHover;
    private bool _dragging;
    private int _dragOffset;
    private int _bottomPadding;
    private HoverPart _hoverPart;

    public VirtualList()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        BackColor = Tokens.Surface;
    }

    private enum HoverPart
    {
        None,
        Details,
        More,
    }

    /// <summary>Enter or double-click on an item row.</summary>
    public event EventHandler<int>? ItemActivated;

    /// <summary>The Details hover button of a two-line row.</summary>
    public event EventHandler<int>? DetailsClicked;

    /// <summary>The More hover button, the context-menu key or a right-click: index and screen point.</summary>
    public event EventHandler<(int Index, Point Screen)>? MoreRequested;

    public event EventHandler? SelectionChanged;

    /// <summary>A sortable header was clicked: column index and direction.</summary>
    public event EventHandler<(int Column, bool Descending)>? SortChanged;

    public ListMode Mode { get; set; } = ListMode.TwoLine;

    /// <summary>Table rows keep a 44 px icon column; without it the first cell starts at its column's inset.</summary>
    public bool Icons { get; set; } = true;

    /// <summary>The list draws its own Surface card with a Stroke border and radius 8.</summary>
    public bool Framed
    {
        get => _framed;
        set
        {
            _framed = value;
            Invalidate();
        }
    }

    /// <summary>Extra scroll room at the bottom (64 px while the selection bar shows).</summary>
    public int BottomPadding
    {
        get => _bottomPadding;
        set
        {
            _bottomPadding = value;
            ClampScroll();
            Invalidate();
        }
    }

    public IReadOnlyList<ListColumn> Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            Invalidate();
        }
    }

    public IListSource? Source
    {
        get => _source;
        set
        {
            _source = value;
            _hover = -1;
            if (_focus >= Count)
            {
                _focus = Count - 1;
            }

            ClampScroll();
            Invalidate();
        }
    }

    public int Count => _source?.Count ?? 0;

    public IReadOnlySet<long> SelectedIds => _selected;

    public int FocusedIndex => _focus;

    public int SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    /// <summary>Scroll offset in device pixels.</summary>
    public int ScrollOffset
    {
        get => _scroll;
        set
        {
            _scroll = value;
            ClampScroll();
            Invalidate();
        }
    }

    protected override bool Interactive => true;

    private int RowHeight => Mode == ListMode.TwoLine ? 44 : 36;

    private int TopInset => Mode == ListMode.Table ? HeaderHeight : 0;

    /// <summary>Sets the sort indicator without raising <see cref="SortChanged"/>.</summary>
    public void SetSort(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        Invalidate();
    }

    /// <summary>The source changed (rows added, removed or refreshed): keeps selection by id.</summary>
    public void Refresh(bool keepScroll = true)
    {
        if (!keepScroll)
        {
            _scroll = 0;
        }

        if (_focus >= Count)
        {
            _focus = Count - 1;
        }

        ClampScroll();
        Invalidate();
    }

    public void ClearSelection()
    {
        if (_selected.Count > 0)
        {
            _selected.Clear();
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectAll()
    {
        if (_source is null)
        {
            return;
        }

        _selected.Clear();
        for (int i = 0; i < _source.Count; i++)
        {
            long id = _source.IdOf(i);
            if (id >= 0)
            {
                _selected.Add(id);
            }
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves keyboard focus to a row, selects only it and scrolls it into view.</summary>
    public void FocusRow(int index, bool select = true)
    {
        if (_source is null || index < 0 || index >= _source.Count)
        {
            return;
        }

        _focus = index;
        _anchor = index;
        if (select)
        {
            SelectOnly(index);
        }

        EnsureVisible(index);
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, index);
    }

    public void EnsureVisible(int index)
    {
        int top = YOf(index);
        int height = S(HeightOf(index));
        int viewTop = S(TopInset);
        if (top < viewTop)
        {
            _scroll -= viewTop - top;
        }
        else if (top + height > Height)
        {
            _scroll += top + height - Height;
        }

        ClampScroll();
    }

    /// <summary>The row index at a client point, or -1.</summary>
    public int IndexAt(Point point)
    {
        if (_source is null || point.Y < S(TopInset))
        {
            return -1;
        }

        int y = point.Y - S(TopInset) + _scroll;
        int index = IndexAtContentY(y);
        return index < _source.Count ? index : -1;
    }

    /// <summary>Paints a two-line row (the harness renders the reference rows through this).</summary>
    public static void DrawTwoLineRow(Graphics g, Rectangle row, ListRow data, bool selected, bool hover, bool focused, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        Color background = DrawRowBackground(g, row, selected, hover, dpi, t);
        if (!data.Loaded)
        {
            DrawSkeleton(g, row, dpi, t);
            return;
        }

        if (data.Severity is Severity severity)
        {
            IconRenderer.DrawSeverity(g, severity, row.X + Dpi.ScaleF(16, dpi), row.Y + Dpi.ScaleF(14, dpi), 16, dpi, t.ForSeverity(severity).Text, background);
        }

        int right = row.Right - Dpi.Scale(16, dpi);
        int folderRight = row.Right - Dpi.Scale(hover ? 100 + 132 : 100, dpi);
        int folderWidth = 0;
        if (data.Folder is not null)
        {
            folderWidth = Math.Min(KitPaint.MeasureWidth(data.Folder, type.Body), Dpi.Scale(264, dpi));
            KitPaint.DrawText(g, data.Folder, type.Body, t.Text3, folderRight - folderWidth, row.Y + Dpi.Scale(22, dpi), folderWidth + 1, TextFormatFlags.Right);
        }

        if (hover)
        {
            DrawHoverActions(g, row, dpi, HoverPart.None);
        }
        else if (data.Size is not null)
        {
            int sizeWidth = KitPaint.MeasureWidth(data.Size, type.Body);
            KitPaint.DrawText(g, data.Size, type.Body, t.Text2, right - sizeWidth, row.Y + Dpi.Scale(22, dpi), sizeWidth + 1, TextFormatFlags.Right);
        }

        int textX = row.X + Dpi.Scale(44, dpi);
        int textRight = data.Folder is null ? right - Dpi.Scale(80, dpi) : folderRight - folderWidth - Dpi.Scale(16, dpi);
        KitPaint.DrawText(g, data.Line1, type.BodyStrong, t.Text, textX, row.Y + Dpi.Scale(13, dpi), textRight - textX);
        if (data.Line2 is not null)
        {
            KitPaint.DrawText(g, data.Line2, type.Body, t.Text2, textX, row.Y + Dpi.Scale(31, dpi), textRight - textX);
        }

        KitPaint.HorizontalLine(g, Color.FromArgb(153, t.Stroke), textX, row.Right, row.Bottom - Dpi.ScaleF(1, dpi), Dpi.ScaleF(1, dpi));
        if (focused)
        {
            KitPaint.DrawFocusRing(g, Rectangle.Inflate(row, -Dpi.Scale(3, dpi), -Dpi.Scale(3, dpi)), Dpi.ScaleF(4, dpi), dpi);
        }
    }

    /// <summary>Paints a group header row: chevron at +14, severity at +38, label at +62, count 8 px after.</summary>
    public static void DrawGroupHeader(Graphics g, Rectangle row, ListRow data, bool focused, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        g.FillRectangle(GdiCache.Brush(Color.FromArgb(140, t.Bg)), row);
        int centreY = row.Y + (row.Height / 2);
        IconRenderer.Draw(g, data.Collapsed ? IconName.ChevronRight : IconName.ChevronDown, row.X + Dpi.ScaleF(14, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text2);
        int labelX = row.X + Dpi.Scale(38, dpi);
        if (data.Severity is Severity severity)
        {
            IconRenderer.DrawSeverity(g, severity, labelX, centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.ForSeverity(severity).Text, t.Surface);
            labelX = row.X + Dpi.Scale(62, dpi);
        }

        int labelWidth = KitPaint.MeasureWidth(data.Line1, type.BodyStrong);
        KitPaint.DrawText(g, data.Line1, type.BodyStrong, t.Text, labelX, centreY, labelWidth + 1);
        if (data.Count is not null)
        {
            KitPaint.DrawText(g, data.Count, type.Body, t.Text3, labelX + labelWidth + Dpi.Scale(8, dpi), centreY, KitPaint.MeasureWidth(data.Count, type.Body) + 1);
        }

        if (focused)
        {
            KitPaint.DrawFocusRing(g, Rectangle.Inflate(row, -Dpi.Scale(3, dpi), -Dpi.Scale(3, dpi)), Dpi.ScaleF(4, dpi), dpi);
        }
    }

    /// <summary>The filter summary row: text in Body Text2 at +16, then the link in Accent, underlined.</summary>
    public static void DrawSummaryRow(Graphics g, Rectangle row, ListRow data, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        int centreY = row.Y + (row.Height / 2);
        int x = row.X + Dpi.Scale(16, dpi);
        int width = KitPaint.MeasureWidth(data.Line1, type.Body);
        KitPaint.DrawText(g, data.Line1, type.Body, t.Text2, x, centreY, width + 1);
        if (data.Link is not null)
        {
            int linkX = x + width + Dpi.Scale(12, dpi);
            int linkWidth = KitPaint.MeasureWidth(data.Link, type.Body);
            KitPaint.DrawText(g, data.Link, type.Body, t.Accent, linkX, centreY, linkWidth + 1);
            KitPaint.HorizontalLine(g, t.Accent, linkX, linkX + linkWidth, centreY + Dpi.ScaleF(8.5f, dpi), Dpi.ScaleF(1, dpi));
        }

        KitPaint.HorizontalLine(g, Color.FromArgb(153, t.Stroke), row.X, row.Right, row.Bottom - Dpi.ScaleF(1, dpi), Dpi.ScaleF(1, dpi));
    }

    /// <summary>Skeleton while a row loads: a 16 px circle and two bars (10 px, then 8 px at 60 % opacity).</summary>
    public static void DrawSkeleton(Graphics g, Rectangle row, int dpi, ThemeTokens t)
    {
        float centreY = row.Y + (row.Height / 2f);
        float d = Dpi.ScaleF(16, dpi);
        KitPaint.FillRoundedRect(g, new RectangleF(row.X + Dpi.ScaleF(16, dpi), centreY - (d / 2f), d, d), d / 2f, t.Hover);
        float x = row.X + Dpi.ScaleF(44, dpi);
        KitPaint.FillRoundedRect(g, new RectangleF(x, centreY - Dpi.ScaleF(10, dpi), Dpi.ScaleF(150, dpi), Dpi.ScaleF(10, dpi)), Dpi.ScaleF(4, dpi), t.Hover);
        KitPaint.FillRoundedRect(g, new RectangleF(x, centreY + Dpi.ScaleF(4, dpi), Dpi.ScaleF(220, dpi), Dpi.ScaleF(8, dpi)), Dpi.ScaleF(4, dpi), Color.FromArgb(153, t.Hover));
    }

    /// <summary>Paints the scrollbar thumb: 6 px (10 on hover), radius 3, StrokeStrong at 60 % (full on hover).</summary>
    public static void DrawScrollThumb(Graphics g, Rectangle thumb, bool hover, int dpi)
    {
        Color color = ThemeService.Tokens.StrokeStrong;
        KitPaint.FillRoundedRect(g, thumb, thumb.Width / 2f, hover ? color : Color.FromArgb(153, color));
    }

    /// <summary>
    /// Right-aligned cells end 4 px before their column's edge (the next column's text starts 16 px in), so
    /// sizes and counts sit 20 px left of the following column as in the references.
    /// </summary>
    private const int RightPad = 4;

    /// <summary>Paints a table header: 32 px, Bg at 55 %, CaptionStrong Text3, 1 px Stroke below, sort arrow after the label.</summary>
    public static void DrawTableHeader(Graphics g, Rectangle bounds, IReadOnlyList<ListColumn> columns, ReadOnlySpan<int> widths, int sortColumn, bool descending, int dpi, bool icons = true)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        g.FillRectangle(GdiCache.Brush(Color.FromArgb(140, t.Bg)), bounds);
        KitPaint.HorizontalLine(g, t.Stroke, bounds.X, bounds.Right, bounds.Bottom - Dpi.ScaleF(1, dpi), Dpi.ScaleF(1, dpi));
        int x = bounds.X;
        int centreY = bounds.Y + (bounds.Height / 2);
        for (int i = 0; i < columns.Count; i++)
        {
            ListColumn column = columns[i];
            int width = widths[i];
            int pad = Dpi.Scale(16, dpi);
            int labelWidth = KitPaint.MeasureWidth(column.Header, type.CaptionStrong);
            int labelX = column.RightAligned ? x + width - Dpi.Scale(RightPad, dpi) - labelWidth : x + (i == 0 && icons ? Dpi.Scale(44, dpi) : Dpi.Scale(column.Pad, dpi));
            KitPaint.DrawText(g, column.Header, type.CaptionStrong, t.Text3, labelX, centreY, labelWidth + 1);
            if (i == sortColumn)
            {
                IconRenderer.Draw(g, descending ? IconName.SortDown : IconName.SortUp, labelX + labelWidth + Dpi.ScaleF(4, dpi), centreY - Dpi.ScaleF(6, dpi), 12, dpi, t.Text3);
            }

            x += width;
        }
    }

    /// <summary>Paints a one-line table row: state or severity icon at (+16, +10), cells in Body.</summary>
    public static void DrawTableRow(Graphics g, Rectangle row, ListRow data, IReadOnlyList<ListColumn> columns, ReadOnlySpan<int> widths, bool selected, bool hover, bool focused, int dpi, bool icons = true)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        Color background = DrawRowBackground(g, row, selected, hover, dpi, t);
        if (!data.Loaded)
        {
            DrawSkeleton(g, row, dpi, t);
            return;
        }

        int centreY = row.Y + (row.Height / 2);
        if (data.Severity is Severity severity)
        {
            IconRenderer.DrawSeverity(g, severity, row.X + Dpi.ScaleF(16, dpi), row.Y + Dpi.ScaleF(10, dpi), 16, dpi, t.ForSeverity(severity).Text, background);
        }
        else if (data.OkIcon)
        {
            IconRenderer.DrawSeverity(g, Core.Model.Severity.None, row.X + Dpi.ScaleF(16, dpi), row.Y + Dpi.ScaleF(10, dpi), 16, dpi, t.Ok.Text, background);
        }
        else if (data.StateIcon is IconName icon)
        {
            IconRenderer.Draw(g, icon, row.X + Dpi.ScaleF(16, dpi), row.Y + Dpi.ScaleF(10, dpi), 16, dpi, t.Text3);
        }

        int x = row.X;
        int pad = Dpi.Scale(16, dpi);
        for (int i = 0; i < columns.Count; i++)
        {
            int width = widths[i];
            string? text = i == 0 ? data.Line1 : data.Cells[i];
            if (text is not null)
            {
                // Left-aligned text may run up to the next column (whose text starts 16 px in); right-aligned
                // text keeps 4 px from both edges.
                bool right = columns[i].RightAligned;
                int left = i == 0 && icons ? row.X + Dpi.Scale(44, dpi) : x + (right ? Dpi.Scale(RightPad, dpi) : Dpi.Scale(columns[i].Pad, dpi));
                int available = x + width - (right ? Dpi.Scale(RightPad, dpi) : 0) - left;
                if (i == data.PillColumn)
                {
                    (Color fg, Color bg) = data.PillTier switch
                    {
                        Severity tier when tier != Core.Model.Severity.None => (t.ForSeverity(tier).Text, t.ForSeverity(tier).Pill),
                        Severity => (t.Ok.Text, t.Ok.Pill),
                        null => (t.Text2, t.NeutralPill),
                    };
                    BadgePainter.PaintPill(g, left, centreY - Dpi.Scale(10, dpi), text, fg, bg, dpi);
                    x += width;
                    continue;
                }

                Font font = type.Body;
                Color color = i == data.MutedColumn ? t.Text3 : i == 0 ? t.Text : t.Text2;
                KitPaint.DrawText(g, text, font, color, left, centreY, available, columns[i].RightAligned ? TextFormatFlags.Right : TextFormatFlags.Default);
            }

            x += width;
        }

        KitPaint.HorizontalLine(g, Color.FromArgb(153, t.Stroke), row.X + Dpi.Scale(icons ? 44 : 16, dpi), row.Right - (icons ? 0 : Dpi.Scale(16, dpi)), row.Bottom - Dpi.ScaleF(1, dpi), Dpi.ScaleF(1, dpi));
        if (focused)
        {
            KitPaint.DrawFocusRing(g, Rectangle.Inflate(row, -Dpi.Scale(3, dpi), -Dpi.Scale(3, dpi)), Dpi.ScaleF(4, dpi), dpi);
        }
    }

    /// <summary>Column widths for a total width: fixed columns keep theirs; the first zero-width column absorbs the rest.</summary>
    public static void ColumnWidths(IReadOnlyList<ListColumn> columns, int total, int dpi, Span<int> widths)
    {
        int fixedTotal = 0;
        int absorbing = -1;
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Width > 0)
            {
                widths[i] = Dpi.Scale(columns[i].Width, dpi);
                fixedTotal += widths[i];
            }
            else if (absorbing < 0)
            {
                absorbing = i;
            }
            else
            {
                widths[i] = 0;
            }
        }

        if (absorbing >= 0)
        {
            widths[absorbing] = Math.Max(Dpi.Scale(80, dpi), total - fixedTotal);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = Tokens;
        Graphics g = e.Graphics;
        int dpi = DeviceDpi;
        Rectangle client = ClientRectangle;
        g.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : t.Bg);
        if (_framed)
        {
            KitPaint.FillRoundedRect(g, client, SF(8), t.Surface);
        }
        else
        {
            g.FillRectangle(GdiCache.Brush(t.Surface), client);
        }

        Span<int> widths = stackalloc int[Math.Max(1, _columns.Count)];
        if (Mode == ListMode.Table)
        {
            ColumnWidths(_columns, Width, dpi, widths);
        }

        if (_source is not null && _source.Count > 0)
        {
            int top = S(TopInset);
            int first = IndexAtContentY(_scroll);
            int y = top + ContentYOf(first) - _scroll;
            System.Drawing.Drawing2D.GraphicsState saved = g.Save();
            g.SetClip(Rectangle.FromLTRB(0, top, Width, Height), System.Drawing.Drawing2D.CombineMode.Intersect);
            for (int i = first; i < _source.Count && y < Height; i++)
            {
                _row.Clear();
                _source.Fill(i, _row);
                int height = S(_row.IsGroup ? GroupHeight : RowHeight);
                Rectangle rowBounds = new(0, y, Width, height);
                bool focused = i == _focus && KeyboardFocused;
                if (_row.IsSummary)
                {
                    DrawSummaryRow(g, rowBounds, _row, dpi);
                }
                else if (_row.IsGroup)
                {
                    DrawGroupHeader(g, rowBounds, _row, focused, dpi);
                }
                else
                {
                    bool selected = _selected.Contains(_source.IdOf(i));
                    bool hover = i == _hover && !_dragging;
                    if (Mode == ListMode.TwoLine)
                    {
                        DrawTwoLineRow(g, rowBounds, _row, selected, hover, focused, dpi);
                        if (hover && _hoverPart != HoverPart.None)
                        {
                            DrawHoverActions(g, rowBounds, dpi, _hoverPart);
                        }
                    }
                    else
                    {
                        DrawTableRow(g, rowBounds, _row, _columns, widths, selected, hover, focused, dpi, Icons);
                    }
                }

                y += height;
            }

            g.Restore(saved);
        }

        if (Mode == ListMode.Table)
        {
            DrawTableHeader(g, new Rectangle(0, 0, Width, S(HeaderHeight)), _columns, widths, _sortColumn, _sortDescending, dpi, Icons);
        }

        if (ThumbBounds() is Rectangle thumb && !thumb.IsEmpty)
        {
            DrawScrollThumb(g, thumb, _scrollHover || _dragging, dpi);
        }

        if (_framed)
        {
            KitPaint.StrokeRoundedRect(g, client, SF(8), t.Stroke, SF(1));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
        _scroll -= e.Delta * lines * S(RowHeight) / 120;
        ClampScroll();
        KitScrolling.MarkHandled(e);
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            DragThumb(e.Y);
            return;
        }

        bool overScroll = ScrollZone().Contains(e.Location);
        if (overScroll != _scrollHover)
        {
            _scrollHover = overScroll;
            Invalidate();
        }

        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _hoverPart = HoverPart.None;
        _scrollHover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left && ThumbBounds() is Rectangle thumb && ScrollZone().Contains(e.Location))
        {
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Y;
                Capture = true;
            }
            else
            {
                _scroll += (e.Y < thumb.Y ? -1 : 1) * Math.Max(S(RowHeight), Height - S(TopInset + RowHeight));
                ClampScroll();
                Invalidate();
            }

            return;
        }

        if (Mode == ListMode.Table && e.Y < S(HeaderHeight))
        {
            HeaderClick(e.X);
            return;
        }

        int index = IndexAt(e.Location);
        if (index < 0 || _source is null)
        {
            return;
        }

        if (e.Button == MouseButtons.Left && Mode == ListMode.TwoLine && index == _hover && _hoverPart != HoverPart.None)
        {
            return;
        }

        _source.Fill(index, _row);
        if (_row.IsGroup)
        {
            if (e.Button == MouseButtons.Left)
            {
                _source.SetCollapsed(index, !_row.Collapsed);
                Refresh();
            }

            _focus = index;
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(_source.IdOf(index)))
            {
                SelectOnly(index);
            }

            _focus = index;
            Invalidate();
            MoreRequested?.Invoke(this, (index, PointToScreen(e.Location)));
            return;
        }

        ApplyClickSelection(index, ModifierKeys);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
            return;
        }

        if (e.Button != MouseButtons.Left || Mode != ListMode.TwoLine || _hover < 0)
        {
            return;
        }

        HoverPart part = HoverPartAt(_hover, e.Location);
        if (part == HoverPart.Details)
        {
            DetailsClicked?.Invoke(this, _hover);
        }
        else if (part == HoverPart.More)
        {
            Rectangle row = RowBounds(_hover);
            MoreRequested?.Invoke(this, (_hover, PointToScreen(new Point(row.Right - S(16 + 28), row.Y + S(36)))));
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = IndexAt(e.Location);
        if (index >= 0 && e.Button == MouseButtons.Left && _source is not null)
        {
            _source.Fill(index, _row);
            if (!_row.IsGroup)
            {
                ItemActivated?.Invoke(this, index);
            }
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Left or Keys.Right or Keys.Enter or Keys.Space or Keys.Apps
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_source is null || _source.Count == 0)
        {
            return;
        }

        int page = Math.Max(1, (Height - S(TopInset)) / S(RowHeight));
        int target = _focus;
        switch (e.KeyCode)
        {
            case Keys.Up:
                target = Math.Max(0, _focus - 1);
                break;
            case Keys.Down:
                target = Math.Min(_source.Count - 1, _focus + 1);
                break;
            case Keys.PageUp:
                target = Math.Max(0, _focus - page);
                break;
            case Keys.PageDown:
                target = Math.Min(_source.Count - 1, _focus + page);
                break;
            case Keys.Home:
                target = 0;
                break;
            case Keys.End:
                target = _source.Count - 1;
                break;
            case Keys.A when e.Control:
                SelectAll();
                e.Handled = true;
                return;
            case Keys.Left:
            case Keys.Right:
                if (_focus >= 0)
                {
                    _source.Fill(_focus, _row);
                    if (_row.IsGroup && _row.Collapsed == (e.KeyCode == Keys.Right))
                    {
                        _source.SetCollapsed(_focus, e.KeyCode == Keys.Left);
                        Refresh();
                    }
                }

                e.Handled = true;
                return;
            case Keys.Space:
                if (_focus >= 0)
                {
                    long id = _source.IdOf(_focus);
                    if (id >= 0)
                    {
                        if (!_selected.Remove(id))
                        {
                            _selected.Add(id);
                        }

                        Invalidate();
                        SelectionChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                e.Handled = true;
                return;
            case Keys.Enter:
                if (_focus >= 0)
                {
                    _source.Fill(_focus, _row);
                    if (_row.IsGroup)
                    {
                        _source.SetCollapsed(_focus, !_row.Collapsed);
                        Refresh();
                    }
                    else
                    {
                        ItemActivated?.Invoke(this, _focus);
                    }
                }

                e.Handled = true;
                return;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                if (_focus >= 0)
                {
                    Rectangle row = RowBounds(_focus);
                    MoreRequested?.Invoke(this, (_focus, PointToScreen(new Point(row.X + S(44), row.Bottom))));
                }

                e.Handled = true;
                return;
            default:
                return;
        }

        e.Handled = true;
        MoveFocus(target, e.Shift, e.Control);
    }

    /// <summary>Applies a click with modifiers to the selection (public for tests).</summary>
    public void ApplyClickSelection(int index, Keys modifiers)
    {
        if (_source is null)
        {
            return;
        }

        long id = _source.IdOf(index);
        if ((modifiers & Keys.Shift) != 0 && _anchor >= 0)
        {
            if ((modifiers & Keys.Control) == 0)
            {
                _selected.Clear();
            }

            SelectRange(_anchor, index);
        }
        else if ((modifiers & Keys.Control) != 0)
        {
            if (!_selected.Remove(id))
            {
                _selected.Add(id);
            }

            _anchor = index;
        }
        else
        {
            SelectOnly(index);
            _anchor = index;
        }

        _focus = index;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Color DrawRowBackground(Graphics g, Rectangle row, bool selected, bool hover, int dpi, ThemeTokens t)
    {
        Color background = t.Surface;
        if (selected || hover)
        {
            background = selected ? t.Selected : t.Hover;
            g.FillRectangle(GdiCache.Brush(background), row);
            float barHeight = Math.Min(row.Height - Dpi.ScaleF(12, dpi), Dpi.ScaleF(32, dpi));
            if (row.Height <= Dpi.Scale(36, dpi))
            {
                barHeight = Dpi.ScaleF(24, dpi);
            }

            KitPaint.FillRoundedRect(g, new RectangleF(row.X, row.Y + ((row.Height - barHeight) / 2f), Dpi.ScaleF(3, dpi), barHeight), Dpi.ScaleF(1.5f, dpi), selected ? t.Accent : t.StrokeStrong);
            if (t.IsHighContrast && hover && !selected)
            {
                KitPaint.StrokeRoundedRect(g, row, 0, t.Accent, Dpi.ScaleF(2, dpi));
            }
        }

        return background;
    }

    private static void DrawHoverActions(Graphics g, Rectangle row, int dpi, HoverPart hot)
    {
        Rectangle more = new(row.Right - Dpi.Scale(16 + 28, dpi), row.Y + Dpi.Scale(8, dpi), Dpi.Scale(28, dpi), Dpi.Scale(28, dpi));
        int detailsWidth = Dpi.Scale(ButtonPainter.MeasureWidth(Strings.Kit.Details, null, false, ButtonKind.Secondary, dpi), dpi);
        Rectangle details = new(more.X - Dpi.Scale(4, dpi) - detailsWidth, more.Y, detailsWidth, more.Height);
        ButtonPainter.Paint(g, details, ButtonKind.Secondary, hot == HoverPart.Details ? VisualState.Hover : VisualState.Rest, Strings.Kit.Details, null, false, dpi);
        IconButton.Draw(g, more, IconName.More, hot == HoverPart.More ? VisualState.Hover : VisualState.Rest, 0, dpi);
    }

    private HoverPart HoverPartAt(int index, Point point)
    {
        Rectangle row = RowBounds(index);
        Rectangle more = new(row.Right - S(16 + 28), row.Y + S(8), S(28), S(28));
        int detailsWidth = S(ButtonPainter.MeasureWidth(Strings.Kit.Details, null, false, ButtonKind.Secondary, DeviceDpi));
        Rectangle details = new(more.X - S(4) - detailsWidth, more.Y, detailsWidth, more.Height);
        return more.Contains(point) ? HoverPart.More : details.Contains(point) ? HoverPart.Details : HoverPart.None;
    }

    private void UpdateHover(Point point)
    {
        int index = ScrollZone().Contains(point) ? -1 : IndexAt(point);
        HoverPart part = HoverPart.None;
        if (index >= 0 && _source is not null)
        {
            _source.Fill(index, _row);
            if (_row.IsGroup)
            {
                index = -1;
            }
            else if (Mode == ListMode.TwoLine)
            {
                part = HoverPartAt(index, point);
            }
        }

        if (index != _hover || part != _hoverPart)
        {
            if (_hover >= 0)
            {
                Invalidate(RowBounds(_hover));
            }

            _hover = index;
            _hoverPart = part;
            if (_hover >= 0)
            {
                Invalidate(RowBounds(_hover));
            }
        }
    }

    private void HeaderClick(int x)
    {
        Span<int> widths = stackalloc int[Math.Max(1, _columns.Count)];
        ColumnWidths(_columns, Width, DeviceDpi, widths);
        int left = 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            if (x >= left && x < left + widths[i])
            {
                if (!_columns[i].Sortable)
                {
                    return;
                }

                _sortDescending = i == _sortColumn && !_sortDescending;
                _sortColumn = i;
                Invalidate();
                SortChanged?.Invoke(this, (i, _sortDescending));
                return;
            }

            left += widths[i];
        }
    }

    private void MoveFocus(int target, bool extend, bool keepSelection)
    {
        if (_source is null || target < 0)
        {
            return;
        }

        _focus = target;
        if (extend && _anchor >= 0)
        {
            _selected.Clear();
            SelectRange(_anchor, target);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!keepSelection)
        {
            _anchor = target;
            SelectOnly(target);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        EnsureVisible(target);
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, target);
    }

    private void SelectOnly(int index)
    {
        _selected.Clear();
        long id = _source!.IdOf(index);
        if (id >= 0)
        {
            _selected.Add(id);
        }
    }

    private void SelectRange(int from, int to)
    {
        int start = Math.Min(from, to);
        int end = Math.Max(from, to);
        for (int i = start; i <= end; i++)
        {
            long id = _source!.IdOf(i);
            if (id >= 0)
            {
                _selected.Add(id);
            }
        }
    }

    private int HeightOf(int index)
    {
        if (_source is null || index < 0 || index >= _source.Count)
        {
            return RowHeight;
        }

        return _source.IdOf(index) < 0 ? GroupHeight : RowHeight;
    }

    /// <summary>Content y of a row: items are 44 (or 36) px, group headers 32 px.</summary>
    private int ContentYOf(int index)
    {
        if (_source is null)
        {
            return index * S(RowHeight);
        }

        int groups = Mode == ListMode.Table ? 0 : _source.GroupsBefore(index);
        return (index * S(RowHeight)) - (groups * (S(RowHeight) - S(GroupHeight)));
    }

    /// <summary>The row containing content y (binary search over <see cref="ContentYOf"/>).</summary>
    private int IndexAtContentY(int y)
    {
        int count = Count;
        if (count == 0)
        {
            return 0;
        }

        int low = 0;
        int high = count - 1;
        while (low < high)
        {
            int mid = low + ((high - low + 1) / 2);
            if (ContentYOf(mid) <= y)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return y >= ContentYOf(count) ? count : low;
    }

    private int YOf(int index) => S(TopInset) + ContentYOf(index) - _scroll;

    private Rectangle RowBounds(int index) => new(0, YOf(index), Width, S(HeightOf(index)));

    private int ContentHeight() => ContentYOf(Count) + S(_bottomPadding);

    private int ViewHeight() => Math.Max(0, Height - S(TopInset));

    private void ClampScroll()
    {
        int max = Math.Max(0, ContentHeight() - ViewHeight());
        _scroll = Math.Clamp(_scroll, 0, max);
    }

    private Rectangle ScrollZone() => new(Width - S(16), S(TopInset), S(16), ViewHeight());

    private Rectangle? ThumbBounds()
    {
        int content = ContentHeight();
        int view = ViewHeight();
        if (content <= view || view <= 0)
        {
            return null;
        }

        int trackTop = S(TopInset) + S(4);
        int track = view - S(8);
        int length = Math.Max(S(32), (int)((long)track * view / content));
        int max = content - view;
        int top = trackTop + (int)((long)(track - length) * _scroll / Math.Max(1, max));
        int width = S(_scrollHover || _dragging ? 10 : 6);
        return new Rectangle(Width - S(3) - width, top, width, length);
    }

    private void DragThumb(int y)
    {
        int content = ContentHeight();
        int view = ViewHeight();
        int track = view - S(8);
        int length = Math.Max(S(32), (int)((long)track * view / Math.Max(1, content)));
        int top = y - _dragOffset - S(TopInset) - S(4);
        double fraction = Math.Clamp(top / (double)Math.Max(1, track - length), 0, 1);
        _scroll = (int)(fraction * Math.Max(0, content - view));
        Invalidate();
    }

    /// <summary>Exposes the visible rows to UI Automation with name, role and selection state (task UK-12).</summary>
    private sealed class ListAccessibleObject(VirtualList owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.List;

        public override int GetChildCount() => owner.Count;

        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner.Count ? new RowAccessibleObject(owner, this, index) : null;

        public override AccessibleObject? GetFocused() => owner._focus >= 0 ? GetChild(owner._focus) : base.GetFocused();
    }

    private sealed class RowAccessibleObject(VirtualList list, AccessibleObject parent, int index) : AccessibleObject
    {
        public override AccessibleObject Parent => parent;

        public override AccessibleRole Role => list.Mode == ListMode.Table ? AccessibleRole.Row : AccessibleRole.ListItem;

        public override string? Name
        {
            get
            {
                ListRow row = new();
                list._source?.Fill(index, row);
                string tier = row.Severity is Severity s ? BadgePainter.TierWord(s) : string.Empty;
                return string.Join(", ", new[] { row.Line1, tier, row.Line2 ?? string.Empty }.Where(p => p.Length > 0));
            }
        }

        public override AccessibleStates State
        {
            get
            {
                AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (list._source is IListSource source && list._selected.Contains(source.IdOf(index)))
                {
                    state |= AccessibleStates.Selected;
                }

                if (index == list._focus)
                {
                    state |= AccessibleStates.Focused;
                }

                Rectangle bounds = list.RowBounds(index);
                if (bounds.Bottom < 0 || bounds.Top > list.Height)
                {
                    state |= AccessibleStates.Invisible | AccessibleStates.Offscreen;
                }

                return state;
            }
        }

        public override Rectangle Bounds => list.RectangleToScreen(list.RowBounds(index));
    }
}
