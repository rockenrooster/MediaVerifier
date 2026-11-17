using System.Runtime.InteropServices;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>Dialog widths of subplan 08: simple message, confirmation, form.</summary>
internal enum DialogWidth
{
    Message = 400,
    Confirmation = 480,
    Form = 560,
}

/// <summary>
/// A modal dialog (subplan 08, Dialog): Raised, Stroke, radius 8, shadow, over the scrim, centred in the
/// window. Padding 24; title in Subtitle centred 34 px below the top; body from 60 px; buttons at the
/// bottom right, 24 px from the edges, 8 px apart, primary on the right. Esc cancels, Enter triggers the
/// focused button, Tab stays inside, and focus returns to the invoker on close.
/// <para>
/// Built like Disk Monitor 2.0's dialog: one borderless window over the owner's client area that paints a
/// picture of the owner, the scrim, the shadow and the dialog itself, so no separate scrim or shadow window
/// can end up in front of it. Nothing is laid out until the dialog is placed over its owner, so a subclass
/// never meets its own half-built fields; subclasses place the body's children in <see cref="LayoutBody"/>.
/// </para>
/// </summary>
internal class KitDialog : Form
{
    /// <summary>Off-screen (render harness, tests) a dialog opens without taking the focus from the user's window.</summary>
    protected override bool ShowWithoutActivation => Diagnostics.ScreenRenderer.Offscreen;

    private readonly List<KitButton> _buttons = [];
    private readonly Panel _body;
    private readonly IconButton? _close;
    private int _bodyHeight = 40;
    private Bitmap? _backdrop;
    private Point _backdropAt;
    private Rectangle _surface;
    private bool _placed;

    protected KitDialog(string title, DialogWidth width, bool closeButton = false)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Dialog;
        Text = title;
        LogicalWidth = (int)width;

        // The body and the buttons clear their background to the form's colour: the dialog surface.
        BackColor = ThemeService.Tokens.Raised;
        _body = new BodyPanel { BackColor = ThemeService.Tokens.Raised, TabIndex = 0 };
        Controls.Add(_body);
        if (closeButton)
        {
            _close = new IconButton { Icon = IconName.Close, Compact = true, TabIndex = 1000 };
            _close.Click += (_, _) => Cancel();
            Controls.Add(_close);
        }

