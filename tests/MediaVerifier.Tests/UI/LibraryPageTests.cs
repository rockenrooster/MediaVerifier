using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using MediaVerifier.App;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Library → Browse in the real window (off-screen, never activated) on a library database: the folder a card opens,
/// the tree keeping what the user opened, a new folder starting at its top, the drawer following Up and Down, the
/// selection's files and the empty-table message.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LibraryPageTests
{
    private const int WmKeyDown = 0x0100;
    private const int VkDown = 0x28;

    [Fact]
    public void Each_folder_card_opens_its_own_folder_and_a_right_click_does_not()
    {
        string[] crumbs = [];
        string[] names = [];
        bool browsingAfterRightClick = true;
        Run(
            async (db, folder) =>
            {
                long music = await AddRoot(db, folder, "Music");
                long photos = await AddRoot(db, folder, "Photos");
                await db.Files.UpsertAsync([new(music, "", "song.flac", "flac", MediaKind.Audio, 100, 1, null, 1), new(photos, "", "beach.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1)]);
            },
            (form, page) =>
            {
                List<Control> cards = [.. Descendants(page).Where(c => c.GetType().Name == "FolderCardControl")];
                Assert.Equal(2, cards.Count);

                // A right click on a card is not a click (it opens the card's menu instead).
                Click(cards[0], MouseButtons.Right);
                Pump(TimeSpan.FromMilliseconds(100));
                browsingAfterRightClick = Field<bool>(page, "_browsing");

                Click(cards[1], MouseButtons.Left);
                Settle(() => page.Loading.IsCompleted && page.Rows.Count > 0);
                crumbs = [.. Find<Breadcrumb>(page).Segments];
                names = [.. page.Rows.Values.Select(r => r.Name)];
            });

        Assert.False(browsingAfterRightClick, "A right click on a folder card opened Browse.");
        Assert.Equal(["Photos"], crumbs);
        Assert.Equal(["beach.jpg"], names);
    }

    [Fact]
    public void The_tree_keeps_open_folders_open_across_reloads()
    {
        bool openAfterChip = false;
        bool openAfterClick = false;
        bool childShown = false;
        Run(
            async (db, folder) =>
            {
                long music = await AddRoot(db, folder, "Music");
                await db.Files.UpsertAsync(
                [
                    new(music, @"Night Drive\Overpass", "1.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
                    new(music, "Night Drive", "2.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
                    new(music, "Glasshouse", "3.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
                ]);
            },
            (form, page) =>
            {
                OpenBrowse(page);
                TreeList tree = Find<TreeList>(page);
                tree.Toggle(Node(tree, "Night Drive"));
                Assert.Contains(tree.VisibleNodes, n => n.Text == "Overpass");

                // A chip reloads the folder: Night Drive stays open.
                Chip ok = Find<Chip>(page, c => c.Text == Strings.Library.ChipOk);
                typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ok, [EventArgs.Empty]);
                Settle(() => page.Loading.IsCompleted);
                openAfterChip = Node(tree, "Night Drive").Expanded;

                // Clicking the open folder itself shows it, and it stays open.
                tree.SelectedNode = Node(tree, "Night Drive");
                Settle(() => page.Loading.IsCompleted);
                Pump(TimeSpan.FromMilliseconds(50));
                openAfterClick = Node(tree, "Night Drive").Expanded;
                childShown = tree.VisibleNodes.Any(n => n.Text == "Overpass");
            });

        Assert.True(openAfterChip, "A chip collapsed the folder the user had opened.");
        Assert.True(openAfterClick, "Clicking an open folder collapsed it.");
        Assert.True(childShown);
    }

    [Fact]
    public void Another_folder_starts_at_its_top_with_nothing_selected()
    {
        int scroll = -1;
        int focus = -1;
        int selected = -1;
        Run(
            async (db, folder) =>
            {
                long music = await AddRoot(db, folder, "Music");
                await db.Files.UpsertAsync([.. Enumerable.Range(0, 120).Select(i => new FileUpsert(music, "Big", $"{i:000}.flac", "flac", MediaKind.Audio, i, 1, null, 1))]);
                await db.Files.UpsertAsync([.. Enumerable.Range(0, 5).Select(i => new FileUpsert(music, "Small", $"s{i}.flac", "flac", MediaKind.Audio, 1, 1, null, 1))]);
            },
            (form, page) =>
            {
                OpenBrowse(page);
                TreeList tree = Find<TreeList>(page);
                VirtualList table = Find<VirtualList>(page);
                tree.SelectedNode = Node(tree, "Big");
                Settle(() => page.Loading.IsCompleted && table.Count == 120);
                table.FocusRow(100);
                table.SelectAll();
                Assert.True(table.ScrollOffset > 0);

                tree.SelectedNode = Node(tree, "Small");
                Settle(() => page.Loading.IsCompleted && table.Count == 5);
                scroll = table.ScrollOffset;
                focus = table.FocusedIndex;
                selected = table.SelectedIds.Count;
            });

        Assert.Equal(0, scroll);
        Assert.Equal(0, focus);
        Assert.Equal(0, selected);
    }

    [Fact]
    public void A_card_keeps_the_users_sort_so_the_header_and_the_order_agree()
    {
        long rootId = 0;
        string first = string.Empty;
        int column = -1;
        Run(
            async (db, folder) =>
            {
                rootId = await AddRoot(db, folder, "Music");
                await db.Files.UpsertAsync([.. Enumerable.Range(0, 5).Select(i => new FileUpsert(rootId, "Small", $"s{i}.flac", "flac", MediaKind.Audio, 1000 + i, 1, null, 1))]);
            },
            (form, page) =>
            {
                OpenBrowse(page);
                TreeList tree = Find<TreeList>(page);
                VirtualList table = Find<VirtualList>(page);

                // A click on the Size header: the list's arrow, then the page's query.
                table.SetSort(3, true);
                Invoke(page, "SetSort", 3, true);
                Settle(() => page.Loading.IsCompleted);

                page.HandleRequest(new LibraryBrowseRequest(rootId, null));
                Settle(() => page.Loading.IsCompleted);
                tree.SelectedNode = Node(tree, "Small");
                Settle(() => page.Loading.IsCompleted && table.Count == 5);
                column = table.SortColumn;
                first = page.Rows[Field<MediaVerifier.UI.Data.BrowseData>(page, "_browse").Ids[0]].Name;
            });

        Assert.Equal(3, column);
        Assert.Equal("s4.flac", first);
    }

    [Fact]
    public void Down_with_the_drawer_open_shows_the_next_file()
    {
        long shownFirst = 0;
        long shownNext = 0;
        long[] ids = [];
        Run(
            async (db, folder) =>
            {
                long music = await AddRoot(db, folder, "Music");
                await db.Files.UpsertAsync([.. Enumerable.Range(0, 3).Select(i => new FileUpsert(music, "Album", $"{i}.flac", "flac", MediaKind.Audio, 100, 1, null, 1))]);
            },
            (form, page) =>
            {
                OpenBrowse(page);
                TreeList tree = Find<TreeList>(page);
                VirtualList table = Find<VirtualList>(page);
                tree.SelectedNode = Node(tree, "Album");
                Settle(() => page.Loading.IsCompleted && table.Count == 3);
                ids = Field<MediaVerifier.UI.Data.BrowseData>(page, "_browse").Ids;
                FileDetailView detail = Field<FileDetailView>(page, "_detail");

                Invoke(page, "OpenDetails", 0);
                Settle(() => detail.Detail is not null);
                shownFirst = detail.Detail?.FileId ?? 0;

                _ = SendMessage(table.Handle, WmKeyDown, VkDown, IntPtr.Zero);
                Settle(() => detail.Detail?.FileId == ids[1]);
                shownNext = detail.Detail?.FileId ?? 0;
                form.SetDrawerContent(null);
            });

        Assert.Equal(ids[0], shownFirst);
        Assert.Equal(ids[1], shownNext);
    }

    [Fact]
    public void Select_all_acts_on_every_file_of_the_folder_even_those_not_painted()
    {
        List<string> paths = [];
        string root = string.Empty;
        Run(
            async (db, folder) =>
            {
                long music = await AddRoot(db, folder, "Music");
                root = Path.Combine(folder, "Music");
                await db.Files.UpsertAsync([.. Enumerable.Range(0, 300).Select(i => new FileUpsert(music, "Many", $"{i:000}.flac", "flac", MediaKind.Audio, 1, 1, null, 1))]);
            },
            (form, page) =>
            {
                OpenBrowse(page);
                TreeList tree = Find<TreeList>(page);
                VirtualList table = Find<VirtualList>(page);
                tree.SelectedNode = Node(tree, "Many");
                Settle(() => page.Loading.IsCompleted && table.Count == 300);

                // Nothing selected: the focused file.
                long[] ids = Field<MediaVerifier.UI.Data.BrowseData>(page, "_browse").Ids;
                Assert.Equal([ids[0]], page.SelectionOrFocused());
                table.SelectAll();
                long[] chosen = page.SelectionOrFocused();
                Assert.Equal(300, chosen.Length);

                // Ctrl+click takes row 5 out: a row menu there acts on that row alone, one inside on the whole selection.
                table.ApplyClickSelection(5, Keys.Control);
                Assert.Equal([ids[5]], page.TargetsOf(5));
                Assert.Equal(299, page.TargetsOf(6).Length);

                Task<List<string>> copy = page.PathsOfAsync(chosen);
                Settle(() => copy.IsCompleted);
                paths = copy.Result;
            });

        Assert.Equal(300, paths.Count);
        Assert.Equal(Path.Combine(root, "Many", "000.flac"), paths[0]);
        Assert.Equal(Path.Combine(root, "Many", "299.flac"), paths[^1]);
    }

    [Fact]
    public void A_folder_without_files_of_its_own_says_so_and_opens_to_its_subfolders()
    {
        bool emptyShown = false;
        string headline = string.Empty;
        bool rootOpen = false;
        Run(
            async (db, folder) =>
            {
                long music = await AddRoot(db, folder, "Music");
                await db.Files.UpsertAsync([new(music, "Album", "1.flac", "flac", MediaKind.Audio, 100, 1, null, 1)]);
            },
            (form, page) =>
            {
                OpenBrowse(page);
                EmptyState empty = Find<EmptyState>(page);
                emptyShown = empty.Visible;
                headline = empty.Headline;
                rootOpen = Node(Find<TreeList>(page), "Music").Expanded;
            });

        Assert.True(emptyShown, "An empty folder showed a blank table.");
        Assert.Equal(Strings.Library.NoFilesHere, headline);
        Assert.True(rootOpen, "The watched folder opened by a card stayed collapsed.");
    }

    /// <summary>A library database seeded off the UI thread, the window off-screen on Library → Folders.</summary>
    private static void Run(Func<Database, string, Task> seed, Action<MainForm, LibraryPage> body)
    {
        Sta.Run(() =>
        {
            using TempDirectory temp = new("library-page");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, null, startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));
            services.ShowToast = _ => { };
            Task.Run(() => seed(services.Database, temp.Path)).GetAwaiter().GetResult();
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            form.Navigate(PageId.Library);
            LibraryPage page = (LibraryPage)form.CurrentPageControl!;
            Settle(() => page.Loading.IsCompleted);
            Pump(TimeSpan.FromMilliseconds(50));
            body(form, page);
            form.Close();
        });
    }

    private static async Task<long> AddRoot(Database db, string folder, string name)
    {
        string path = Directory.CreateDirectory(Path.Combine(folder, name)).FullName;
        return await db.Roots.AddAsync(new RootRecord { Path = path, Label = name, Role = RootRole.Library, MediaMask = MediaKindMask.All });
    }

    /// <summary>Browse as the first card opens it (its watched folder's top folder).</summary>
    private static void OpenBrowse(LibraryPage page)
    {
        Click(Descendants(page).First(c => c.GetType().Name == "FolderCardControl"), MouseButtons.Left);
        Settle(() => page.Loading.IsCompleted && Field<bool>(page, "_browsing") && Find<TreeList>(page).SelectedNode is not null);
        Pump(TimeSpan.FromMilliseconds(50));
    }

    /// <summary>The click a mouse button raises on a control (WinForms raises Click for any button).</summary>
    private static void Click(Control control, MouseButtons button) =>
        typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [new MouseEventArgs(button, 1, 10, 10, 0)]);

    private static TreeItem Node(TreeList tree, string text) => tree.VisibleNodes.First(n => n.Text == text);

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static void Invoke(object owner, string name, params object[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);

    private static T Find<T>(Control root, Func<T, bool>? match = null)
        where T : Control =>
        Descendants(root).OfType<T>().FirstOrDefault(match ?? (_ => true)) ?? throw new InvalidOperationException($"No {typeof(T).Name} in {root.GetType().Name}.");

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void Settle(Func<bool> done)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Pump(TimeSpan time)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < time)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
