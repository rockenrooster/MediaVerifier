using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Settings;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

/// <summary>
/// Settings are saved off the UI thread: the caller never waits for the flush, a burst of changes ends in one more
/// write with the newest values, the exit waits for what is queued, and a file that could not be opened at start is
/// never replaced with defaults.
/// </summary>
public sealed class SettingsSaveTests
{
    [Fact]
    public async Task Saving_returns_before_the_file_is_written()
    {
        using TempDirectory temp = new("save-async");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);
        using ManualResetEventSlim release = new(false);
        state.SettingsStore.BeforeReplace = () => release.Wait(TimeSpan.FromSeconds(10));
        state.Settings.Theme = ThemeMode.Dark;

        Task<bool> saved = state.SaveSettings();

        Assert.False(saved.IsCompleted, "The save waited for the disk on the calling thread.");
        release.Set();
        Assert.True(await saved);
        Assert.Equal(ThemeMode.Dark, new SettingsStore(paths.SettingsFile).Load().Theme);
    }

    [Fact]
    public async Task A_burst_of_changes_writes_the_newest_values_once_more()
    {
        using TempDirectory temp = new("save-burst");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);
        using ManualResetEventSlim writing = new(false);
        using ManualResetEventSlim release = new(false);
        int writes = 0;
        state.SettingsStore.BeforeReplace = () =>
        {
            Interlocked.Increment(ref writes);
            writing.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        // Typing 3650 into a number box: one save per key while the first write is still running.
        state.Settings.Scanning.ScrubPeriodDays = 3;
        List<Task<bool>> saves = [state.SaveSettings()];
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)), "The first write did not start.");
        foreach (int days in new[] { 36, 365, 3650 })
        {
            state.Settings.Scanning.ScrubPeriodDays = days;
            saves.Add(state.SaveSettings());
        }

        release.Set();
        bool[] results = await Task.WhenAll(saves);

        Assert.All(results, r => Assert.True(r));
        Assert.Equal(2, writes);
        Assert.Equal(3650, new SettingsStore(paths.SettingsFile).Load().Scanning.ScrubPeriodDays);
    }

    [Fact]
    public void Closing_writes_what_is_still_queued()
    {
        using TempDirectory temp = new("save-exit");
        AppPaths paths = temp.CreateAppPaths();
        AppState state = AppState.Load(paths, installSharedLog: false);
        state.SettingsStore.BeforeReplace = () => Thread.Sleep(200);
        state.Settings.WindowPlacement.LastPage = "Schedule";
        _ = state.SaveSettings();

        state.Dispose();

        Assert.Equal("Schedule", new SettingsStore(paths.SettingsFile).Load().WindowPlacement.LastPage);
    }

    [Fact]
    public async Task A_save_that_cannot_write_reports_false()
    {
        using TempDirectory temp = new("save-fails");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);

        // The temporary file's name is taken by a folder, so the write fails.
        Directory.CreateDirectory(state.SettingsStore.TempPath);

        Assert.False(await state.SaveSettings());
        Directory.Delete(state.SettingsStore.TempPath);
        Assert.True(await state.SaveSettings());
    }

    /// <summary>
    /// Another program holds settings.json at start (a sync client, a scheduled run's save): the app starts with defaults
    /// instead of failing to open, and never saves those defaults over the user's file.
    /// </summary>
    [Fact]
    public void A_settings_file_that_cannot_be_opened_gives_defaults_and_is_kept()
    {
        using TempDirectory temp = new("settings-locked");
        string path = temp.Combine("settings.json");
        SettingsStore writer = new(path);
        writer.Save(new AppSettings { Theme = ThemeMode.Dark });
        string before = File.ReadAllText(path);

        SettingsStore store = new(path);
        AppSettings loaded;
        using (FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            loaded = store.Load();
        }

        Assert.Equal(ThemeMode.System, loaded.Theme);
        Assert.True(store.SavingDisabled);
        Assert.Null(store.LastCorruptCopy);
        Assert.False(store.TrySave(loaded));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(ThemeMode.Dark, new SettingsStore(path).Load().Theme);
    }

    /// <summary>"Keep originals after a repair in" is gone (it was never read): files that still hold it load, and drop it.</summary>
    [Fact]
    public void The_removed_keep_originals_values_load_and_are_dropped()
    {
        using TempDirectory temp = new("settings-old");
        string path = temp.Combine("settings.json");
        File.WriteAllText(path, """{ "theme": "Dark", "scanning": { "keepOriginalsIn": "RecycleBin", "originalsFolder": "D:\\Old", "emptyQuarantineAfterDays": 12 } }""");

        SettingsStore store = new(path);
        AppSettings loaded = store.Load();
        store.Save(loaded);

        Assert.Null(store.LastCorruptCopy);
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal(12, loaded.Scanning.EmptyQuarantineAfterDays);
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("keepOriginalsIn", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("originalsFolder", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_file_held_for_a_moment_is_read_on_a_later_try()
    {
        using TempDirectory temp = new("settings-busy");
        string path = temp.Combine("settings.json");
        new SettingsStore(path).Save(new AppSettings { Theme = ThemeMode.Light });
        using FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using System.Threading.Timer unlock = new(_ => held.Dispose(), null, 50, Timeout.Infinite);

        SettingsStore store = new(path);
        AppSettings loaded = store.Load();

        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.False(store.SavingDisabled);
    }
}