        ThemeService.Changed += OnThemeChanged;
    }

    public int LogicalWidth { get; }

    /// <summary>The body panel: the subclass adds its children and places them in <see cref="LayoutBody"/>.</summary>
    protected Panel Body => _body;

    /// <summary>Tooltip of the optional close button (Add folder has one).</summary>
    protected string CloseToolTip
    {
        set
        {
            if (_close is not null)
            {
                _close.ToolTipText = value;
            }
        }
    }

    /// <summary>Height of the body area in logical pixels; an open dialog re-centres when it changes.</summary>
    protected int BodyHeight
    {
        get => _bodyHeight;
        set
        {
            _bodyHeight = value;
            Relayout();
        }
    }

    protected int S(int logical) => Dpi.Scale(logical, DeviceDpi);

    /// <summary>
    /// Adds a bottom-right button; add from left to right, the primary last. A button with
    /// <see cref="DialogResult.None"/> leaves the dialog open: its own Click handler does the work and then
    /// calls <see cref="Finish"/>.
    /// </summary>
    protected KitButton AddButton(string text, ButtonKind kind, DialogResult result)
    {
        KitButton button = new() { Text = text, Kind = kind, TabIndex = 100 + _buttons.Count };
        if (result != DialogResult.None)
        {
            button.Click += (_, _) => Finish(result);
        }

        _buttons.Add(button);
        Controls.Add(button);
        return button;
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>.</summary>
    public DialogResult ShowOver(Form owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Control? invoker = owner.ActiveControl;
        PlaceOver(owner);
        DialogResult result = ShowDialog(owner);
        if (invoker is { IsDisposed: false, CanFocus: true })
        {
            invoker.Focus();
        }

        return result;
    }

    /// <summary>
    /// Covers the owner's client area (and the dialog, when the window is smaller than the dialog), takes the
    /// owner's picture for the backdrop and lays the dialog out. <see cref="ShowOver"/> does this before it
    /// shows the dialog; render tests call it and then Show.
    /// </summary>
    internal void PlaceOver(Form owner)
    {
        if (owner.WindowState == FormWindowState.Minimized)
        {
            owner.WindowState = FormWindowState.Normal;
        }

        Rectangle client = owner.RectangleToScreen(owner.ClientRectangle);
        Size need = Dpi.Scale(new Size(LogicalWidth, TotalLogicalHeight()), owner.DeviceDpi);
        Rectangle area = client;
        if (need.Width > client.Width || need.Height > client.Height)
        {
            Point centre = new(client.X + (client.Width / 2), client.Y + (client.Height / 2));
            area = Rectangle.Union(client, new Rectangle(centre.X - (need.Width / 2), centre.Y - (need.Height / 2), need.Width, need.Height));
        }

        Bounds = area;
        if (!IsHandleCreated)
        {
            // From here on DeviceDpi is that of the monitor the dialog is on.
            CreateHandle();
        }

        _backdrop?.Dispose();
        _backdrop = Snapshot(owner);
        _backdropAt = new Point(client.X - area.X, client.Y - area.Y);
        _placed = true;
        LayoutDialog();
    }

    /// <summary>Paints the dialog frame and title (the reference sheets paint dialogs through this too).</summary>
    public static void DrawFrame(Graphics g, Rectangle bounds, string title, int dpi) => DrawFrame(g, bounds, title, dpi, 64);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        InitialFocus()?.Focus();
    }

    /// <summary>The control focused when the dialog opens: in destructive confirmations the safe button.</summary>
    protected virtual Control? InitialFocus() => _buttons.Count > 0 ? _buttons[^1] : null;

    /// <summary>Places the body's children (logical pixels times DPI). Runs once the dialog is placed, never before.</summary>
    protected virtual void LayoutBody()
    {
    }

    /// <summary>Lays the dialog out again after its content changed; does nothing before it is placed.</summary>
    protected void Relayout()
    {
        if (_placed)
        {
            LayoutDialog();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint covers every pixel.
    }

    protected override void OnPaint(PaintEventArgs e) => PaintWindow(e.Graphics);

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Cancel();
            return true;
        }

        if (keyData == Keys.Enter && ActiveControl is KitButton focused)
        {
            focused.PerformClick();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    protected virtual void Cancel() => Finish(DialogResult.Cancel);

    protected void Finish(DialogResult result)
    {
        DialogResult = result;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeService.Changed -= OnThemeChanged;
            _backdrop?.Dispose();
            _backdrop = null;
        }

        base.Dispose(disposing);
    }

    internal int TotalLogicalHeight() => 60 + _bodyHeight + 24 + 32 + 24;

    private static void DrawFrame(Graphics g, Rectangle bounds, string title, int dpi, int titleInset)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(8, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        KitPaint.DrawText(g, title, TypeRamp.For(dpi).Subtitle, t.Text, bounds.X + Dpi.Scale(24, dpi), bounds.Y + Dpi.Scale(34, dpi), bounds.Width - Dpi.Scale(titleInset, dpi));
    }

    /// <summary>
    /// The client area as it looks now, for the backdrop: a kit dialog paints itself first (a dialog over a
    /// dialog), then every child is captured where it sits, the bottom of the z-order first.
    /// </summary>
    private static Bitmap Snapshot(Form owner)
    {
        Bitmap bitmap = new(Math.Max(1, owner.ClientSize.Width), Math.Max(1, owner.ClientSize.Height));
        using Graphics g = Graphics.FromImage(bitmap);
        if (owner is KitDialog dialog)
        {
            dialog.PaintWindow(g);
        }
        else
        {
            g.Clear(ThemeService.Tokens.Bg);
        }

        for (int i = owner.Controls.Count - 1; i >= 0; i--)
        {
            Control child = owner.Controls[i];
            if (!child.Visible || child.Width <= 0 || child.Height <= 0)
            {
                continue;
            }

            try
            {
                using Bitmap part = new(child.Width, child.Height);
                child.DrawToBitmap(part, new Rectangle(Point.Empty, child.Size));
                g.DrawImageUnscaled(part, child.Location);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
            {
                // A control that cannot be captured leaves the plain background, which is fine behind the scrim.
            }
        }

        return bitmap;
    }

    private void LayoutDialog()
    {
        Size size = new(S(LogicalWidth), S(TotalLogicalHeight()));
        _surface = new Rectangle((ClientSize.Width - size.Width) / 2, (ClientSize.Height - size.Height) / 2, size.Width, size.Height);
        _body.Bounds = new Rectangle(_surface.X + S(24), _surface.Y + S(60), _surface.Width - S(48), S(_bodyHeight));
        int x = _surface.Right - S(24);
        int y = _surface.Bottom - S(24) - S(32);
        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            KitButton button = _buttons[i];
            int width = S(button.PreferredLogicalWidth);
            x -= width;
            button.Bounds = new Rectangle(x, y, width, S(32));
            x -= S(8);
        }

        _close?.SetBounds(_surface.Right - S(16) - S(28), _surface.Y + S(20), S(28), S(28));
        LayoutBody();
        Invalidate();
    }

    private void PaintWindow(Graphics g)
    {
        ThemeTokens t = ThemeService.Tokens;
        g.Clear(t.Bg);
        if (_backdrop is not null)
        {
            g.DrawImageUnscaled(_backdrop, _backdropAt);
        }

        g.FillRectangle(ThemeResources.Brush(t.Scrim), ClientRectangle);
        if (_surface.IsEmpty)
        {
            return;
        }

        ShadowCache.Draw(g, _surface, 8, DeviceDpi, t.Shadow);
        DrawFrame(g, _surface, Text, DeviceDpi, _close is null ? 48 : 72);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        BackColor = ThemeService.Tokens.Raised;
        _body.BackColor = ThemeService.Tokens.Raised;
        Invalidate(true);
    }

    private sealed class BodyPanel : Panel
    {
        public BodyPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
    }
}

