using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// The notifications flyout under the bell (subplan 09, reference <c>dialogs</c>, H): title and
/// <c>Mark all read</c>, items with tier shape, title, one line, time and an unread dot, and
/// <c>Clear all</c>. Six items show at a time; the rest scroll (wheel, keys, thumb). Keyboard: Up and Down move
/// through <c>Mark all read</c>, the items and <c>Clear all</c>, Home and End jump to the ends, Enter or Space acts.
/// It closes like a menu (<see cref="TransientPopup"/>): Esc, a click elsewhere or on the bell, the window moving or
/// losing focus. Only one is open at a time.
/// </summary>
internal sealed class NotificationsFlyout : TransientPopup
{
    public const int LogicalWidth = 380;

    /// <summary>Items shown at once; more scroll.</summary>
    public const int MaxItems = 6;

    /// <summary>The <c>Mark all read</c> link as the hot target (hover or keys).</summary>
    internal const int HotMark = -2;

    /// <summary>The <c>Clear all</c> button as the hot target.</summary>
    internal const int HotClear = -3;

    private const int HeaderHeight = 56;
    private const int RowHeight = 68;
    private const int FooterHeight = 48;

    [ThreadStatic]
    private static NotificationsFlyout? t_open;

    private readonly IReadOnlyList<NotificationRecord> _items;
    private readonly DateTimeOffset _now;
    private int _first;
    private int _hot = -1;
    private bool _keyboard;
    private int _dragOffset = -1;

    private NotificationsFlyout(IReadOnlyList<NotificationRecord> items, DateTimeOffset now)
    {
        _items = items;
        _now = now;
    }

    /// <summary>Raised when <c>Mark all read</c> is chosen (the flyout has closed).</summary>
    public event EventHandler? MarkAllRead;

    /// <summary>Raised when <c>Clear all</c> is chosen (the flyout has closed).</summary>
    public event EventHandler? ClearAll;

    /// <summary>Raised when an item is chosen (the flyout has closed).</summary>
    public event EventHandler<NotificationRecord>? ItemClicked;

    /// <summary>The first item shown (0 unless the list scrolls).</summary>
    internal int FirstVisible => _first;

    /// <summary>The item index, <see cref="HotMark"/>, <see cref="HotClear"/> or -1 under the pointer or the keys.</summary>
    internal int Hot => _hot;

    protected override Size ContentSize => new(Dpi.Scale(LogicalWidth, PaintDpi), Dpi.Scale(LogicalHeight(_items.Count), PaintDpi));

    private int ShownCount => Math.Min(_items.Count, MaxItems);

    private bool Scrolls => _items.Count > MaxItems;

    /// <summary>Opens the flyout 4 px below the bell, right-aligned with it; an open one closes first.</summary>
    public static NotificationsFlyout Show(Control bell, IReadOnlyList<NotificationRecord> items, DateTimeOffset now)
    {
        // A second Enter on the bell while the first list loaded stacked two flyouts, and Esc closed one at a time.
        t_open?.Dismiss();
        NotificationsFlyout flyout = new(items, now) { PaintDpi = bell.DeviceDpi };
        Point corner = bell.PointToScreen(new Point(bell.Width, bell.Height + Dpi.Scale(4, bell.DeviceDpi)));
        Rectangle screen = Screen.FromControl(bell).WorkingArea;
        int x = Math.Clamp(corner.X - flyout.ContentSize.Width, screen.Left, Math.Max(screen.Left, screen.Right - flyout.ContentSize.Width));
        flyout.Dismissed += (_, _) =>
        {
            if (t_open == flyout)
            {
                t_open = null;
            }
        };
        t_open = flyout;
        flyout.Watch(bell, new Point(x, corner.Y), bell.RectangleToScreen(bell.ClientRectangle));
        return flyout;
    }

    /// <summary>Logical height for a number of items (the empty state when none).</summary>
    public static int LogicalHeight(int items) => items == 0 ? HeaderHeight + 120 : HeaderHeight + (Math.Min(items, MaxItems) * RowHeight) + FooterHeight;

