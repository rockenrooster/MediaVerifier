using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Logging;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// <c>--render-screens &lt;dir&gt;</c> (subplan 08, Visual QA harness; task UK-13): renders the reference
/// sheets at 100 % and 150 % and every page at the reference window size, in dark, light and high
/// contrast, then writes <c>index.html</c> placing each render beside its reference from docs/design/png.
/// Uses a throwaway data folder, never the user's.
/// </summary>
internal static class ScreenRenderer
{
    private static readonly (string Name, ThemeTokens Tokens)[] Themes =
    [
        ("dark", ThemeTokens.Dark),
        ("light", ThemeTokens.Light),
    ];

    public static int Run(string directory)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string root = Path.Combine(Path.GetTempPath(), "MediaVerifier.Render." + Guid.NewGuid().ToString("N")[..8]);
        ThemeTokens original = ThemeService.Tokens;
        try
        {
            Directory.CreateDirectory(directory);
            ApplicationConfiguration.Initialize();

            // Outside Application.Run each DoEvents call swaps the WinForms synchronization context for a
            // thread-pool one, which would send the pages' await continuations to pool threads.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            // The references were drawn with British formats (03:12, 8 Nov).
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            List<(string Group, string Label, string File, string? Reference)> renders = [];
            foreach ((string theme, ThemeTokens tokens) in Themes.Append(("contrast", ThemeTokens.HighContrast())))
            {
                ThemeService.Set(tokens);
                foreach (int dpi in new[] { 96, 144 })
                {
                    string scale = dpi == 96 ? "100" : "150";
                    renders.Add(("Sheets", $"components {theme} {scale} %", SaveSheet(directory, $"components-{theme}-{scale}.png", ReferenceSheets.ComponentsSize, dpi, ReferenceSheets.Components), dpi == 96 ? $"components-{theme}.png" : null));
                    renders.Add(("Sheets", $"dialogs {theme} {scale} %", SaveSheet(directory, $"dialogs-{theme}-{scale}.png", ReferenceSheets.DialogsSize, dpi, ReferenceSheets.Dialogs), dpi == 96 ? $"dialogs-{theme}.png" : null));
                    renders.Add(("Sheets", $"icons {theme} {scale} %", SaveSheet(directory, $"icons-{theme}-{scale}.png", ReferenceSheets.IconsSize, dpi, ReferenceSheets.Icons), dpi == 96 ? $"icons-{theme}.png" : null));
                }
            }

            using (AppState state = AppState.Load(AppPaths.UnderBase(root)))
            using (UiServices services = DemoUiData.CreateServices(state, root))
            {
                state.Settings.WindowPlacement = new Core.Settings.WindowPlacement();
                foreach ((string theme, ThemeTokens tokens) in Themes.Append(("contrast", ThemeTokens.HighContrast())))
                {
                    ThemeService.Set(tokens);
                    using MainForm form = new(services);
                    PrepareOffscreen(form, MainForm.DefaultClientSize);
                    foreach (PageId page in PageInfo.All)
                    {
                        // The mockups of these pages were drawn during a scan; the others with none running.
                        if (services.Scans is DemoScanControl demoScan)
                        {
                            demoScan.Status = page is PageId.Dashboard or PageId.Issues or PageId.Scans ? DemoScanControl.Running : Core.Engine.EngineStatus.Idle;
                            form.PollScan();
                        }

                        form.Navigate(page);
                        WaitFor(form.CurrentPageLoading);
                        form.Shell.PerformLayout();
                        Application.DoEvents();
                        string name = $"{page.ToString().ToLowerInvariant()}-{theme}.png";
                        Save(form.Shell, Path.Combine(directory, name));
                        renders.Add(("Screens", $"{page} {theme}", name, ReferenceFor(page, theme)));
                        if (form.CurrentPageControl is SettingsPage settingsPage)
                        {
                            WaitFor(settingsPage.ShowDemoRulesAsync());
                            form.Shell.PerformLayout();
                            Application.DoEvents();
                            Save(form.Shell, Path.Combine(directory, name));
                        }

                        if (form.CurrentPageControl is LibraryPage library)
                        {
                            library.ShowDemoBrowse();
                            WaitFor(form.CurrentPageLoading);
                            form.Shell.PerformLayout();
                            Application.DoEvents();
                            string browse = $"library-browse-{theme}.png";
                            Save(form.Shell, Path.Combine(directory, browse));
                            renders.Add(("Screens", $"Library browse {theme}", browse, theme == "dark" ? "library-browse-dark.png" : null));
                        }

                        if (form.CurrentPageControl is IssuesPage issues)
                        {
                            issues.ShowDemoSelection();
                            Application.DoEvents();
                            string selection = $"issues-selection-{theme}.png";
                            Save(form.Shell, Path.Combine(directory, selection));
                            renders.Add(("Screens", $"Issues selection {theme}", selection, theme == "dark" ? "issues-selection-dark.png" : null));
                            WaitFor(issues.ShowDemoDetailAsync());
                            form.Shell.PerformLayout();
                            Application.DoEvents();
                            string detail = $"issues-detail-{theme}.png";
                            Save(form.Shell, Path.Combine(directory, detail));
                            renders.Add(("Screens", $"Issues detail {theme}", detail, theme is "dark" or "light" ? $"issues-detail-{theme}.png" : null));
                            form.SetDrawerContent(null);
                            Application.DoEvents();
                        }
                    }

                    form.Close();
                }

                WriteIndex(directory, renders, watch.Elapsed);
                state.Log.Info($"Rendered {renders.Count} images to {directory} in {watch.Elapsed.TotalSeconds:F1} s");
            }

            return 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Render screens", ex);

            // The harness's own log folder is deleted below; keep the failure beside the renders.
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "render-error.txt"), ex.ToString());
            }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException)
            {
            }

            return 1;
        }
        finally
        {
            ThemeService.Set(original);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Pumps messages until a page's data has loaded (at most 5 s).</summary>
    internal static void WaitFor(Task task)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < 5000)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        Application.DoEvents();
    }

    /// <summary>
    /// Shows the window far off-screen at the given client size so every control has a handle and a
    /// layout; nothing appears on the user's screen.
    /// </summary>
    internal static void PrepareOffscreen(MainForm form, Size logicalClientSize)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.ShowInTaskbar = false;
        form.Location = new Point(-32000, -32000);
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.ClientSize = UI.Kit.Dpi.Scale(logicalClientSize, form.DeviceDpi);
        form.Location = new Point(-32000, -32000);
        form.Shell.PerformLayout();
        Application.DoEvents();
    }

    internal static void Save(Control control, string file)
    {
        // GDI text clears the alpha of the pixels it touches: render opaque so ClearType text stays intact.
        using Bitmap bitmap = new(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            Print(control, g, Point.Empty, new Rectangle(Point.Empty, control.ClientSize));
        }

        bitmap.Save(file, ImageFormat.Png);
    }

    /// <summary>
    /// Prints a control and then its visible children, bottom of the z-order first and each clipped to its
    /// parent. (DrawToBitmap lets Windows print children top first, so overlapping children such as
    /// scrollbar thumbs would vanish under the content they float over.)
    /// </summary>
    private static void Print(Control control, Graphics target, Point origin, Rectangle clip)
    {
        Rectangle area = Rectangle.Intersect(clip, new Rectangle(origin, control.Size));
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        using (Bitmap own = new(control.Width, control.Height, PixelFormat.Format32bppRgb))
        {
            using (Graphics g = Graphics.FromImage(own))
            {
                nint hdc = g.GetHdc();
                try
                {
                    SendMessageW(control.Handle, 0x0317, hdc, 0x04 | 0x08 | 0x02);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            target.SetClip(area);
            target.DrawImage(own, new Rectangle(origin, own.Size), new Rectangle(Point.Empty, own.Size), GraphicsUnit.Pixel);
            target.ResetClip();
        }

        for (int i = control.Controls.Count - 1; i >= 0; i--)
        {
            Control child = control.Controls[i];
            if (child.Visible && child.Width > 0 && child.Height > 0)
            {
                Print(child, target, new Point(origin.X + child.Left, origin.Y + child.Top), area);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);

    /// <summary>Renders one sheet at a DPI into a PNG and returns its file name.</summary>
    internal static string SaveSheet(string directory, string name, Size logical, int dpi, Action<Graphics, int> paint)
    {
        Size size = UI.Kit.Dpi.Scale(logical, dpi);
        using Bitmap bitmap = new(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            paint(g, dpi);
        }

        bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
        return name;
    }

    private static string? ReferenceFor(PageId page, string theme) => (page, theme) switch
    {
        (PageId.Dashboard, "dark" or "light") => $"dashboard-{theme}.png",
        (PageId.Issues, "dark") => "issues-dark.png",
        (PageId.Library, "dark") => "library-dark.png",
        (PageId.Scans, "dark") => "scans-dark.png",
        (PageId.Schedule, "dark") => "schedule-dark.png",
        (PageId.Tools, "dark") => "tools-dark.png",
        (PageId.Settings, "dark") => "settings-rules-dark.png",
        _ => null,
    };

    /// <summary>The docs/design/png folder of the repository the app runs from, when there is one.</summary>
    private static string? FindReferences()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "design", "png");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void WriteIndex(string directory, List<(string Group, string Label, string File, string? Reference)> renders, TimeSpan elapsed)
    {
        string? references = FindReferences();
        StringBuilder html = new();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>MediaVerifier renders</title><style>")
            .Append("body{font-family:Segoe UI,sans-serif;background:#1F1F1F;color:#F5F5F5;margin:24px}")
            .Append("h2{font-size:16px;margin:32px 0 8px}figure{margin:0 0 24px}figcaption{font-size:12px;color:#B0B0B0;margin:4px 0}")
            .Append(".pair{display:flex;gap:16px;align-items:flex-start;overflow-x:auto}.pair img{border:1px solid #454545;max-width:none}")
            .Append("</style></head><body>")
            .Append("<h1 style=\"font-size:20px\">Renders beside references</h1><p style=\"color:#CECECE;font-size:13px\">")
            .Append(WebUtility.HtmlEncode($"{renders.Count} images in {elapsed.TotalSeconds:F1} s. Left: this build. Right: docs/design/png reference (100 % only)."))
            .Append("</p>");
        string? group = null;
        foreach ((string g, string label, string file, string? reference) in renders)
        {
            if (g != group)
            {
                group = g;
                html.Append("<h2>").Append(WebUtility.HtmlEncode(g)).Append("</h2>");
            }

            html.Append("<figure><figcaption>").Append(WebUtility.HtmlEncode(label)).Append("</figcaption><div class=\"pair\">")
                .Append("<img src=\"").Append(WebUtility.HtmlEncode(file)).Append("\">");
            if (reference is not null && references is not null && File.Exists(Path.Combine(references, reference)))
            {
                html.Append("<img src=\"").Append(WebUtility.HtmlEncode(new Uri(Path.Combine(references, reference)).AbsoluteUri)).Append("\">");
            }

            html.Append("</div></figure>");
        }

        html.Append("</body></html>");
        File.WriteAllText(Path.Combine(directory, "index.html"), html.ToString(), new UTF8Encoding(false));
    }
}

