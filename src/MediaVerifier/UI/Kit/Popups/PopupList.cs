using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit.Popups;

/// <summary>One entry of a menu or drop-down list.</summary>
internal sealed record PopupItem(string Text, IconName? Icon = null, string? Shortcut = null, bool Enabled = true, object? Tag = null)
{
    public static PopupItem Separator { get; } = new(string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; private init; }
}

/// <summary>Menus (icon, label, shortcut) and drop-down lists (current item marked) look slightly different.</summary>
internal enum PopupStyle
{
    Menu,
    DropDown,
}

/// <summary>
/// Context menus and drop-down lists (subplan 08, Menu and Drop-down list): Raised, Stroke border, radius
/// 8, shadow, 4 px padding, 32 px items. Keyboard: Up, Down, Home, End, Page Up, Page Down move; Enter or
/// Space choose; Esc closes. A click outside or the window losing activation closes it. A list taller than
/// the room on screen shows the whole items that fit and scrolls (wheel, keys, dragging its thumb), so the
/// Issues Rule list with forty rules stays usable on a 1080p screen. Pressing an item shows `Pressed`.
/// </summary>
internal sealed class PopupList : LayeredPopup, IMessageFilter
{
    public const int MenuWidth = 280;
    private const int ItemHeight = 32;
    private const int SeparatorHeight = 9;
    private const int PaddingSize = 4;
    private const int ScreenMargin = 8;
    private const int WheelItems = 3;
    private const int ThumbWidth = 6;
    private const int ThumbHitWidth = 14;
    private const int MinThumb = 32;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmMouseWheel = 0x020A;

    private readonly IReadOnlyList<PopupItem> _items;
    private readonly PopupStyle _style;
    private readonly int _logicalWidth;
    private int _hover = -1;
    private int _current;
    private int _pressed = -1;
    private int _first;
    private int _viewHeight;
    private int _dragOffset = -1;
    private bool _filtering;
    private Form? _ownerForm;

    public PopupList(IReadOnlyList<PopupItem> items, PopupStyle style, int logicalWidth, int current = -1)
    {
        _items = items;
        _style = style;
        _logicalWidth = logicalWidth;
        _current = current;
        _hover = style == PopupStyle.DropDown ? current : -1;
    }

    /// <summary>Raised with the index of the chosen item; the list has closed already.</summary>
    public event EventHandler<int>? Chosen;

    /// <summary>Raised whenever the list closes, chosen or not.</summary>
    public event EventHandler? Dismissed;

    /// <summary>True when not every item fits on screen and the list scrolls.</summary>
    internal bool Scrolls => _viewHeight > 0;

    /// <summary>The first item shown (0 unless the list scrolls).</summary>
    internal int FirstVisible => _first;

    /// <summary>The item under the pointer or the keyboard (-1 for none).</summary>
    internal int HoverIndex => _hover;

    protected override Size ContentSize => new(Dpi.Scale(_logicalWidth, PaintDpi), _viewHeight > 0 ? _viewHeight : Dpi.Scale(MeasureHeight(_items), PaintDpi));

    /// <summary>Logical height: 4 px padding, 32 px items, 9 px separators.</summary>
    public static int MeasureHeight(IReadOnlyList<PopupItem> items)
    {
        int height = 2 * PaddingSize;
        foreach (PopupItem item in items)
        {
            height += item.IsSeparator ? SeparatorHeight : ItemHeight;
        }

        return height;
    }

    /// <summary>The logical width a drop-down list needs to show its longest item in full (at most 560).</summary>
    public static int DropDownWidth(IReadOnlyList<PopupItem> items, int dpi)
    {
        Font font = TypeRamp.For(dpi).BodyStrong;
        int widest = 0;
        foreach (PopupItem item in items)
        {
            if (!item.IsSeparator)
            {
                widest = Math.Max(widest, KitPaint.MeasureWidth(item.Text, font));
            }
        }

        // Text 12 px into the row, 36 px kept for the check mark, the list's padding on both sides.
        int logical = (int)Math.Ceiling(widest * 96.0 / dpi) + 12 + 36 + (2 * PaddingSize) + 4;
        return Math.Min(logical, 560);
    }

    /// <summary>Paints the list surface and items (shadow excluded) into <paramref name="bounds"/>.</summary>
    public static void PaintList(Graphics g, Rectangle bounds, IReadOnlyList<PopupItem> items, PopupStyle style, int current, int hover, int dpi) =>
        PaintList(g, bounds, items, style, current, hover, -1, 0, dpi);

