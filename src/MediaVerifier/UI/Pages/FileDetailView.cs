using System.Drawing.Imaging;
using System.Globalization;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// The detail drawer's content for one file (subplan 09, Detail drawer; tasks UI-05 and UI-14): header,
/// fingerprint line, actions, findings with their explanations and evidence, history and technical
/// details. Geometry from docs/design/issues-detail-dark.svg (drawer content origin at its padding).
/// </summary>
internal sealed class FileDetailView : KitControl
{
    private const int Pad = 20;
    private readonly KitButton _primary = new() { Kind = ButtonKind.Primary, Icon = IconName.Report };
    private readonly KitButton _showInFolder = new() { Kind = ButtonKind.Secondary, Icon = IconName.Folder, Text = Strings.Menu.ShowInFolder };
    private readonly IconButton _more = new() { Icon = IconName.More };
    private readonly IconButton _close = new() { Icon = IconName.Close };
    private readonly List<(KitButton Ignore, KitButton Rescan)> _findingButtons = [];

    // The cards' chevrons are buttons, so the keyboard opens what a click on the row opens (findings past the first,
    // History, Technical details were mouse only).
    private readonly List<IconButton> _expanders = [];
    private readonly IconButton _historyToggle = new() { Icon = IconName.ChevronDown, Compact = true, ToolTipText = Strings.Drawer.History };
    private readonly IconButton _technicalToggle = new() { Icon = IconName.ChevronDown, Compact = true, ToolTipText = Strings.Drawer.TechnicalDetails };
    private readonly KitButton _copyDetails = new() { Kind = ButtonKind.Subtle, Icon = IconName.Copy, Text = Strings.Drawer.CopyDetails, Visible = false };
    private bool _hasPrimary;
    private readonly HashSet<int> _expanded = [0];
    private readonly List<Rectangle> _findingHeaders = [];
    private Rectangle _hoverHeader;
    private Rectangle _historyRow;
    private Rectangle _technicalRow;
    private readonly List<(Rectangle Hit, long FindingId, int Mark)> _markHits = [];
    private bool _historyOpen;
    private bool _technicalOpen;
    private FileDetail? _detail;
    private DateTimeOffset _now;
    private CancellationTokenSource? _evidenceCancel;
    private AudioEvidence? _evidence;
    private bool _evidenceLoading;
    private Bitmap? _spectrogramImage;
    private (long FindingId, int Mark)? _zoom;
    private CancellationTokenSource? _pictureCancel;
    private Bitmap? _thumbnail;
    private Bitmap? _strip;
    private Bitmap? _compare;
    private int _stripCount;
    private int _compareIndex = -1;
    private bool _compareLoading;
    private bool _holding;
    private Rectangle _compareRect;
    private readonly List<(Rectangle Hit, int Index)> _stripHits = [];

    // The breadcrumb, whose segments link to Library > Browse.
    private readonly CrumbStrip _crumbs = new();
    private static readonly System.Drawing.Imaging.ImageAttributes Faded = FadedAttributes();

