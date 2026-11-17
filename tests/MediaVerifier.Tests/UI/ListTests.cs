using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>Virtual list, tree, breadcrumb and data graphics (tasks UK-07, UK-08, UK-10).</summary>
[Collection(UiCollection.Name)]
public sealed class ListTests
{
    [Fact]
    public void A_million_rows_scroll_without_allocations()
    {
        using VirtualList list = new() { Size = new Size(1000, 600), Mode = ListMode.TwoLine, Source = new BigSource(1_000_000, groupEvery: 1000) };
        using Bitmap bitmap = new(1000, 600);
        using Graphics g = Graphics.FromImage(bitmap);
        void PaintOnce()
        {
            using PaintEventArgs args = new(g, new Rectangle(0, 0, 1000, 600));
            typeof(Control).GetMethod("OnPaint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(list, [args]);
        }

        for (int i = 0; i < 20; i++)
        {
            list.ScrollOffset += 4000;
            PaintOnce();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            list.ScrollOffset += 7919;
            PaintOnce();
        }

        long perStep = (GC.GetAllocatedBytesForCurrentThread() - before) / 200;

        // Reflection invocation and PaintEventArgs cost a few hundred bytes per call; painting itself nothing.
        Assert.True(perStep < 1024, $"{perStep} bytes per scroll step");
        Assert.True(list.ScrollOffset > 0);
    }

    [Fact]
    public void Rows_are_positioned_with_short_group_headers()
    {
        using VirtualList list = new() { Size = new Size(800, 400), Mode = ListMode.TwoLine, Source = new BigSource(100, groupEvery: 10) };

        // Row 0 is a 32 px header, rows 1..10 are 44 px items, row 11 the next header.
        Assert.Equal(0, list.IndexAt(new Point(10, 10)));
        Assert.Equal(1, list.IndexAt(new Point(10, 40)));
        Assert.Equal(11, list.IndexAt(new Point(10, 32 + (10 * 44) + 5)));
    }

    [Fact]
    public void Click_selection_follows_ctrl_and_shift()
    {
        BigSource source = new(50, groupEvery: 0);
        using VirtualList list = new() { Size = new Size(800, 400), Source = source };
        list.ApplyClickSelection(3, Keys.None);
        list.ApplyClickSelection(6, Keys.Shift);
        Assert.Equal([3L, 4L, 5L, 6L], list.SelectedIds.Order());
        list.ApplyClickSelection(10, Keys.Control);
        Assert.Equal(5, list.SelectedIds.Count);
        list.ApplyClickSelection(4, Keys.Control);
        Assert.DoesNotContain(4L, list.SelectedIds);
        list.SelectAll();
        Assert.Equal(50, list.SelectedIds.Count);
        list.ClearSelection();
        Assert.Empty(list.SelectedIds);
    }

    [Fact]
    public void Table_columns_keep_fixed_widths_and_absorb_the_rest()
    {
        ListColumn[] columns = [new("Name", 0), new("Format", 200), new("Size", 90, RightAligned: true)];
        Span<int> widths = stackalloc int[3];
        VirtualList.ColumnWidths(columns, 1000, 96, widths);
        Assert.Equal(710, widths[0]);
        Assert.Equal(200, widths[1]);
        Assert.Equal(90, widths[2]);
    }

    [Fact]
    public void Tree_loads_children_lazily_and_navigates_with_keys()
    {
        using TreeList tree = new() { Size = new Size(280, 400) };
        TreeItem music = new("Music") { Severity = Severity.Critical };
        tree.Roots.Add(music);
        int loads = 0;
        tree.LoadChildren += (_, node) =>
        {
            loads++;
            node.SetChildren([new TreeItem("Glasshouse") { HasChildren = false }, new TreeItem("Night Drive") { HasChildren = false }]);
        };
        tree.Rebuild();
        tree.SelectedNode = music;
        Send(tree, Keys.Right);
        Assert.True(music.Expanded);
        Assert.Equal(1, loads);
        Assert.Equal(3, tree.VisibleNodes.Count);
        Send(tree, Keys.Right);
        Assert.Equal("Glasshouse", tree.SelectedNode!.Text);
        Send(tree, Keys.Left);
        Assert.Same(music, tree.SelectedNode);
        Send(tree, Keys.Left);
        Assert.False(music.Expanded);
        Send(tree, Keys.Right);
        Assert.Equal(1, loads);
        Assert.Equal(AccessibleRole.OutlineItem, tree.AccessibilityObject.GetChild(0)!.Role);
    }

    [Fact]
    public void Severity_bar_segments_hit_test_by_tier()
    {
        using SeverityBar bar = new() { Size = new Size(320, 16) };
        bar.SetCounts(1, 2, 10, 20, 60);
        using Bitmap bitmap = new(320, 16);
        bar.DrawToBitmap(bitmap, new Rectangle(0, 0, 320, 16));
        Assert.Equal(Severity.Critical, bar.TierAt(new Point(1, 8)));
        Assert.Equal(Severity.None, bar.TierAt(new Point(318, 8)));
        Assert.Contains("Critical: 1", bar.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void Graphics_paint_at_every_scale()
    {
        RectangleF[] segments = new RectangleF[5];
        foreach (int dpi in new[] { 96, 120, 144, 192 })
        {
            using Bitmap bitmap = new(800, 300);
            using Graphics g = Graphics.FromImage(bitmap);
            DataPaint.ProgressBar(g, new RectangleF(10, 10, 280, 8), 0.42);
            DataPaint.ProgressBar(g, new RectangleF(10, 30, 280, 4), 0.001);
            DataPaint.IndeterminateBar(g, new RectangleF(10, 40, 280, 4), 0.3);
            DataPaint.ProgressRing(g, new PointF(336, 26), 8, 30, dpi);
            DataPaint.SeverityBar(g, new RectangleF(400, 20, 320, 8), [3, 4, 30, 70, 200], segments, dpi);
            DataPaint.Donut(g, new PointF(100, 150), 40, 10, 0.82, ThemeService.Tokens.Accent);
            DataPaint.Donut(g, new PointF(200, 150), 22, 6, 1, ThemeService.Tokens.Ok.Fill);
            DataPaint.Sparkline(g, new RectangleF(300, 120, 200, 40), [3, 5, 4, 8, 6, 7], dpi);
            DataPaint.Timeline(g, new Rectangle(520, 100, 260, 160), [("Scan finished", "03:12"), ("Verified", "Yesterday")], 40, dpi);
            VirtualList.DrawScrollThumb(g, new Rectangle(780, 10, 6, 40), false, dpi);
            Breadcrumb.Draw(g, new Rectangle(10, 260, 400, 32), ["Music", "Night Drive", "Overpass"], 1, -1, dpi);
        }
    }

    private static void Send(Control control, Keys key) =>
        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, [new KeyEventArgs(key)]);

    /// <summary>A large synthetic source: a group header every <c>groupEvery</c> items (0 = none).</summary>
    private sealed class BigSource(int count, int groupEvery) : IListSource
    {
        private const string Name = "Night Drive - 07 - Overpass.flac";
        private const string Summary = "Likely made from a lossy source · hard cutoff at 16.6 kHz";
        private const string Folder = @"D:\Music\Night Drive\Overpass";
        private const string Size = "31.2 MB";

        public int Count => count;

        public long IdOf(int index) => IsGroup(index) ? -1 - index : index;

        public void Fill(int index, ListRow row)
        {
            if (IsGroup(index))
            {
                row.IsGroup = true;
                row.Line1 = "High";
                row.Count = "14";
                row.Severity = Severity.High;
                return;
            }

            row.Severity = Severity.High;
            row.Line1 = Name;
            row.Line2 = Summary;
            row.Folder = Folder;
            row.Size = Size;
        }

        public void SetCollapsed(int index, bool collapsed)
        {
        }

        public int GroupsBefore(int index) => groupEvery <= 0 ? 0 : (index + groupEvery) / (groupEvery + 1);

        private bool IsGroup(int index) => groupEvery > 0 && index % (groupEvery + 1) == 0;
    }
}
