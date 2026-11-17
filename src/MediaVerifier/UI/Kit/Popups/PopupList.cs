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
/// 8, shadow, 4 px padding, 32 px items. Keyboard: Up, Down, Home, End move; Enter or Space choose; Esc
/// closes. A click outside or the window losing activation closes it.
/// </summary>
internal sealed class PopupList : LayeredPopup, IMessageFilter
{
    public const int MenuWidth = 280;
    private const int ItemHeight = 32;
    private const int SeparatorHeight = 9;
    private const int PaddingSize = 4;
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

    protected override Size ContentSize => new(Dpi.Scale(_logicalWidth, PaintDpi), Dpi.Scale(MeasureHeight(_items), PaintDpi));

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

    /// <summary>Paints the list surface and items (shadow excluded) into <paramref name="bounds"/>.</summary>
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

    public static void PaintList(Graphics g, Rectangle bounds, IReadOnlyList<PopupItem> items, PopupStyle style, int current, int hover, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(8, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        int pad = Dpi.Scale(PaddingSize, dpi);
        int y = bounds.Y + pad;
        for (int i = 0; i < items.Count; i++)
        {
            PopupItem item = items[i];
            if (item.IsSeparator)
            {
                int h = Dpi.Scale(SeparatorHeight, dpi);
                KitPaint.HorizontalLine(g, t.Stroke, bounds.X + pad + Dpi.Scale(12, dpi), bounds.Right - pad - Dpi.Scale(12, dpi), y + (h / 2), Dpi.ScaleF(1, dpi));
                y += h;
                continue;
            }

            Rectangle row = new(bounds.X + pad, y, bounds.Width - (2 * pad), Dpi.Scale(ItemHeight, dpi));
            PaintItem(g, row, item, style, i == current, i == hover && item.Enabled, dpi, t, type);
            y += row.Height;
        }
    }

    public void Open(Control anchor, Point screenTopLeft)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        PaintDpi = anchor.DeviceDpi;
        _ownerForm = anchor.FindForm();
        Rectangle screen = Screen.FromControl(anchor).WorkingArea;
        Size size = ContentSize;
        int x = Math.Clamp(screenTopLeft.X, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        int y = screenTopLeft.Y;
        if (y + size.Height > screen.Bottom)
        {
            // Not enough room below: open above the anchor.
            y = anchor.PointToScreen(Point.Empty).Y - size.Height - Dpi.Scale(4, PaintDpi);
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
            case WmLButtonDown:
            case WmRButtonDown:
            case WmMButtonDown:
            case WmNcLButtonDown:
            case WmMouseWheel:
                if (m.HWnd != Handle)
                {
                    CloseList(-1);
                }

                return false;
            default:
                return false;
        }
    }

    protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => PaintList(g, bounds, _items, _style, _style == PopupStyle.DropDown ? _current : -1, _hover, dpi);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(IndexAt(ToContent(e.Location)));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_style == PopupStyle.Menu)
        {
            SetHover(-1);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = IndexAt(ToContent(e.Location));
        if (index >= 0 && _items[index].Enabled)
        {
            CloseList(index);
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

    private static void PaintItem(Graphics g, Rectangle row, PopupItem item, PopupStyle style, bool current, bool hover, int dpi, ThemeTokens t, TypeRamp type)
    {
        float radius = Dpi.ScaleF(4, dpi);
        int centreY = row.Y + (row.Height / 2);
        if (current || hover)
        {
            KitPaint.FillRoundedRect(g, row, radius, current ? t.Selected : t.Hover);
            float barHeight = Dpi.ScaleF(16, dpi);
            KitPaint.FillRoundedRect(g, new RectangleF(row.X, centreY - (barHeight / 2f), Dpi.ScaleF(3, dpi), barHeight), Dpi.ScaleF(1.5f, dpi), current ? t.Accent : t.StrokeStrong);
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
                SetHover(Step(-1));
                return true;
            case Keys.Down:
                SetHover(Step(1));
                return true;
            case Keys.Home:
                SetHover(Next(-1, 1));
                return true;
            case Keys.End:
                SetHover(Next(_items.Count, -1));
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

    private void SetHover(int index)
    {
        if (index >= 0 && (_items[index].IsSeparator || !_items[index].Enabled))
        {
            index = _style == PopupStyle.DropDown ? _hover : -1;
        }

        if (index != _hover)
        {
            _hover = index;
            Render();
        }
    }

    private int IndexAt(Point content)
    {
        int y = Dpi.Scale(PaddingSize, PaintDpi);
        if (content.X < 0 || content.X >= ContentSize.Width)
        {
            return -1;
        }

        for (int i = 0; i < _items.Count; i++)
        {
            int h = Dpi.Scale(_items[i].IsSeparator ? SeparatorHeight : ItemHeight, PaintDpi);
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
    /// <summary>Shows a menu at a screen point; <paramref name="chosen"/> receives the chosen item.</summary>
    public static PopupList Show(Control anchor, Point screen, IReadOnlyList<PopupItem> items, Action<PopupItem> chosen)
    {
        PopupList list = new(items, PopupStyle.Menu, PopupList.MenuWidth);
        list.Chosen += (_, index) => chosen(items[index]);
        list.Open(anchor, screen);
        return list;
    }

    /// <summary>Shows a menu 4 px below <paramref name="anchor"/>, right-aligned with it.</summary>
    public static PopupList ShowBelow(Control anchor, IReadOnlyList<PopupItem> items, Action<PopupItem> chosen)
    {
        Point bottomRight = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + Dpi.Scale(4, anchor.DeviceDpi)));
        return Show(anchor, new Point(bottomRight.X - Dpi.Scale(PopupList.MenuWidth, anchor.DeviceDpi), bottomRight.Y), items, chosen);
    }
}
