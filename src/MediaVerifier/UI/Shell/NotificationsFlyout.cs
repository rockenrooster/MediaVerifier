using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// The notifications flyout under the bell (subplan 09, reference <c>dialogs</c>, H): title and
/// <c>Mark all read</c>, items with tier shape, title, one line, time and an unread dot, and
/// <c>Clear all</c>. Closes on a click elsewhere, Esc, or when the window moves or loses focus.
/// </summary>
internal sealed class NotificationsFlyout : LayeredPopup, IMessageFilter
{
    public const int LogicalWidth = 380;
    private const int MaxItems = 6;
    private const int WmKeyDown = 0x0100;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmNcLButtonDown = 0x00A1;
    private readonly IReadOnlyList<NotificationRecord> _items;
    private readonly DateTimeOffset _now;
    private Form? _owner;
    private bool _filtering;
    private int _hover = -1;
    private bool _markHover;
    private bool _clearHover;

    private NotificationsFlyout(IReadOnlyList<NotificationRecord> items, DateTimeOffset now)
    {
        _items = items.Count > MaxItems ? [.. items.Take(MaxItems)] : items;
        _now = now;
    }

    /// <summary>Raised when <c>Mark all read</c> is clicked.</summary>
    public event EventHandler? MarkAllRead;

    /// <summary>Raised when <c>Clear all</c> is clicked.</summary>
    public event EventHandler? ClearAll;

    /// <summary>Raised when an item is clicked (the flyout closes first).</summary>
    public event EventHandler<NotificationRecord>? ItemClicked;

    protected override Size ContentSize => new(Dpi.Scale(LogicalWidth, PaintDpi), Dpi.Scale(LogicalHeight(_items.Count), PaintDpi));

    /// <summary>Opens the flyout 4 px below the bell, right-aligned with it.</summary>
    public static NotificationsFlyout Show(Control bell, IReadOnlyList<NotificationRecord> items, DateTimeOffset now)
    {
        NotificationsFlyout flyout = new(items, now) { PaintDpi = bell.DeviceDpi };
        Point corner = bell.PointToScreen(new Point(bell.Width, bell.Height + Dpi.Scale(4, bell.DeviceDpi)));
        Rectangle screen = Screen.FromControl(bell).WorkingArea;
        int x = Math.Clamp(corner.X - flyout.ContentSize.Width, screen.Left, Math.Max(screen.Left, screen.Right - flyout.ContentSize.Width));
        flyout._owner = bell.FindForm();
        flyout.ShowAt(new Point(x, corner.Y), flyout._owner);
        if (flyout._owner is not null)
        {
            flyout._owner.Deactivate += flyout.OnOwnerChanged;
            flyout._owner.Move += flyout.OnOwnerChanged;
        }

        Application.AddMessageFilter(flyout);
        flyout._filtering = true;
        KitToolTip.Hide();
        return flyout;
    }

    /// <summary>Logical height for a number of items (the empty state when none).</summary>
    public static int LogicalHeight(int items) => items == 0 ? 56 + 120 : 56 + (Math.Min(items, MaxItems) * 68) + 48;

    /// <summary>Paints the flyout content; the reference sheet and the popup share it.</summary>
    public static void Draw(Graphics g, Rectangle bounds, IReadOnlyList<NotificationRecord> items, DateTimeOffset now, int hover, bool markHover, bool clearHover, int dpi)
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