    /// <summary>
    /// Paints the flyout content from item <paramref name="first"/>. <paramref name="hot"/> is the item, link or button
    /// under the pointer or the keys; reached with the keys (<paramref name="keyboard"/>), the links show the focus ring.
    /// </summary>
    internal static void Draw(Graphics g, Rectangle bounds, IReadOnlyList<NotificationRecord> items, DateTimeOffset now, int first, int hot, bool keyboard, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        int S(int v) => Dpi.Scale(v, dpi);
        KitPaint.DrawText(g, Strings.Dialogs.NotificationsTitle, type.Subtitle, t.Text, bounds.X + S(16), bounds.Y + S(28), bounds.Width - S(140));
        if (items.Count == 0)
        {
            IconRenderer.Draw(g, IconName.Bell, bounds.X + (bounds.Width / 2f) - Dpi.ScaleF(16, dpi), bounds.Y + S(72), 32, dpi, t.Text3);
            KitPaint.DrawTextCentred(g, Strings.Dialogs.NoNotifications, type.Body, t.Text2, bounds.X + (bounds.Width / 2), bounds.Y + S(128));
            return;
        }

        // Links use AccentHover on hover (subplan 08).
        Rectangle mark = MarkBounds(bounds, dpi);
        KitPaint.DrawText(g, Strings.Dialogs.MarkAllRead, type.Body, hot == HotMark ? t.AccentHover : t.Accent, mark.X, mark.Y + (mark.Height / 2), mark.Width + 1);
        if (hot == HotMark && keyboard)
        {
            KitPaint.DrawFocusRing(g, mark, Dpi.ScaleF(4, dpi), dpi);
        }

        int shown = Math.Min(items.Count, MaxItems);
        first = Math.Clamp(first, 0, items.Count - shown);
        for (int row = 0; row < shown; row++)
        {
            int i = first + row;
            NotificationRecord item = items[i];
            int top = bounds.Y + S(HeaderHeight + (row * RowHeight));
            Color background = t.Raised;
            if (i == hot)
            {
                background = t.Hover;
                Rectangle area = new(bounds.X + S(1), top, bounds.Width - S(2), S(RowHeight));
                g.FillRectangle(GdiCache.Brush(t.Hover), area);
                if (t.IsHighContrast)
                {
                    // Contrast themes: hover is a 2 px Highlight border instead of a fill (subplan 08).
                    KitPaint.StrokeRoundedRect(g, area, 0, t.Accent, Dpi.ScaleF(2, dpi));
                }

                KitPaint.FillRoundedRect(g, new RectangleF(bounds.X + S(1), top + S(8), Dpi.ScaleF(3, dpi), S(52)), Dpi.ScaleF(1.5f, dpi), t.StrokeStrong);
            }

            if (!item.IsRead)
            {
                float r = Dpi.ScaleF(3, dpi);
                KitPaint.FillRoundedRect(g, new RectangleF(bounds.X + S(10) - r, top + S(24) - r, 2 * r, 2 * r), r, t.Accent);
            }

            IconRenderer.DrawSeverity(g, item.Level, bounds.X + S(22), top + S(16), 16, dpi, t.ForSeverity(item.Level).Text, background);
            string time = TimeText(new DateTimeOffset(item.Utc, TimeSpan.Zero), now);
            int timeWidth = KitPaint.MeasureWidth(time, type.Caption);
            KitPaint.DrawText(g, time, type.Caption, t.Text3, bounds.Right - S(16) - timeWidth, top + S(24), timeWidth + 1);
            KitPaint.DrawText(g, item.Title, type.BodyStrong, t.Text, bounds.X + S(50), top + S(24), bounds.Width - S(66) - timeWidth - S(8));
            KitPaint.DrawText(g, item.Body ?? string.Empty, type.Caption, t.Text2, bounds.X + S(50), top + S(45), bounds.Width - S(66));
            KitPaint.HorizontalLine(g, t.Stroke, bounds.X + S(16), bounds.Right - S(16), top + S(67), Dpi.ScaleF(1, dpi));
        }

        if (items.Count > shown)
        {
            VirtualList.DrawScrollThumb(g, PopupList.ThumbRect(ListBounds(bounds, shown, dpi), items.Count, shown, first, dpi), hover: false, dpi);
        }

        Rectangle clear = ClearBounds(bounds, dpi);
        if (hot == HotClear)
        {
            KitPaint.FillRoundedRect(g, clear, Dpi.ScaleF(4, dpi), t.Hover);
            if (keyboard)
            {
                KitPaint.DrawFocusRing(g, clear, Dpi.ScaleF(4, dpi), dpi);
            }
        }

        KitPaint.DrawTextCentred(g, Strings.Dialogs.ClearAll, type.Body, t.Text, clear.X + (clear.Width / 2), clear.Y + (clear.Height / 2));
    }

