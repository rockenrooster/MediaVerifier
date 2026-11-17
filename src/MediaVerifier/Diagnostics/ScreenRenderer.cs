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
            {
                state.Settings.WindowPlacement = new Core.Settings.WindowPlacement();
                foreach ((string theme, ThemeTokens tokens) in Themes.Append(("contrast", ThemeTokens.HighContrast())))
                {
                    ThemeService.Set(tokens);
                    using MainForm form = new(state);
                    PrepareOffscreen(form, MainForm.DefaultClientSize);
                    foreach (PageId page in PageInfo.All)
                    {
                        form.Navigate(page);
                        form.Shell.PerformLayout();
                        Application.DoEvents();
                        string name = $"{page.ToString().ToLowerInvariant()}-{theme}.png";
                        Save(form.Shell, Path.Combine(directory, name));
                        renders.Add(("Screens", $"{page} {theme}", name, ReferenceFor(page, theme)));
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
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.ClientSize));
        bitmap.Save(file, ImageFormat.Png);
    }

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
