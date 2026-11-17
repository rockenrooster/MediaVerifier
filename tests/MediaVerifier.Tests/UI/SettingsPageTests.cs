using System.Security.Cryptography;
using MediaVerifier.App;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.Tests.Updates;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>Settings → Updates, Advanced and About (subplan 09, Settings; tasks U-08, UI-10).</summary>
[Collection(UiCollection.Name)]
public sealed class SettingsPageTests
{
    [Fact]
    public async Task The_update_buttons_follow_the_check()
    {
        using TempDirectory temp = new("updatecard");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        using UiServices demo = Diagnostics.DemoUiData.CreateServices(state, temp.Path);
        using RSA key = RSA.Create(2048);
        using UpdateService updates = UpdateCheckTests.Service(UpdateCheckTests.Release(key, _ => { }), key, new Version(0, 1, 0, 1));
        using UiServices services = new()
        {
            State = state,
            Database = demo.Database,
            Clock = demo.Clock,
            Scans = new Diagnostics.DemoScanControl { Status = Core.Engine.EngineStatus.Idle },
            MediaEngine = demo.MediaEngine,
            Data = demo.Data,
            Updates = updates,
            IsDemo = true,
        };
        using SettingsPage page = new(services) { Size = new Size(1000, 640) };
        page.ShowSection(SettingsPage.UpdatesSection);
        page.PerformLayout();
        KitButton check = Descendants<KitButton>(page).Single(b => b.Text == MediaVerifier.UI.Strings.Settings.CheckNow);
        KitButton install = Descendants<KitButton>(page).Single(b => b.Text == MediaVerifier.UI.Strings.Settings.DownloadInstall);
        page.RefreshUpdateUi();
        Assert.True(check.Enabled);
        Assert.False(install.Enabled);

        _ = await updates.CheckAsync();
        page.RefreshUpdateUi();
        Assert.True(install.Enabled);
        Assert.True(services.UpdateAvailable);
        Save(page, "settings-updates.png");

        page.ShowSection(6);
        Save(page, "settings-advanced.png");
        Assert.Contains(Descendants<KitButton>(page), b => b.Text == MediaVerifier.UI.Strings.Settings.RunSelfCheck);
        page.ShowSection(7);
        Save(page, "settings-about.png");
        Assert.Equal(3, Descendants<KitLink>(page).Count(l => l.Parent?.Visible == true && l.Text is "Website" or "Release notes" or "Licences"));
        page.ShowSection(1);
        Save(page, "settings-scanning.png");
        Assert.Equal(2, Descendants<KitButton>(page).Count(b => b.Text == MediaVerifier.UI.Strings.Settings.EditEllipsis));
    }

    [Fact]
    public void List_edits_split_on_commas_and_drop_repeats()
    {
        Assert.Equal(["jpg", "png", "heic"], ListEditDialog.Split(" jpg, png;heic , PNG ,, "));
        using ListEditDialog dialog = ListEditDialog.Create(MediaVerifier.UI.Strings.Settings.FileTypes, MediaVerifier.UI.Strings.Settings.FileTypesHelper,
        [
            ("Photos", ["jpg", "png"], ["jpg"]),
            ("Videos", ["mp4"], ["mp4"]),
            ("Music", ["flac"], ["flac"]),
        ]);
        using Form owner = DialogStage.Show(dialog);
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            Diagnostics.ScreenRenderer.Save(dialog, Path.Combine(folder, "file-types.png"));
        }

        Assert.True(dialog.TotalLogicalHeight() >= 360, $"{dialog.TotalLogicalHeight()}");
        dialog.Close();
    }

    private static void Save(Control page, string name)
    {
        page.PerformLayout();
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
        {
            using Bitmap bitmap = new(page.Width, page.Height);
            page.DrawToBitmap(bitmap, new Rectangle(Point.Empty, page.Size));
            bitmap.Save(Path.Combine(folder, name));
        }
    }

    private static IEnumerable<T> Descendants<T>(Control root)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (T inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }
}