    /// <summary>Today's items show the time, older ones the day (<c>Yesterday</c>, <c>Mon</c>, <c>12 Nov</c>).</summary>
    public static string TimeText(DateTimeOffset utc, DateTimeOffset now) =>
        utc.ToLocalTime().Date == now.ToLocalTime().Date ? UiFormat.TimeOfDay(utc.ToLocalTime().DateTime) : UiFormat.RecentDay(utc, now);

    protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => Draw(g, bounds, _items, _now, _first, _hot, _keyboard, dpi);

    protected override bool OnKey(Keys key)
    {
        if (_items.Count == 0)
        {
            return true;
        }

        switch (key)
        {
            case Keys.Down:
                MoveTo(Step(1));
                break;
            case Keys.Up:
                MoveTo(Step(-1));
                break;
            case Keys.PageDown:
                MoveTo(Step(MaxItems - 1));
                break;
            case Keys.PageUp:
                MoveTo(Step(-(MaxItems - 1)));
                break;
            case Keys.Home:
                MoveTo(HotMark);
                break;
            case Keys.End:
                MoveTo(HotClear);
                break;
            case Keys.Enter:
            case Keys.Space:
                Activate(_hot);
                break;
        }

        // Every key is the flyout's while it is open (Enter no longer reaches the bell and opens a second flyout).
        return true;
    }