        int markWidth = KitPaint.MeasureWidth(Strings.Dialogs.MarkAllRead, type.Body);
        KitPaint.DrawText(g, Strings.Dialogs.MarkAllRead, type.Body, markHover ? t.Text : t.Accent, bounds.Right - S(16) - markWidth, bounds.Y + S(28), markWidth + 1);
        for (int i = 0; i < Math.Min(items.Count, MaxItems); i++)
        {
            NotificationRecord item = items[i];
            int top = bounds.Y + S(56 + (i * 68));
            Color background = t.Raised;
            if (i == hover)
            {
                background = t.Hover;
                g.FillRectangle(GdiCache.Brush(t.Hover), new Rectangle(bounds.X + S(1), top, bounds.Width - S(2), S(68)));
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

        Rectangle clear = ClearBounds(bounds, dpi);
        if (clearHover)
        {
            KitPaint.FillRoundedRect(g, clear, Dpi.ScaleF(4, dpi), t.Hover);
        }

        KitPaint.DrawTextCentred(g, Strings.Dialogs.ClearAll, type.Body, t.Text, clear.X + (clear.Width / 2), clear.Y + (clear.Height / 2));
    }

    /// <summary>Today's items show the time, older ones the day (<c>Yesterday</c>, <c>Mon</c>, <c>12 Nov</c>).</summary>
    public static string TimeText(DateTimeOffset utc, DateTimeOffset now) =>
        utc.ToLocalTime().Date == now.ToLocalTime().Date ? UiFormat.TimeOfDay(utc.ToLocalTime().DateTime) : UiFormat.RecentDay(utc, now);

    public bool PreFilterMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case WmKeyDown when (Keys)(int)m.WParam == Keys.Escape:
                Close(null);
                return true;
            case WmLButtonDown or WmRButtonDown or WmNcLButtonDown when m.HWnd != Handle:
                Close(null);
                return false;
            default:
                return false;
        }
    }

    protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => Draw(g, bounds, _items, _now, _hover, _markHover, _clearHover, dpi);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = ToContent(e.Location);
        Rectangle bounds = new(Point.Empty, ContentSize);
        int hover = ItemAt(p);
        bool mark = MarkBounds(bounds).Contains(p);
        bool clear = _items.Count > 0 && ClearBounds(bounds, PaintDpi).Contains(p);
        if (hover != _hover || mark != _markHover || clear != _clearHover)
        {
            _hover = hover;
            _markHover = mark;
            _clearHover = clear;
            Render();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Point p = ToContent(e.Location);
        Rectangle bounds = new(Point.Empty, ContentSize);
        if (_items.Count > 0 && MarkBounds(bounds).Contains(p))
        {
            MarkAllRead?.Invoke(this, EventArgs.Empty);
            Close(null);
        }
        else if (_items.Count > 0 && ClearBounds(bounds, PaintDpi).Contains(p))
        {
            ClearAll?.Invoke(this, EventArgs.Empty);
            Close(null);
        }
        else if (ItemAt(p) is int index and >= 0)
        {
            Close(_items[index]);
        }
    }

    private static Rectangle ClearBounds(Rectangle bounds, int dpi)
    {
        int width = KitPaint.MeasureWidth(Strings.Dialogs.ClearAll, TypeRamp.For(dpi).Body) + Dpi.Scale(24, dpi);
        return new Rectangle(bounds.Right - Dpi.Scale(8, dpi) - width, bounds.Bottom - Dpi.Scale(40, dpi), width, Dpi.Scale(32, dpi));
    }

    private Rectangle MarkBounds(Rectangle bounds)
    {
        int width = KitPaint.MeasureWidth(Strings.Dialogs.MarkAllRead, TypeRamp.For(PaintDpi).Body);
        return new Rectangle(bounds.Right - Dpi.Scale(16, PaintDpi) - width, Dpi.Scale(16, PaintDpi), width, Dpi.Scale(24, PaintDpi));
    }

    private int ItemAt(Point p)
    {
        if (p.Y < Dpi.Scale(56, PaintDpi))
        {
            return -1;
        }

        int index = (p.Y - Dpi.Scale(56, PaintDpi)) / Dpi.Scale(68, PaintDpi);
        return index < Math.Min(_items.Count, MaxItems) ? index : -1;
    }

    private void OnOwnerChanged(object? sender, EventArgs e) => Close(null);

    private void Close(NotificationRecord? clicked)
    {
        if (_filtering)
        {
            Application.RemoveMessageFilter(this);
            _filtering = false;
        }

        if (_owner is not null)
        {
            _owner.Deactivate -= OnOwnerChanged;
            _owner.Move -= OnOwnerChanged;
            _owner = null;
        }

        Hide();
        if (clicked is not null)
        {
            ItemClicked?.Invoke(this, clicked);
        }

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
