using MediaVerifier.UI.Kit;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// The status strip while a scan runs (subplan 08, Window and shell): 36 px, Surface with a 1 px Stroke
/// top line and a 2 px Accent progress line along it; a progress ring r 8 at +32, the text from +48 (facts
/// in Text, then Text2 details), and compact subtle Pause and Stop buttons 16 px from the right. Below a
/// 1100 px window the text shortens to files and time left. A click opens Scans, so the strip has the Hover fill
/// and the hand cursor; a paused scan shows the pause shape instead of a turning ring.
/// </summary>
internal sealed class ScanStatusStrip : KitControl
{
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 16 };
    private readonly long _start = Environment.TickCount64;
    private double? _progress;
    private string _facts = string.Empty;
    private string _details = string.Empty;
    private string _shortText = string.Empty;
    private bool _narrow;
    private Form? _form;

    public ScanStatusStrip()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.StatusBar;
        BackColor = Tokens.Surface;
        Cursor = Cursors.Hand;
        Pause = new KitButton { Kind = ButtonKind.Subtle, Compact = true, Icon = IconName.Pause, Text = Strings.Shell.Pause, TabIndex = 0 };
        Stop = new KitButton { Kind = ButtonKind.Subtle, Compact = true, Icon = IconName.Stop, Text = Strings.Shell.Stop, TabIndex = 1 };

        // The shell turns Pause into Resume while the scan is paused: the ring stops with it.
        Pause.TextChanged += (_, _) =>
        {
            UpdateSpin();
            Invalidate();
        };
        Controls.Add(Pause);
        Controls.Add(Stop);
        _spin.Tick += (_, _) => Invalidate(new Rectangle(S(20), 0, S(24), Height));
    }

    public KitButton Pause { get; }

    public KitButton Stop { get; }

    /// <summary>0..1, or null while the total is still being counted.</summary>
    public double? Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            Invalidate();
        }
    }

    /// <summary>Below 1100 px of window width the strip shows the short text only.</summary>
    public bool Narrow
    {
        get => _narrow;
        set
        {
            _narrow = value;
            Invalidate();
        }
    }

    /// <summary>True while the scan is paused or pausing (the Pause button reads Resume).</summary>
    private bool Paused => Pause.Visible && Pause.Text == Strings.Shell.Resume;

    protected override bool Interactive => true;

    /// <summary>Sets the text: <paramref name="facts"/> in Text, <paramref name="details"/> after it in Text2, and the narrow form.</summary>
    public void SetText(string facts, string details, string shortText)
    {
        _facts = facts;
        _details = details;
        _shortText = shortText;
        AccessibleName = string.IsNullOrEmpty(details) ? facts : facts + "  " + details;
        Invalidate();
    }

    /// <summary>Paints the strip (without its buttons, which are child controls) into <paramref name="bounds"/>.</summary>
    /// <param name="ringAngle">The ring's turn; null draws the pause shape (a paused scan).</param>
    public static void Draw(Graphics g, Rectangle bounds, double? progress, string facts, string details, float? ringAngle, int dpi, bool hover = false)
    {
        Theme.ThemeTokens t = Theme.ThemeService.Tokens;
        Theme.TypeRamp type = Theme.TypeRamp.For(dpi);
        g.FillRectangle(GdiCache.Brush(hover ? t.Hover : t.Surface), bounds);
        KitPaint.HorizontalLine(g, t.Stroke, bounds.X, bounds.Right, bounds.Y, Dpi.ScaleF(1, dpi));
        if (progress is double fraction && fraction > 0)
        {
            g.FillRectangle(GdiCache.Brush(t.Accent), bounds.X, bounds.Y, (float)(bounds.Width * Math.Clamp(fraction, 0, 1)), Dpi.ScaleF(2, dpi));
        }

        int centreY = bounds.Y + (bounds.Height / 2);
        if (ringAngle is float angle)
        {
            DataPaint.ProgressRing(g, new PointF(bounds.X + Dpi.ScaleF(32, dpi), centreY), Dpi.ScaleF(8, dpi), angle, dpi);
        }
        else
        {
            IconRenderer.Draw(g, IconName.Pause, bounds.X + Dpi.ScaleF(24, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, t.Text2);
        }

        int x = bounds.X + Dpi.Scale(48, dpi);
        int width = KitPaint.MeasureWidth(facts, type.Body);
        KitPaint.DrawText(g, facts, type.Body, t.Text, x, centreY, width + 1);
        if (!string.IsNullOrEmpty(details))
        {
            int detailsX = x + width + Dpi.Scale(5, dpi);
            KitPaint.DrawText(g, details, type.Body, t.Text2, detailsX, centreY, bounds.Right - detailsX - Dpi.Scale(200, dpi));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float? angle = Paused ? null : SystemInformation.UIEffectsEnabled ? ((Environment.TickCount64 - _start) % 1000 / 1000f * 360f) - 60 : -60;
        Draw(e.Graphics, ClientRectangle, _progress, _narrow ? _shortText : _facts, _narrow ? string.Empty : _details, angle, DeviceDpi, IsHovered);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int height = S(28);
        int y = (Height - height) / 2;
        int stopWidth = S(Stop.PreferredLogicalWidth);
        int pauseWidth = S(Pause.PreferredLogicalWidth);
        Stop.Bounds = new Rectangle(Width - S(16) - stopWidth, y, stopWidth, height);
        Pause.Bounds = new Rectangle(Stop.Left - S(8) - pauseWidth, y, pauseWidth, height);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateSpin();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        HookWindow();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HookWindow();
    }

    protected override void OnThemeChanged()
    {
        BackColor = Tokens.Surface;
        base.OnThemeChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_form is not null)
            {
                _form.Resize -= OnWindowResize;
                _form = null;
            }

            _spin.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The window's minimise and restore: a minimised window keeps the strip Visible, and the ring woke it 60 times a second.</summary>
    private void HookWindow()
    {
        Form? form = FindForm();
        if (ReferenceEquals(form, _form))
        {
            return;
        }

        if (_form is not null)
        {
            _form.Resize -= OnWindowResize;
        }

        _form = form;
        if (form is not null)
        {
            form.Resize += OnWindowResize;
        }

        UpdateSpin();
    }

    private void OnWindowResize(object? sender, EventArgs e) => UpdateSpin();

    /// <summary>The ring turns while the strip shows a running scan in a window that is not minimised.</summary>
    private void UpdateSpin() =>
        _spin.Enabled = Visible && SystemInformation.UIEffectsEnabled && !Paused && _form?.WindowState != FormWindowState.Minimized;
}
