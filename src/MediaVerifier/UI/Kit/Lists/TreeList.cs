using MediaVerifier.Core.Model;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit.Lists;

/// <summary>One node of a <see cref="TreeList"/>. Children load lazily on first expand.</summary>
internal sealed class TreeItem(string text, object? tag = null)
{
    private List<TreeItem>? _children;

    public string Text { get; set; } = text;

    public object? Tag { get; } = tag;

    public TreeItem? Parent { get; private set; }

    /// <summary>Worst open tier in the node and below (roll-up); null shows no icon.</summary>
    public Severity? Severity { get; set; }

    public IconName Icon { get; set; } = IconName.Folder;

    public bool Expanded { get; set; }

    /// <summary>False when the node is known to have no children (no chevron).</summary>
    public bool HasChildren { get; set; } = true;

    public IReadOnlyList<TreeItem>? LoadedChildren => _children;

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public void SetChildren(IEnumerable<TreeItem> children)
    {
        _children = [.. children];
        foreach (TreeItem child in _children)
        {
            child.Parent = this;
        }

        HasChildren = _children.Count > 0;
    }
}

/// <summary>
/// The folder tree (subplan 08, task UK-08): 32 px rows inset 4 px with radius 4, 18 px per level, chevron
/// at +14, folder icon at +32, label at +56, roll-up severity icon 14 px at 16 px from the right. Lazy
/// children; Left collapses (or moves to the parent), Right expands (or moves to the first child), Enter
/// and a chevron click toggle. A right-click selects the folder under the pointer, like a left click, and asks for its
/// menu on release (<see cref="ContextMenuRequested"/>); so does the context-menu key for the selected folder. A tree
/// taller than its view shows the lists' scroll thumb.
/// </summary>
internal sealed class TreeList : KitControl
{
    private const int RowHeight = 32;
    private const int Indent = 18;
    private readonly List<TreeItem> _roots = [];
    private readonly List<TreeItem> _visible = [];
    private readonly DeactivateHook _deactivation;
    private int _scroll;
    private int _hover = -1;
    private int _focusRow = -1;
    private TreeItem? _selected;
    private TreeItem? _menuNode;
    private bool _scrollHover;
    private bool _dragging;
    private int _dragOffset;
    private bool _keyNavigation;

