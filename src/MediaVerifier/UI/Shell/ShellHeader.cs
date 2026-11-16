using MediaVerifier.UI.Kit;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// The 56 px header right of the rail: page title in Title at x 24, centred on y 28. Search, bell and
/// the Scan split button are added by task UI-02.
/// </summary>
internal sealed class ShellHeader : KitControl
{
    private string _title = "";

    public ShellHeader()
    {
        TabStop = false;
    }

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

    /// <summary>Right edge (device pixels) available to the title, left of the header's own controls.</summary>
    public int TitleRightLimit { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Tokens.Bg);
        int limit = TitleRightLimit > 0 ? TitleRightLimit : Width - S(24);
        KitPaint.DrawText(g, _title, Type.Title, Tokens.Text, S(24), S(28), limit - S(24));
    }
}
