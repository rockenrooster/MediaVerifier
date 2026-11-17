using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Before a large scan (subplan 09, Scans; reference <c>dialogs</c>, F): the scope, three depths with
/// their estimates, and a note that a scan can be paused or stopped.
/// </summary>
internal sealed class LargeScanDialog : KitDialog
{
    private readonly ScanEstimate _estimate;
    private readonly OptionRow[] _depths;

    private LargeScanDialog(ScanEstimate estimate, ScanDepth selected)
        : base(string.Format(CultureInfo.CurrentCulture, Strings.Scans.LargeTitle, UiFormat.Count(estimate.Files)), DialogWidth.Confirmation)
    {
        _estimate = estimate;
        _depths =
        [
            Option(Strings.Depths.Quick, Strings.Scans.QuickText, estimate.Quick, 0),
            Option(Strings.Depths.Standard, Strings.Scans.StandardText, estimate.Standard, 1),
            Option(Strings.Depths.Deep, Strings.Scans.DeepText, estimate.Deep, 2),
        ];
        _depths[selected switch { ScanDepth.Quick => 0, ScanDepth.Deep => 2, _ => 1 }].Checked = true;
        Body.Controls.AddRange(_depths);
        Body.Paint += (_, e) => PaintBody(e.Graphics);
        AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        AddButton(Strings.Scans.StartScan, ButtonKind.Primary, DialogResult.OK);
        BodyHeight = 278;
    }

    /// <summary>The depth chosen, or null when the user cancelled.</summary>
    public static ScanDepth? Ask(Form owner, ScanEstimate estimate, ScanDepth selected)
    {
        using LargeScanDialog dialog = new(estimate, selected);
        if (dialog.ShowOver(owner) != DialogResult.OK)
        {
            return null;
        }

        return dialog._depths[0].Checked ? ScanDepth.Quick : dialog._depths[2].Checked ? ScanDepth.Deep : ScanDepth.Standard;
    }

    protected override Control? InitialFocus() => _depths.FirstOrDefault(d => d.Checked) ?? base.InitialFocus();

    protected override void LayoutBody()
    {
        for (int i = 0; i < _depths.Length; i++)
        {
            _depths[i].Bounds = new Rectangle(0, S(27 + (i * 64)), Body.Width, S(56));
        }
    }

    private static OptionRow Option(string title, string description, TimeSpan? estimate, int index) => new()
    {
        Text = title,
        Description = description,
        Value = estimate is TimeSpan time ? UiFormat.About(time) : Strings.Scans.NotKnown,
        GroupName = "depth",
        TabIndex = index,
    };

    private void PaintBody(Graphics g)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(DeviceDpi);
        string line = UiFormat.Facts(_estimate.Folders, UiFormat.Size(_estimate.Bytes));
        KitPaint.DrawText(g, line, type.Body, t.Text2, 0, S(6), Body.Width);
        TextWrap.Draw(g, Strings.Scans.LargeNote, type.Caption, t.Text3, 0, S(224), Body.Width, S(16));
    }
}

/// <summary>Starts a scan of every Library folder, asking first when it is large (header Scan, Scans page).</summary>
internal static class ScanLauncher
{
    public static async Task StartLibraryScanAsync(Form owner, UiServices services, ScanDepth depth, ScanOptions? options)
    {
        if (services.IsDemo || services.CannotScanReason(true) is not null)
        {
            return;
        }

        List<RootRecord> roots = await Task.Run(services.Database.Roots.List).ConfigureAwait(true);
        List<RootRecord> library = [.. roots.Where(r => r.Role == RootRole.Library && r.Enabled)];
        if (library.Count == 0)
        {
            return;
        }

        if (options is null)
        {
            ScanEstimate estimate = await Task.Run(() => services.Data.LoadScanEstimate([.. library.Select(r => r.Id)])).ConfigureAwait(true);
            if (estimate.IsLarge)
            {
                if (LargeScanDialog.Ask(owner, estimate, depth) is not ScanDepth chosen)
                {
                    return;
                }

                depth = chosen;
            }
        }

        string scope = library.Count == roots.Count(r => r.Role == RootRole.Library)
            ? Strings.Scans.AllFolders
            : UiFormat.JoinNames([.. library.Select(r => r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\')))]);
        await services.StartScanAsync([.. library.Select(r => new ScanScope(r.Id))], depth, scope, options).ConfigureAwait(true);
    }
}
