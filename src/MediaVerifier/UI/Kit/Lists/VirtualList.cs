using System.Drawing.Drawing2D;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Kit.Popups;
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

/// <summary>The buttons a two-line row shows on hover and keyboard focus (subplan 08: Details and More).</summary>
internal enum RowButton
{
    None,
    Details,
    More,
}

/// <summary>
/// A table column: header text, fixed logical width (0 = absorbs the remaining width), alignment. When the absorbing
/// column would get less than its <paramref name="MinWidth"/>, the columns with a <paramref name="Drop"/> order hide,
/// 1 first, so a narrow window or an open drawer keeps the table readable instead of pushing columns off the edge.
/// </summary>
internal sealed record ListColumn(string Header, int Width, bool RightAligned = false, bool Sortable = true, int Pad = 16, int Drop = 0, int MinWidth = 80);

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

    /// <summary>A two-line row's column between the text and the folder (Issues: the finding's title).</summary>
    public string? Column;

    /// <summary>Line 2 when the list is too narrow for <see cref="Column"/>: the column's text in front.</summary>
    public string? Line2Narrow;
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
        Column = null;
        Line2Narrow = null;
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

    /// <summary>
    /// A stable id per row: selection, keyboard focus and the row at the top follow it when the data changes. Group
    /// headers and the summary row use negative ids and are never selected.
    /// </summary>
    long IdOf(int index);

    void Fill(int index, ListRow row);

    /// <summary>Expands or collapses a group header row; for the filter summary row, follows its link.</summary>
    void SetCollapsed(int index, bool collapsed)
    {
    }

    /// <summary>
    /// Number of group header rows before <paramref name="index"/>. Group headers are shorter than items,
    /// so the list positions rows from this; sources with groups answer it in O(log groups).
    /// </summary>
    int GroupsBefore(int index) => 0;

    /// <summary>
    /// The row that shows <paramref name="id"/> now, or -1 when none does (the item is gone or inside a collapsed group).
    /// Null when the source keeps no id map: after a data change the list then finds its rows with one pass over
    /// <see cref="IdOf"/>. A source with many rows can answer from a map built with its data, off the UI thread.
    /// </summary>
    int? IndexOf(long id) => null;
}

/// <summary>Where a two-line row puts its parts; device pixels. A zero <see cref="FolderWidth"/> hides the folder.</summary>
internal readonly record struct TwoLineLayout(bool ShowColumn, int ColumnX, int ColumnRight, int FolderX, int FolderWidth, int TextRight);

/// <summary>
/// The virtual list of subplan 08 (task UK-07): only visible rows are drawn, the row height is fixed,
/// and scrolling allocates nothing. Two-line rows with group headers, or one-line table rows under a
/// sortable header; multi-select with Ctrl, Shift and Ctrl+A; hover actions (Details and More) on two-line
/// rows; skeleton rows while data loads; a custom scrollbar.
/// </summary>
/// <remarks>
/// Selection, keyboard focus, the Shift anchor and the row at the top are kept by row id: when the source's data
/// changes (<see cref="Refresh"/>, or assigning <see cref="Source"/> again) they follow their rows, and rows that are gone
/// leave the selection with a <see cref="SelectionChanged"/>, so nothing acts on a row that is no longer shown.
/// A menu or row button always acts on its own row: a right-click, the More and Details buttons and the context-menu key
/// select that row first unless it is part of the selection.
/// </remarks>
internal sealed class VirtualList : KitControl
{
    /// <summary>Width of a two-line row's column (<see cref="ListRow.Column"/>), logical pixels.</summary>
    public const int TwoLineColumnWidth = 220;

    private const int GroupHeight = 32;
    private const int HeaderHeight = 32;

    /// <summary>
    /// Right-aligned cells end 4 px before their column's edge (the next column's text starts 16 px in), so
    /// sizes and counts sit 20 px left of the following column as in the references.
    /// </summary>
    private const int RightPad = 4;

    /// <summary>A remembered row whose new index only a pass over the source can tell.</summary>
    private const int Unresolved = int.MinValue;

    private static int s_detailsDpi;
    private static int s_detailsWidth;
    private readonly ListRow _row = new();
    private readonly HashSet<long> _selected = [];
    private readonly HashSet<long> _kept = [];
    private readonly DeactivateHook _deactivation;
    private IListSource? _source;
    private IReadOnlyList<ListColumn> _columns = [];
    private GraphicsPath? _framePath;
    private Size _frameSize;
    private int _frameDpi;
    private DoubleClickGuard? _guard;
    private int _scroll;
    private int _topIndex = -1;
    private long? _topId;
    private int _topDelta;
    private bool _sorted;
    private int _focus = -1;
    private long? _focusId;
    private int _focusHint = -1;
    private int _anchor = -1;
    private long? _anchorId;
    private int _hover = -1;
    private RowButton _hoverButton;
    private int _hoverLinkRow = -1;
    private int _headerHover = -1;
    private int _pressedRow = -1;
    private RowButton _pressedButton;
    private int _pressedLinkRow = -1;
    private int _headerPressed = -1;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private bool _framed = true;
    private bool _scrollHover;
    private bool _dragging;
    private int _dragOffset;
    private int _bottomPadding;
    private bool _keyNavigation;
    private bool _doubleClicked;
    private long _pressTick;
    private Point _pressScreen;