    public FileDetailView()
    {
        TabStop = false;
        BackColor = Tokens.Surface;
        _more.ToolTipText = Strings.Kit.MoreActions;
        _close.ToolTipText = Strings.Kit.CloseEsc;
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _showInFolder.Click += (_, _) => ShowInFolderRequested?.Invoke(this, EventArgs.Empty);
        _primary.Click += (_, _) => PrimaryRequested?.Invoke(this, EventArgs.Empty);
        _more.Click += (_, _) => MoreRequested?.Invoke(this, _more.PointToScreen(new Point(0, _more.Height)));
        _historyToggle.Click += (_, _) =>
        {
            _historyOpen = !_historyOpen;
            Relayout();
        };
        _technicalToggle.Click += (_, _) =>
        {
            _technicalOpen = !_technicalOpen;
            Relayout();
        };
        _copyDetails.Click += (_, _) =>
        {
            if (_detail is FileDetail shown)
            {
                CopyRequested?.Invoke(this, shown.TechnicalText);
            }
        };
        _crumbs.SegmentClicked += (_, index) => CrumbClicked?.Invoke(this, index);
        _historyToggle.TabIndex = 1000;
        _technicalToggle.TabIndex = 1001;
        _copyDetails.TabIndex = 1002;
        Controls.AddRange([_close, _crumbs, _primary, _showInFolder, _more, _historyToggle, _technicalToggle, _copyDetails]);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? ShowInFolderRequested;

    public event EventHandler? PrimaryRequested;

    public event EventHandler<Point>? MoreRequested;

    /// <summary>Ignore (for an ignored finding, Stop ignoring) or Scan again on one finding: finding id and which (true = the first).</summary>
    public event EventHandler<(long FindingId, bool Ignore)>? FindingAction;

    /// <summary>Copy details: the technical text to copy.</summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>A breadcrumb segment was clicked (its index in <see cref="FileDetail.Breadcrumb"/>): a link to Library, Browse.</summary>
    public event EventHandler<int>? CrumbClicked;

    public FileDetail? Detail => _detail;

    /// <summary>Renders a file's spectrogram and waveform; called on a pool thread when a file with such evidence is shown.</summary>
    public Func<FileDetail, CancellationToken, AudioEvidence?>? EvidenceLoader { get; set; }

    /// <summary>Loads the header thumbnail as PNG; called on a pool thread for every file shown.</summary>
    public Func<FileDetail, CancellationToken, byte[]?>? ThumbnailLoader { get; set; }

    /// <summary>Loads the frame strip of an upscale finding as PNG.</summary>
    public Func<FileDetail, FindingDetail, CancellationToken, byte[]?>? StripLoader { get; set; }

    /// <summary>Loads the original and rebuilt crops of one frame side by side as PNG.</summary>
    public Func<FileDetail, FindingDetail, double, CancellationToken, byte[]?>? CompareLoader { get; set; }

    /// <summary>The evidence load started by the last <see cref="Bind"/> (the render harness waits for it).</summary>
    public Task EvidenceLoading { get; private set; } = Task.CompletedTask;

    /// <summary>Shows a file; the first finding is expanded.</summary>
    public void Bind(FileDetail detail, DateTimeOffset now, string primaryAction)
    {
        _detail = detail;
        _now = now;
        _expanded.Clear();
        _expanded.Add(0);
        _historyOpen = false;
        _technicalOpen = false;
        StartEvidence(detail);
        _crumbs.Crumbs = detail.Breadcrumb;
        _primary.Text = primaryAction;
        _hasPrimary = primaryAction.Length > 0;
        _primary.Visible = _hasPrimary;
        foreach ((KitButton ignore, KitButton rescan) in _findingButtons)
        {
            Controls.Remove(ignore);
            Controls.Remove(rescan);
            ignore.Dispose();
            rescan.Dispose();
        }

        _findingButtons.Clear();
        foreach (IconButton expander in _expanders)
        {
            Controls.Remove(expander);
            expander.Dispose();
        }

        _expanders.Clear();
        for (int i = 0; i < detail.Findings.Count; i++)
        {
            FindingDetail finding = detail.Findings[i];

            // An ignored finding (the Ignored view) offers Stop ignoring; a resolved one only Scan again.
            KitButton ignore = new()
            {
                Kind = ButtonKind.Subtle,
                Icon = IconName.Ignore,
                Text = finding.Status == FindingStatus.Ignored ? Strings.Menu.StopIgnoring : Strings.Issues.Ignore,
                TabIndex = 11 + (3 * i),
                Tag = finding.Status != FindingStatus.Resolved,
            };
            KitButton rescan = new() { Kind = ButtonKind.Subtle, Icon = IconName.Refresh, Text = Strings.Issues.ScanAgain, TabIndex = 12 + (3 * i) };
            IconButton expander = new() { Icon = IconName.ChevronDown, Compact = true, ToolTipText = finding.Title, TabIndex = 10 + (3 * i) };
            long id = finding.Id;
            int index = i;
            ignore.Click += (_, _) => FindingAction?.Invoke(this, (id, true));
            rescan.Click += (_, _) => FindingAction?.Invoke(this, (id, false));
            expander.Click += (_, _) => ToggleFinding(index);
            _findingButtons.Add((ignore, rescan));
            _expanders.Add(expander);
            Controls.Add(expander);
            Controls.Add(ignore);
            Controls.Add(rescan);
        }

        Relayout();
    }

    /// <summary>
    /// Takes the fixes of a second load of the shown file (one that looked for a healthy copy) without starting over:
    /// open cards, evidence and the scroll position stay.
    /// </summary>
    public void UpdateFixes(FileDetail detail)
    {
        if (_detail is null || detail.FileId != _detail.FileId)
        {
            return;
        }

        _detail = _detail with
        {
            RecommendedAction = detail.RecommendedAction,
            RecommendedActionId = detail.RecommendedActionId,
            FixActionIds = detail.FixActionIds,
            CopiesPending = false,
        };
        _primary.Text = detail.RecommendedAction;
        _hasPrimary = detail.RecommendedAction.Length > 0;
        _primary.Visible = _hasPrimary;
        Relayout();
    }

    /// <summary>Lays out children and sets the height to fit the content.</summary>
    public void Relayout()
    {
        if (_detail is null)
        {
            return;
        }

        int width = Width;
        _close.Bounds = new Rectangle(width - S(Pad) - S(32), S(16), S(32), S(32));

        // The breadcrumb line of the header (centred at 53), with room around its text for the focus ring.
        _crumbs.Bounds = new Rectangle(S(100) - S(CrumbStrip.Inset), S(53) - S(12), Math.Max(0, width - S(100) - S(56)) + S(CrumbStrip.Inset) + S(3), S(24));
        int x = S(Pad);
        int y = S(126);

        // Not _primary.Visible: before the drawer shows, WinForms reports every child hidden, and Show in folder and
        // More were placed on top of the main button the first time a file was opened.
        if (_hasPrimary)
        {
            int w = S(_primary.PreferredLogicalWidth);
            _primary.Bounds = new Rectangle(x, y, w, S(32));
            x += w + S(8);
        }

        int folderWidth = S(_showInFolder.PreferredLogicalWidth);
        _showInFolder.Bounds = new Rectangle(x, y, folderWidth, S(32));
        x += folderWidth + S(8);
        _more.Bounds = new Rectangle(x, y, S(32), S(32));
        Height = LayoutFindings(null);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _evidenceCancel?.Cancel();
            _evidenceCancel?.Dispose();
            _evidenceCancel = null;
            _spectrogramImage?.Dispose();
            _spectrogramImage = null;
            _pictureCancel?.Cancel();
            _pictureCancel?.Dispose();
            _thumbnail?.Dispose();
            _strip?.Dispose();
            _compare?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_detail is not null && Width > 0)
        {
            Relayout();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = Tokens;
        Graphics g = e.Graphics;
        g.Clear(t.Surface);
        if (_detail is not FileDetail d)
        {
            return;
        }

        TypeRamp type = Type;
        Rectangle thumb = new(S(Pad), S(16), S(64), S(64));
        if (_thumbnail is not null)
        {
            g.DrawImage(_thumbnail, thumb);
            KitPaint.StrokeRoundedRect(g, thumb, SF(4), t.Stroke, SF(1));
        }
        else
        {
            KitPaint.FillRoundedRect(g, thumb, SF(4), t.Bg);
            KitPaint.StrokeRoundedRect(g, thumb, SF(4), t.Stroke, SF(1));
            IconName kindIcon = d.Kind switch { MediaKind.Video => IconName.Video, MediaKind.Photo => IconName.Photo, _ => IconName.Music };
            IconRenderer.Draw(g, kindIcon, thumb.X + SF(20), thumb.Y + SF(20), 24, DeviceDpi, t.Text3);
        }
        int textX = S(100);
        int textWidth = Width - textX - S(56);
        KitPaint.DrawText(g, d.Name, type.Subtitle, t.Text, textX, S(30), textWidth);
        KitPaint.DrawText(g, d.FormatLine, type.Caption, t.Text2, textX, S(73), textWidth);
        DrawFingerprint(g, d, t, type);
        KitPaint.HorizontalLine(g, t.Stroke, 0, Width, S(174), SF(1));
        KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Findings, d.Findings.Count), type.CaptionStrong, t.Text3, S(Pad), S(196), Width - S(40));
        LayoutFindings(g);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_holding)
        {
            _holding = false;
            Invalidate();
            return;
        }

        foreach ((Rectangle hit, int index) in _stripHits)
        {
            if (hit.Contains(e.Location))
            {
                SelectFrame(index);
                return;
            }
        }

        foreach ((Rectangle hit, long findingId, int mark) in _markHits)
        {
            if (hit.Contains(e.Location))
            {
                _zoom = _zoom == (findingId, mark) || mark < 0 ? null : (findingId, mark);
                Invalidate();
                return;
            }
        }

        for (int i = 0; i < _findingHeaders.Count; i++)
        {
            if (_findingHeaders[i].Contains(e.Location))
            {
                if (!_expanded.Remove(i))
                {
                    _expanded.Add(i);
                }

                Relayout();
                return;
            }
        }

        if (_historyRow.Contains(e.Location))
        {
            _historyOpen = !_historyOpen;
            Relayout();
        }
        else if (_technicalRow.Contains(e.Location))
        {
            _technicalOpen = !_technicalOpen;
            Relayout();
        }
    }

    /// <summary>Opens or closes a finding card, as a click on its header does.</summary>
    internal void ToggleFinding(int index)
    {
        if (!_expanded.Remove(index))
        {
            _expanded.Add(index);
        }

        Relayout();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _compare is not null && _compareRect.Contains(e.Location))
        {
            // Press and hold shows the copy rebuilt from the effective resolution.
            _holding = true;
            Invalidate();
        }
    }

    /// <summary>A card whose header opens or closes it shows Hover while the pointer is on that header.</summary>
    private VisualState HeaderState(Rectangle header) => header == _hoverHeader ? VisualState.Hover : VisualState.Rest;

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hoverHeader.IsEmpty)
        {
            _hoverHeader = Rectangle.Empty;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Rectangle header = _historyRow.Contains(e.Location) ? _historyRow
            : _technicalRow.Contains(e.Location) ? _technicalRow
            : _findingHeaders.Find(r => r.Contains(e.Location));
        if (header != _hoverHeader)
        {
            _hoverHeader = header;
            Invalidate();
        }

        bool overMark = false;
        foreach ((Rectangle hit, _, _) in _markHits)
        {
            overMark |= hit.Contains(e.Location);
        }

        foreach ((Rectangle hit, _) in _stripHits)
        {
            overMark |= hit.Contains(e.Location);
        }

        Cursor = overMark ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnThemeChanged()
    {
        BackColor = Tokens.Surface;
        base.OnThemeChanged();
    }

    /// <summary>
    /// The breadcrumb (subplan 09, Detail drawer): each segment links to Library → Browse at that folder. One tab stop
    /// (the links were mouse only): Left and Right move between the segments, Enter or Space opens the focused one.
    /// </summary>
    private sealed class CrumbStrip : KitControl
    {
        /// <summary>Room left of the text for the focus ring.</summary>
        public const int Inset = 6;

        // The segments that fit, as laid out (text left edge and the width shown).
        private readonly List<Rectangle> _hits = [];
        private IReadOnlyList<string> _crumbs = [];
        private int _focus;

        public CrumbStrip()
        {
            TabStop = false;
            AccessibleRole = AccessibleRole.Link;
        }

        public event EventHandler<int>? SegmentClicked;

        /// <summary>The segments; the keyboard starts on the last (the file's own folder).</summary>
        public IReadOnlyList<string> Crumbs
        {
            get => _crumbs;
            set
            {
                _crumbs = value;
                _focus = value.Count - 1;
                Measure();
            }
        }

        protected override string? FallbackAccessibleName => Current >= 0 ? _crumbs[Current] : null;

        /// <summary>The segment the keyboard is on, among those shown; -1 when none is.</summary>
        private int Current => Math.Min(_focus, _hits.Count - 1);

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Measure();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            int centre = Height / 2;
            for (int i = 0; i < _hits.Count; i++)
            {
                Rectangle hit = _hits[i];
                if (i > 0)
                {
                    IconRenderer.Draw(g, IconName.ChevronRight, hit.X - S(16) + SF(2), centre - SF(6), 12, DeviceDpi, t.Text3);
                }

                KitPaint.DrawText(g, _crumbs[i], Type.Caption, i == _crumbs.Count - 1 ? t.Text : t.Text2, hit.X, centre, hit.Width);
            }

            if (KeyboardFocused && Current >= 0)
            {
                KitPaint.DrawFocusRing(g, _hits[Current], SF(2), DeviceDpi);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = HitAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && HitAt(e.Location) is int index and >= 0)
            {
                SegmentClicked?.Invoke(this, index);
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Current < 0)
            {
                return;
            }

            switch (e.KeyCode)
            {
                case Keys.Left or Keys.Right:
                    _focus = Math.Clamp(Current + (e.KeyCode == Keys.Left ? -1 : 1), 0, _hits.Count - 1);
                    Invalidate();
                    e.Handled = true;
                    break;
                case Keys.Enter or Keys.Space:
                    SegmentClicked?.Invoke(this, Current);
                    e.Handled = true;
                    break;
            }
        }

        private int HitAt(Point point) => _hits.FindIndex(hit => hit.Contains(point));

        /// <summary>Lays out the segments that fit; a segment cut off at the edge is shown in part.</summary>
        private void Measure()
        {
            _hits.Clear();
            int x = S(Inset);
            int right = Width - S(3);
            for (int i = 0; i < _crumbs.Count; i++)
            {
                x += i > 0 ? S(16) : 0;
                if (x >= right)
                {
                    break;
                }

                int w = KitPaint.MeasureWidth(_crumbs[i], Type.Caption);
                _hits.Add(new Rectangle(x, (Height / 2) - S(8), Math.Min(w + 1, right - x), S(16)));
                x += w + S(4);
            }

            TabStop = _hits.Count > 0;
            Invalidate();
        }
    }

    private void DrawFingerprint(Graphics g, FileDetail d, ThemeTokens t, TypeRamp type)
    {
        int y = S(106);
        string text;
        switch (d.Fingerprint)
        {
            case FingerprintState.Unchanged:
                IconRenderer.Draw(g, IconName.ShieldCheck, S(24), y - SF(8), 16, DeviceDpi, t.Ok.Text);
                string since = d.BaselineUtc is DateTimeOffset baseline ? UiFormat.RecentDay(baseline, _now) : Strings.Dash;
                string last = d.LastVerifiedUtc is DateTimeOffset verified ? UiFormat.RecentDay(verified, _now) : Strings.Dash;
                text = d.VerifyCount == 1
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Drawer.UnchangedOnce, since, last)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Unchanged, since, UiFormat.Count(d.VerifyCount), last);
                break;
            case FingerprintState.NeverVerified:
                IconRenderer.Draw(g, IconName.Shield, S(24), y - SF(8), 16, DeviceDpi, t.Text3);
                text = string.Format(CultureInfo.CurrentCulture, Strings.Drawer.NeverVerified, d.BaselineUtc is DateTimeOffset b ? UiFormat.RecentDay(b, _now) : Strings.Dash);
                break;
            case FingerprintState.Changed:
                IconRenderer.DrawSeverity(g, Severity.Critical, S(24), y - SF(8), 16, DeviceDpi, t.Critical.Text, t.Surface);
                text = string.Format(CultureInfo.CurrentCulture, Strings.Drawer.ChangedSince, d.BaselineUtc is DateTimeOffset c ? UiFormat.RecentDay(c, _now) : Strings.Dash);
                break;
            default:
                IconRenderer.Draw(g, IconName.Shield, S(24), y - SF(8), 16, DeviceDpi, t.Text3);
                text = Strings.Drawer.NoFingerprint;
                break;
        }

        KitPaint.DrawText(g, text, type.Caption, t.Text2, S(44), y, Width - S(64));
    }

    /// <summary>Lays out (and with a <paramref name="g"/>, paints) the finding cards, history and technical details; returns the content height.</summary>
    private int LayoutFindings(Graphics? g)
    {
        if (_detail is not FileDetail d)
        {
            return S(240);
        }

        ThemeTokens t = Tokens;
        TypeRamp type = Type;
        int x = S(Pad);
        int width = Width - S(2 * Pad);
        int inner = width - S(24);
        int y = S(212);
        _findingHeaders.Clear();
        if (g is not null)
        {
            _markHits.Clear();
            _stripHits.Clear();
        }

        for (int i = 0; i < d.Findings.Count; i++)
        {
            FindingDetail f = d.Findings[i];
            bool open = _expanded.Contains(i);
            int top = y;
            int cardHeight;
            if (!open)
            {
                cardHeight = S(44);
                if (g is not null)
                {
                    Card.Draw(g, new Rectangle(x, top, width, cardHeight), null, HeaderState(new Rectangle(x, top, width, S(44))), false, DeviceDpi);
                    int badge = BadgePainter.PaintSeverityBadge(g, x + S(12), top + S(12), f.Severity, DeviceDpi);
                    KitPaint.DrawText(g, f.Title, type.BodyStrong, t.Text, x + S(12) + badge + S(12), top + S(22), width - badge - S(64));
                }

                HideButtons(i);
            }
            else
            {
                int cy = top + S(50);
                List<(string Caption, string Text)> blocks = [(Strings.Drawer.WhatItMeans, f.WhatItMeans), (Strings.Drawer.WhyItMatters, f.WhyItMatters), (Strings.Drawer.WhatYouCanDo, f.WhatYouCanDo)];
                int blocksHeight = 0;
                foreach ((string caption, string text) in blocks)
                {
                    blocksHeight += S(26) + TextWrap.Measure(text, type.Body, inner, S(19));
                }

                int evidenceHeight = f.View == EvidenceView.None ? 0 : S(26) + EvidenceHeight(f) + (f.EvidenceCaption.Length > 0 ? S(36) : S(8));
                cardHeight = S(50) + S(12) + blocksHeight + evidenceHeight + S(12) + S(32) + S(14);
                if (g is not null)
                {
                    Card.Draw(g, new Rectangle(x, top, width, cardHeight), null, HeaderState(new Rectangle(x, top, width, S(44))), false, DeviceDpi);
                    int badge = BadgePainter.PaintSeverityBadge(g, x + S(12), top + S(12), f.Severity, DeviceDpi);
                    string? pill = SeverityPolicy.PillText(f.Confidence);
                    if (pill is not null)
                    {
                        BadgePainter.PaintPill(g, x + S(12) + badge + S(8), top + S(12), pill, DeviceDpi);
                    }

                    KitPaint.DrawText(g, f.Title, type.BodyStrong, t.Text, x + S(12), cy, inner);
                    int by = cy + S(26);
                    foreach ((string caption, string text) in blocks)
                    {
                        KitPaint.DrawText(g, caption, type.CaptionStrong, t.Text3, x + S(12), by, inner);
                        by += S(10);
                        by += TextWrap.Draw(g, text, type.Body, t.Text, x + S(12), by, inner, S(19)) + S(16);
                    }

                    if (f.View != EvidenceView.None)
                    {
                        KitPaint.DrawText(g, Strings.Drawer.Evidence, type.CaptionStrong, t.Text3, x + S(12), by - S(6), inner);
                        Rectangle view = new(x + S(12), by + S(7), inner, EvidenceHeight(f));
                        DrawEvidence(g, f, view, d, t, type);
                        string caption = ZoomCaption(f) ?? CompareCaption(f) ?? f.EvidenceCaption;
                        if (caption.Length > 0)
                        {
                            KitPaint.DrawText(g, caption, type.Caption, t.Text2, x + S(12), view.Bottom + S(29), inner);
                        }
                    }
                }

                PlaceButtons(i, x + S(12), top + cardHeight - S(14) - S(32));
            }

            _findingHeaders.Add(new Rectangle(x, top, width, S(44)));
            PlaceExpander(i < _expanders.Count ? _expanders[i] : null, x, top, width, open);
            y = top + cardHeight + S(12);
        }

        // History and technical details: collapsed rows that open in place.
        _historyRow = new Rectangle(x, y, width, S(44));
        int historyHeight = _historyOpen ? S(44) + (d.History.Count * S(30)) + S(8) : S(44);
        if (g is not null)
        {
            Card.Draw(g, new Rectangle(x, y, width, historyHeight), null, HeaderState(_historyRow), false, DeviceDpi);
            KitPaint.DrawText(g, Strings.Drawer.History, type.BodyStrong, t.Text, x + S(24), y + S(22), width - S(64));
            if (_historyOpen)
            {
                IReadOnlyList<(string, string)> events = [.. d.History.Select(h => (h.Text, UiFormat.RecentDate(h.When, _now)))];
                DataPaint.Timeline(g, new Rectangle(x + S(12), y + S(36), width - S(28), d.History.Count * S(30)), events, S(30), DeviceDpi);
            }
        }

        PlaceExpander(_historyToggle, x, y, width, _historyOpen);
        y += historyHeight + S(12);
        _technicalRow = new Rectangle(x, y, width, S(44));
        int technicalText = _technicalOpen ? TextWrap.Measure(d.TechnicalText, type.Mono, width - S(24), S(16)) : 0;
        int technicalHeight = _technicalOpen ? S(40) + technicalText + S(8) + S(32) + S(12) : S(44);
        if (g is not null)
        {
            Card.Draw(g, new Rectangle(x, y, width, technicalHeight), null, HeaderState(_technicalRow), false, DeviceDpi);
            KitPaint.DrawText(g, Strings.Drawer.TechnicalDetails, type.BodyStrong, t.Text, x + S(24), y + S(22), width - S(64));
            if (_technicalOpen)
            {
                TextWrap.Draw(g, d.TechnicalText, type.Mono, t.Text2, x + S(12), y + S(40), width - S(24), S(16));
            }
        }

        PlaceExpander(_technicalToggle, x, y, width, _technicalOpen);

        // Copy details (subplan 09, Detail drawer): the technical text as shown, for a bug report or a forum post.
        _copyDetails.Visible = _technicalOpen;
        if (_technicalOpen)
        {
            _copyDetails.Bounds = new Rectangle(x + S(12), y + S(40) + technicalText + S(8), S(_copyDetails.PreferredLogicalWidth), S(32));
        }

        y += technicalHeight + S(Pad);
        return y;
    }

    /// <summary>A card's chevron: a button where the painted chevron was, pointing up while the card is open.</summary>
    private void PlaceExpander(IconButton? expander, int x, int top, int width, bool open)
    {
        if (expander is null)
        {
            return;
        }

        expander.Icon = open ? IconName.ChevronUp : IconName.ChevronDown;
        expander.Bounds = new Rectangle(x + width - S(34), top + S(8), S(28), S(28));
    }

    private int EvidenceHeight(FindingDetail f) => f.View switch
    {
        EvidenceView.Messages => S(32) + (Math.Min(3, MessageCount(f)) * S(28)),
        EvidenceView.ByteMap => S(24),
        EvidenceView.FrameStrip => S(54 + 20) + (_compareIndex >= 0 ? S(8) + CompareHeight() : 0),
        EvidenceView.Bitrate => S(44),
        _ => S(96),
    };

    private static int MessageCount(FindingDetail f) => f.Evidence.TryGetValue("messages", out object? messages) && messages is object[] list ? list.Length : 0;

    private void DrawEvidence(Graphics g, FindingDetail f, Rectangle view, FileDetail d, ThemeTokens t, TypeRamp type)
    {
        switch (f.View)
        {
            case EvidenceView.Messages:
                DrawMessageTable(g, f, view, t, type);
                break;
            case EvidenceView.ByteMap:
                DrawByteMap(g, view, f.ZeroRuns ?? d.ZeroRunStarts, d.SizeBytes, t);
                break;
            case EvidenceView.Waveform:
                DrawWaveform(g, f, view, d, t);
                break;
            case EvidenceView.FrameStrip:
                DrawFrameStrip(g, f, view, t, type);
                break;
            case EvidenceView.Bitrate:
                DrawBitrate(g, f, view, t, type);
                break;
            case EvidenceView.Spectrogram:
                DrawSpectrogram(g, f, view, t, type);
                break;
        }
    }

    /// <summary>Message table (Message, Count) for decoder findings.</summary>
    private void DrawMessageTable(Graphics g, FindingDetail f, Rectangle view, ThemeTokens t, TypeRamp type)
    {
        object[] messages = f.Evidence.TryGetValue("messages", out object? m) && m is object[] a ? a : [];
        object[] counts = f.Evidence.TryGetValue("messageCounts", out object? c) && c is object[] b ? b : [];
        g.FillRectangle(GdiCache.Brush(Color.FromArgb(140, t.Bg)), view.X, view.Y, view.Width, S(32));
        KitPaint.DrawText(g, Strings.Drawer.Message, type.CaptionStrong, t.Text3, view.X + S(12), view.Y + S(16), view.Width - S(80));
        int countWidth = KitPaint.MeasureWidth(Strings.Drawer.Count, type.CaptionStrong);
        KitPaint.DrawText(g, Strings.Drawer.Count, type.CaptionStrong, t.Text3, view.Right - S(12) - countWidth, view.Y + S(16), countWidth + 1);
        KitPaint.HorizontalLine(g, t.Stroke, view.X, view.Right, view.Y + S(31), SF(1));
        for (int i = 0; i < Math.Min(3, messages.Length); i++)
        {
            int y = view.Y + S(32) + (i * S(28)) + S(14);
            string count = i < counts.Length ? Convert.ToString(counts[i], CultureInfo.CurrentCulture) ?? string.Empty : string.Empty;
            int w = KitPaint.MeasureWidth(count, type.Body);
            KitPaint.DrawText(g, Convert.ToString(messages[i], CultureInfo.InvariantCulture) ?? string.Empty, type.Body, t.Text, view.X + S(12), y, view.Width - S(36) - w);
            KitPaint.DrawText(g, count, type.Body, t.Text2, view.Right - S(12) - w, y, w + 1);
        }
    }

    /// <summary>
    /// Byte map: one 4 px column per slice of the file; slices that overlap a zero-filled region in CritFill, the rest in
    /// Stroke. <paramref name="runs"/> holds offset and length pairs: the longest run is drawn over its whole length (a
    /// file that is 60 % zeros showed one red column where the run started).
    /// </summary>
    private void DrawByteMap(Graphics g, Rectangle view, IReadOnlyList<long> runs, long sizeBytes, ThemeTokens t)
    {
        int columns = Math.Max(1, view.Width / S(4));
        float columnWidth = view.Width / (float)columns;
        long size = Math.Max(1, sizeBytes);
        for (int i = 0; i < columns; i++)
        {
            long start = size * i / columns;
            long end = Math.Max(start + 1, size * (i + 1) / columns);
            bool zero = false;
            for (int r = 0; r + 1 < runs.Count; r += 2)
            {
                if (runs[r] < end && runs[r] + Math.Max(1, runs[r + 1]) > start)
                {
                    zero = true;
                    break;
                }
            }

            g.FillRectangle(GdiCache.Brush(zero ? t.Critical.Fill : t.Stroke), view.X + (i * columnWidth), view.Y, columnWidth - SF(1), view.Height);
        }
    }

    /// <summary>
    /// The spectrogram (subplan 08, Spectrogram): the rendered image through the palette, the cutoff marker,
    /// for inflated bitrates the knee the stated bitrate would give, and the axis captions. While the image
    /// renders the frame shows the skeleton fill.
    /// </summary>
    private void DrawSpectrogram(Graphics g, FindingDetail f, Rectangle view, ThemeTokens t, TypeRamp type)
    {
        double nyquist = _evidence is { SampleRate: > 0 } e ? e.SampleRate / 2.0
            : f.Evidence.TryGetValue("sampleRateHz", out object? rate) ? Convert.ToDouble(rate, CultureInfo.InvariantCulture) / 2 : 22050;
        if (_evidence is { Spectrogram.Length: > 0 } evidence)
        {
            if (_spectrogramImage is null || _spectrogramImage.Size != view.Size)
            {
                _spectrogramImage?.Dispose();
                _spectrogramImage = RenderSpectrogram(evidence, view.Size, SF(4));
            }

            g.DrawImageUnscaled(_spectrogramImage, view.Location);
        }
        else if (_evidenceLoading)
        {
            KitPaint.FillRoundedRect(g, view, SF(4), t.Hover);
        }

        KitPaint.StrokeRoundedRect(g, view, SF(4), t.Stroke, SF(1));
        if (f.Evidence.TryGetValue("expectedKneeHz", out object? expected))
        {
            double hz = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
            string label = string.Format(CultureInfo.CurrentCulture, Strings.Drawer.ExpectedKilohertz, (hz / 1000).ToString("0.0", CultureInfo.CurrentCulture));
            DrawFrequencyMarker(g, view, hz, nyquist, label, t.Text3, t.Bg, type, left: true);
        }

        double cutoff = f.Evidence.TryGetValue("kneeHz", out object? knee) ? Convert.ToDouble(knee, CultureInfo.InvariantCulture)
            : f.Evidence.TryGetValue("cutoffHz", out object? hzValue) ? Convert.ToDouble(hzValue, CultureInfo.InvariantCulture) : 0;
        if (cutoff > 0)
        {
            string label = string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Kilohertz, (cutoff / 1000).ToString("0.0", CultureInfo.CurrentCulture));
            DrawFrequencyMarker(g, view, cutoff, nyquist, label, t.Accent, t.OnAccent, type, left: false);
        }

        KitPaint.DrawText(g, Strings.Drawer.ZeroHz, type.Caption, t.Text3, view.X, view.Bottom + S(11), S(80));
        string top = string.Format(CultureInfo.CurrentCulture, Strings.Drawer.TopOfScale, (nyquist / 1000).ToString("0.##", CultureInfo.CurrentCulture));
        int topWidth = KitPaint.MeasureWidth(top, type.Caption);
        KitPaint.DrawText(g, top, type.Caption, t.Text3, view.Right - topWidth, view.Bottom + S(11), topWidth + 1);
    }

    /// <summary>A 1 px dashed line (4 on, 3 off) at a frequency with its label chip 3 px above, 6 px from the right (or left) edge.</summary>
    private void DrawFrequencyMarker(Graphics g, Rectangle view, double hz, double nyquist, string label, Color line, Color text, TypeRamp type, bool left)
    {
        int y = view.Bottom - (int)Math.Round(view.Height * Math.Min(1, hz / nyquist));
        using Pen dashed = new(line, SF(1)) { DashPattern = [4, 3] };
        g.DrawLine(dashed, view.X, y + 0.5f, view.Right, y + 0.5f);
        int w = KitPaint.MeasureWidth(label, type.CaptionStrong) + S(12);
        int chipY = Math.Max(view.Y + S(3), y - S(3) - S(18));
        Rectangle chip = new(left ? view.X + S(6) : view.Right - S(6) - w, chipY, w, S(18));
        KitPaint.FillRoundedRect(g, chip, SF(3), line);
        KitPaint.DrawTextCentred(g, label, type.CaptionStrong, text, chip.X + (chip.Width / 2), chip.Y + (chip.Height / 2));
    }

    /// <summary>
    /// The spectrogram at the frame's pixel size: each pixel takes the highest level of the columns and bands
    /// it covers, through the palette (−120 to 0 dB); the frame's rounded corners stay transparent.
    /// </summary>
    internal static Bitmap RenderSpectrogram(AudioEvidence evidence, Size size, float radius)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        int columns = evidence.SpectrogramColumns;
        const int rows = AudioEvidence.Rows;
        int[] palette = Palette.Value;
        int[] pixels = new int[width * height];
        byte[] column = new byte[rows];
        for (int x = 0; x < width; x++)
        {
            int c0 = (int)((long)x * columns / width);
            int c1 = Math.Max(c0 + 1, (int)((long)(x + 1) * columns / width));
            Array.Clear(column);
            for (int c = c0; c < c1; c++)
            {
                System.Numerics.Tensors.TensorPrimitives.Max<byte>(column, evidence.Spectrogram.AsSpan(c * rows, rows), column);
            }

            for (int y = 0; y < height; y++)
            {
                int fromBottom = height - 1 - y;
                int r0 = fromBottom * rows / height;
                int r1 = Math.Max(r0 + 1, (fromBottom + 1) * rows / height);
                byte level = 0;
                for (int r = r0; r < r1; r++)
                {
                    level = Math.Max(level, column[r]);
                }

                pixels[(y * width) + x] = palette[level];
            }
        }

        // Clear the four corners outside the frame's radius.
        int rad = (int)Math.Ceiling(radius);
        for (int y = 0; y < Math.Min(rad, height); y++)
        {
            for (int x = 0; x < Math.Min(rad, width); x++)
            {
                double dx = rad - x - 0.5;
                double dy = rad - y - 0.5;
                if ((dx * dx) + (dy * dy) > radius * radius)
                {
                    pixels[(y * width) + x] = 0;
                    pixels[(y * width) + (width - 1 - x)] = 0;
                    pixels[((height - 1 - y) * width) + x] = 0;
                    pixels[((height - 1 - y) * width) + (width - 1 - x)] = 0;
                }
            }
        }

        Bitmap bitmap = new(width, height, PixelFormat.Format32bppPArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * width, data.Scan0 + (y * data.Stride), width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>The spectrogram palette of subplan 08 for each level byte, as opaque ARGB.</summary>
    private static readonly Lazy<int[]> Palette = new(() =>
    {
        (double At, int Rgb)[] stops = [(0, 0x000004), (0.25, 0x3B0F70), (0.5, 0x8C2981), (0.75, 0xDE4968), (0.9, 0xFE9F6D), (1, 0xFCFDBF)];
        int[] table = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            int s = 1;
            while (s < stops.Length - 1 && v > stops[s].At)
            {
                s++;
            }

            double f = (v - stops[s - 1].At) / (stops[s].At - stops[s - 1].At);
            int a = stops[s - 1].Rgb;
            int b = stops[s].Rgb;
            int Channel(int shift) => (int)Math.Round(((a >> shift) & 0xFF) + ((((b >> shift) & 0xFF) - ((a >> shift) & 0xFF)) * f));
            table[i] = unchecked((int)0xFF000000) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
        }

        return table;
    });

    /// <summary>
    /// The waveform (subplan 08, Waveform): the envelope in <c>Text3</c> about a <c>Stroke</c> centre line,
    /// clipped runs as <c>CritFill</c> ticks at the top edge and dropouts as <c>HighFill</c> ticks at the
    /// bottom. A tick opens the 20 ms around it; a click in the zoomed view goes back.
    /// </summary>
    private void DrawWaveform(Graphics g, FindingDetail f, Rectangle view, FileDetail d, ThemeTokens t)
    {
        AudioEvidence? evidence = _evidence;
        int centre = view.Y + (view.Height / 2);
        float half = (view.Height / 2f) - SF(1);
        if (_zoom is (long zoomId, int mark) && zoomId == f.Id && evidence is not null && mark < evidence.MarkWindows.Length)
        {
            float[] window = evidence.MarkWindows[mark];
            DrawEnvelope(g, view, window, window, Math.Max(1, evidence.WindowChannels), centre, half, t.Text2);
            KitPaint.HorizontalLine(g, t.Stroke, view.X, view.Right, centre, SF(1));
            using Pen dashed = new(t.Accent, SF(1)) { DashPattern = [4, 3] };
            float mid = view.X + (view.Width / 2f);
            g.DrawLine(dashed, mid, view.Y, mid, view.Bottom);
            KitPaint.StrokeRoundedRect(g, view, SF(4), t.Stroke, SF(1));
            _markHits.Add((view, f.Id, -1));
            return;
        }

        if (evidence is { WaveMin.Length: > 0 })
        {
            DrawEnvelope(g, view, evidence.WaveMin, evidence.WaveMax, 1, centre, half, t.Text3);
        }
        else if (_evidenceLoading)
        {
            KitPaint.FillRoundedRect(g, view, SF(4), t.Hover);
        }

        KitPaint.HorizontalLine(g, t.Stroke, view.X, view.Right, centre, SF(1));
        KitPaint.StrokeRoundedRect(g, view, SF(4), t.Stroke, SF(1));
        if (evidence is not { Seconds: > 0 })
        {
            return;
        }

        foreach (FindingDetail other in d.Findings)
        {
            bool clip = other.RuleId == "AUD-CLIPPING";
            if (!clip && other.RuleId != "COR-DROPOUT")
            {
                continue;
            }

            if (!other.Evidence.TryGetValue("positionsSeconds", out object? positions) || positions is not object[] list)
            {
                continue;
            }

            foreach (object position in list)
            {
                double seconds = Convert.ToDouble(position, CultureInfo.InvariantCulture);
                int x = view.X + (int)Math.Round(view.Width * Math.Clamp(seconds / evidence.Seconds, 0, 1));
                int tickY = clip ? view.Y + S(1) : view.Bottom - S(1) - S(6);
                g.FillRectangle(GdiCache.Brush(clip ? t.Critical.Fill : t.High.Fill), x - S(1), tickY, S(2), S(6));
                int index = IndexOfMark(evidence.Marks, seconds);
                if (index >= 0)
                {
                    _markHits.Add((new Rectangle(x - S(4), view.Y, S(8), view.Height), f.Id, index));
                }
            }
        }
    }

    /// <summary>
    /// Fills one 1 px column per pixel from the minimum to the maximum of the values it covers; values are
    /// <paramref name="stride"/> apart per position (interleaved channels share a column).
    /// </summary>
    private static void DrawEnvelope(Graphics g, Rectangle view, float[] lows, float[] highs, int stride, int centre, float half, Color color)
    {
        Brush brush = GdiCache.Brush(color);
        int count = lows.Length / stride;
        for (int x = 0; x < view.Width && count > 0; x++)
        {
            int from = (int)((long)x * count / view.Width);
            int to = Math.Min(count, Math.Max(from + 1, (int)((long)(x + 1) * count / view.Width)));
            float low = System.Numerics.Tensors.TensorPrimitives.Min(lows.AsSpan(from * stride, (to - from) * stride));
            float high = System.Numerics.Tensors.TensorPrimitives.Max(highs.AsSpan(from * stride, (to - from) * stride));
            float top = centre - (Math.Clamp(high, -1, 1) * half);
            float bottom = centre - (Math.Clamp(low, -1, 1) * half);
            g.FillRectangle(brush, view.X + x, top, 1, Math.Max(1, bottom - top));
        }
    }

    private static int IndexOfMark(double[] marks, double seconds)
    {
        for (int i = 0; i < marks.Length; i++)
        {
            if (Math.Abs(marks[i] - seconds) < 1e-6)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The caption of a zoomed waveform: the mark's position and how to go back.</summary>
    private string? ZoomCaption(FindingDetail f) =>
        _zoom is (long id, int mark) && id == f.Id && _evidence is { } e && mark < e.Marks.Length
            ? string.Format(CultureInfo.CurrentCulture, Strings.Drawer.ZoomCaption, EvidenceText.MediaTime(e.Marks[mark]))
            : null;

    /// <summary>Starts rendering the spectrogram and waveform when one of the findings shows them.</summary>
    /// <summary>Starts the header thumbnail and, for an upscale finding, its frame strip.</summary>
    private void StartPictures(FileDetail detail)
    {
        _pictureCancel?.Cancel();
        _pictureCancel?.Dispose();
        _pictureCancel = new CancellationTokenSource();
        _thumbnail?.Dispose();
        _thumbnail = null;
        _strip?.Dispose();
        _strip = null;
        _compare?.Dispose();
        _compare = null;
        _compareIndex = -1;
        _holding = false;
        CancellationToken token = _pictureCancel.Token;
        if (ThumbnailLoader is { } thumbnails)
        {
            UiTask.Run(LoadPictureAsync(detail, () => thumbnails(detail, token), image => _thumbnail = image, token));
        }

        FindingDetail? finding = detail.Findings.FirstOrDefault(f => f.View == EvidenceView.FrameStrip);
        _stripCount = finding is null ? 0 : Math.Max(1, UI.Data.LiveUiData.FrameTimes(finding.Evidence).Length);
        if (StripLoader is { } strips && finding is not null)
        {
            UiTask.Run(LoadPictureAsync(detail, () => strips(detail, finding, token), image => _strip = image, token));
        }
    }

    private async Task LoadPictureAsync(FileDetail detail, Func<byte[]?> load, Action<Bitmap> apply, CancellationToken token)
    {
        byte[]? png;
        try
        {
            png = await Task.Run(load, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (png is null || token.IsCancellationRequested || IsDisposed || !ReferenceEquals(_detail, detail))
        {
            return;
        }

        try
        {
            using MemoryStream stream = new(png);
            using Bitmap decoded = new(stream);
            apply(new Bitmap(decoded));
            Invalidate();
        }
        catch (ArgumentException)
        {
            // Not an image after all; the placeholder stays.
        }
    }

    /// <summary>Shows (or, for the frame already shown, hides) the compare view of one sampled frame.</summary>
    private void SelectFrame(int index)
    {
        if (_detail is not FileDetail detail || detail.Findings.FirstOrDefault(f => f.View == EvidenceView.FrameStrip) is not FindingDetail finding)
        {
            return;
        }

        _compare?.Dispose();
        _compare = null;
        _holding = false;
        if (_compareIndex == index)
        {
            _compareIndex = -1;
            Relayout();
            return;
        }

        _compareIndex = index;
        Relayout();
        if (CompareLoader is not { } compare || _pictureCancel is null)
        {
            return;
        }

        double[] times = UI.Data.LiveUiData.FrameTimes(finding.Evidence);
        double time = index < times.Length ? times[index] : 0;
        CancellationToken token = _pictureCancel.Token;
        _compareLoading = true;
        UiTask.Run(LoadPictureAsync(detail, () => compare(detail, finding, time, token), image =>
        {
            _compare = image;
            _compareLoading = false;
        }, token));
    }

    private int CompareHeight() => (int)Math.Round((Width - S(2 * Pad) - S(24)) * (double)Core.Analysis.PictureRenderer.CompareHeight / Core.Analysis.PictureRenderer.CompareWidth);

    /// <summary>
    /// The frame strip (subplan 08, Frame strip): thumbnails 96 × 54, radius 4, 8 px apart, the frame's score beneath,
    /// unusable frames at 40 % opacity. A click opens the compare view below it.
    /// </summary>
    private void DrawFrameStrip(Graphics g, FindingDetail f, Rectangle view, ThemeTokens t, TypeRamp type)
    {
        object[] scores = f.Evidence.TryGetValue("frameSsim", out object? s) && s is object[] a ? a : [];
        object[] usable = f.Evidence.TryGetValue("frameUsable", out object? u) && u is object[] b ? b : [];
        int width = S(96);
        int height = S(54);
        int fit = Math.Max(1, (view.Width + S(8)) / (width + S(8)));
        int count = Math.Min(fit, _stripCount);
        for (int i = 0; i < count; i++)
        {
            Rectangle cell = new(view.X + (i * (width + S(8))), view.Y, width, height);
            bool used = i >= usable.Length || usable[i] is not bool flag || flag;
            if (_strip is not null)
            {
                int sourceWidth = _strip.Width / Math.Max(1, _stripCount);
                Rectangle source = new(i * sourceWidth, 0, sourceWidth, _strip.Height);
                if (used)
                {
                    g.DrawImage(_strip, cell, source, GraphicsUnit.Pixel);
                }
                else
                {
                    g.DrawImage(_strip, cell, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, Faded);
                }
            }
            else
            {
                KitPaint.FillRoundedRect(g, cell, SF(4), t.Hover);
            }

            KitPaint.StrokeRoundedRect(g, cell, SF(4), i == _compareIndex ? t.Accent : t.Stroke, SF(1));
            if (i < scores.Length)
            {
                string score = Convert.ToDouble(scores[i], CultureInfo.InvariantCulture).ToString("0.000", CultureInfo.CurrentCulture);
                KitPaint.DrawText(g, score, type.Caption, used ? t.Text2 : t.Text3, cell.X, cell.Bottom + S(10), width);
            }

            _stripHits.Add((cell, i));
        }

        _compareRect = Rectangle.Empty;
        if (_compareIndex < 0)
        {
            return;
        }

        Rectangle compare = new(view.X, view.Y + S(54 + 20 + 8), view.Width, CompareHeight());
        _compareRect = compare;
        if (_compare is not null)
        {
            int half = _compare.Width / 2;
            Rectangle source = new(_holding ? half : 0, 0, half, _compare.Height);
            g.DrawImage(_compare, compare, source, GraphicsUnit.Pixel);
        }
        else if (_compareLoading)
        {
            KitPaint.FillRoundedRect(g, compare, SF(4), t.Hover);
        }

        KitPaint.StrokeRoundedRect(g, compare, SF(4), t.Stroke, SF(1));
    }

    private string? CompareCaption(FindingDetail f)
    {
        if (f.View != EvidenceView.FrameStrip)
        {
            return null;
        }

        int effective = f.Evidence.TryGetValue("effectiveHeight", out object? e) ? Convert.ToInt32(e, CultureInfo.InvariantCulture) : 0;
        return _compareIndex < 0 ? Strings.Drawer.PickFrame
            : string.Format(CultureInfo.CurrentCulture, _holding ? Strings.Drawer.CompareRebuilt : Strings.Drawer.CompareOriginal, effective);
    }

    /// <summary>
    /// The bitrate card (subplan 11, Evidence views): resolution, frame rate, codec and bitrate, then the H.264-equivalent
    /// bits per pixel on a logarithmic scale from 0.005 to 0.5 with the rule's limits marked.
    /// </summary>
    private void DrawBitrate(Graphics g, FindingDetail f, Rectangle view, ThemeTokens t, TypeRamp type)
    {
        IReadOnlyDictionary<string, object> e = f.Evidence;
        double Number(string key) => e.TryGetValue(key, out object? v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
        double kbps = Number("bitrateKbps");
        string rate = kbps >= 1000 ? string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Mbps, (kbps / 1000).ToString("0.0", CultureInfo.CurrentCulture)) : string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Kbps, kbps.ToString("0", CultureInfo.CurrentCulture));
        string facts = UiFormat.Facts(
            Number("width") > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Dimensions, Number("width"), Number("height")) : null,
            Number("fps") > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Drawer.Fps, Number("fps").ToString("0.##", CultureInfo.CurrentCulture)) : null,
            e.TryGetValue("codec", out object? codec) ? UiFormat.CodecName(Convert.ToString(codec, CultureInfo.InvariantCulture)) : null,
            rate);
        KitPaint.DrawText(g, facts, type.Body, t.Text, view.X, view.Y + S(8), view.Width);
        Rectangle bar = new(view.X, view.Y + S(24), view.Width, S(8));
        KitPaint.FillRoundedRect(g, bar, SF(2), t.Hover);
        float X(double bpp) => bar.X + (float)(bar.Width * Math.Clamp(Math.Log10(Math.Max(bpp, 0.005) / 0.005) / 2, 0, 1));
        foreach ((double limit, Color color) in new[] { (0.020, t.High.Fill), (0.035, t.Medium.Fill), (0.20, t.Text3) })
        {
            g.FillRectangle(GdiCache.Brush(color), X(limit) - SF(1), bar.Y - SF(3), SF(2), bar.Height + SF(6));
        }

        double value = Number("equivalentBpp");
        float mx = X(value);
        g.FillEllipse(GdiCache.Brush(t.Accent), mx - SF(5), bar.Y + (bar.Height / 2f) - SF(5), SF(10), SF(10));
        KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Drawer.BitsPerPixel, value.ToString("0.000", CultureInfo.CurrentCulture)), type.Caption, t.Text2, view.X, view.Y + S(42), view.Width);
    }

    private static System.Drawing.Imaging.ImageAttributes FadedAttributes()
    {
        System.Drawing.Imaging.ImageAttributes attributes = new();
        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.4f });
        return attributes;
    }

    private void StartEvidence(FileDetail detail)
    {
        StartPictures(detail);
        _evidenceCancel?.Cancel();
        _evidenceCancel?.Dispose();
        _evidenceCancel = null;
        _spectrogramImage?.Dispose();
        _spectrogramImage = null;
        _evidence = null;
        _zoom = null;
        _evidenceLoading = false;
        EvidenceLoading = Task.CompletedTask;
        if (EvidenceLoader is not { } loader || !detail.Findings.Any(f => f.View is EvidenceView.Spectrogram or EvidenceView.Waveform))
        {
            return;
        }

        _evidenceCancel = new CancellationTokenSource();
        _evidenceLoading = true;
        EvidenceLoading = LoadEvidenceAsync(detail, loader, _evidenceCancel.Token);
    }

    private async Task LoadEvidenceAsync(FileDetail detail, Func<FileDetail, CancellationToken, AudioEvidence?> loader, CancellationToken token)
    {
        AudioEvidence? evidence;
        try
        {
            evidence = await Task.Run(() => loader(detail, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || IsDisposed || !ReferenceEquals(_detail, detail))
        {
            return;
        }

        _evidence = evidence;
        _evidenceLoading = false;
        Invalidate();
    }

    private void PlaceButtons(int index, int x, int y)
    {
        if (index >= _findingButtons.Count)
        {
            return;
        }

        (KitButton ignore, KitButton rescan) = _findingButtons[index];
        bool canIgnore = ignore.Tag is not false;
        int w1 = canIgnore ? S(ignore.PreferredLogicalWidth) + S(8) : 0;
        int w2 = S(rescan.PreferredLogicalWidth);
        ignore.Bounds = new Rectangle(x, y, Math.Max(0, w1 - S(8)), S(32));
        rescan.Bounds = new Rectangle(x + w1, y, w2, S(32));
        ignore.Visible = canIgnore;
        rescan.Visible = true;
    }

    private void HideButtons(int index)
    {
        if (index < _findingButtons.Count)
        {
            _findingButtons[index].Ignore.Visible = false;
            _findingButtons[index].Rescan.Visible = false;
        }
    }
}
