using System.Drawing.Imaging;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Logging;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// <c>--render-screens &lt;dir&gt;</c>: renders every page to PNG in dark and light at the reference size
/// (subplan 08, Visual QA harness), then exits. Uses a throwaway data folder, never the user's.
/// </summary>
internal static class ScreenRenderer
{
    public static int Run(string directory)
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaVerifier.Render." + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(directory);
            ApplicationConfiguration.Initialize();
            using AppState state = AppState.Load(AppPaths.UnderBase(root));
            state.Settings.WindowPlacement = new Core.Settings.WindowPlacement();
            int count = 0;
            foreach (ThemeTokens tokens in new[] { ThemeTokens.Dark, ThemeTokens.Light })
            {
                ThemeService.Set(tokens);
                using MainForm form = new(state);
                PrepareOffscreen(form, MainForm.DefaultClientSize);
                foreach (PageId page in PageInfo.All)
                {
                    form.Navigate(page);
                    form.Shell.PerformLayout();
                    Application.DoEvents();
                    string file = Path.Combine(directory, $"{page.ToString().ToLowerInvariant()}-{tokens.Name.ToLowerInvariant()}.png");
                    Save(form.Shell, file);
                    count++;
                }

                form.Close();
            }

            state.Log.Info($"Rendered {count} screens to {directory}");
            return 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Render screens", ex);
            return 1;
        }
        finally
        {
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
        using Bitmap bitmap = new(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format32bppArgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.ClientSize));
        bitmap.Save(file, ImageFormat.Png);
    }
}