    protected override void OnWheel(int delta, Point screen)
    {
        if (ScrollTo(_first + (delta > 0 ? -1 : 1)))
        {
            int under = HotAt(ToContent(PointToClient(screen)));
            _hot = under;
            _keyboard = false;
            Render();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Point p = ToContent(e.Location);
        Rectangle list = ListBounds(new Rectangle(Point.Empty, ContentSize), ShownCount, PaintDpi);
        if (!Scrolls || !PopupList.InThumbColumn(p, list, PaintDpi))
        {
            return;
        }

        Rectangle thumb = PopupList.ThumbRect(list, _items.Count, ShownCount, _first, PaintDpi);
        if (p.Y >= thumb.Top && p.Y < thumb.Bottom)
        {
            _dragOffset = p.Y - thumb.Top;
            Capture = true;
        }
        else if (ScrollTo(_first + (p.Y < thumb.Top ? -(MaxItems - 1) : MaxItems - 1)))
        {
            // A click on the track pages towards it.
            Render();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = ToContent(e.Location);
        if (_dragOffset >= 0)
        {
            Rectangle list = ListBounds(new Rectangle(Point.Empty, ContentSize), ShownCount, PaintDpi);
            if (ScrollTo(PopupList.FirstForThumb(list, _items.Count, ShownCount, _items.Count - ShownCount, p.Y - _dragOffset, PaintDpi)))
            {
                Render();
            }

            return;
        }

        int hot = HotAt(p);

        // The hand is for inline links (subplan 08).
        Cursor = hot == HotMark ? Cursors.Hand : Cursors.Default;
        if (hot != _hot || _keyboard)
        {
            _hot = hot;
            _keyboard = false;
            Render();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_keyboard && _dragOffset < 0 && _hot != -1)
        {
            _hot = -1;
            Render();
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

        Activate(HotAt(ToContent(e.Location)));
    }

    private static Rectangle ListBounds(Rectangle bounds, int shown, int dpi) =>
        new(bounds.X, bounds.Y + Dpi.Scale(HeaderHeight, dpi), bounds.Width, Dpi.Scale(shown * RowHeight, dpi));

    private static Rectangle ClearBounds(Rectangle bounds, int dpi)
    {
        int width = KitPaint.MeasureWidth(Strings.Dialogs.ClearAll, TypeRamp.For(dpi).Body) + Dpi.Scale(24, dpi);
        return new Rectangle(bounds.Right - Dpi.Scale(8, dpi) - width, bounds.Bottom - Dpi.Scale(40, dpi), width, Dpi.Scale(32, dpi));
    }

    private static Rectangle MarkBounds(Rectangle bounds, int dpi)
    {
        int width = KitPaint.MeasureWidth(Strings.Dialogs.MarkAllRead, TypeRamp.For(dpi).Body);
        return new Rectangle(bounds.Right - Dpi.Scale(16, dpi) - width, bounds.Y + Dpi.Scale(16, dpi), width, Dpi.Scale(24, dpi));
    }

    /// <summary>The link, button or item at a content point (-1 for none).</summary>
    private int HotAt(Point p)
    {
        if (_items.Count == 0)
        {
            return -1;
        }

        Rectangle bounds = new(Point.Empty, ContentSize);
        if (MarkBounds(bounds, PaintDpi).Contains(p))
        {
            return HotMark;
        }

        if (ClearBounds(bounds, PaintDpi).Contains(p))
        {
            return HotClear;
        }

        Rectangle list = ListBounds(bounds, ShownCount, PaintDpi);
        if (!list.Contains(p) || (Scrolls && PopupList.InThumbColumn(p, list, PaintDpi)))
        {
            return -1;
        }

        int row = (p.Y - list.Y) / Dpi.Scale(RowHeight, PaintDpi);
        return row < ShownCount ? _first + row : -1;
    }

    /// <summary>
    /// The target <paramref name="by"/> steps along Mark all read, the items, Clear all; from nothing, Down starts at
    /// the first item and Up at the last.
    /// </summary>
    private int Step(int by)
    {
        int last = _items.Count + 1;
        int position = _hot switch
        {
            HotMark => 0,
            HotClear => last,
            >= 0 => _hot + 1,
            _ => by > 0 ? 0 : last,
        };
        position = Math.Clamp(position + by, 0, last);
        return position == 0 ? HotMark : position == last ? HotClear : position - 1;
    }

    private void MoveTo(int hot)
    {
        if (hot >= 0)
        {
            // Scroll the item into view.
            ScrollTo(hot < _first ? hot : hot >= _first + ShownCount ? hot - ShownCount + 1 : _first);
        }

        _hot = hot;
        _keyboard = true;
        Render();
    }

    /// <summary>Shows items from <paramref name="first"/> (clamped); true when that changed what shows.</summary>
    private bool ScrollTo(int first)
    {
        int next = Math.Clamp(first, 0, _items.Count - ShownCount);
        if (next == _first)
        {
            return false;
        }

        _first = next;
        return true;
    }

    private void Activate(int hot)
    {
        if (_items.Count == 0)
        {
            return;
        }

        switch (hot)
        {
            case HotMark:
                CloseWith(() => MarkAllRead?.Invoke(this, EventArgs.Empty));
                break;
            case HotClear:
                CloseWith(() => ClearAll?.Invoke(this, EventArgs.Empty));
                break;
            case >= 0 when hot < _items.Count:
                NotificationRecord item = _items[hot];
                CloseWith(() => ItemClicked?.Invoke(this, item));
                break;
        }
    }
}
