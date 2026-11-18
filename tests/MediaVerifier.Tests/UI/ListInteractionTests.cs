using System.Reflection;
using System.Runtime.InteropServices;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The virtual list and the folder tree driven like a user drives them: mouse messages sent to their windows in a form
/// far off-screen that never takes activation, and keys through their handlers (audit, kit lists).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ListInteractionTests
{
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int MkLButton = 0x0001;
    private const int MkRButton = 0x0002;
    private const int MkMButton = 0x0010;

    /// <summary>A right-click on an unselected row selects it alone, moves the anchor and focus there, and says so.</summary>
    [Fact]
    public void A_right_click_selects_its_row_and_raises_selection_changed()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(30);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            list.ApplyClickSelection(1, Keys.None);
            list.ApplyClickSelection(2, Keys.Control);
            list.ApplyClickSelection(3, Keys.Control);
            int changes = 0;
            int menuRow = -1;
            int focusWhenChanged = -1;
            list.SelectionChanged += (_, _) =>
            {
                changes++;
                focusWhenChanged = list.FocusedIndex;
            };
            list.MoreRequested += (_, args) => menuRow = args.Index;

            Click(list, WmRButtonDown, WmRButtonUp, MkRButton, RowPoint(list, 6, 200));

            Assert.Equal([6L], list.SelectedIds.Order());
            Assert.Equal(1, changes);
            Assert.Equal(6, focusWhenChanged);
            Assert.Equal(6, menuRow);

            // The anchor moved too: Shift+click extends from the right-clicked row.
            list.ApplyClickSelection(8, Keys.Shift);
            Assert.Equal([6L, 7L, 8L], list.SelectedIds.Order());

            // A right-click inside the selection keeps it.
            Click(list, WmRButtonDown, WmRButtonUp, MkRButton, RowPoint(list, 7, 200));
            Assert.Equal([6L, 7L, 8L], list.SelectedIds.Order());
            Assert.Equal(7, list.FocusedIndex);
        });
    }

    /// <summary>
    /// The hover More button acts on its own row (its menu's Ignore, Copy path and Quarantine read the selection, which
    /// was another row), and the menu opens right-aligned under the button instead of running off the window.
    /// </summary>
    [Fact]
    public void The_more_button_targets_its_own_row_and_opens_its_menu_under_it()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(30);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            list.ApplyClickSelection(1, Keys.None);
            (int Index, Point Screen) menu = (-1, Point.Empty);
            list.MoreRequested += (_, args) => menu = args;
            int dpi = list.DeviceDpi;
            Rectangle more = MoreButton(list, 5);

            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, Centre(more));

            Assert.Equal(5, menu.Index);
            Assert.Equal([5L], list.SelectedIds.Order());
            Assert.Equal(5, list.FocusedIndex);
            Point expected = list.PointToScreen(new Point(more.Right - Dpi.Scale(PopupList.MenuWidth, dpi), more.Bottom + Dpi.Scale(4, dpi)));
            Assert.Equal(expected, menu.Screen);

            // Pressed on the button but released elsewhere: nothing.
            menu = (-1, Point.Empty);
            Send(list, WmLButtonDown, MkLButton, Centre(MoreButton(list, 7)));
            Send(list, WmLButtonUp, 0, RowPoint(list, 7, 200));
            Assert.Equal(-1, menu.Index);
            Assert.Equal([5L], list.SelectedIds.Order());
        });
    }

    /// <summary>The hover Details button selects and focuses its row, so Up and Down in the drawer go on from it.</summary>
    [Fact]
    public void The_details_button_targets_its_own_row()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(30);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            list.ApplyClickSelection(1, Keys.None);
            int details = -1;
            list.DetailsClicked += (_, index) => details = index;
            Rectangle more = MoreButton(list, 4);
            Point detailsPoint = new(more.X - Dpi.Scale(24, list.DeviceDpi), more.Y + (more.Height / 2));

            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, detailsPoint);

            Assert.Equal(4, details);
            Assert.Equal([4L], list.SelectedIds.Order());
            Assert.Equal(4, list.FocusedIndex);
        });
    }

    /// <summary>The context-menu key on a focused row outside the selection (Ctrl+arrows) selects that row first.</summary>
    [Fact]
    public void The_context_menu_key_targets_the_focused_row()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(30);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            list.ApplyClickSelection(2, Keys.None);
            Key(list, Keys.Down | Keys.Control);
            Key(list, Keys.Down | Keys.Control);
            Assert.Equal([2L], list.SelectedIds.Order());
            int menuRow = -1;
            list.MoreRequested += (_, args) => menuRow = args.Index;

            Key(list, Keys.Apps);

            Assert.Equal(4, menuRow);
            Assert.Equal([4L], list.SelectedIds.Order());
        });
    }

    /// <summary>
    /// After a fix, a filter change or a reload, selected rows that are gone leave the selection and the page hears it
    /// (the bar kept advertising them, and Ignore turned resolved findings into ignored ones). Rows inside a group that
    /// collapses leave it too: nothing acts on rows that are not shown.
    /// </summary>
    [Fact]
    public void Refresh_drops_selected_rows_that_are_gone()
    {
        Rows rows = Rows.Items(20);
        using VirtualList list = new() { Size = new Size(800, 400), Source = rows };
        list.ApplyClickSelection(3, Keys.None);
        list.ApplyClickSelection(4, Keys.Control);
        list.ApplyClickSelection(5, Keys.Control);
        int changes = 0;
        list.SelectionChanged += (_, _) => changes++;

        rows.Ids.Remove(3);
        rows.Ids.Remove(5);
        list.Refresh();

        Assert.Equal([4L], list.SelectedIds.Order());
        Assert.Equal(1, changes);

        // Nothing gone: no event.
        list.Refresh();
        Assert.Equal(1, changes);

        // The same through re-assigning the source, as the Library and Scans pages do.
        rows.Ids.Remove(4);
        list.Source = rows;
        Assert.Empty(list.SelectedIds);
        Assert.Equal(2, changes);

        // A group that collapses takes its selected rows out of the selection.
        Rows grouped = new() { Ids = [-1, 10, 11, 12, -2, 20, 21] };
        list.Source = grouped;
        list.ApplyClickSelection(2, Keys.None);
        list.ApplyClickSelection(5, Keys.Control);
        grouped.Ids.RemoveRange(1, 3);
        list.Refresh();
        Assert.Equal([20L], list.SelectedIds.Order());
    }

    /// <summary>
    /// Keyboard focus and the Shift anchor follow their rows by id: after a sort, Ctrl+C and Show in folder acted on the
    /// file that moved into the focused position, not on the highlighted one.
    /// </summary>
    [Fact]
    public void Focus_and_anchor_follow_their_rows_after_a_reorder()
    {
        Rows rows = Rows.Items(10);
        using VirtualList list = new() { Size = new Size(800, 400), Source = rows };
        list.ApplyClickSelection(3, Keys.None);
        Assert.Equal(3, list.FocusedIndex);

        rows.Ids.Reverse();
        list.Refresh();

        Assert.Equal(6, list.FocusedIndex);
        Assert.Equal(3L, rows.IdOf(list.FocusedIndex));
        Assert.Equal([3L], list.SelectedIds.Order());

        // The anchor moved with its row: Shift+click ranges from id 3 (now index 6).
        list.ApplyClickSelection(8, Keys.Shift);
        Assert.Equal([1L, 2L, 3L], list.SelectedIds.Order());
    }

    /// <summary>
    /// When the focused row goes away nothing is focused, so Delete and Ctrl+I do not act on the row that slid into its
    /// place; the next arrow key continues there.
    /// </summary>
    [Fact]
    public void A_focused_row_that_goes_away_leaves_nothing_focused()
    {
        Rows rows = Rows.Items(10);
        using VirtualList list = new() { Size = new Size(800, 400), Source = rows };
        list.ApplyClickSelection(4, Keys.None);

        rows.Ids.Remove(4);
        list.Refresh();

        Assert.Equal(-1, list.FocusedIndex);
        Assert.Empty(list.SelectedIds);
        Key(list, Keys.Down);
        Assert.Equal(4, list.FocusedIndex);
        Assert.Equal(5L, rows.IdOf(list.FocusedIndex));
    }

    /// <summary>
    /// Rows removed above the view do not move the rows in it; a new query (Refresh without keeping the scroll) starts at
    /// the top; a list at the top stays at the top when the data is reordered.
    /// </summary>
    [Fact]
    public void The_row_at_the_top_stays_in_place_when_rows_above_it_go()
    {
        Rows rows = Rows.Items(100);
        using VirtualList list = new() { Size = new Size(800, 400), Mode = ListMode.Table, Source = rows };
        int row = Dpi.Scale(36, list.DeviceDpi);
        list.ScrollOffset = (20 * row) + 5;

        rows.Ids.RemoveRange(0, 5);
        list.Refresh();

        Assert.Equal((15 * row) + 5, list.ScrollOffset);

        list.Refresh(keepScroll: false);
        Assert.Equal(0, list.ScrollOffset);

        rows.Ids.Reverse();
        list.Refresh();
        Assert.Equal(0, list.ScrollOffset);
    }

    /// <summary>A source that keeps an id map spares the list its pass over every row after a reorder.</summary>
    [Fact]
    public void A_source_map_spares_the_pass_over_every_row()
    {
        Rows rows = Rows.Items(10_000);
        using VirtualList list = new() { Size = new Size(800, 400), Source = rows };
        list.ApplyClickSelection(10, Keys.None);
        list.ApplyClickSelection(20, Keys.Control);
        list.ApplyClickSelection(30, Keys.Control);
        rows.Ids.Reverse();

        rows.IdOfCalls = 0;
        rows.Map = true;
        list.Refresh();
        Assert.True(rows.IdOfCalls < 100, $"{rows.IdOfCalls} ids read with a map");
        Assert.Equal(30L, rows.IdOf(list.FocusedIndex));
        Assert.Equal([10L, 20L, 30L], list.SelectedIds.Order());

        // Without one, a single pass finds focus, anchor, top and selection together.
        rows.Ids.Reverse();
        rows.Map = false;
        rows.IdOfCalls = 0;
        list.Refresh();
        Assert.True(rows.IdOfCalls < 2 * rows.Count, $"{rows.IdOfCalls} ids read for {rows.Count} rows");
        Assert.Equal(30L, rows.IdOf(list.FocusedIndex));
    }

    /// <summary>
    /// The filter summary row clears the filters only through its link (a click anywhere on the row, or Left on it,
    /// cleared every chip and drop-down); Enter on it follows the link; the pointer is a hand over the link.
    /// </summary>
    [Fact]
    public void The_summary_row_clears_filters_only_through_its_link()
    {
        Sta.Run(() =>
        {
            Rows rows = new() { Ids = [long.MinValue, -1, 1, 2, 3] };
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            int dpi = list.DeviceDpi;
            int centreY = Dpi.Scale(16, dpi);

            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, new Point(Dpi.Scale(20, dpi), centreY));
            Assert.Empty(rows.Collapses);
            Assert.Equal(0, list.FocusedIndex);

            Key(list, Keys.Left);
            Key(list, Keys.Right);
            Assert.Empty(rows.Collapses);

            Point link = new(LinkX(list, rows) + Dpi.Scale(10, dpi), centreY);
            Send(list, WmMouseMove, 0, link);
            Assert.Same(Cursors.Hand, list.Cursor);
            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, link);
            Assert.Equal([0], rows.Collapses.Select(c => c.Index));

            Key(list, Keys.Enter);
            Assert.Equal(2, rows.Collapses.Count);

            Send(list, WmMouseMove, 0, new Point(Dpi.Scale(20, dpi), centreY));
            Assert.Same(Cursors.Default, list.Cursor);
        });
    }

    /// <summary>Tab into a list with nothing focused focuses the first row in view, so a focus ring shows somewhere.</summary>
    [Fact]
    public void Tabbing_into_a_list_focuses_the_first_row_in_view()
    {
        Rows rows = Rows.Items(100);
        using VirtualList list = new() { Size = new Size(800, 400), Mode = ListMode.Table, Source = rows };
        list.ScrollOffset = 10 * Dpi.Scale(36, list.DeviceDpi);

        Invoke(list, "OnGotFocus", EventArgs.Empty);

        Assert.Equal(10, list.FocusedIndex);
    }

    /// <summary>Headers sort on release over the same column, and only with the left button (a right-click re-sorted).</summary>
    [Fact]
    public void Headers_sort_on_a_left_release_over_the_same_column()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(10);
            using Form host = Host(out VirtualList list, rows, ListMode.Table);
            list.Columns = [new ListColumn("Name", 0), new ListColumn("Size", 200)];
            int dpi = list.DeviceDpi;
            List<(int Column, bool Descending)> sorts = [];
            list.SortChanged += (_, sort) => sorts.Add(sort);
            Point name = new(Dpi.Scale(40, dpi), Dpi.Scale(16, dpi));
            Point size = new(list.Width - Dpi.Scale(100, dpi), Dpi.Scale(16, dpi));

            Click(list, WmRButtonDown, WmRButtonUp, MkRButton, name);
            Click(list, WmMButtonDown, WmMButtonUp, MkMButton, name);
            Assert.Empty(sorts);

            Send(list, WmLButtonDown, MkLButton, name);
            Send(list, WmLButtonUp, 0, size);
            Assert.Empty(sorts);

            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, size);
            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, size);
            Assert.Equal([(1, false), (1, true)], sorts);
        });
    }

    /// <summary>A middle click does not select a row (only left and right clicks act on rows).</summary>
    [Fact]
    public void A_middle_click_does_not_select()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(10);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            Click(list, WmMButtonDown, WmMButtonUp, MkMButton, RowPoint(list, 3, 200));
            Assert.Empty(list.SelectedIds);
            Assert.NotEqual(3, list.FocusedIndex);
        });
    }

    /// <summary>Arrowing down keeps the focused row above the selection bar's 64 px at the bottom.</summary>
    [Fact]
    public void Keyboard_focus_stays_above_the_selection_bar()
    {
        Rows rows = Rows.Items(100);
        using VirtualList list = new() { Size = new Size(800, 400), Source = rows, BottomPadding = 64 };
        int dpi = list.DeviceDpi;
        list.ApplyClickSelection(0, Keys.None);
        for (int i = 0; i < 20; i++)
        {
            Key(list, Keys.Down | Keys.Shift);
            int bottom = ((list.FocusedIndex + 1) * Dpi.Scale(44, dpi)) - list.ScrollOffset;
            Assert.True(bottom <= list.Height - Dpi.Scale(64, dpi), $"row {list.FocusedIndex} ends at {bottom}, under the bar");
        }
    }

    /// <summary>When another window takes the mouse mid-drag, the drag ends: plain mouse moves no longer scroll.</summary>
    [Fact]
    public void A_lost_capture_ends_a_thumb_drag()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(200);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            Point thumb = new(list.Width - Dpi.Scale(6, list.DeviceDpi), Dpi.Scale(20, list.DeviceDpi));
            Send(list, WmLButtonDown, MkLButton, thumb);
            Send(list, WmMouseMove, MkLButton, thumb with { Y = thumb.Y + 40 });
            int dragged = list.ScrollOffset;
            Assert.True(dragged > 0, "The thumb did not drag.");

            list.Capture = false;
            Send(list, WmMouseMove, 0, thumb with { Y = thumb.Y + 200 });

            Assert.Equal(dragged, list.ScrollOffset);
        });
    }

    /// <summary>Window deactivation (Alt+Tab, a dialog) clears the row hover and its buttons (subplan 08).</summary>
    [Fact]
    public void Window_deactivation_clears_the_row_hover()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(10);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            Point point = RowPoint(list, 2, 200);
            Send(list, WmMouseMove, 0, point);
            Color hoverBar = BarPixel(list, 2);
            Assert.Equal(ThemeService.Tokens.StrokeStrong.ToArgb(), hoverBar.ToArgb());

            typeof(Form).GetMethod("OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [EventArgs.Empty]);

            Assert.NotEqual(ThemeService.Tokens.StrokeStrong.ToArgb(), BarPixel(list, 2).ToArgb());
        });
    }

    /// <summary>
    /// A click opens the drawer at once and the drawer slides over the right part of the list; the second press of a
    /// double-click then lands on the drawer. It is taken, with its release, as the row's double-click (open the file)
    /// instead of pressing whatever drawer button is there.
    /// </summary>
    [Fact]
    public void A_double_click_whose_second_press_lands_on_the_drawer_opens_the_row()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(10);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            using Panel drawer = new() { Bounds = new Rectangle(list.Width / 2, 0, list.Width / 2, list.Height) };
            host.Controls.Add(drawer);
            drawer.BringToFront();
            int clicked = -1;
            int opened = -1;
            list.ItemClicked += (_, index) => clicked = index;
            list.ItemDoubleClicked += (_, index) => opened = index;
            Point point = RowPoint(list, 1, list.Width - Dpi.Scale(150, list.DeviceDpi));

            Click(list, WmLButtonDown, WmLButtonUp, MkLButton, point);
            Assert.Equal(1, clicked);

            IntPtr onDrawer = LParam(drawer.PointToClient(list.PointToScreen(point)));
            Message down = Message.Create(drawer.Handle, WmLButtonDown, MkLButton, onDrawer);
            Message up = Message.Create(drawer.Handle, WmLButtonUp, IntPtr.Zero, onDrawer);
            Assert.True(Application.FilterMessage(ref down), "The second press reached the drawer.");
            Assert.True(Application.FilterMessage(ref up), "The second release reached the drawer.");
            Assert.Equal(1, opened);

            // Afterwards presses reach the drawer again.
            Message later = Message.Create(drawer.Handle, WmLButtonDown, MkLButton, onDrawer);
            Assert.False(Application.FilterMessage(ref later));
        });
    }

    /// <summary>With the drawer already open a double-click reaches the list: it opens the file without loading the details again.</summary>
    [Fact]
    public void A_double_click_on_the_list_opens_the_file_without_reopening_the_details()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(10);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            int clicks = 0;
            int opened = 0;
            list.ItemClicked += (_, _) => clicks++;
            list.ItemDoubleClicked += (_, _) => opened++;
            Point point = RowPoint(list, 2, 200);
            MouseEventArgs first = new(MouseButtons.Left, 1, point.X, point.Y, 0);
            MouseEventArgs second = new(MouseButtons.Left, 2, point.X, point.Y, 0);

            Invoke(list, "OnMouseDown", first);
            Invoke(list, "OnMouseUp", first);
            Invoke(list, "OnMouseDown", second);
            Invoke(list, "OnMouseDoubleClick", second);
            Invoke(list, "OnMouseUp", second);

            Assert.Equal(1, clicks);
            Assert.Equal(1, opened);
        });
    }

    /// <summary>
    /// Hover does not reflow a two-line row: the problem column stays a column at the same place (with the drawer open
    /// it jumped into line 2), and beside a column the folder gives up width rather than covering it.
    /// </summary>
    [Fact]
    public void Two_line_rows_keep_their_layout_on_hover()
    {
        const int dpi = 96;
        ListRow narrow = new() { Line1 = "Night Drive - 07 - Overpass.flac", Line2 = "hard cutoff at 16.6 kHz", Column = "Likely made from a lossy source", Size = "31.2 MB" };
        Rectangle row = new(0, 0, 600, 44);
        TwoLineLayout rest = VirtualList.LayoutTwoLine(row, narrow, actions: false, dpi);
        TwoLineLayout hover = VirtualList.LayoutTwoLine(row, narrow, actions: true, dpi);
        Assert.True(rest.ShowColumn);
        Assert.True(hover.ShowColumn);
        Assert.Equal(rest.ColumnX, hover.ColumnX);
        Assert.Equal(rest.TextRight, hover.TextRight);

        ListRow wide = new() { Line1 = narrow.Line1, Line2 = narrow.Line2, Column = narrow.Column, Folder = @"D:\Music\Night Drive\Overpass", Size = narrow.Size };
        row = new Rectangle(0, 0, 1032, 44);
        rest = VirtualList.LayoutTwoLine(row, wide, actions: false, dpi);
        hover = VirtualList.LayoutTwoLine(row, wide, actions: true, dpi);
        Assert.Equal(rest.ColumnX, hover.ColumnX);
        Assert.Equal(rest.TextRight, hover.TextRight);
        Assert.Equal(row.Right - 232, hover.FolderX + hover.FolderWidth);
        Assert.True(hover.FolderX >= hover.ColumnRight + 16, "The folder covers the column on hover.");

        // Without a column the folder moves 132 px left as subplan 08 draws it, and the name gives way.
        ListRow plain = new() { Line1 = wide.Line1, Line2 = wide.Line2, Folder = wide.Folder, Size = wide.Size };
        rest = VirtualList.LayoutTwoLine(row, plain, actions: false, dpi);
        hover = VirtualList.LayoutTwoLine(row, plain, actions: true, dpi);
        Assert.Equal(rest.FolderWidth, hover.FolderWidth);
        Assert.Equal(rest.FolderX - 132, hover.FolderX);
    }

    /// <summary>The painted More button has its tooltip, More actions (subplan 09), while the pointer is on it.</summary>
    [Fact]
    public void The_more_button_has_its_tooltip()
    {
        Sta.Run(() =>
        {
            Rows rows = Rows.Items(10);
            using Form host = Host(out VirtualList list, rows, ListMode.TwoLine);
            Send(list, WmMouseMove, 0, Centre(MoreButton(list, 3)));
            Assert.Equal(Strings.Kit.MoreActions, KitToolTip.Get(list));
            Send(list, WmMouseMove, 0, RowPoint(list, 3, 200));
            Assert.Equal(string.Empty, KitToolTip.Get(list));
            KitToolTip.Hide();
        });
    }

    /// <summary>
    /// A right-click selects the folder under the pointer (the menu scanned, verified and ignored rules in the folder
    /// selected before) and asks for its menu on release; empty space asks for nothing.
    /// </summary>
    [Fact]
    public void A_right_click_in_the_tree_selects_its_folder_and_asks_for_its_menu()
    {
        Sta.Run(() =>
        {
            using Form host = HostTree(out TreeList tree, out TreeItem[] roots, 5);
            tree.SelectedNode = roots[0];
            List<TreeItem> menus = [];
            tree.ContextMenuRequested += (_, args) => menus.Add(args.Node);

            Click(tree, WmRButtonDown, WmRButtonUp, MkRButton, TreeRow(tree, 3));
            Assert.Same(roots[3], tree.SelectedNode);
            Assert.Equal([roots[3]], menus);

            Click(tree, WmRButtonDown, WmRButtonUp, MkRButton, new Point(Dpi.Scale(60, tree.DeviceDpi), tree.Height - 4));
            Assert.Same(roots[3], tree.SelectedNode);
            Assert.Single(menus);
            Assert.Null(tree.NodeAt(new Point(Dpi.Scale(60, tree.DeviceDpi), tree.Height - 4)));
        });
    }

    /// <summary>The context-menu key asks for the selected folder's menu, placed under its row.</summary>
    [Fact]
    public void The_context_menu_key_asks_for_the_selected_folders_menu()
    {
        Sta.Run(() =>
        {
            using Form host = HostTree(out TreeList tree, out TreeItem[] roots, 5);
            tree.SelectedNode = roots[2];
            (TreeItem? Node, Point Screen) menu = (null, Point.Empty);
            tree.ContextMenuRequested += (_, args) => menu = args;

            Key(tree, Keys.Apps);

            Assert.Same(roots[2], menu.Node);
            int dpi = tree.DeviceDpi;
            Assert.Equal(tree.PointToScreen(new Point(Dpi.Scale(44, dpi), Dpi.Scale(4, dpi) + (3 * Dpi.Scale(32, dpi)))), menu.Screen);
        });
    }

    /// <summary>A tree taller than its view paints the lists' scroll thumb and scrolls when it is dragged.</summary>
    [Fact]
    public void A_tall_tree_paints_and_drags_a_scroll_thumb()
    {
        Sta.Run(() =>
        {
            using Form host = HostTree(out TreeList tree, out TreeItem[] roots, 60);
            int dpi = tree.DeviceDpi;
            Point thumb = new(tree.Width - Dpi.Scale(6, dpi), Dpi.Scale(12, dpi));
            using (Bitmap bitmap = new(tree.Width, tree.Height))
            {
                tree.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tree.Size));
                Assert.NotEqual(ThemeService.Tokens.Surface.ToArgb(), bitmap.GetPixel(thumb.X, thumb.Y).ToArgb());
            }

            Assert.Same(roots[0], tree.NodeAt(new Point(Dpi.Scale(60, dpi), Dpi.Scale(10, dpi))));
            Send(tree, WmLButtonDown, MkLButton, thumb);
            Send(tree, WmMouseMove, MkLButton, thumb with { Y = thumb.Y + Dpi.Scale(100, dpi) });
            Send(tree, WmLButtonUp, 0, thumb with { Y = thumb.Y + Dpi.Scale(100, dpi) });

            Assert.NotSame(roots[0], tree.NodeAt(new Point(Dpi.Scale(60, dpi), Dpi.Scale(10, dpi))));
        });
    }

    /// <summary>
    /// A deep path in a narrow breadcrumb: leading folders give way to one '…' that goes to the last of them, and the
    /// current folder stays whole (it ran off the control's right edge).
    /// </summary>
    [Fact]
    public void A_long_breadcrumb_hides_its_leading_folders_behind_an_ellipsis()
    {
        string[] path = ["D:", "Media", "Photos", "2024", "Holiday in the mountains", "Day 3"];
        using Bitmap surface = new(260, 32);
        using Graphics g = Graphics.FromImage(surface);
        List<(int Left, int Right)> ranges = [];
        List<(int Left, int Right)> wide = [];

        bool cut = Breadcrumb.Draw(g, new Rectangle(0, 0, 260, 32), path, -1, -1, 96, ranges);
        bool fits = Breadcrumb.Draw(g, new Rectangle(0, 0, 2000, 32), path, -1, -1, 96, wide);

        Assert.True(cut);
        Assert.False(fits);
        Assert.Equal(path.Length, ranges.Count);
        Assert.Equal(path.Length, wide.Count);
        int ellipsis = ranges.FindIndex(r => r.Right > r.Left);
        Assert.True(ellipsis > 0, "No leading folder was hidden.");
        Assert.All(ranges.Take(ellipsis), r => Assert.Equal((0, 0), r));
        Assert.Equal(0, ranges[ellipsis].Left);
        Assert.True(ranges[^1].Right <= 260, "The current folder runs past the edge.");
        Assert.Equal(wide[^1].Right - wide[^1].Left, ranges[^1].Right - ranges[^1].Left);
    }

    /// <summary>A selected folder that is no longer in the tree (a search replaced the roots) stops being selected.</summary>
    [Fact]
    public void Rebuilding_the_tree_drops_a_selection_that_left_it()
    {
        using TreeList tree = new() { Size = new Size(280, 400) };
        TreeItem music = new("Music") { HasChildren = false };
        tree.Roots.Add(music);
        tree.Rebuild();
        tree.SelectedNode = music;
        int changes = 0;
        tree.SelectionChanged += (_, _) => changes++;

        tree.Roots.Clear();
        tree.Roots.Add(new TreeItem("Music") { HasChildren = false });
        tree.Rebuild();

        Assert.Null(tree.SelectedNode);
        Assert.Equal(1, changes);
    }

    /// <summary>With the selection hidden in a collapsed folder, keys go on from that folder (Down jumped to the top).</summary>
    [Fact]
    public void Keys_go_on_from_the_shown_folder_that_hides_the_selection()
    {
        using TreeList tree = new() { Size = new Size(280, 400) };
        TreeItem first = new("First") { HasChildren = false };
        TreeItem music = new("Music");
        TreeItem album = new("Album") { HasChildren = false };
        TreeItem last = new("Last") { HasChildren = false };
        music.SetChildren([album]);
        tree.Roots.Add(first);
        tree.Roots.Add(music);
        tree.Roots.Add(last);
        tree.Rebuild();
        tree.SelectedNode = album;
        tree.Toggle(music);
        Assert.False(music.Expanded);

        Key(tree, Keys.Down);

        Assert.Same(last, tree.SelectedNode);
    }

    /// <summary>A right double-click does not expand or collapse a folder.</summary>
    [Fact]
    public void A_right_double_click_does_not_toggle_a_folder()
    {
        using TreeList tree = new() { Size = new Size(280, 400) };
        TreeItem music = new("Music");
        music.SetChildren([new TreeItem("Album") { HasChildren = false }]);
        tree.Roots.Add(music);
        tree.Rebuild();
        int dpi = tree.DeviceDpi;
        MouseEventArgs right = new(MouseButtons.Right, 2, Dpi.Scale(80, dpi), Dpi.Scale(20, dpi), 0);

        Invoke(tree, "OnMouseDoubleClick", right);

        Assert.False(music.Expanded);
    }

    private static Form Host(out VirtualList list, IListSource source, ListMode mode)
    {
        QuietForm form = new() { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(900, 500), ShowInTaskbar = false };
        list = new VirtualList { Mode = mode, Bounds = new Rectangle(0, 0, 800, 400) };
        form.Controls.Add(list);
        list.Source = source;
        form.Show();
        return form;
    }

    private static Form HostTree(out TreeList tree, out TreeItem[] roots, int count)
    {
        QuietForm form = new() { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(400, 400), ShowInTaskbar = false };
        tree = new TreeList { Bounds = new Rectangle(0, 0, 280, 300) };
        roots = [.. Enumerable.Range(0, count).Select(i => new TreeItem($"Folder {i}") { HasChildren = false })];
        foreach (TreeItem root in roots)
        {
            tree.Roots.Add(root);
        }

        form.Controls.Add(tree);
        tree.Rebuild();
        form.Show();
        return form;
    }

    /// <summary>A point in an item row of a list without groups, <paramref name="x"/> device pixels from the left.</summary>
    private static Point RowPoint(VirtualList list, int index, int x)
    {
        int dpi = list.DeviceDpi;
        int top = list.Mode == ListMode.Table ? Dpi.Scale(32, dpi) : 0;
        int height = Dpi.Scale(list.Mode == ListMode.Table ? 36 : 44, dpi);
        return new Point(x, top + (index * height) - list.ScrollOffset + (height / 2));
    }

    /// <summary>The More button of a two-line row in a list without groups.</summary>
    private static Rectangle MoreButton(VirtualList list, int index)
    {
        int dpi = list.DeviceDpi;
        int top = (index * Dpi.Scale(44, dpi)) - list.ScrollOffset;
        return new Rectangle(list.Width - Dpi.Scale(16 + 28, dpi), top + Dpi.Scale(8, dpi), Dpi.Scale(28, dpi), Dpi.Scale(28, dpi));
    }

    private static Point TreeRow(TreeList tree, int index)
    {
        int dpi = tree.DeviceDpi;
        return new Point(Dpi.Scale(80, dpi), Dpi.Scale(4, dpi) + (index * Dpi.Scale(32, dpi)) + Dpi.Scale(16, dpi));
    }

    /// <summary>The x where a summary row's link starts, as the list paints it.</summary>
    private static int LinkX(VirtualList list, Rows rows)
    {
        ListRow summary = new();
        rows.Fill(0, summary);
        Font body = TypeRamp.For(list.DeviceDpi).Body;
        return Dpi.Scale(16, list.DeviceDpi) + KitPaint.MeasureWidth(summary.Line1, body) + Dpi.Scale(12, list.DeviceDpi);
    }

    /// <summary>The pixel of a row's leading 3 px bar (StrokeStrong while hovered).</summary>
    private static Color BarPixel(VirtualList list, int index)
    {
        using Bitmap bitmap = new(list.Width, list.Height);
        list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, list.Size));
        Point point = RowPoint(list, index, 1);
        return bitmap.GetPixel(Dpi.Scale(2, list.DeviceDpi), point.Y);
    }

    private static Point Centre(Rectangle rectangle) => new(rectangle.X + (rectangle.Width / 2), rectangle.Y + (rectangle.Height / 2));

    private static void Click(Control control, int down, int up, int buttons, Point point)
    {
        Send(control, down, buttons, point);
        Send(control, up, 0, point);
    }

    private static void Send(Control control, int message, int wParam, Point point) => _ = SendMessage(control.Handle, message, wParam, LParam(point));

    private static IntPtr LParam(Point point) => (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));

    private static void Key(Control control, Keys keys) => Invoke(control, "OnKeyDown", new KeyEventArgs(keys));

    private static void Invoke(Control control, string method, EventArgs args)
    {
        MethodInfo handler = control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, [args.GetType()])
            ?? typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, [args.GetType()])!;
        handler.Invoke(control, [args]);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, IntPtr lParam) => SendMessage(hwnd, msg, (IntPtr)wParam, lParam);

    /// <summary>
    /// Rows by id: items are their id (0 and up), group headers negative, <see cref="long.MinValue"/> the summary row.
    /// Optionally answers <see cref="IListSource.IndexOf"/> from its list, and counts the ids read.
    /// </summary>
    private sealed class Rows : IListSource
    {
        public List<long> Ids { get; init; } = [];

        public List<(int Index, bool Collapsed)> Collapses { get; } = [];

        public bool Map { get; set; }

        public int IdOfCalls { get; set; }

        public int Count => Ids.Count;

        public static Rows Items(int count) => new() { Ids = [.. Enumerable.Range(0, count).Select(i => (long)i)] };

        public long IdOf(int index)
        {
            IdOfCalls++;
            return Ids[index];
        }

        public void Fill(int index, ListRow row)
        {
            long id = Ids[index];
            if (id == long.MinValue)
            {
                row.IsGroup = true;
                row.IsSummary = true;
                row.Line1 = "Showing 3 of 9";
                row.Link = "Clear filters";
                return;
            }

            if (id < 0)
            {
                row.IsGroup = true;
                row.Line1 = "High";
                row.Count = "3";
                return;
            }

            row.Line1 = "Night Drive - 07 - Overpass.flac";
            row.Line2 = "Likely made from a lossy source";
            row.Folder = @"D:\Music\Night Drive";
            row.Size = "31.2 MB";
        }

        public void SetCollapsed(int index, bool collapsed) => Collapses.Add((index, collapsed));

        public int GroupsBefore(int index)
        {
            int groups = 0;
            for (int i = 0; i < index && i < Ids.Count; i++)
            {
                groups += Ids[i] < 0 ? 1 : 0;
            }

            return groups;
        }

        public int? IndexOf(long id) => Map ? Ids.IndexOf(id) : null;
    }

    /// <summary>A form that is shown without taking activation from whatever the user is doing.</summary>
    private sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000;
                return parameters;
            }
        }
    }
}
