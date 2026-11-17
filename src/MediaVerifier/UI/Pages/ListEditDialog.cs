using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Settings → Scanning, <c>File types</c> and <c>Always skip</c> (subplan 09, Settings): one labelled text box per list
/// with its items separated by commas, <c>Restore defaults</c>, then Cancel and Save.
/// </summary>
internal sealed class ListEditDialog : KitDialog
{
    private const int FieldPitch = 64;
    private readonly (string Label, KitTextBox Box, IReadOnlyList<string> Defaults)[] _fields;
    private readonly KitButton _restore = new() { Kind = ButtonKind.Subtle, Text = Strings.Settings.RestoreDefaults };
    private readonly string? _helper;

    private ListEditDialog(string title, string? helper, IReadOnlyList<(string Label, IReadOnlyList<string> Items, IReadOnlyList<string> Defaults)> fields)
        : base(title, DialogWidth.Confirmation)
    {
        _helper = helper;
        _fields = [.. fields.Select((f, i) => (f.Label, new KitTextBox { Text = Join(f.Items), AccessibleName = f.Label, Inset = true, TabIndex = i, MaxLength = 4000 }, f.Defaults))];
        foreach ((_, KitTextBox box, _) in _fields)
        {
            Body.Controls.Add(box);
        }

        _restore.TabIndex = _fields.Length;
        _restore.Click += (_, _) =>
        {
            foreach ((_, KitTextBox box, IReadOnlyList<string> defaults) in _fields)
            {
                box.Text = Join(defaults);
            }
        };
        Body.Controls.Add(_restore);
        Body.Paint += (_, e) => PaintBody(e.Graphics);
        AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        AddButton(Strings.Kit.Save, ButtonKind.Primary, DialogResult.OK);
        BodyHeight = HelperHeight() + (FieldPitch * _fields.Length) + 40;
    }

    /// <summary>The lists after Save (items split at commas, semicolons and line breaks, trimmed); null after Cancel.</summary>
    public static List<string>[]? Ask(Form owner, string title, string? helper, IReadOnlyList<(string Label, IReadOnlyList<string> Items, IReadOnlyList<string> Defaults)> fields)
    {
        using ListEditDialog dialog = new(title, helper, fields);
        if (dialog.ShowOver(owner) != DialogResult.OK)
        {
            return null;
        }

        return [.. dialog._fields.Select(f => Split(f.Box.Text))];
    }

    /// <summary>The dialog without showing it (render tests).</summary>
    internal static ListEditDialog Create(string title, string? helper, IReadOnlyList<(string Label, IReadOnlyList<string> Items, IReadOnlyList<string> Defaults)> fields) => new(title, helper, fields);

    internal static List<string> Split(string text) =>
        [.. text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    protected override Control? InitialFocus() => _fields.Length > 0 ? _fields[0].Box : base.InitialFocus();

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_fields is null)
        {
            return;
        }

        int y = S(HelperHeight());
        foreach ((_, KitTextBox box, _) in _fields)
        {
            box.Bounds = new Rectangle(0, y + S(24), Body.Width, S(32));
            y += S(FieldPitch);
        }

        _restore.Bounds = new Rectangle(0, y + S(4), S(_restore.PreferredLogicalWidth), S(32));
    }

    private static string Join(IEnumerable<string> items) => string.Join(", ", items);

    private int HelperHeight() => _helper is null ? 0 : 32;

    private void PaintBody(Graphics g)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(DeviceDpi);
        if (_helper is not null)
        {
            KitPaint.DrawText(g, _helper, type.Body, t.Text2, 0, S(10), Body.Width);
        }

        int y = S(HelperHeight());
        foreach ((string label, _, _) in _fields)
        {
            KitPaint.DrawText(g, label, type.Body, t.Text, 0, y + S(10), Body.Width);
            y += S(FieldPitch);
        }
    }
}
