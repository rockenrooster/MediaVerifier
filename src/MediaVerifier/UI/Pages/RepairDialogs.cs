using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>What dialog A shows (subplan 09, Dialogs, A).</summary>
/// <param name="NothingToRun">Every file changed since the scan: the action stays disabled, the bar above it says why.</param>
internal sealed record RepairPreview(
    string Title,
    string Paragraph,
    IconName KindIcon,
    string WhatAndSize,
    string Duration,
    string? WhereOriginalsGo,
    bool CanUndo,
    int ChangedSkipped,
    IReadOnlyList<string> ChangedNames,
    bool ScanRunning,
    string Verb,
    bool NothingToRun = false);

/// <summary>
/// Dialog A, confirm a repair (subplan 09; reference <c>dialogs</c>, A): the action's paragraph, four fact lines
/// with icons, an optional Medium info bar for files that changed since the scan, the scan-pause check box while a
/// scan runs, then Cancel and the action verb (primary, focused).
/// </summary>
internal sealed class RepairDialog : KitDialog
{
    private const int LineHeight = 20;
    private readonly RepairPreview _preview;
    private readonly InfoBar? _skipped;
    private readonly KitCheckBox? _pause;
    private readonly KitButton _cancel;
    private readonly KitButton _action;
    private int _paragraphHeight;

    private RepairDialog(RepairPreview preview)
        : base(preview.Title, DialogWidth.Confirmation)
    {
        _preview = preview;
        if (preview.ChangedSkipped > 0)
        {
            _skipped = new InfoBar
            {
                Tier = Severity.Medium,
                Text = preview.ChangedSkipped == 1 ? Strings.Repair.ChangedSkippedOne : string.Format(CultureInfo.CurrentCulture, Strings.Repair.ChangedSkipped, UiFormat.Count(preview.ChangedSkipped)),
                Action = Strings.Dialogs.Show,
            };
            _skipped.ActionClicked += (_, _) => MessageDialog.Show(this, _skipped.Text, string.Join(Environment.NewLine, preview.ChangedNames.Take(12)), Strings.Kit.Close);
            Body.Controls.Add(_skipped);
        }

        if (preview.ScanRunning)
        {
            _pause = new KitCheckBox { Text = Strings.Repair.PauseScan, Checked = true };
            Body.Controls.Add(_pause);
        }

        Body.Paint += (_, e) => PaintBody(e.Graphics);
        _cancel = AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        _action = AddButton(preview.Verb, ButtonKind.Primary, DialogResult.OK);

        // When every file changed since the scan there is nothing to run: the action stays off and the Medium bar says
        // why (a confirmed dialog used to end without a result or a word).
        _action.Enabled = !preview.NothingToRun;
        if (preview.NothingToRun)
        {
            KitToolTip.Set(_action, Core.CoreStrings.Remediation.ChangedSinceScan);
        }

        if (_pause is not null)
        {
            // The engine must be idle on the folder: without pausing, nothing can run.
            _pause.CheckedChanged += (_, _) => _action.Enabled = _pause.Checked && !preview.NothingToRun;
        }

        BodyHeight = Measure();
    }

    /// <summary>The dialog without showing it (render tests).</summary>
    internal static RepairDialog Create(RepairPreview preview) => new(preview);

    /// <summary>Shows dialog A; true when the user chose the action.</summary>
    public static bool Ask(Form owner, RepairPreview preview)
    {
        using RepairDialog dialog = new(preview);
        return dialog.ShowOver(owner) == DialogResult.OK;
    }

    protected override Control? InitialFocus() => _action.Enabled ? _action : _cancel;

    protected override void LayoutBody()
    {
        int y = FactsTop() + S(116);
        if (_skipped is not null)
        {
            _skipped.Bounds = new Rectangle(0, y, Body.Width, S(40));
            y += S(56);
        }

        _pause?.SetBounds(0, y, Body.Width, S(20));
    }

    private int Measure()
    {
        _paragraphHeight = TextWrap.Measure(_preview.Paragraph, TypeRamp.For(DeviceDpi).Body, Math.Max(1, Dpi.Scale(432, DeviceDpi)), S(LineHeight));
        int height = FactsTop() + S(116);
        height += _skipped is not null ? S(56) : 0;
        height += _pause is not null ? S(20) + S(16) : 0;
        return (int)Math.Round(height * 96.0 / DeviceDpi) + 12;
    }

    private int FactsTop() => _paragraphHeight + S(18);

    private void PaintBody(Graphics g)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(DeviceDpi);
        TextWrap.Draw(g, _preview.Paragraph, type.Body, t.Text, 0, S(9), Body.Width, S(LineHeight));
        (IconName Icon, string? Line)[] facts =
        [
            (_preview.KindIcon, _preview.WhatAndSize),
            (IconName.Clock, _preview.Duration),
            (IconName.Quarantine, _preview.WhereOriginalsGo),
            (IconName.Undo, _preview.CanUndo ? Strings.Repair.CanUndo : Strings.Repair.CannotUndo),
        ];
        int y = FactsTop();
        foreach ((IconName icon, string? line) in facts)
        {
            if (line is null)
            {
                continue;
            }

            IconRenderer.Draw(g, icon, 0, y, 16, DeviceDpi, t.Text2);
            KitPaint.DrawText(g, line, type.Body, t.Text, S(28), y + S(8), Body.Width - S(28));
            y += S(28);
        }
    }
}

/// <summary>
/// Accept a changed file (subplan 09, Dialogs): the check box <c>I changed this file myself</c> must be ticked before
/// Accept is enabled.
/// </summary>
internal sealed class AcceptDialog : KitDialog
{
    private readonly KitCheckBox _check = new() { Text = Strings.Dialogs.AcceptCheck };
    private readonly KitButton _accept;

    private AcceptDialog()
        : base(Strings.Dialogs.AcceptTitle, DialogWidth.Confirmation)
    {
        Body.Controls.Add(_check);
        Body.Paint += (_, e) => TextWrap.Draw(e.Graphics, Strings.Dialogs.AcceptText, TypeRamp.For(DeviceDpi).Body, ThemeService.Tokens.Text, 0, S(9), Body.Width, S(20));
        AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        _accept = AddButton(Strings.Dialogs.Accept, ButtonKind.Primary, DialogResult.OK);
        _accept.Enabled = false;
        _check.CheckedChanged += (_, _) => _accept.Enabled = _check.Checked;
        BodyHeight = 96;
    }

    public static bool Ask(Form owner)
    {
        using AcceptDialog dialog = new();
        return dialog.ShowOver(owner) == DialogResult.OK;
    }

    protected override Control? InitialFocus() => _check;

    protected override void LayoutBody() => _check.SetBounds(0, S(58), Body.Width, S(20));
}
