using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>What pop-ups show: drop-down lists in contrast themes, toasts (where, when and how much text), dialog titles.</summary>
[Collection(UiCollection.Name)]
public sealed class PopupPaintTests
{
    private const int GwlExStyle = -20;
    private const int WsExTopMost = 0x00000008;

    /// <summary>
    /// In Windows contrast themes the current item was WindowText on Highlight (unreadable in several themes) and hover
    /// changed nothing but a bar. Subplan 08: HighlightText on selected items, a 2 px Highlight border for hover.
    /// </summary>
    [Fact]
    public void Contrast_themes_show_the_current_item_in_HighlightText_and_hover_as_a_border()
    {
        ThemeTokens original = ThemeService.Tokens;
        ThemeTokens contrast = ThemeTokens.HighContrast();
        PopupItem[] items = [new("Up to Critical"), new("Up to High"), new("Up to Medium")];
        using Bitmap surface = new(220, 8 + (3 * 32));
        try
        {
            ThemeService.Set(contrast);
            using Graphics g = Graphics.FromImage(surface);
            g.Clear(Color.Magenta);
            PopupList.PaintList(g, new Rectangle(0, 0, surface.Width, surface.Height), items, PopupStyle.DropDown, 1, 2, 96);
        }
        finally
        {
            ThemeService.Set(original);
        }

        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            surface.Save(Path.Combine(folder, "dropdown-contrast.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        // Row 1 (current) spans y 36..68: its text is drawn in OnAccent, so pixels close to OnAccent appear inside it.
        bool onAccentText = Enumerable.Range(40, 24).Any(y => Enumerable.Range(16, 100).Any(x => Near(surface.GetPixel(x, y), contrast.OnAccent)));
        Assert.True(onAccentText, "The current item's text is not HighlightText.");

        // Row 2 (hover) spans y 68..100: its top edge is a Highlight (Accent) border, not the plain Window fill.
        Assert.True(Near(surface.GetPixel(110, 69), contrast.Accent), $"No Highlight border on the hovered item ({surface.GetPixel(110, 69)}).");
    }

    /// <summary>Toasts were topmost: they floated over other programs. Menus and tooltips stay topmost.</summary>
    [Fact]
    public void A_toast_is_not_topmost_but_a_menu_is()
    {
        bool toastTopMost = true;
        bool menuTopMost = false;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out Control anchor);
            using ToastHost host = new(owner, () => 0);
            host.Show(new ToastContent(Severity.None, "Added Music", "1,204 files are waiting to be scanned"));
            Application.DoEvents();
            LayeredPopup toast = Application.OpenForms.OfType<LayeredPopup>().Single(f => f.Owner == owner);
            toastTopMost = (GetWindowLong(toast.Handle, GwlExStyle) & WsExTopMost) != 0;

            PopupList menu = KitMenu.ShowBelow(anchor, [new PopupItem("Details")], _ => { });
            menuTopMost = (GetWindowLong(menu.Handle, GwlExStyle) & WsExTopMost) != 0;
            menu.Dismiss();
            Application.DoEvents();
        });