/// <summary>
/// A simple message or confirmation: title, the message wrapped (room for three lines at least; a longer one,
/// such as a list of files, makes the dialog taller, up to 400 px), and buttons.
/// </summary>
internal sealed class MessageDialog : KitDialog
{
    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private readonly string _message;
    private KitButton? _safe;

    private MessageDialog(string title, string message, DialogWidth width)
        : base(title, width)
    {
        _message = message;
        Body.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, _message, TypeRamp.For(DeviceDpi).Body, new Rectangle(Point.Empty, Body.Size), ThemeService.Tokens.Text, TextFlags);
        int dpi = DeviceDpi;
        Size text = TextRenderer.MeasureText(message, TypeRamp.For(dpi).Body, new Size(Dpi.Scale((int)width - 48, dpi), int.MaxValue), TextFlags);
        BodyHeight = Math.Clamp((int)Math.Ceiling(text.Height * 96.0 / dpi), 60, 400);
    }

    /// <summary>Asks for a confirmation; a destructive action keeps the initial focus on the safe button.</summary>
    public static bool Confirm(Form owner, string title, string message, string cancel, string action, bool destructive)
    {
        using MessageDialog dialog = new(title, message, DialogWidth.Confirmation);
        dialog._safe = dialog.AddButton(cancel, ButtonKind.Secondary, DialogResult.Cancel);
        dialog.AddButton(action, destructive ? ButtonKind.Danger : ButtonKind.Primary, DialogResult.OK);
        if (!destructive)
        {
            dialog._safe = null;
        }

        return dialog.ShowOver(owner) == DialogResult.OK;
    }

    /// <summary>Shows a message with one button.</summary>
    public static void Show(Form owner, string title, string message, string button)
    {
        using MessageDialog dialog = new(title, message, DialogWidth.Message);
        dialog.AddButton(button, ButtonKind.Primary, DialogResult.OK);
        dialog.ShowOver(owner);
    }

    protected override Control? InitialFocus() => _safe ?? base.InitialFocus();
}