    /// <summary>
    /// Paints the surface and the whole items that fit, starting at <paramref name="first"/>, with a scroll thumb when
    /// some items are not shown.
    /// </summary>
    public static void PaintList(Graphics g, Rectangle bounds, IReadOnlyList<PopupItem> items, PopupStyle style, int current, int hover, int pressed, int first, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(8, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        int pad = Dpi.Scale(PaddingSize, dpi);
        int bottom = bounds.Bottom - pad;
        int y = bounds.Y + pad;
        int visible = 0;
        for (int i = Math.Max(0, first); i < items.Count; i++)
        {
            PopupItem item = items[i];
            int h = Dpi.Scale(item.IsSeparator ? SeparatorHeight : ItemHeight, dpi);
            if (y + h > bottom)
            {
                break;
            }

            visible++;
            if (item.IsSeparator)
            {
                KitPaint.HorizontalLine(g, t.Stroke, bounds.X + pad + Dpi.Scale(12, dpi), bounds.Right - pad - Dpi.Scale(12, dpi), y + (h / 2), Dpi.ScaleF(1, dpi));
                y += h;
                continue;
            }

            Rectangle row = new(bounds.X + pad, y, bounds.Width - (2 * pad), Dpi.Scale(ItemHeight, dpi));
            PaintItem(g, row, item, style, i == current, i == hover && item.Enabled, i == pressed && item.Enabled, dpi, t, type);
            y += row.Height;
        }

        if (first > 0 || first + visible < items.Count)
        {
            Rectangle thumb = ThumbRect(bounds, items.Count, visible, first, dpi);
            Lists.VirtualList.DrawScrollThumb(g, thumb, hover: false, dpi);
        }
    }

    /// <summary>
    /// Opens the list with its content's top-left corner at <paramref name="screenTopLeft"/>. When there is not
    /// enough room below, it opens above <paramref name="flipAboveY"/> (the anchor's top edge by default; a context
    /// menu passes the pointer), and when it fits on neither side it takes the larger side and scrolls. Tests pass
    /// <paramref name="workingArea"/> to stand in for the screen.
    /// </summary>
    public void Open(Control anchor, Point screenTopLeft, int? flipAboveY = null, Rectangle? workingArea = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        PaintDpi = anchor.DeviceDpi;
        _ownerForm = anchor.FindForm();
        Rectangle screen = workingArea ?? Screen.FromControl(anchor).WorkingArea;
        int margin = Dpi.Scale(ScreenMargin, PaintDpi);
        int full = Dpi.Scale(MeasureHeight(_items), PaintDpi);
        int above = flipAboveY ?? (anchor.PointToScreen(Point.Empty).Y - Dpi.Scale(4, PaintDpi));
        int roomBelow = screen.Bottom - margin - screenTopLeft.Y;
        int roomAbove = above - (screen.Top + margin);
        int y = screenTopLeft.Y;
        _viewHeight = 0;
        if (full > roomBelow)
        {
            if (full <= roomAbove)
            {
                y = above - full;
            }
            else if (roomAbove > roomBelow)
            {
                _viewHeight = FitHeight(roomAbove);
                y = above - _viewHeight;
            }
            else
            {
                _viewHeight = FitHeight(Math.Max(roomBelow, Dpi.Scale(ItemHeight + (2 * PaddingSize), PaintDpi)));
            }
        }

        Size size = ContentSize;
        int x = Math.Clamp(screenTopLeft.X, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        if (_style == PopupStyle.DropDown && _current >= 0)
        {
            Reveal(_current, centre: true);
        }

        ShowAt(new Point(x, y), _ownerForm);
        if (_ownerForm is not null)
        {
            _ownerForm.Deactivate += OnOwnerDeactivate;
            _ownerForm.Move += OnOwnerDeactivate;
        }

        Application.AddMessageFilter(this);
        _filtering = true;
        KitToolTip.Hide();
    }

    public void Dismiss() => CloseList(-1);

    public bool PreFilterMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case WmKeyDown:
            case WmSysKeyDown:
                return HandleKey((Keys)(int)m.WParam);
            case WmMouseWheel:
                {
                    // The wheel message goes to the window under the pointer or to the focused one; the pointer decides.
                    long lParam = m.LParam.ToInt64();
                    Point pointer = new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                    if (ContentScreenBounds.Contains(pointer))
                    {
                        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                        ScrollBy(delta > 0 ? -WheelItems : WheelItems);
                        return true;
                    }

                    CloseList(-1);
                    return false;
                }

            case WmLButtonDown:
            case WmRButtonDown:
            case WmMButtonDown:
            case WmNcLButtonDown:
                if (m.HWnd != Handle)
                {
                    CloseList(-1);
                }

                return false;
            default:
                return false;
        }
    }

    protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) =>
        PaintList(g, bounds, _items, _style, _style == PopupStyle.DropDown ? _current : -1, _hover, _pressed, _first, dpi);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Point content = ToContent(e.Location);
        if (Scrolls && InThumbColumn(content))
        {
            Rectangle thumb = ThumbRect(new Rectangle(Point.Empty, ContentSize), _items.Count, VisibleCount(_first), _first, PaintDpi);
            if (content.Y >= thumb.Top && content.Y < thumb.Bottom)
            {
                _dragOffset = content.Y - thumb.Top;
                Capture = true;
            }
            else
            {
                // A click on the track pages towards it.
                int page = Math.Max(1, VisibleCount(_first) - 1);
                ScrollBy(content.Y < thumb.Top ? -page : page);
            }

            return;
        }

        int index = IndexAt(content);
        if (index >= 0 && _items[index].Enabled && index != _pressed)
        {
            _pressed = index;
            Render();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point content = ToContent(e.Location);
        if (_dragOffset >= 0)
        {
            DragThumbTo(content.Y - _dragOffset);
            return;
        }

        SetHover(IndexAt(content));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_style == PopupStyle.Menu && _dragOffset < 0)
        {
            SetHover(-1);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragOffset >= 0)
        {
            _dragOffset = -1;
            Capture = false;
            return;
        }

        int index = IndexAt(ToContent(e.Location));
        if (index >= 0 && _items[index].Enabled)
        {
            CloseList(index);
            return;
        }

        if (_pressed >= 0)
        {
            _pressed = -1;
            Render();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopFiltering();
        }

        base.Dispose(disposing);
    }

    private static Rectangle ThumbRect(Rectangle bounds, int count, int visible, int first, int dpi)
    {
        int top = bounds.Y + Dpi.Scale(4, dpi);
        int track = Math.Max(1, bounds.Height - Dpi.Scale(8, dpi));
        int length = Math.Clamp(track * Math.Max(1, visible) / Math.Max(1, count), Math.Min(track, Dpi.Scale(MinThumb, dpi)), track);
        int maxFirst = Math.Max(1, count - visible);
        int offset = (int)((long)(track - length) * Math.Clamp(first, 0, maxFirst) / maxFirst);
        return new Rectangle(bounds.Right - Dpi.Scale(3 + ThumbWidth, dpi), top + offset, Dpi.Scale(ThumbWidth, dpi), length);
    }

    private static void PaintItem(Graphics g, Rectangle row, PopupItem item, PopupStyle style, bool current, bool hover, bool pressed, int dpi, ThemeTokens t, TypeRamp type)
    {
        float radius = Dpi.ScaleF(4, dpi);
        int centreY = row.Y + (row.Height / 2);
        if (current || hover || pressed)
        {
            Color fill = pressed ? t.Pressed : current ? t.Selected : t.Hover;
            KitPaint.FillRoundedRect(g, row, radius, fill);
            float barHeight = Dpi.ScaleF(16, dpi);
            KitPaint.FillRoundedRect(g, new RectangleF(row.X, centreY - (barHeight / 2f), Dpi.ScaleF(3, dpi), barHeight), Dpi.ScaleF(1.5f, dpi), current || pressed ? t.Accent : t.StrokeStrong);
        }

        Color text = item.Enabled ? t.Text : t.TextDisabled;
        if (style == PopupStyle.Menu)
        {
            if (item.Icon is IconName icon)
            {
                IconRenderer.Draw(g, icon, row.X + Dpi.ScaleF(12, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, item.Enabled ? t.Text2 : t.TextDisabled);
            }

            int labelX = row.X + Dpi.Scale(40, dpi);
            int shortcutWidth = item.Shortcut is null ? 0 : KitPaint.MeasureWidth(item.Shortcut, type.Caption);
            KitPaint.DrawText(g, item.Text, type.Body, text, labelX, centreY, row.Right - labelX - shortcutWidth - Dpi.Scale(24, dpi));
            if (item.Shortcut is not null)
            {
                KitPaint.DrawText(g, item.Shortcut, type.Caption, t.Text3, row.Right - Dpi.Scale(12, dpi) - shortcutWidth, centreY, shortcutWidth + 2);
            }

            return;
        }

        Font font = current ? type.BodyStrong : type.Body;
        KitPaint.DrawText(g, item.Text, font, text, row.X + Dpi.Scale(12, dpi), centreY, row.Width - Dpi.Scale(48, dpi));
        if (current)
        {
            IconRenderer.Draw(g, IconName.Check, row.Right - Dpi.ScaleF(14 + 16, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text);
        }
    }

    private bool HandleKey(Keys key)
    {
        switch (key)
        {
            case Keys.Escape:
                CloseList(-1);
                return true;
            case Keys.Up:
                SetHover(Step(-1), reveal: true);
                return true;
            case Keys.Down:
                SetHover(Step(1), reveal: true);
                return true;
            case Keys.Home:
                SetHover(Next(-1, 1), reveal: true);
                return true;
            case Keys.End:
                SetHover(Next(_items.Count, -1), reveal: true);
                return true;
            case Keys.PageUp:
                SetHover(Page(-1), reveal: true);
                return true;
            case Keys.PageDown:
                SetHover(Page(1), reveal: true);
                return true;
            case Keys.Enter:
            case Keys.Space:
                if (_hover >= 0 && _items[_hover].Enabled)
                {
                    CloseList(_hover);
                }

                return true;
            case Keys.Tab:
            case Keys.Menu:
                CloseList(-1);
                return false;
            default:
                return true;
        }
    }

    private int Step(int direction)
    {
        int start = _hover < 0 ? (direction > 0 ? -1 : _items.Count) : _hover;
        int next = Next(start, direction);
        return next < 0 ? _hover : next;
    }

    private int Page(int direction)
    {
        int target = _hover < 0 ? (direction > 0 ? -1 : _items.Count) : _hover;
        int moved = _hover;
        for (int steps = Math.Max(1, VisibleCount(_first) - 1); steps > 0; steps--)
        {
            int next = Next(target, direction);
            if (next < 0)
            {
                break;
            }

            target = next;
            moved = next;
        }

        return moved;
    }

    private int Next(int from, int direction)
    {
        for (int i = from + direction; i >= 0 && i < _items.Count; i += direction)
        {
            if (!_items[i].IsSeparator && _items[i].Enabled)
            {
                return i;
            }
        }

        return -1;
    }

    private void SetHover(int index, bool reveal = false)
    {
        if (index >= 0 && (_items[index].IsSeparator || !_items[index].Enabled))
        {
            index = _style == PopupStyle.DropDown ? _hover : -1;
        }

        bool scrolled = reveal && index >= 0 && Reveal(index, centre: false);
        if (index != _hover || scrolled)
        {
            _hover = index;
            Render();
        }
    }

    /// <summary>Scrolls so <paramref name="index"/> is shown; true when the first shown item changed.</summary>
    private bool Reveal(int index, bool centre)
    {
        if (!Scrolls)
        {
            return false;
        }

        int before = _first;
        if (centre)
        {
            _first = index - (VisibleCount(index) / 2);
        }
        else if (index < _first)
        {
            _first = index;
        }
        else
        {
            while (_first < index && index >= _first + VisibleCount(_first))
            {
                _first++;
            }
        }

        _first = Math.Clamp(_first, 0, MaxFirst());
        return _first != before;
    }

    private void ScrollBy(int items)
    {
        if (!Scrolls)
        {
            return;
        }

        int next = Math.Clamp(_first + items, 0, MaxFirst());
        if (next != _first)
        {
            _first = next;
            Render();
        }
    }

    private void DragThumbTo(int thumbTop)
    {
        Rectangle bounds = new(Point.Empty, ContentSize);
        int visible = VisibleCount(_first);
        Rectangle thumb = ThumbRect(bounds, _items.Count, visible, 0, PaintDpi);
        int track = Math.Max(1, bounds.Height - Dpi.Scale(8, PaintDpi) - thumb.Height);
        int maxFirst = MaxFirst();
        int next = Math.Clamp((int)Math.Round((thumbTop - Dpi.Scale(4, PaintDpi)) * (double)maxFirst / track), 0, maxFirst);
        if (next != _first)
        {
            _first = next;
            Render();
        }
    }

    /// <summary>How many items (separators included) fit from <paramref name="first"/> on.</summary>
    private int VisibleCount(int first)
    {
        int room = ContentSize.Height - (2 * Dpi.Scale(PaddingSize, PaintDpi));
        int count = 0;
        for (int i = Math.Max(0, first); i < _items.Count; i++)
        {
            room -= Dpi.Scale(_items[i].IsSeparator ? SeparatorHeight : ItemHeight, PaintDpi);
            if (room < 0)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>The last useful first item: from it on, every remaining item fits.</summary>
    private int MaxFirst()
    {
        int room = ContentSize.Height - (2 * Dpi.Scale(PaddingSize, PaintDpi));
        int first = _items.Count;
        while (first > 0)
        {
            room -= Dpi.Scale(_items[first - 1].IsSeparator ? SeparatorHeight : ItemHeight, PaintDpi);
            if (room < 0)
            {
                break;
            }

            first--;
        }

        return first;
    }

    /// <summary>A content height of whole items no taller than <paramref name="room"/>.</summary>
    private int FitHeight(int room)
    {
        int pad = 2 * Dpi.Scale(PaddingSize, PaintDpi);
        int item = Dpi.Scale(ItemHeight, PaintDpi);
        return pad + (Math.Max(1, (room - pad) / item) * item);
    }

    private bool InThumbColumn(Point content) => content.X >= ContentSize.Width - Dpi.Scale(ThumbHitWidth, PaintDpi) && content.X < ContentSize.Width;

    private int IndexAt(Point content)
    {
        if (content.X < 0 || content.X >= ContentSize.Width || (Scrolls && InThumbColumn(content)))
        {
            return -1;
        }

        int pad = Dpi.Scale(PaddingSize, PaintDpi);
        int bottom = ContentSize.Height - pad;
        int y = pad;
        for (int i = _first; i < _items.Count; i++)
        {
            int h = Dpi.Scale(_items[i].IsSeparator ? SeparatorHeight : ItemHeight, PaintDpi);
            if (y + h > bottom)
            {
                break;
            }

            if (content.Y >= y && content.Y < y + h)
            {
                return _items[i].IsSeparator ? -1 : i;
            }

            y += h;
        }

        return -1;
    }

    private void OnOwnerDeactivate(object? sender, EventArgs e) => CloseList(-1);

    private void StopFiltering()
    {
        if (_filtering)
        {
            Application.RemoveMessageFilter(this);
            _filtering = false;
        }

        if (_ownerForm is not null)
        {
            _ownerForm.Deactivate -= OnOwnerDeactivate;
            _ownerForm.Move -= OnOwnerDeactivate;
            _ownerForm = null;
        }
    }

    private void CloseList(int chosen)
    {
        if (!_filtering && !Visible)
        {
            return;
        }

        StopFiltering();
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
        if (chosen >= 0)
        {
            Chosen?.Invoke(this, chosen);
        }

        BeginInvokeDispose();
    }

    private void BeginInvokeDispose()
    {
        if (IsHandleCreated)
        {
            BeginInvoke(Dispose);
        }
        else
        {
            Dispose();
        }
    }
}

/// <summary>Opens context menus (subplan 08, Menu): 280 px wide, under the pointer or below an anchor.</summary>
internal static class KitMenu
{
    /// <summary>
    /// Shows a menu at a screen point (the pointer); <paramref name="chosen"/> receives the chosen item. Without room
    /// below the point, the menu opens above it.
    /// </summary>
    public static PopupList Show(Control anchor, Point screen, IReadOnlyList<PopupItem> items, Action<PopupItem> chosen) =>
        Open(anchor, screen, screen.Y, items, chosen);

    /// <summary>Shows a menu 4 px below <paramref name="anchor"/>, right-aligned with it (above it when there is no room below).</summary>
    public static PopupList ShowBelow(Control anchor, IReadOnlyList<PopupItem> items, Action<PopupItem> chosen)
    {
        Point bottomRight = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + Dpi.Scale(4, anchor.DeviceDpi)));
        return Open(anchor, new Point(bottomRight.X - Dpi.Scale(PopupList.MenuWidth, anchor.DeviceDpi), bottomRight.Y), null, items, chosen);
    }

    private static PopupList Open(Control anchor, Point screen, int? flipAboveY, IReadOnlyList<PopupItem> items, Action<PopupItem> chosen)
    {
        PopupList list = new(items, PopupStyle.Menu, PopupList.MenuWidth);
        list.Chosen += (_, index) => chosen(items[index]);
        list.Open(anchor, screen, flipAboveY);
        return list;
    }
}