        Assert.False(toastTopMost, "A toast floats over other programs.");
        Assert.True(menuTopMost);
    }

    /// <summary>
    /// A toast shown while the window was hidden in the notification area appeared where the window used to be (or, while
    /// minimized, off-screen, and expired unseen with its Undo). It now waits for the window.
    /// </summary>
    [Fact]
    public void A_toast_waits_while_its_window_is_hidden()
    {
        bool visibleWhileHidden = true;
        bool visibleAfterShow = false;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out _);
            using ToastHost host = new(owner, () => 0);
            owner.Hide();
            host.Show(new ToastContent(Severity.None, "Index rebuilt for 36 videos", "2 skipped", "Undo", () => { }));
            Pump();
            visibleWhileHidden = Application.OpenForms.OfType<LayeredPopup>().Any(f => f.Visible);
            owner.Show();
            Pump();
            visibleAfterShow = Application.OpenForms.OfType<LayeredPopup>().Any(f => f.Visible && f.Owner == owner);
        });

        Assert.False(visibleWhileHidden, "A toast showed while its window was hidden.");
        Assert.True(visibleAfterShow, "The toast did not show when the window came back.");
    }

    /// <summary>
    /// A failure's reason was cut to a third of a line with no way to read the rest (subplan 09 D: failure toasts have
    /// Show). A failure whose text is cut gets Show; short ones, successes and toasts with a button of their own do not.
    /// </summary>
    [Fact]
    public void A_failure_toast_whose_text_is_cut_gets_Show()
    {
        const string reason = @"The process cannot access the file 'D:\Music\Night Drive\Overpass\Night Drive - 07 - Overpass.flac' because it is being used by another process.";
        ToastContent? cut = null;
        ToastContent? shortFailure = null;
        ToastContent? success = null;
        ToastContent? ownButton = null;
        Sta.Run(() =>
        {
            using Form owner = PopupTestWindows.Owner(out _);
            using ToastHost host = new(owner, () => 0);
            cut = host.Prepare(new ToastContent(Severity.Critical, "Undo failed", reason));
            shortFailure = host.Prepare(new ToastContent(Severity.Critical, "Undo failed", "Access is denied."));
            success = host.Prepare(new ToastContent(Severity.None, "Undone", reason));
            ownButton = host.Prepare(new ToastContent(Severity.High, "Diagnostics", reason, "Open logs", () => { }));
        });

        Assert.Equal(MediaVerifier.UI.Strings.Dialogs.Show, cut?.ActionText);
        Assert.NotNull(cut?.Action);
        Assert.Null(shortFailure?.ActionText);
        Assert.Null(success?.ActionText);
        Assert.Equal("Open logs", ownButton?.ActionText);
    }

    /// <summary>"Ignore '{rule}' in {folder}?" lost the folder to the ellipsis; a long title now takes two lines.</summary>
    [Fact]
    public void A_long_dialog_title_takes_two_lines_and_moves_the_body_down()
    {
        IReadOnlyList<string> longLines = [];
        IReadOnlyList<string> shortLines = [];
        int longHeight = 0;
        int shortHeight = 0;
        int longBodyTop = 0;
        int shortBodyTop = 0;
        Sta.Run(() =>
        {
            using TitleDialog longTitle = new("Ignore \"Damaged video frames (hidden by error concealment)\" in Night Drive?");
            using (Form stage = DialogStage.Show(longTitle))
            {
                longLines = [.. longTitle.TitleLines()];
                longHeight = longTitle.TotalLogicalHeight();
                longBodyTop = longTitle.BodyTop;
                if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
                {
                    MediaVerifier.Diagnostics.ScreenRenderer.Save(longTitle, Path.Combine(folder, "dialog-long-title.png"));
                }

                longTitle.Close();
            }

            using TitleDialog shortTitle = new("Remove Music?");
            using (Form stage = DialogStage.Show(shortTitle))
            {
                shortLines = [.. shortTitle.TitleLines()];
                shortHeight = shortTitle.TotalLogicalHeight();
                shortBodyTop = shortTitle.BodyTop;
                shortTitle.Close();
            }
        });

        Assert.Equal(2, longLines.Count);
        Assert.EndsWith("Night Drive?", longLines[1], StringComparison.Ordinal);
        Assert.Single(shortLines);
        Assert.Equal(shortHeight + 24, longHeight);
        Assert.True(longBodyTop > shortBodyTop, "The body did not move down under the second title line.");
    }

    private static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 40;

    private static void Pump()
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 250)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    private sealed class TitleDialog(string title) : KitDialog(title, DialogWidth.Confirmation)
    {
        /// <summary>The body's top inside the dialog surface, in logical pixels.</summary>
        public int BodyTop => (int)Math.Round((Body.Top - SurfaceTop()) * 96.0 / DeviceDpi);

        private int SurfaceTop() => (ClientSize.Height - S(TotalLogicalHeight())) / 2;
    }
}
