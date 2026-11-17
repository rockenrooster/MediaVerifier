using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Pop-ups (menus, drop-down lists, tooltips, toasts) paint their text through a real GDI surface, so ClearType
/// text keeps its colour; and they stay per-pixel layered windows while they fade, never a blank box.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PopupRenderTests
{
    /// <summary>On a GDI+ bitmap's Graphics, ClearType edges blended against an empty copy and grey text came out near-black.</summary>
    [Fact]
    public void Popup_text_keeps_its_colour()
    {
        ThemeTokens original = ThemeService.Tokens;
        try
        {
            ThemeService.Set(ThemeTokens.Light);
            using TextPopup popup = new();
            using Bitmap surface = popup.PaintOpaque(new Size(240, 40), 96);
            double darkest = 255;
            for (int y = 0; y < surface.Height; y++)
            {
                for (int x = 0; x < surface.Width; x++)
                {
                    darkest = Math.Min(darkest, Luma(surface.GetPixel(x, y)));
                }
            }

            double floor = Luma(ThemeTokens.Light.Text3) - 40;
            Assert.True(darkest < 200, "The text was not drawn.");
            Assert.True(darkest >= floor, $"The text came out darker ({darkest:0}) than its colour allows ({floor:0}).");
        }
        finally
        {
            ThemeService.Set(original);
        }
    }

    /// <summary>The Issues row menu through the same path (saved with MEDIAVERIFIER_SAVE_RENDERS for a look).</summary>
    [Fact]
    public void The_issues_row_menu_paints_its_items()
    {
        ThemeTokens original = ThemeService.Tokens;
        try
        {
            ThemeService.Set(ThemeTokens.Dark);
            PopupItem[] items =
            [
                new(MediaVerifier.UI.Strings.Menu.Details, IconName.Info, MediaVerifier.UI.Strings.Menu.ShortcutEnter),
                new(MediaVerifier.UI.Strings.Menu.ScanAgain, IconName.Refresh, MediaVerifier.UI.Strings.Menu.ShortcutShiftF5),
                PopupItem.Separator,
                new(MediaVerifier.UI.Strings.Menu.OpenFile, IconName.Open),
                new(MediaVerifier.UI.Strings.Menu.ShowInFolder, IconName.Folder, MediaVerifier.UI.Strings.Menu.ShortcutAltEnter),
            ];
            using PopupList menu = new(items, PopupStyle.Menu, PopupList.MenuWidth);
            using Bitmap surface = menu.PaintOpaque(new Size(PopupList.MenuWidth, 160), 96);
            if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
            {
                surface.Save(Path.Combine(folder, "menu-issues-row.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            double brightest = 0;
            for (int y = 0; y < surface.Height; y++)
            {
                for (int x = 0; x < surface.Width; x++)
                {
                    brightest = Math.Max(brightest, Luma(surface.GetPixel(x, y)));
                }
            }

            Assert.True(brightest > 150, "The menu's text was not drawn.");
        }
        finally
        {
            ThemeService.Set(original);
        }
    }

    /// <summary>
    /// Form.Opacity switches a window to SetLayeredWindowAttributes, after which the toast showed as a white box; and
    /// the toast took its window's own size for its content, so it grew each time it was painted.
    /// </summary>
    [Fact]
    public void A_toast_stays_a_per_pixel_layered_window_while_it_fades()
    {
        bool attributes = true;
        byte opacity = 0;
        Size content = Size.Empty;
        Size first = Size.Empty;
        Size later = Size.Empty;
        Sta.Run(() =>
        {
            using Form owner = new() { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(900, 700), ShowInTaskbar = false };
            owner.Show();
            using ToastHost host = new(owner, () => 0);
            host.Show(new ToastContent(Severity.None, "Added Music", "1,204 files are waiting to be scanned", "Scan now"));
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMilliseconds(400))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }

            LayeredPopup toast = Application.OpenForms.OfType<LayeredPopup>().Single(f => f.Owner == owner);
            opacity = toast.PopupOpacity;
            attributes = GetLayeredWindowAttributes(toast.Handle, out _, out _, out _);
            content = toast.ContentScreenBounds.Size;
            first = toast.Size;

            // Moving the window paints the toast again at its new place: it keeps its size.
            owner.Location = new Point(-31900, -31900);
            owner.Location = new Point(-32000, -32000);
            Application.DoEvents();
            later = toast.Size;
        });

        Assert.Equal(255, opacity);
        Assert.False(attributes, "The toast was switched to SetLayeredWindowAttributes (Form.Opacity), which hides its image.");
        Assert.Equal(new Size(ToastHost.Width, ToastHost.Height), content);
        Assert.Equal(first, later);
    }

    private static double Luma(Color c) => (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    private sealed class TextPopup : LayeredPopup
    {
        protected override Size ContentSize => new(240, 40);

        protected override void PaintContent(Graphics g, Rectangle bounds, int dpi) =>
            KitPaint.DrawText(g, "Show in folder   Alt+Enter", TypeRamp.For(dpi).Body, ThemeService.Tokens.Text3, 8, 20, bounds.Width - 16);
    }
}
