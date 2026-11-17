using System.Runtime.InteropServices;
using MediaVerifier.UI.Kit.Popups;
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
/// </summary>
internal class KitDialog : Form
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmRound = 2;
    private readonly List<KitButton> _buttons = [];
    private readonly Panel _body;
    private IconButton? _close;
    private int _bodyHeight = 40;

    protected KitDialog(string title, DialogWidth width, bool closeButton = false)
    {
        // Setting the form's properties lays it out before the body exists; a subclass's OnLayout would then run
        // against a null body (the Add folder dialog threw this way and never opened). Lay out once it exists.
        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = title;
        LogicalWidth = (int)width;
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
        ResumeLayout(false);
    }

    public int LogicalWidth { get; }

    /// <summary>The body panel: children are placed by the subclass in logical pixels times DPI.</summary>
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

    /// <summary>Height of the body area in logical pixels.</summary>
    protected int BodyHeight
    {
        get => _bodyHeight;
        set
        {
            _bodyHeight = value;
            PerformLayout();
        }
    }

    protected int S(int logical) => Dpi.Scale(logical, DeviceDpi);

    /// <summary>Adds a bottom-right button; add from left to right, the primary last.</summary>
    protected KitButton AddButton(string text, ButtonKind kind, DialogResult result)
    {
        KitButton button = new() { Text = text, Kind = kind, TabIndex = 100 + _buttons.Count };
        button.Click += (_, _) => Finish(result);
        _buttons.Add(button);
        Controls.Add(button);
        PerformLayout();
        return button;
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/> with the scrim and the shadow.</summary>
    public DialogResult ShowOver(Form owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Control? invoker = owner.ActiveControl;
        Rectangle client = owner.RectangleToScreen(owner.ClientRectangle);
        using ScrimWindow scrim = new(client);
        using ShadowWindow shadow = new();
        Size size = new(Dpi.Scale(LogicalWidth, owner.DeviceDpi), Dpi.Scale(TotalLogicalHeight(), owner.DeviceDpi));
        Location = new Point(client.X + ((client.Width - size.Width) / 2), client.Y + ((client.Height - size.Height) / 2));
        Size = size;
        scrim.Show(owner);
        shadow.PaintDpi = owner.DeviceDpi;
        shadow.Owner = owner;
        shadow.ShowFor(new Rectangle(Location, Size));
        DialogResult result;
        try
        {
            result = ShowDialog(owner);
        }
        finally
        {
            shadow.Hide();
            scrim.Hide();
        }

        if (invoker is { IsDisposed: false, CanFocus: true })
        {
            invoker.Focus();
        }

        return result;
    }

    /// <summary>Paints the dialog frame and title (the harness renders dialogs through this too).</summary>
    public static void DrawFrame(Graphics g, Rectangle bounds, string title, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(8, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        KitPaint.DrawText(g, title, TypeRamp.For(dpi).Subtitle, t.Text, bounds.X + Dpi.Scale(24, dpi), bounds.Y + Dpi.Scale(34, dpi), bounds.Width - Dpi.Scale(64, dpi));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DwmRound;
        _ = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref round, sizeof(int));
        ApplyBorderColour();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Control? first = InitialFocus();
        first?.Focus();
    }

    /// <summary>The control focused when the dialog opens: in destructive confirmations the safe button.</summary>
    protected virtual Control? InitialFocus() => _buttons.Count > 0 ? _buttons[^1] : null;

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = ThemeService.Tokens;
        e.Graphics.Clear(t.Raised);
        KitPaint.DrawText(e.Graphics, Text, TypeRamp.For(DeviceDpi).Subtitle, t.Text, S(24), S(34), Width - S(_close is null ? 48 : 72));
        if (Environment.OSVersion.Version.Build < 22000)
        {
            // Windows 10 draws no DWM border on a borderless window.
            KitPaint.StrokeRoundedRect(e.Graphics, ClientRectangle, 0, t.Stroke, 1);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        // The form's own constructor lays out once (AutoScaleMode) before the body exists.
        if (_body is null)
        {
            return;
        }

        _body.Bounds = new Rectangle(S(24), S(60), Math.Max(0, Width - S(48)), S(_bodyHeight));
        int x = Width - S(24);
        int y = Height - S(24) - S(32);
        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            KitButton button = _buttons[i];
            int width = S(button.PreferredLogicalWidth);
            x -= width;
            button.Bounds = new Rectangle(x, y, width, S(32));
            x -= S(8);
        }

        if (_close is not null)
        {
            _close.Bounds = new Rectangle(Width - S(16) - S(28), S(20), S(28), S(28));
        }
    }

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
        }

        base.Dispose(disposing);
    }

    internal int TotalLogicalHeight() => 60 + _bodyHeight + 24 + 32 + 24;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        BackColor = ThemeService.Tokens.Raised;
        _body.BackColor = ThemeService.Tokens.Raised;
        ApplyBorderColour();
        Invalidate(true);
    }

    private void ApplyBorderColour()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        Color stroke = ThemeService.Tokens.Stroke;
        int colorRef = stroke.R | (stroke.G << 8) | (stroke.B << 16);
        _ = DwmSetWindowAttribute(Handle, DwmBorderColor, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class BodyPanel : Panel
    {
        public BodyPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
    }
}

/// <summary>A simple message or confirmation: title, one or two paragraphs, and buttons.</summary>
internal sealed class MessageDialog : KitDialog
{
    private readonly string _message;
    private KitButton? _safe;

    private MessageDialog(string title, string message, DialogWidth width)
        : base(title, width)
    {
        _message = message;
        BodyHeight = 60;
        Body.Paint += (_, e) =>
        {
            ThemeTokens t = ThemeService.Tokens;
            Rectangle bounds = new(0, 0, Body.Width, Body.Height);
            TextRenderer.DrawText(e.Graphics, _message, TypeRamp.For(DeviceDpi).Body, bounds, t.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        };
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

/// <summary>The Scrim rectangle behind a modal dialog, covering the owner window's client area.</summary>
internal sealed class ScrimWindow : Form
{
    public ScrimWindow(Rectangle screenBounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        Color scrim = ThemeService.Tokens.Scrim;
        BackColor = Color.FromArgb(scrim.R, scrim.G, scrim.B);
        Opacity = scrim.A / 255.0;
    }

    protected override bool ShowWithoutActivation => true;
}

/// <summary>A layered window that shows only the drop shadow of a dialog beneath it.</summary>
internal sealed class ShadowWindow : LayeredPopup
{
    private Size _content;

    protected override Size ContentSize => _content;

    public void ShowFor(Rectangle dialogScreenBounds)
    {
        _content = dialogScreenBounds.Size;
        ShowAt(dialogScreenBounds.Location, Owner);
    }

    protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) => g.Clear(ThemeService.Tokens.Raised);
}
