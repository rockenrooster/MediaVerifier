using MediaVerifier.Core.Model;
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
/// and a chevron click toggle.
/// </summary>
internal sealed class TreeList : KitControl
{
    private const int RowHeight = 32;
    private const int Indent = 18;
    private readonly List<TreeItem> _roots = [];
    private readonly List<TreeItem> _visible = [];
    private int _scroll;
    private int _hover = -1;
    private TreeItem? _selected;

    public TreeList()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.Outline;
        BackColor = Tokens.Surface;
    }

    /// <summary>Asked for a node's children the first time it expands.</summary>
    public event EventHandler<TreeItem>? LoadChildren;

    public event EventHandler? SelectionChanged;

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

    /// <summary>Rebuilds the visible rows after nodes were added, removed or changed.</summary>
    public void Rebuild()
    {
        _visible.Clear();
        foreach (TreeItem root in _roots)
        {
            AddVisible(root);
        }

        ClampScroll();
        Invalidate();
    }

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
        for (int i = first; i < _visible.Count; i++)
        {
            int y = top + (i * height) - _scroll;
            if (y > Height)
            {
                break;
            }

            TreeItem node = _visible[i];
            bool selected = ReferenceEquals(node, _selected);
            DrawRow(e.Graphics, new Rectangle(0, y, Width, height), node, selected, i == _hover, selected && KeyboardFocused, DeviceDpi);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Y);
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int index = IndexAt(e.Y);
        if (index < 0 || e.Button != MouseButtons.Left)
        {
            return;
        }

        TreeItem node = _visible[index];
        int chevronLeft = S(14 + (node.Depth * Indent));
        if (node.HasChildren && e.X >= chevronLeft - S(4) && e.X < chevronLeft + S(20))
        {
            Toggle(node);
            return;
        }

        SelectedNode = node;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = IndexAt(e.Y);
        if (index >= 0)
        {
            Toggle(_visible[index]);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll -= e.Delta * Math.Max(1, SystemInformation.MouseWheelScrollLines) * S(RowHeight) / 120;
        ClampScroll();
        KitScrolling.MarkHandled(e);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_visible.Count == 0)
        {
            return;
        }

        int index = _selected is null ? -1 : _visible.IndexOf(_selected);
        switch (e.KeyCode)
        {
            case Keys.Up:
                SelectedNode = _visible[Math.Max(0, index - 1)];
                break;
            case Keys.Down:
                SelectedNode = _visible[Math.Min(_visible.Count - 1, index + 1)];
                break;
            case Keys.Home:
                SelectedNode = _visible[0];
                break;
            case Keys.End:
                SelectedNode = _visible[^1];
                break;
            case Keys.Left when _selected is not null:
                if (_selected.Expanded && _selected.HasChildren)
                {
                    Toggle(_selected);
                }
                else if (_selected.Parent is not null)
                {
                    SelectedNode = _selected.Parent;
                }

                break;
            case Keys.Right when _selected is not null:
                if (!_selected.Expanded && _selected.HasChildren)
                {
                    Toggle(_selected);
                }
                else if (_selected.Expanded && _selected.LoadedChildren is { Count: > 0 } children)
                {
                    SelectedNode = children[0];
                }

                break;
            case Keys.Enter when _selected is not null:
                Toggle(_selected);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
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

    private int IndexAt(int y)
    {
        int index = (y + _scroll - S(4)) / S(RowHeight);
        return y + _scroll - S(4) >= 0 && index < _visible.Count ? index : -1;
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
            _scroll = top;
        }
        else if (top + S(RowHeight) > _scroll + Height)
        {
            _scroll = top + S(RowHeight) - Height;
        }

        ClampScroll();
        Invalidate();
    }

    private void ClampScroll()
    {
        int content = S(8) + (_visible.Count * S(RowHeight));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, content - Height));
    }

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

    /// <summary>Paints a breadcrumb; writes each segment's x range into <paramref name="ranges"/> when given.</summary>
    public static void Draw(Graphics g, Rectangle bounds, IReadOnlyList<string> segments, int hover, int focused, int dpi, List<(int Left, int Right)>? ranges = null)
    {
        ThemeTokens t = ThemeService.Tokens;
        Font font = TypeRamp.For(dpi).Body;
        int x = bounds.X;
        int centreY = bounds.Y + (bounds.Height / 2);
        ranges?.Clear();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                x += Dpi.Scale(4, dpi);
                IconRenderer.Draw(g, IconName.ChevronRight, x, centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text3);
                x += Dpi.Scale(16 + 4, dpi);
            }

            int width = KitPaint.MeasureWidth(segments[i], font);
            bool last = i == segments.Count - 1;
            Color color = i == hover ? t.Accent : last ? t.Text : t.Text2;
            KitPaint.DrawText(g, segments[i], font, color, x, centreY, width + 1);
            if (i == hover)
            {
                KitPaint.HorizontalLine(g, color, x, x + width, centreY + Dpi.ScaleF(8.5f, dpi), Dpi.ScaleF(1, dpi));
            }

            if (i == focused)
            {
                KitPaint.DrawFocusRing(g, new Rectangle(x - Dpi.Scale(2, dpi), centreY - Dpi.Scale(10, dpi), width + Dpi.Scale(4, dpi), Dpi.Scale(20, dpi)), Dpi.ScaleF(2, dpi), dpi);
            }

            ranges?.Add((x, x + width));
            x += width;
        }
    }

    private readonly List<(int Left, int Right)> _ranges = [];

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);
        Draw(e.Graphics, ClientRectangle, _segments, _hover, KeyboardFocused ? _focusIndex : -1, DeviceDpi, _ranges);
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
