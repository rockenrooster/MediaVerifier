using MediaVerifier.UI.Kit;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// The 56 px header right of the rail (subplan 08, Window and shell): page title in Title at x 24,
/// centred on y 28; from the right edge inward a 24 px margin, the Scan split button, 8 px, the bell icon
/// button, 8 px, and the 280 px search box, all 32 px high at y 12.
/// </summary>
internal sealed class ShellHeader : KitControl
{
    private string _title = "";

    public ShellHeader()
    {
        TabStop = false;
        Search = new SearchBox { Placeholder = Strings.Shell.SearchPlaceholder, TabIndex = 0, AccessibleName = Strings.Shell.SearchPlaceholder };
        Bell = new IconButton { Icon = IconName.Bell, TabIndex = 1 };
        Bell.ToolTipText = Strings.Shell.Notifications;
        Scan = new SplitButton { Text = Strings.Shell.Scan, TabIndex = 2, AccessibleName = Strings.Shell.Scan };
        Controls.Add(Search);
        Controls.Add(Bell);
        Controls.Add(Scan);
        BackColor = Tokens.Bg;
    }

    public SearchBox Search { get; }

    public IconButton Bell { get; }

    public SplitButton Scan { get; }

    public string PageTitle
    {
        get => _title;
        set
        {
            if (!string.Equals(_title, value, StringComparison.Ordinal))
            {
                _title = value;
                Invalidate();
            }
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int y = S(12);
        int height = S(32);
        int scanWidth = S(Scan.PreferredLogicalWidth);
        int x = Width - S(24) - scanWidth;
        Scan.Bounds = new Rectangle(x, y, scanWidth, height);
        x -= S(8) + S(32);
        Bell.Bounds = new Rectangle(x, y, S(32), height);
        x -= S(8) + S(280);
        Search.Bounds = new Rectangle(x, y, S(280), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Tokens.Bg);
        int limit = Search.Left - S(24);
        KitPaint.DrawText(g, _title, Type.Title, Tokens.Text, S(24), S(28), limit - S(24));
    }

    protected override void OnThemeChanged()
    {
        BackColor = Tokens.Bg;
        base.OnThemeChanged();
    }
}