    public VirtualList()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        BackColor = Tokens.Surface;
        _deactivation = new DeactivateHook(this, ClearHover);
    }

    /// <summary>Enter or double-click on an item row (double-click raises <see cref="ItemDoubleClicked"/> instead when a page handles it).</summary>
    public event EventHandler<int>? ItemActivated;

    /// <summary>A double-click on an item row, when it means something else than Enter (Issues and Library open the file).</summary>
    public event EventHandler<int>? ItemDoubleClicked;

    /// <summary>A plain left click on an item row (Issues and Library show its details).</summary>
    public event EventHandler<int>? ItemClicked;

    /// <summary>The Details button of a two-line row (the row is selected first unless it is part of the selection).</summary>
    public event EventHandler<int>? DetailsClicked;

    /// <summary>
    /// The More button, the context-menu key or a right-click: the row (selected first unless it is part of the
    /// selection) and the screen point for the menu's top-left corner.
    /// </summary>
    public event EventHandler<(int Index, Point Screen)>? MoreRequested;

    public event EventHandler? SelectionChanged;

    /// <summary>A sortable header was clicked: column index and direction.</summary>
    public event EventHandler<(int Column, bool Descending)>? SortChanged;

    public ListMode Mode { get; set; } = ListMode.TwoLine;

    /// <summary>Table rows keep a 44 px icon column; without it the first cell starts at its column's inset.</summary>
    public bool Icons { get; set; } = true;

    /// <summary>The list draws its own Surface card with a Stroke border and radius 8, and keeps its rows inside the corners.</summary>
    public bool Framed
    {
        get => _framed;
        set
        {
            _framed = value;
            Invalidate();
        }
    }

    /// <summary>Extra scroll room at the bottom (64 px while the selection bar shows); keyboard focus stays above it.</summary>
    public int BottomPadding
    {
        get => _bottomPadding;
        set
        {
            _bottomPadding = value;
            SetScroll(_scroll);
            Invalidate();
        }
    }

    public IReadOnlyList<ListColumn> Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            _headerHover = -1;
            _headerPressed = -1;
            Invalidate();
        }
    }

    /// <summary>The rows. Assigning it (again) after the data changed is the same as <see cref="Refresh"/>.</summary>
    public IListSource? Source
    {
        get => _source;
        set
        {
            _source = value;
            Resync(keepScroll: true);
        }
    }

    public int Count => _source?.Count ?? 0;

    public IReadOnlySet<long> SelectedIds => _selected;

    /// <summary>The row keyboard shortcuts act on; -1 when none (nothing focused yet, or the focused row went away).</summary>
    public int FocusedIndex => _focus;

    public int SortColumn => _sortColumn;

    /// <summary>Scroll offset in device pixels.</summary>
    public int ScrollOffset
    {
        get => _scroll;
        set
        {
            SetScroll(value);
            Invalidate();
        }
    }

    protected override bool Interactive => true;

    private int RowHeight => Mode == ListMode.TwoLine ? 44 : 36;

    private int TopInset => Mode == ListMode.Table ? HeaderHeight : 0;

    /// <summary>The focus ring shows for keyboard focus, and also once keys moved in the list after a click (focus cues
    /// stay hidden then until Tab, which left Space, Delete and Ctrl+I acting on a row nothing pointed at).</summary>
    private bool ShowFocus => Focused && (KeyboardFocused || _keyNavigation);

    /// <summary>Sets the sort indicator without raising <see cref="SortChanged"/>.</summary>
    public void SetSort(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        Invalidate();
    }

    /// <summary>
    /// The source's data changed (rows added, removed, refreshed or reordered). Selection, keyboard focus and the Shift
    /// anchor follow their rows by id; selected rows that are gone leave the selection and raise
    /// <see cref="SelectionChanged"/>. The rows in view stay in place; when none of them is left (another folder or
    /// filter), after a sort from the header, or with <paramref name="keepScroll"/> false, the list starts at the top.
    /// </summary>
    public void Refresh(bool keepScroll = true) => Resync(keepScroll);

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

        SetFocusIndex(index);
        SetAnchorIndex(index);
        if (select)
        {
            SelectOnly(index);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        EnsureVisible(index);
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, index);
    }

    /// <summary>Scrolls a row into view, above the selection bar's padding at the bottom.</summary>
    public void EnsureVisible(int index)
    {
        int top = YOf(index);
        int height = S(HeightOf(index));
        int viewTop = S(TopInset);
        int viewBottom = Math.Max(viewTop + height, Height - S(_bottomPadding));
        if (top < viewTop)
        {
            SetScroll(_scroll - (viewTop - top));
        }
        else if (top + height > viewBottom)
        {
            SetScroll(_scroll + top + height - viewBottom);
        }
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

    /// <summary>
    /// The layout of a two-line row. Hover and keyboard focus (<paramref name="actions"/>) put the Details and More
    /// buttons in place of the size and move the folder 132 px left (subplan 08). Whether the column shows, and where it
    /// and the name end, come from the resting layout, so the row does not reflow under the pointer: beside a column the
    /// folder gives up width instead of covering it.
    /// </summary>
    public static TwoLineLayout LayoutTwoLine(Rectangle row, ListRow data, bool actions, int dpi)
    {
        Font body = TypeRamp.For(dpi).Body;
        int right = row.Right - Dpi.Scale(16, dpi);
        int restFolderRight = row.Right - Dpi.Scale(100, dpi);
        int folderRight = actions ? row.Right - Dpi.Scale(100 + 132, dpi) : restFolderRight;
        int textX = row.X + Dpi.Scale(44, dpi);

        // With a column, the folder gives up width (down to 120 px) before the name keeps less than 200 px; with no room
        // for the column and 160 px of name (a narrow window with the drawer open), the column's text goes into line 2.
        bool column = data.Column is not null && restFolderRight - textX >= Dpi.Scale(TwoLineColumnWidth + 16 + 160, dpi);
        int folderMax = Dpi.Scale(264, dpi);
        if (column)
        {
            folderMax = Math.Clamp(restFolderRight - textX - Dpi.Scale(TwoLineColumnWidth + 16 + 16 + 200, dpi), Dpi.Scale(120, dpi), folderMax);
        }

        // At the same place in every row, so it reads as a column.
        int columnRight = data.Folder is null ? restFolderRight : restFolderRight - folderMax - Dpi.Scale(16, dpi);
        int columnX = columnRight - Dpi.Scale(TwoLineColumnWidth, dpi);
        int actionsLeft = HoverButtons(row, dpi).Details.X;
        if (column && actions && data.Folder is null)
        {
            columnRight = Math.Min(columnRight, actionsLeft - Dpi.Scale(16, dpi));
        }

        int folderWidth = 0;
        if (data.Folder is not null)
        {
            int room = column ? folderRight - columnRight - Dpi.Scale(16, dpi) : folderMax;
            folderWidth = Math.Max(0, Math.Min(Math.Min(KitPaint.MeasureWidth(data.Folder, body), folderMax), room));
        }

        int textRight = column ? columnX - Dpi.Scale(16, dpi)
            : data.Folder is not null ? folderRight - folderWidth - Dpi.Scale(16, dpi)
            : (actions ? actionsLeft : right - Dpi.Scale(64, dpi)) - Dpi.Scale(16, dpi);
        return new TwoLineLayout(column, columnX, columnRight, folderRight - folderWidth, folderWidth, textRight);
    }

    /// <summary>
    /// Paints a two-line row (the harness renders the reference rows through this). <paramref name="hot"/> is the button
    /// under the pointer, <paramref name="pressed"/> whether it is held down.
    /// </summary>
    public static void DrawTwoLineRow(Graphics g, Rectangle row, ListRow data, bool selected, bool hover, bool focused, int dpi, RowButton hot = RowButton.None, bool pressed = false)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        Color background = DrawRowBackground(g, row, selected, hover, dpi, t);
        if (!data.Loaded)
        {
            DrawSkeleton(g, row, dpi, t);
        }
        else
        {
            if (data.Severity is Severity severity)
            {
                IconRenderer.DrawSeverity(g, severity, row.X + Dpi.ScaleF(16, dpi), row.Y + Dpi.ScaleF(14, dpi), 16, dpi, t.ForSeverity(severity).Text, background);
            }

            bool actions = hover || focused;
            TwoLineLayout layout = LayoutTwoLine(row, data, actions, dpi);
            int textX = row.X + Dpi.Scale(44, dpi);
            if (data.Folder is not null && layout.FolderWidth > 0)
            {
                KitPaint.DrawText(g, data.Folder, type.Body, t.Text3, layout.FolderX, row.Y + Dpi.Scale(22, dpi), layout.FolderWidth + 1, TextFormatFlags.Right);
            }

            if (!actions && data.Size is not null)
            {
                int right = row.Right - Dpi.Scale(16, dpi);
                int sizeWidth = KitPaint.MeasureWidth(data.Size, type.Body);
                KitPaint.DrawText(g, data.Size, type.Body, t.Text2, right - sizeWidth, row.Y + Dpi.Scale(22, dpi), sizeWidth + 1, TextFormatFlags.Right);
            }

            if (layout.ShowColumn)
            {
                KitPaint.DrawText(g, data.Column!, type.Body, t.Text, layout.ColumnX, row.Y + Dpi.Scale(22, dpi), layout.ColumnRight - layout.ColumnX);
            }

            KitPaint.DrawText(g, data.Line1, type.BodyStrong, t.Text, textX, row.Y + Dpi.Scale(13, dpi), layout.TextRight - textX);
            if ((layout.ShowColumn ? data.Line2 : data.Line2Narrow ?? data.Line2) is string line2)
            {
                KitPaint.DrawText(g, line2, type.Body, t.Text2, textX, row.Y + Dpi.Scale(31, dpi), layout.TextRight - textX);
            }

            KitPaint.HorizontalLine(g, Color.FromArgb(153, t.Stroke), textX, row.Right, row.Bottom - Dpi.ScaleF(1, dpi), Dpi.ScaleF(1, dpi));
            if (actions)
            {
                DrawHoverActions(g, row, dpi, hot, pressed);
            }
        }

        if (focused)
        {
            KitPaint.DrawFocusRing(g, Rectangle.Inflate(row, -Dpi.Scale(3, dpi), -Dpi.Scale(3, dpi)), Dpi.ScaleF(4, dpi), dpi);
        }
    }

    /// <summary>Paints a group header row: chevron at +14, severity at +38, label at +62, count 8 px after; hover as a row.</summary>
    public static void DrawGroupHeader(Graphics g, Rectangle row, ListRow data, bool focused, int dpi, bool hover = false)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        Color background = hover ? DrawRowBackground(g, row, false, true, dpi, t) : t.Surface;
        if (!hover)
        {
            g.FillRectangle(GdiCache.Brush(Color.FromArgb(140, t.Bg)), row);
        }

        int centreY = row.Y + (row.Height / 2);
        IconRenderer.Draw(g, data.Collapsed ? IconName.ChevronRight : IconName.ChevronDown, row.X + Dpi.ScaleF(14, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text2);
        int labelX = row.X + Dpi.Scale(38, dpi);
        if (data.Severity is Severity severity)
        {
            IconRenderer.DrawSeverity(g, severity, labelX, centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.ForSeverity(severity).Text, background);
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

    /// <summary>
    /// The filter summary row: text in Body Text2 at +16, then the link in Accent, underlined (AccentHover on hover,
    /// AccentPressed while pressed). Keyboard focus rings the link, the row's only action.
    /// </summary>
    public static void DrawSummaryRow(Graphics g, Rectangle row, ListRow data, int dpi, bool focused = false, VisualState link = VisualState.Rest)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        int centreY = row.Y + (row.Height / 2);
        int x = row.X + Dpi.Scale(16, dpi);
        int width = KitPaint.MeasureWidth(data.Line1, type.Body);
        KitPaint.DrawText(g, data.Line1, type.Body, t.Text2, x, centreY, width + 1);
        if (data.Link is not null)
        {
            (int linkX, int linkWidth) = SummaryLink(row, data, dpi);
            Color color = link.HasFlag(VisualState.Pressed) ? t.AccentPressed : link.HasFlag(VisualState.Hover) ? t.AccentHover : t.Accent;
            KitPaint.DrawText(g, data.Link, type.Body, color, linkX, centreY, linkWidth + 1);

            // High contrast has one link colour: hover thickens the underline instead.
            float underline = Dpi.ScaleF(t.IsHighContrast && link != VisualState.Rest ? 2 : 1, dpi);
            KitPaint.HorizontalLine(g, color, linkX, linkX + linkWidth, centreY + Dpi.ScaleF(8.5f, dpi), underline);
            if (focused)
            {
                KitPaint.DrawFocusRing(g, new Rectangle(linkX - Dpi.Scale(2, dpi), centreY - Dpi.Scale(10, dpi), linkWidth + Dpi.Scale(4, dpi), Dpi.Scale(20, dpi)), Dpi.ScaleF(2, dpi), dpi);
            }
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
    /// Paints a table header: 32 px, Bg at 55 %, CaptionStrong Text3, 1 px Stroke below, sort arrow after the label. The
    /// sortable column under the pointer (<paramref name="hotColumn"/>) gets the Hover fill, Pressed while held down.
    /// </summary>
    public static void DrawTableHeader(Graphics g, Rectangle bounds, IReadOnlyList<ListColumn> columns, ReadOnlySpan<int> widths, int sortColumn, bool descending, int dpi, bool icons = true, int hotColumn = -1, bool hotPressed = false)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        g.FillRectangle(GdiCache.Brush(Color.FromArgb(140, t.Bg)), bounds);
        int x = bounds.X;
        int centreY = bounds.Y + (bounds.Height / 2);
        for (int i = 0; i < columns.Count; i++)
        {
            ListColumn column = columns[i];
            int width = widths[i];
            if (width <= 0)
            {
                // Hidden in a narrow table.
                continue;
            }

            if (i == hotColumn)
            {
                Rectangle cell = new(x, bounds.Y, width, bounds.Height - Dpi.Scale(1, dpi));
                g.FillRectangle(GdiCache.Brush(hotPressed ? t.Pressed : t.Hover), cell);
                if (t.IsHighContrast)
                {
                    KitPaint.StrokeRoundedRect(g, cell, 0, t.Accent, Dpi.ScaleF(2, dpi));
                }
            }

            int labelWidth = KitPaint.MeasureWidth(column.Header, type.CaptionStrong);
            int labelX = column.RightAligned ? x + width - Dpi.Scale(RightPad, dpi) - labelWidth : x + (i == 0 && icons ? Dpi.Scale(44, dpi) : Dpi.Scale(column.Pad, dpi));
            KitPaint.DrawText(g, column.Header, type.CaptionStrong, t.Text3, labelX, centreY, labelWidth + 1);
            if (i == sortColumn)
            {
                IconRenderer.Draw(g, descending ? IconName.SortDown : IconName.SortUp, labelX + labelWidth + Dpi.ScaleF(4, dpi), centreY - Dpi.ScaleF(6, dpi), 12, dpi, t.Text3);
            }

            x += width;
        }

        KitPaint.HorizontalLine(g, t.Stroke, bounds.X, bounds.Right, bounds.Bottom - Dpi.ScaleF(1, dpi), Dpi.ScaleF(1, dpi));
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
        for (int i = 0; i < columns.Count; i++)
        {
            int width = widths[i];
            string? text = i == 0 ? data.Line1 : data.Cells[i];
            if (text is not null && width > 0)
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

    /// <summary>
    /// Column widths for a total width: fixed columns keep theirs; the first zero-width column absorbs the rest. When
    /// that would be less than its <see cref="ListColumn.MinWidth"/>, columns with a drop order get width 0, 1 first.
    /// </summary>
    public static void ColumnWidths(IReadOnlyList<ListColumn> columns, int total, int dpi, Span<int> widths)
    {
        int fixedTotal = 0;
        int absorbing = -1;
        int lastDrop = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            lastDrop = Math.Max(lastDrop, columns[i].Drop);
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

        if (absorbing < 0)
        {
            return;
        }

        int floor = Dpi.Scale(columns[absorbing].MinWidth, dpi);
        for (int order = 1; order <= lastDrop && total - fixedTotal < floor; order++)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].Drop == order && i != absorbing)
                {
                    fixedTotal -= widths[i];
                    widths[i] = 0;
                }
            }
        }

        widths[absorbing] = Math.Max(Dpi.Scale(80, dpi), total - fixedTotal);
    }

    /// <summary>Applies a click with modifiers to the selection (public for tests).</summary>
    public void ApplyClickSelection(int index, Keys modifiers)
    {
        if (_source is null || index < 0 || index >= _source.Count)
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
            if (id >= 0 && !_selected.Remove(id))
            {
                _selected.Add(id);
            }

            SetAnchorIndex(index);
        }
        else
        {
            SelectOnly(index);
            SetAnchorIndex(index);
        }

        SetFocusIndex(index);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
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

        GraphicsState saved = g.Save();

        // Row and header fills are rectangles: inside a frame they keep to its rounded corners.
        if (_framed)
        {
            g.SetClip(FramePath(), CombineMode.Intersect);
        }

        if (Mode == ListMode.Table)
        {
            DrawTableHeader(g, new Rectangle(0, 0, Width, S(HeaderHeight)), _columns, widths, _sortColumn, _sortDescending, dpi, Icons, _headerHover, _headerPressed >= 0 && _headerPressed == _headerHover);
        }

        if (_source is not null && _source.Count > 0)
        {
            int top = S(TopInset);
            int first = IndexAtContentY(_scroll);
            int y = top + ContentYOf(first) - _scroll;
            bool showFocus = ShowFocus;
            g.SetClip(Rectangle.FromLTRB(0, top, Width, Height), CombineMode.Intersect);
            for (int i = first; i < _source.Count && y < Height; i++)
            {
                FillRow(i);
                int height = S(_row.IsGroup ? GroupHeight : RowHeight);
                Rectangle rowBounds = new(0, y, Width, height);
                bool focused = i == _focus && showFocus;
                bool hover = i == _hover && !_dragging;
                if (_row.IsSummary)
                {
                    VisualState link = i != _hoverLinkRow || _dragging ? VisualState.Rest : i == _pressedLinkRow ? VisualState.Hover | VisualState.Pressed : VisualState.Hover;
                    DrawSummaryRow(g, rowBounds, _row, dpi, focused, link);
                }
                else if (_row.IsGroup)
                {
                    DrawGroupHeader(g, rowBounds, _row, focused, dpi, hover);
                }
                else if (Mode == ListMode.TwoLine)
                {
                    bool selected = _selected.Contains(_source.IdOf(i));
                    RowButton hot = hover ? _hoverButton : RowButton.None;
                    DrawTwoLineRow(g, rowBounds, _row, selected, hover, focused, dpi, hot, hot != RowButton.None && i == _pressedRow && hot == _pressedButton);
                }
                else
                {
                    bool selected = _selected.Contains(_source.IdOf(i));
                    DrawTableRow(g, rowBounds, _row, _columns, widths, selected, hover, focused, dpi, Icons);
                }

                y += height;
            }
        }

        g.Restore(saved);
        if (ThumbBounds() is Rectangle thumb)
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
        SetScroll(_scroll);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        _deactivation.Attach();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _deactivation.Attach();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _guard?.Remove();
        base.OnHandleDestroyed(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        // Tab (not a click) into a list with nothing focused focuses the row the user looks at, so the ring shows.
        bool keyboard = MouseButtons == MouseButtons.None;
        base.OnGotFocus(e);
        if (keyboard && _focus < 0 && Count > 0)
        {
            SetFocusIndex(StartRow());
            Invalidate();
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _keyNavigation = false;
        base.OnLostFocus(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
        SetScroll(_scroll - (e.Delta * lines * S(RowHeight) / 120));
        KitScrolling.MarkHandled(e);
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetScroll(ListScrollbar.OffsetAt(e.Y - _dragOffset, S(TopInset), ViewHeight(), ContentHeight(), DeviceDpi));
            Invalidate();
            return;
        }

        bool overScroll = InScrollZone(e.Location);
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
        ClearHover();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _keyNavigation = false;
        Focus();
        if (e.Button == MouseButtons.Left && ThumbBounds() is Rectangle thumb && InScrollZone(e.Location))
        {
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Y;
                Capture = true;
            }
            else
            {
                SetScroll(_scroll + ((e.Y < thumb.Y ? -1 : 1) * Math.Max(S(RowHeight), Height - S(TopInset + RowHeight))));
            }

            Invalidate();
            return;
        }

        if (Mode == ListMode.Table && e.Y < S(HeaderHeight))
        {
            // Sorts on release over the same column, like a button; other buttons do not sort.
            _headerPressed = e.Button == MouseButtons.Left ? SortableColumnAt(e.X) : -1;
            Invalidate(new Rectangle(0, 0, Width, S(HeaderHeight)));
            return;
        }

        int index = IndexAt(e.Location);
        if (index < 0 || _source is null || e.Button is not (MouseButtons.Left or MouseButtons.Right))
        {
            return;
        }

        FillRow(index);
        if (_row.IsGroup)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            SetFocusIndex(index);
            if (_row.IsSummary)
            {
                // Only the link acts, on release over it.
                _pressedLinkRow = SummaryLinkAt(index, e.Location) ? index : -1;
                Invalidate(RowBounds(index));
            }
            else
            {
                _source.SetCollapsed(index, !_row.Collapsed);
                Resync(keepScroll: true);
            }

            return;
        }

        if (e.Button == MouseButtons.Left && Mode == ListMode.TwoLine && ButtonAt(index, e.Location) is RowButton button && button != RowButton.None)
        {
            // Details and More act on release over the same button.
            _pressedRow = index;
            _pressedButton = button;
            Invalidate(RowBounds(index));
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            TargetRow(index);
            MoreRequested?.Invoke(this, (index, PointToScreen(e.Location)));
            return;
        }

        _pressTick = Environment.TickCount64;
        _pressScreen = PointToScreen(e.Location);
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

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_headerPressed >= 0)
        {
            int column = _headerPressed;
            _headerPressed = -1;
            Invalidate(new Rectangle(0, 0, Width, S(HeaderHeight)));
            if (e.Y >= 0 && e.Y < S(HeaderHeight) && SortableColumnAt(e.X) == column)
            {
                Sort(column);
            }

            return;
        }

        if (_pressedLinkRow >= 0)
        {
            int summary = _pressedLinkRow;
            _pressedLinkRow = -1;
            Invalidate(RowBounds(summary));
            if (IndexAt(e.Location) == summary && SummaryLinkAt(summary, e.Location))
            {
                FollowSummaryLink(summary);
            }

            return;
        }

        if (_pressedButton != RowButton.None)
        {
            RowButton button = _pressedButton;
            int row = _pressedRow;
            _pressedButton = RowButton.None;
            _pressedRow = -1;
            if (row < Count)
            {
                Invalidate(RowBounds(row));
            }

            if (row < Count && IndexAt(e.Location) == row && ButtonAt(row, e.Location) == button)
            {
                PressRowButton(row, button);
            }

            return;
        }

        if (_doubleClicked)
        {
            // The release of a double-click's second press: the press opened the file, not the details again.
            _doubleClicked = false;
            return;
        }

        if (ModifierKeys == Keys.None && _source is not null && IndexAt(e.Location) is int index && index >= 0 && index == _focus)
        {
            // A plain click on an item (not a group, not part of a Ctrl or Shift selection).
            FillRow(index);
            if (!_row.IsGroup)
            {
                if (ItemClicked is not null && (ItemDoubleClicked ?? ItemActivated) is not null)
                {
                    _guard?.Remove();
                    _guard = new DoubleClickGuard(this, index, _source.IdOf(index), _pressScreen, _pressTick);
                }

                ItemClicked?.Invoke(this, index);
            }
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _guard?.Remove();
        if (e.Button != MouseButtons.Left || _pressedButton != RowButton.None || _pressedLinkRow >= 0 || _source is null)
        {
            // A double-click on a row button or the summary link is two presses of it, not an open.
            return;
        }

        int index = IndexAt(e.Location);
        if (index >= 0)
        {
            FillRow(index);
            if (!_row.IsGroup)
            {
                _doubleClicked = true;
                (ItemDoubleClicked ?? ItemActivated)?.Invoke(this, index);
            }
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        // A dialog or another window took the mouse mid-drag or mid-press: without the release, the next plain mouse moves
        // kept dragging the thumb.
        if (!Capture && (_dragging || _pressedButton != RowButton.None || _pressedLinkRow >= 0 || _headerPressed >= 0))
        {
            _dragging = false;
            _pressedButton = RowButton.None;
            _pressedRow = -1;
            _pressedLinkRow = -1;
            _headerPressed = -1;
            Invalidate();
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
        if (!_keyNavigation)
        {
            _keyNavigation = true;
            Invalidate();
        }

        // A key the page handled (Alt+Enter, Ctrl+Enter in Issues) is not also an Enter here.
        if (e.Handled || _source is null || _source.Count == 0)
        {
            return;
        }

        int count = _source.Count;
        int page = Math.Max(1, (Height - S(TopInset)) / S(RowHeight));
        int target;
        switch (e.KeyCode)
        {
            case Keys.Up:
                target = _focus < 0 ? StartRow() : Math.Max(0, _focus - 1);
                break;
            case Keys.Down:
                target = _focus < 0 ? StartRow() : Math.Min(count - 1, _focus + 1);
                break;
            case Keys.PageUp:
                target = _focus < 0 ? StartRow() : Math.Max(0, _focus - page);
                break;
            case Keys.PageDown:
                target = _focus < 0 ? StartRow() : Math.Min(count - 1, _focus + page);
                break;
            case Keys.Home:
                target = 0;
                break;
            case Keys.End:
                target = count - 1;
                break;
            case Keys.A when e.Control:
                SelectAll();
                e.Handled = true;
                return;
            case Keys.Left:
            case Keys.Right:
                if (_focus >= 0)
                {
                    // Group headers collapse and expand; the summary row has nothing to collapse.
                    FillRow(_focus);
                    if (_row.IsGroup && !_row.IsSummary && _row.Collapsed == (e.KeyCode == Keys.Right))
                    {
                        _source.SetCollapsed(_focus, e.KeyCode == Keys.Left);
                        Resync(keepScroll: true);
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

                        SetAnchorIndex(_focus);
                        Invalidate();
                        SelectionChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                e.Handled = true;
                return;
            case Keys.Enter:
                if (_focus >= 0)
                {
                    FillRow(_focus);
                    if (_row.IsSummary)
                    {
                        FollowSummaryLink(_focus);
                    }
                    else if (_row.IsGroup)
                    {
                        _source.SetCollapsed(_focus, !_row.Collapsed);
                        Resync(keepScroll: true);
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
                    FillRow(_focus);
                    if (!_row.IsGroup)
                    {
                        int row = _focus;
                        EnsureVisible(row);
                        TargetRow(row);
                        Rectangle bounds = RowBounds(row);
                        MoreRequested?.Invoke(this, (row, PointToScreen(new Point(bounds.X + S(44), bounds.Bottom))));
                    }
                }

                e.Handled = true;
                return;
            default:
                return;
        }

        e.Handled = true;
        MoveFocus(target, e.Shift, e.Control);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _guard?.Remove();
            _deactivation.Dispose();
            _framePath?.Dispose();
            _framePath = null;
        }

        base.Dispose(disposing);
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

    /// <summary>The Details and More buttons of a two-line row: More 28 px square 16 px from the right, Details 4 px before it.</summary>
    private static (Rectangle Details, Rectangle More) HoverButtons(Rectangle row, int dpi)
    {
        Rectangle more = new(row.Right - Dpi.Scale(16 + 28, dpi), row.Y + Dpi.Scale(8, dpi), Dpi.Scale(28, dpi), Dpi.Scale(28, dpi));
        int detailsWidth = DetailsWidth(dpi);
        return (new Rectangle(more.X - Dpi.Scale(4, dpi) - detailsWidth, more.Y, detailsWidth, more.Height), more);
    }

    /// <summary>The Details button's width, measured once per DPI (it was measured twice per hovered row and paint).</summary>
    private static int DetailsWidth(int dpi)
    {
        if (s_detailsDpi != dpi)
        {
            s_detailsWidth = Dpi.Scale(ButtonPainter.MeasureWidth(Strings.Kit.Details, null, false, ButtonKind.Secondary, dpi), dpi);
            s_detailsDpi = dpi;
        }

        return s_detailsWidth;
    }

    private static void DrawHoverActions(Graphics g, Rectangle row, int dpi, RowButton hot, bool pressed)
    {
        (Rectangle details, Rectangle more) = HoverButtons(row, dpi);
        ButtonPainter.Paint(g, details, ButtonKind.Secondary, StateOf(RowButton.Details, hot, pressed), Strings.Kit.Details, null, false, dpi);
        IconButton.Draw(g, more, IconName.More, StateOf(RowButton.More, hot, pressed), 0, dpi);

        static VisualState StateOf(RowButton button, RowButton hot, bool pressed) =>
            button != hot ? VisualState.Rest : pressed ? VisualState.Hover | VisualState.Pressed : VisualState.Hover;
    }

    /// <summary>The x range of a summary row's link, as <see cref="DrawSummaryRow"/> paints it.</summary>
    private static (int X, int Width) SummaryLink(Rectangle row, ListRow data, int dpi)
    {
        Font body = TypeRamp.For(dpi).Body;
        int x = row.X + Dpi.Scale(16, dpi) + KitPaint.MeasureWidth(data.Line1, body) + Dpi.Scale(12, dpi);
        return (x, data.Link is null ? 0 : KitPaint.MeasureWidth(data.Link, body));
    }

    /// <summary>Fills the shared row from the source, cleared first: a row never keeps another row's fields.</summary>
    private void FillRow(int index)
    {
        _row.Clear();
        _source!.Fill(index, _row);
    }

    /// <summary>Whether a client point is on the link of the summary row at <paramref name="index"/> (its text, 4 px around it).</summary>
    private bool SummaryLinkAt(int index, Point point)
    {
        FillRow(index);
        if (!_row.IsSummary || _row.Link is null)
        {
            return false;
        }

        (int x, int width) = SummaryLink(RowBounds(index), _row, DeviceDpi);
        return point.X >= x - S(4) && point.X < x + width + S(4);
    }

    /// <summary>The summary row's link clears the filters: the source hears it as the row's collapse.</summary>
    private void FollowSummaryLink(int index)
    {
        _source!.SetCollapsed(index, true);
        Resync(keepScroll: true);
    }

    /// <summary>The Details or More button under a point of a two-line item row.</summary>
    private RowButton ButtonAt(int index, Point point)
    {
        (Rectangle details, Rectangle more) = HoverButtons(RowBounds(index), DeviceDpi);
        return more.Contains(point) ? RowButton.More : details.Contains(point) ? RowButton.Details : RowButton.None;
    }

    private void PressRowButton(int row, RowButton button)
    {
        TargetRow(row);
        if (button == RowButton.Details)
        {
            DetailsClicked?.Invoke(this, row);
            return;
        }

        // The menu opens below the button, right-aligned with it (a menu is 280 px wide; it ran off the window's right edge).
        Rectangle more = HoverButtons(RowBounds(row), DeviceDpi).More;
        MoreRequested?.Invoke(this, (row, PointToScreen(new Point(more.Right - S(PopupList.MenuWidth), more.Bottom + S(4)))));
    }

    /// <summary>
    /// The row a menu or a row button acts on (the right-click rule): it takes keyboard focus, and unless it is part of
    /// the selection it becomes the selection, with <see cref="SelectionChanged"/>.
    /// </summary>
    private void TargetRow(int index)
    {
        SetFocusIndex(index);
        long id = _source!.IdOf(index);
        if (id >= 0 && !_selected.Contains(id))
        {
            SelectOnly(index);
            SetAnchorIndex(index);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Invalidate();
    }

    private void UpdateHover(Point point)
    {
        int index = -1;
        int linkRow = -1;
        int header = -1;
        RowButton button = RowButton.None;
        if (_source is not null && !InScrollZone(point) && ClientRectangle.Contains(point))
        {
            if (Mode == ListMode.Table && point.Y < S(HeaderHeight))
            {
                header = SortableColumnAt(point.X);
            }
            else if (IndexAt(point) is int row && row >= 0)
            {
                FillRow(row);
                if (_row.IsSummary)
                {
                    // Only the link of the summary row reacts to the pointer.
                    linkRow = SummaryLinkAt(row, point) ? row : -1;
                }
                else
                {
                    index = row;
                    if (!_row.IsGroup && Mode == ListMode.TwoLine)
                    {
                        button = ButtonAt(row, point);
                    }
                }
            }
        }

        SetHover(index, button, linkRow, header);
    }

    private void ClearHover() => SetHover(-1, RowButton.None, -1, -1);

    private void SetHover(int index, RowButton button, int linkRow, int header)
    {
        if (header != _headerHover)
        {
            _headerHover = header;
            Invalidate(new Rectangle(0, 0, Width, S(HeaderHeight)));
        }

        if (linkRow != _hoverLinkRow)
        {
            InvalidateRow(_hoverLinkRow);
            _hoverLinkRow = linkRow;
            InvalidateRow(linkRow);
            Cursor = linkRow >= 0 ? Cursors.Hand : Cursors.Default;
        }

        if (index != _hover || button != _hoverButton)
        {
            InvalidateRow(_hover);
            RowButton previous = _hoverButton;
            _hover = index;
            _hoverButton = button;
            InvalidateRow(_hover);
            if (button != previous)
            {
                UpdateToolTip(previous, button);
            }
        }
    }

    /// <summary>The More button's tooltip (subplan 09: More actions; subplan 08: icon buttons always have one).</summary>
    private void UpdateToolTip(RowButton previous, RowButton button)
    {
        if (button == RowButton.More)
        {
            KitToolTip.Set(this, Strings.Kit.MoreActions);

            // KitToolTip times its delay from MouseEnter; the button is part of this control, so its delay starts here.
            OnMouseEnter(EventArgs.Empty);
        }
        else if (previous == RowButton.More)
        {
            KitToolTip.Hide();
            KitToolTip.Set(this, string.Empty);
        }
    }

    /// <summary>The hover follows the rows that moved under a still pointer (keyboard scrolling, new data).</summary>
    private void RefreshHover()
    {
        Point point = IsHandleCreated ? PointToClient(MousePosition) : new Point(-1, -1);
        if (ClientRectangle.Contains(point))
        {
            UpdateHover(point);
        }
        else
        {
            ClearHover();
        }
    }

    private void InvalidateRow(int index)
    {
        if (index >= 0 && index < Count)
        {
            Invalidate(RowBounds(index));
        }
    }

    /// <summary>The sortable column under a header x, or -1.</summary>
    private int SortableColumnAt(int x)
    {
        Span<int> widths = stackalloc int[Math.Max(1, _columns.Count)];
        ColumnWidths(_columns, Width, DeviceDpi, widths);
        int left = 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            if (x >= left && x < left + widths[i])
            {
                return _columns[i].Sortable ? i : -1;
            }

            left += widths[i];
        }

        return -1;
    }

    private void Sort(int column)
    {
        _sortDescending = column == _sortColumn && !_sortDescending;
        _sortColumn = column;

        // The sorted rows arrive with the page's next data change; they show from the top (the rows that were in view
        // are somewhere else in the new order).
        _sorted = true;
        Invalidate();
        SortChanged?.Invoke(this, (column, _sortDescending));
    }

    private void MoveFocus(int target, bool extend, bool keepSelection)
    {
        if (_source is null || target < 0)
        {
            return;
        }

        SetFocusIndex(target);
        if (extend && _anchor >= 0)
        {
            _selected.Clear();
            SelectRange(_anchor, target);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!keepSelection)
        {
            SetAnchorIndex(target);
            SelectOnly(target);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        EnsureVisible(target);
        RefreshHover();
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Focus, target);
    }

    /// <summary>
    /// Where keys start when no row has focus: the row that took the place of a focused row that went away, otherwise
    /// the first row in view.
    /// </summary>
    private int StartRow()
    {
        int count = Count;
        int start = _focusHint >= 0 ? _focusHint : IndexAtContentY(_scroll);
        return Math.Clamp(start, 0, Math.Max(0, count - 1));
    }

    private void SetFocusIndex(int index)
    {
        _focus = index;
        _focusId = index >= 0 && index < Count ? _source!.IdOf(index) : null;
        _focusHint = -1;
    }

    private void SetAnchorIndex(int index)
    {
        _anchor = index;
        _anchorId = index >= 0 && index < Count ? _source!.IdOf(index) : null;
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

    /// <summary>
    /// Brings selection, focus, anchor and scroll in line with the source's current data (see <see cref="Refresh"/>).
    /// Remembered rows are looked up at their old index first (unchanged data costs nothing), then through the source's
    /// id map; without one, a single pass over the ids finds them all.
    /// </summary>
    private void Resync(bool keepScroll)
    {
        keepScroll &= !_sorted;
        _sorted = false;
        int count = Count;
        bool selectionChanged = false;
        int focus = -1;
        int anchor = -1;
        int top = -1;
        if (count == 0)
        {
            selectionChanged = _selected.Count > 0;
            _selected.Clear();
        }
        else
        {
            focus = Resolve(_focusId, _focus);
            anchor = Resolve(_anchorId, _anchor);
            top = keepScroll && _scroll > 0 ? Resolve(_topId, _topIndex) : -1;
            bool scanSelection = false;
            _kept.Clear();
            foreach (long id in _selected)
            {
                if (_source!.IndexOf(id) is not int index)
                {
                    scanSelection = true;
                    break;
                }

                if (index >= 0 && index < count)
                {
                    _kept.Add(id);
                }
            }

            if (scanSelection || focus == Unresolved || anchor == Unresolved || top == Unresolved)
            {
                if (scanSelection)
                {
                    _kept.Clear();
                }

                for (int i = 0; i < count; i++)
                {
                    long id = _source!.IdOf(i);
                    focus = focus == Unresolved && id == _focusId ? i : focus;
                    anchor = anchor == Unresolved && id == _anchorId ? i : anchor;
                    top = top == Unresolved && id == _topId ? i : top;
                    if (scanSelection && _selected.Contains(id))
                    {
                        _kept.Add(id);
                    }
                }

                focus = focus == Unresolved ? -1 : focus;
                anchor = anchor == Unresolved ? -1 : anchor;
                top = top == Unresolved ? -1 : top;
            }

            if (_kept.Count < _selected.Count)
            {
                _selected.IntersectWith(_kept);
                selectionChanged = true;
            }

            _kept.Clear();
        }

        if (focus < 0 && _focus >= 0 && count > 0)
        {
            // The focused row went away: keys continue from the row that took its place.
            _focusHint = Math.Min(_focus, count - 1);
        }

        _focus = focus;
        _focusId = focus >= 0 ? _focusId : null;
        _anchor = anchor;
        _anchorId = anchor >= 0 ? _anchorId : null;
        SetScroll(!keepScroll ? 0 : top >= 0 ? ContentYOf(top) + _topDelta : _scroll);
        _pressedRow = -1;
        _pressedButton = RowButton.None;
        _pressedLinkRow = -1;
        RefreshHover();
        Invalidate();
        if (selectionChanged)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The row that holds a remembered id: the old index when it still does, the source's map when it keeps one,
    /// <see cref="Unresolved"/> when only a pass can tell; -1 for no id or a row that is gone.
    /// </summary>
    private int Resolve(long? id, int hint)
    {
        if (id is not long value)
        {
            return -1;
        }

        int count = Count;
        if (hint >= 0 && hint < count && _source!.IdOf(hint) == value)
        {
            return hint;
        }

        return _source!.IndexOf(value) is int index ? (index < count ? index : -1) : Unresolved;
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

    /// <summary>Scrolls (clamped) and remembers the row at the top, which a data change keeps in place.</summary>
    private void SetScroll(int value)
    {
        _scroll = Math.Clamp(value, 0, Math.Max(0, ContentHeight() - ViewHeight()));
        int count = Count;
        if (count == 0)
        {
            _topIndex = -1;
            _topId = null;
            _topDelta = 0;
            return;
        }

        _topIndex = Math.Min(IndexAtContentY(_scroll), count - 1);
        _topId = _source!.IdOf(_topIndex);
        _topDelta = _scroll - ContentYOf(_topIndex);
    }

    private Rectangle? ThumbBounds() => ListScrollbar.Thumb(Width, S(TopInset), ViewHeight(), ContentHeight(), _scroll, _scrollHover || _dragging, DeviceDpi);

    /// <summary>The scrollbar's pointer zone, only while there is a thumb (rows keep their right edge otherwise).</summary>
    private bool InScrollZone(Point point) => ThumbBounds() is not null && ListScrollbar.Zone(Width, S(TopInset), ViewHeight(), DeviceDpi).Contains(point);

    private GraphicsPath FramePath()
    {
        if (_framePath is null || _frameSize != Size || _frameDpi != DeviceDpi)
        {
            _framePath ??= new GraphicsPath();
            _framePath.Reset();
            IconGeometry.AddRoundedRect(_framePath, new RectangleF(0, 0, Width, Height), SF(8));
            _frameSize = Size;
            _frameDpi = DeviceDpi;
        }

        return _framePath;
    }

    private void RaiseDoubleClick(int row, long rowId)
    {
        if (_source is not null && row < _source.Count && _source.IdOf(row) == rowId)
        {
            (ItemDoubleClicked ?? ItemActivated)?.Invoke(this, row);
        }
    }

    /// <summary>
    /// A plain click raises <see cref="ItemClicked"/> at once, and the page's drawer can slide over the right part of the
    /// list before the second press of a double-click arrives: that press then hit a button in the drawer (its primary
    /// action, Show in folder) and the file never opened. For the double-click time after such a click, this filter
    /// takes a press near the clicked point on another window, with its release, as this list's double-click.
    /// </summary>
    private sealed class DoubleClickGuard : IMessageFilter
    {
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmLButtonDblClk = 0x0203;
        private readonly VirtualList _list;
        private readonly IntPtr _handle;
        private readonly int _row;
        private readonly long _rowId;
        private readonly Point _screen;
        private readonly long _deadline;
        private bool _swallowRelease;
        private bool _removed;

        public DoubleClickGuard(VirtualList list, int row, long rowId, Point screen, long pressTick)
        {
            _list = list;
            _handle = list.Handle;
            _row = row;
            _rowId = rowId;
            _screen = screen;
            _deadline = pressTick + SystemInformation.DoubleClickTime;
            Application.AddMessageFilter(this);
        }

        public void Remove()
        {
            if (!_removed)
            {
                _removed = true;
                Application.RemoveMessageFilter(this);
            }

            if (ReferenceEquals(_list._guard, this))
            {
                _list._guard = null;
            }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (_swallowRelease)
            {
                if (m.Msg == WmLButtonUp)
                {
                    Remove();
                    return true;
                }

                return false;
            }

            if (m.Msg is not (WmLButtonDown or WmLButtonDblClk))
            {
                if (Environment.TickCount64 > _deadline)
                {
                    Remove();
                }

                return false;
            }

            if (Environment.TickCount64 > _deadline || _list.IsDisposed || m.HWnd == _handle || !Near(ScreenPoint(m)))
            {
                // Too late, the list's own double-click, or a press somewhere else: not this guard's business.
                Remove();
                return false;
            }

            _swallowRelease = true;
            _list.RaiseDoubleClick(_row, _rowId);
            return true;
        }

        private static Point ScreenPoint(in Message m)
        {
            long lParam = m.LParam.ToInt64();
            Point client = new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            return Control.FromHandle(m.HWnd) is Control control ? control.PointToScreen(client) : MousePosition;
        }

        private bool Near(Point point)
        {
            Size size = SystemInformation.DoubleClickSize;
            return Math.Abs(point.X - _screen.X) <= size.Width / 2 && Math.Abs(point.Y - _screen.Y) <= size.Height / 2;
        }
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
                return string.Join(", ", new[] { row.Line1, tier, row.Column ?? string.Empty, row.Line2 ?? string.Empty }.Where(p => p.Length > 0));
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

/// <summary>
/// The painted scrollbar of the lists and the tree (subplan 08, Scrollbar): the thumb's geometry, the pointer zone at
/// the right edge, and the offset a drag puts it at, in one place.
/// </summary>
internal static class ListScrollbar
{
    /// <summary>Width of the zone at the right edge that hovers, drags and pages the thumb, logical pixels.</summary>
    public const int ZoneWidth = 16;

    /// <summary>
    /// The thumb in client coordinates: 6 px wide (10 while hovered or dragged), 3 px from the right edge, 4 px from the
    /// view's top and bottom, at least 32 px long; null when the content fits.
    /// </summary>
    public static Rectangle? Thumb(int clientWidth, int viewTop, int viewHeight, int content, int offset, bool wide, int dpi)
    {
        if (content <= viewHeight || viewHeight <= 0)
        {
            return null;
        }

        int track = viewHeight - Dpi.Scale(8, dpi);
        int length = ThumbLength(track, viewHeight, content, dpi);
        int max = content - viewHeight;
        int top = viewTop + Dpi.Scale(4, dpi) + (int)((long)(track - length) * Math.Clamp(offset, 0, max) / max);
        int width = Dpi.Scale(wide ? 10 : 6, dpi);
        return new Rectangle(clientWidth - Dpi.Scale(3, dpi) - width, top, width, length);
    }

    /// <summary>The scroll offset that puts the thumb's top edge at <paramref name="thumbTop"/> (client y).</summary>
    public static int OffsetAt(int thumbTop, int viewTop, int viewHeight, int content, int dpi)
    {
        int track = viewHeight - Dpi.Scale(8, dpi);
        int length = ThumbLength(track, viewHeight, content, dpi);
        double fraction = Math.Clamp((thumbTop - viewTop - Dpi.Scale(4, dpi)) / (double)Math.Max(1, track - length), 0, 1);
        return (int)(fraction * Math.Max(0, content - viewHeight));
    }

    /// <summary>The pointer zone at the right edge of the view.</summary>
    public static Rectangle Zone(int clientWidth, int viewTop, int viewHeight, int dpi) =>
        new(clientWidth - Dpi.Scale(ZoneWidth, dpi), viewTop, Dpi.Scale(ZoneWidth, dpi), viewHeight);

    private static int ThumbLength(int track, int viewHeight, int content, int dpi) =>
        Math.Max(Dpi.Scale(32, dpi), (int)((long)track * viewHeight / Math.Max(1, content)));
}

/// <summary>
/// Calls back when the window holding a control deactivates (subplan 08: hover is cleared on window deactivation).
/// KitControl clears only its own hover and press, not the row a list or tree shows as hovered.
/// </summary>
internal sealed class DeactivateHook(Control owner, Action deactivated) : IDisposable
{
    private Form? _form;

    /// <summary>Follows the control to its current form (call when its parent or handle changes).</summary>
    public void Attach()
    {
        Form? form = owner.FindForm();
        if (ReferenceEquals(form, _form))
        {
            return;
        }

        Dispose();
        _form = form;
        if (form is not null)
        {
            form.Deactivate += OnDeactivate;
        }
    }

    public void Dispose()
    {
        if (_form is not null)
        {
            _form.Deactivate -= OnDeactivate;
            _form = null;
        }
    }

    private void OnDeactivate(object? sender, EventArgs e) => deactivated();
}