    public TreeList()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.Outline;
        BackColor = Tokens.Surface;
        _deactivation = new DeactivateHook(this, ClearHover);
    }

    /// <summary>Asked for a node's children the first time it expands.</summary>
    public event EventHandler<TreeItem>? LoadChildren;

    public event EventHandler? SelectionChanged;

    /// <summary>
    /// A right-click released on a folder (selected by the press), or the context-menu key (Apps, Shift+F10) on the
    /// selected one: the folder and the screen point for its menu's top-left corner. Empty space asks for nothing.
    /// </summary>
    public event EventHandler<(TreeItem Node, Point Screen)>? ContextMenuRequested;

    public IList<TreeItem> Roots => _roots;

    public TreeItem? SelectedNode
    {
        get => _selected;
        set
        {
            if (!ReferenceEquals(_selected, value))
            {
                _selected = value;
                for (TreeItem? parent = value?.Parent; parent is not null; parent = parent.Parent)
                {
                    parent.Expanded = true;
                }

                Rebuild();
                EnsureVisible(value);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IReadOnlyList<TreeItem> VisibleNodes => _visible;

    protected override bool Interactive => true;

    /// <summary>The focus ring shows for keyboard focus, and also once keys moved in the tree after a click.</summary>
    private bool ShowFocus => Focused && (KeyboardFocused || _keyNavigation);

    private int ContentHeight => S(8) + (_visible.Count * S(RowHeight));

    /// <summary>
    /// Rebuilds the visible rows after nodes were added, removed or changed. A selected node that is no longer in the
    /// tree (the roots were replaced) stops being selected, with <see cref="SelectionChanged"/>.
    /// </summary>
    public void Rebuild()
    {
        _visible.Clear();
        foreach (TreeItem root in _roots)
        {
            AddVisible(root);
        }

        bool dropped = _selected is not null && !InTree(_selected);
        if (dropped)
        {
            _selected = null;
        }

        _focusRow = -1;
        for (TreeItem? node = _selected; node is not null && _focusRow < 0; node = node.Parent)
        {
            _focusRow = _visible.IndexOf(node);
        }

        if (_menuNode is not null && !_visible.Contains(_menuNode))
        {
            _menuNode = null;
        }

        SetScroll(_scroll);
        _hover = Math.Min(_hover, _visible.Count - 1);
        Invalidate();
        if (dropped)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The node under a client point, or null (empty space, the scrollbar).</summary>
    public TreeItem? NodeAt(Point point) => !InScrollZone(point) && IndexAt(point.Y) is int index && index >= 0 ? _visible[index] : null;

    public void Toggle(TreeItem node)
    {
        if (!node.HasChildren)
        {
            return;
        }

        if (!node.Expanded && node.LoadedChildren is null)
        {
            LoadChildren?.Invoke(this, node);
        }

        node.Expanded = !node.Expanded;
        Rebuild();
    }

    /// <summary>Paints one tree row.</summary>
    public static void DrawRow(Graphics g, Rectangle bounds, TreeItem node, bool selected, bool hover, bool focused, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        Rectangle row = new(bounds.X + Dpi.Scale(4, dpi), bounds.Y, bounds.Width - Dpi.Scale(8, dpi), bounds.Height);
        Color background = t.Surface;
        if (selected || hover)
        {
            background = selected ? t.Selected : t.Hover;
            KitPaint.FillRoundedRect(g, row, Dpi.ScaleF(4, dpi), background);
            KitPaint.FillRoundedRect(g, new RectangleF(row.X, row.Y + Dpi.ScaleF(8, dpi), Dpi.ScaleF(3, dpi), Dpi.ScaleF(16, dpi)), Dpi.ScaleF(1.5f, dpi), selected ? t.Accent : t.StrokeStrong);
        }

        int level = node.Depth * Indent;
        int centreY = bounds.Y + (bounds.Height / 2);
        if (node.HasChildren)
        {
            IconRenderer.Draw(g, node.Expanded ? IconName.ChevronDown : IconName.ChevronRight, bounds.X + Dpi.ScaleF(14 + level, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text2);
        }

        IconRenderer.Draw(g, node.Icon, bounds.X + Dpi.ScaleF(32 + level, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text2);
        int labelX = bounds.X + Dpi.Scale(56 + level, dpi);
        int severityX = bounds.Right - Dpi.Scale(30, dpi);
        KitPaint.DrawText(g, node.Text, selected ? type.BodyStrong : type.Body, t.Text, labelX, centreY, severityX - labelX - Dpi.Scale(8, dpi));
        if (node.Severity is Severity severity)
        {
            IconRenderer.DrawSeverity(g, severity, severityX, centreY - Dpi.ScaleF(7, dpi), 14, dpi, t.ForSeverity(severity).Text, background);
        }

        if (focused)
        {
            KitPaint.DrawFocusRing(g, Rectangle.Inflate(row, -Dpi.Scale(2, dpi), -Dpi.Scale(2, dpi)), Dpi.ScaleF(4, dpi), dpi);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new TreeAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = Tokens;
        e.Graphics.Clear(t.Surface);
        int height = S(RowHeight);
        int top = S(4);
        int first = Math.Max(0, (_scroll - top) / height);

        // The ring marks where keys act: the selected folder, its shown ancestor while a collapsed folder hides it, or
        // the first row in view when nothing is selected.
        int ring = !ShowFocus ? -1 : _focusRow >= 0 ? _focusRow : TopRow();
        for (int i = first; i < _visible.Count; i++)
        {
            int y = top + (i * height) - _scroll;
            if (y > Height)
            {
                break;
            }

            TreeItem node = _visible[i];
            bool selected = ReferenceEquals(node, _selected);
            DrawRow(e.Graphics, new Rectangle(0, y, Width, height), node, selected, i == _hover && !_dragging, i == ring, DeviceDpi);
        }

        if (ThumbBounds() is Rectangle thumb)
        {
            VirtualList.DrawScrollThumb(e.Graphics, thumb, _scrollHover || _dragging, DeviceDpi);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
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

    protected override void OnLostFocus(EventArgs e)
    {
        _keyNavigation = false;
        base.OnLostFocus(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetScroll(ListScrollbar.OffsetAt(e.Y - _dragOffset, 0, Height, ContentHeight, DeviceDpi));
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
                SetScroll(_scroll + ((e.Y < thumb.Y ? -1 : 1) * Math.Max(S(RowHeight), Height - S(RowHeight))));
            }

            Invalidate();
            return;
        }

        _menuNode = null;
        TreeItem? node = NodeAt(e.Location);
        if (node is null)
        {
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            // The menu acts on the folder right-clicked: it becomes the selection, as with a left click.
            _menuNode = node;
            SelectedNode = node;
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (OnChevron(node, e.X))
        {
            Toggle(node);
            return;
        }

        SelectedNode = node;
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

        if (e.Button == MouseButtons.Right && _menuNode is TreeItem node)
        {
            _menuNode = null;
            if (ReferenceEquals(NodeAt(e.Location), node))
            {
                ContextMenuRequested?.Invoke(this, (node, PointToScreen(e.Location)));
            }
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // A double-click on the label toggles; on the chevron each press already toggled. Other buttons do nothing.
        if (e.Button == MouseButtons.Left && NodeAt(e.Location) is TreeItem node && !OnChevron(node, e.X))
        {
            Toggle(node);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _dragging)
        {
            // Another window took the mouse mid-drag: without the release, plain mouse moves kept dragging.
            _dragging = false;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetScroll(_scroll - (e.Delta * Math.Max(1, SystemInformation.MouseWheelScrollLines) * S(RowHeight) / 120));
        KitScrolling.MarkHandled(e);
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_keyNavigation)
        {
            _keyNavigation = true;
            Invalidate();
        }

        // A key the page handled is not also handled here.
        if (e.Handled || _visible.Count == 0)
        {
            return;
        }

        // Keys act where the ring is: the selected folder, or its shown ancestor while a collapsed folder hides it.
        TreeItem? current = _focusRow >= 0 ? _visible[_focusRow] : null;
        switch (e.KeyCode)
        {
            case Keys.Up:
                SelectedNode = _visible[_focusRow < 0 ? TopRow() : Math.Max(0, _focusRow - 1)];
                break;
            case Keys.Down:
                SelectedNode = _visible[_focusRow < 0 ? TopRow() : Math.Min(_visible.Count - 1, _focusRow + 1)];
                break;
            case Keys.Home:
                SelectedNode = _visible[0];
                break;
            case Keys.End:
                SelectedNode = _visible[^1];
                break;
            case Keys.Left when current is not null:
                if (current.Expanded && current.HasChildren)
                {
                    Toggle(current);
                }
                else if (current.Parent is not null)
                {
                    SelectedNode = current.Parent;
                }
                else
                {
                    SelectedNode = current;
                }

                break;
            case Keys.Right when current is not null:
                if (!current.Expanded && current.HasChildren)
                {
                    Toggle(current);
                }
                else if (current.Expanded && current.LoadedChildren is { Count: > 0 } children)
                {
                    SelectedNode = children[0];
                }

                break;
            case Keys.Enter when current is not null:
                Toggle(current);
                break;
            case Keys.Apps when current is not null:
            case Keys.F10 when e.Shift && current is not null:
                SelectedNode = current;
                EnsureVisible(current);
                int row = _visible.IndexOf(current);
                int bottom = S(4) + ((row + 1) * S(RowHeight)) - _scroll;
                ContextMenuRequested?.Invoke(this, (current, PointToScreen(new Point(S(44), bottom))));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SetScroll(_scroll);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _deactivation.Dispose();
        }

        base.Dispose(disposing);
    }

    private void AddVisible(TreeItem node)
    {
        _visible.Add(node);
        if (node.Expanded && node.LoadedChildren is not null)
        {
            foreach (TreeItem child in node.LoadedChildren)
            {
                AddVisible(child);
            }
        }
    }

    /// <summary>Whether a node still hangs in this tree (each level is in its parent's children, the top one in the roots).</summary>
    private bool InTree(TreeItem node)
    {
        for (TreeItem item = node; ; item = item.Parent)
        {
            if (item.Parent is null)
            {
                return _roots.Contains(item);
            }

            if (item.Parent.LoadedChildren is not { } siblings || !siblings.Contains(item))
            {
                return false;
            }
        }
    }

    private bool OnChevron(TreeItem node, int x)
    {
        int chevronLeft = S(14 + (node.Depth * Indent));
        return node.HasChildren && x >= chevronLeft - S(4) && x < chevronLeft + S(20);
    }

    private int IndexAt(int y)
    {
        int index = (y + _scroll - S(4)) / S(RowHeight);
        return y >= 0 && y < Height && y + _scroll - S(4) >= 0 && index < _visible.Count ? index : -1;
    }

    /// <summary>The first row in view.</summary>
    private int TopRow() => Math.Clamp((_scroll - S(4) + S(RowHeight) - 1) / S(RowHeight), 0, Math.Max(0, _visible.Count - 1));

    private void UpdateHover(Point point)
    {
        int index = InScrollZone(point) ? -1 : IndexAt(point.Y);
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }
    }

    private void ClearHover()
    {
        if (_hover >= 0 || _scrollHover)
        {
            _hover = -1;
            _scrollHover = false;
            Invalidate();
        }
    }

    /// <summary>The hover follows the rows that moved under a still pointer (keyboard scrolling).</summary>
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

    private void EnsureVisible(TreeItem? node)
    {
        if (node is null)
        {
            return;
        }

        int index = _visible.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        int top = S(4) + (index * S(RowHeight));
        if (top < _scroll)
        {
            SetScroll(top);
        }
        else if (top + S(RowHeight) > _scroll + Height)
        {
            SetScroll(top + S(RowHeight) - Height);
        }

        RefreshHover();
        Invalidate();
    }

    private void SetScroll(int value) => _scroll = Math.Clamp(value, 0, Math.Max(0, ContentHeight - Height));

    private Rectangle? ThumbBounds() => ListScrollbar.Thumb(Width, 0, Height, ContentHeight, _scroll, _scrollHover || _dragging, DeviceDpi);

    /// <summary>The scrollbar's pointer zone, only while there is a thumb.</summary>
    private bool InScrollZone(Point point) => ThumbBounds() is not null && ListScrollbar.Zone(Width, 0, Height, DeviceDpi).Contains(point);

    private sealed class TreeAccessibleObject(TreeList owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Outline;

        public override int GetChildCount() => owner._visible.Count;

        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner._visible.Count ? new NodeAccessibleObject(owner, this, owner._visible[index]) : null;
    }

    private sealed class NodeAccessibleObject(TreeList tree, AccessibleObject parent, TreeItem node) : AccessibleObject
    {
        public override AccessibleObject Parent => parent;

        public override AccessibleRole Role => AccessibleRole.OutlineItem;

        public override string? Name => node.Severity is Severity s ? $"{node.Text}, {BadgePainter.TierWord(s)}" : node.Text;

        public override string? Value => node.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override AccessibleStates State =>
            AccessibleStates.Selectable
            | (ReferenceEquals(tree._selected, node) ? AccessibleStates.Selected | AccessibleStates.Focused : AccessibleStates.None)
            | (node.HasChildren ? (node.Expanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed) : AccessibleStates.None);
    }
}

/// <summary>
/// The breadcrumb (subplan 08): segments in Body Text2, the last in Text, separated by 16 px chevron-right
/// in Text3 with 4 px on each side. Every segment is a link (hand cursor, Enter on the focused segment).
/// </summary>
internal sealed class Breadcrumb : KitControl
{
    private readonly List<string> _segments = [];
    private int _hover = -1;
    private int _focusIndex = -1;

    public Breadcrumb()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.Link;
    }

    /// <summary>Raised with the segment index that was clicked.</summary>
    public event EventHandler<int>? SegmentClicked;

    public IReadOnlyList<string> Segments => _segments;

    protected override bool Interactive => true;

    public void SetSegments(IEnumerable<string> segments)
    {
        _segments.Clear();
        _segments.AddRange(segments);
        _focusIndex = _segments.Count - 1;
        AccessibleName = string.Join(" > ", _segments);
        Invalidate();
    }

    /// <summary>
    /// Paints a breadcrumb; writes each segment's x range into <paramref name="ranges"/> when given. When the path is
    /// wider than <paramref name="bounds"/>, its leading segments give way to one '…' that stands for the last of them
    /// (subplan 08: text that does not fit is cut with an ellipsis); the current folder stays whole while it fits.
    /// Returns true when something was cut.
    /// </summary>
    public static bool Draw(Graphics g, Rectangle bounds, IReadOnlyList<string> segments, int hover, int focused, int dpi, List<(int Left, int Right)>? ranges = null)
    {
        ThemeTokens t = ThemeService.Tokens;
        Font font = TypeRamp.For(dpi).Body;
        int x = bounds.X;
        int centreY = bounds.Y + (bounds.Height / 2);
        int separator = Dpi.Scale(4 + 16 + 4, dpi);
        ranges?.Clear();

        // Hide leading segments until the rest fits; 'hidden' of them stand behind the '…', which acts as the last one.
        int hidden = 0;
        int total = -separator;
        for (int i = 0; i < segments.Count; i++)
        {
            total += separator + KitPaint.MeasureWidth(segments[i], font);
        }

        if (total > bounds.Width && segments.Count > 1)
        {
            int ellipsis = KitPaint.MeasureWidth(Strings.Ellipsis, font);
            while (hidden < segments.Count - 1 && total > bounds.Width)
            {
                total -= KitPaint.MeasureWidth(segments[hidden], font) + separator;
                hidden++;
                total = hidden == 1 ? total + ellipsis + separator : total;
            }
        }

        for (int i = hidden == 0 ? 0 : hidden - 1; i < segments.Count; i++)
        {
            if (i > 0 && i >= hidden)
            {
                x += Dpi.Scale(4, dpi);
                IconRenderer.Draw(g, IconName.ChevronRight, x, centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text3);
                x += Dpi.Scale(16 + 4, dpi);
            }

            bool cut = i < hidden;
            string text = cut ? Strings.Ellipsis : segments[i];
            int width = Math.Min(KitPaint.MeasureWidth(text, font), Math.Max(0, bounds.Right - x));
            bool last = i == segments.Count - 1;
            Color color = i == hover ? t.Accent : last ? t.Text : t.Text2;
            KitPaint.DrawText(g, text, font, color, x, centreY, width + 1);
            if (i == hover)
            {
                KitPaint.HorizontalLine(g, color, x, x + width, centreY + Dpi.ScaleF(8.5f, dpi), Dpi.ScaleF(1, dpi));
            }

            if (i == focused)
            {
                KitPaint.DrawFocusRing(g, new Rectangle(x - Dpi.Scale(2, dpi), centreY - Dpi.Scale(10, dpi), width + Dpi.Scale(4, dpi), Dpi.Scale(20, dpi)), Dpi.ScaleF(2, dpi), dpi);
            }

            if (ranges is not null)
            {
                // Hidden segments keep their index with an empty range; the '…' is the last hidden one.
                while (ranges.Count < i)
                {
                    ranges.Add((0, 0));
                }

                ranges.Add((x, x + width));
            }

            x += width;
        }

        return hidden > 0 || total > bounds.Width;
    }

    private readonly List<(int Left, int Right)> _ranges = [];
    private bool _cut;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);
        bool cut = Draw(e.Graphics, ClientRectangle, _segments, _hover, KeyboardFocused ? _focusIndex : -1, DeviceDpi, _ranges);
        if (cut != _cut)
        {
            // The full path as a tooltip whenever part of it is hidden.
            _cut = cut;
            KitToolTip.Set(this, cut ? AccessibleName ?? string.Empty : string.Empty);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = _ranges.FindIndex(r => e.X >= r.Left && e.X < r.Right);
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = _ranges.FindIndex(r => e.X >= r.Left && e.X < r.Right);
        if (index >= 0 && e.Button == MouseButtons.Left)
        {
            SegmentClicked?.Invoke(this, index);
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left:
                _focusIndex = Math.Max(0, _focusIndex - 1);
                break;
            case Keys.Right:
                _focusIndex = Math.Min(_segments.Count - 1, _focusIndex + 1);
                break;
            case Keys.Enter when _focusIndex >= 0:
                SegmentClicked?.Invoke(this, _focusIndex);
                break;
            default:
                return;
        }

        e.Handled = true;
        Invalidate();
    }
}
