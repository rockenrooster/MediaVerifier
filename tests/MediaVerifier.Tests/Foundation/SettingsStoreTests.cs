using MediaVerifier.Core.Settings;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

public sealed class SettingsStoreTests
{
    [Fact]
    public void RoundTripKeepsEveryValue()
    {
        using TempDirectory temp = new("settings");
        SettingsStore store = new(temp.Combine("settings.json"));
        AppSettings settings = new()
        {
            Theme = ThemeMode.Dark,
            StartWithWindows = true,
            MinimizeToTray = true,
            CheckForUpdatesOnStartup = false,
            DatabasePath = @"D:\Data\library.db",
            WindowPlacement = new WindowPlacement { Left = 10, Top = 20, Width = 1300, Height = 820, Maximized = true, LastPage = "Issues" },
        };

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.True(loaded.StartWithWindows);
        Assert.True(loaded.MinimizeToTray);
        Assert.False(loaded.CheckForUpdatesOnStartup);
        Assert.Equal(@"D:\Data\library.db", loaded.DatabasePath);
        Assert.Equal(10, loaded.WindowPlacement.Left);
        Assert.Equal(20, loaded.WindowPlacement.Top);
        Assert.Equal(1300, loaded.WindowPlacement.Width);
        Assert.Equal(820, loaded.WindowPlacement.Height);
        Assert.True(loaded.WindowPlacement.Maximized);
        Assert.Equal("Issues", loaded.WindowPlacement.LastPage);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(File.Exists(store.TempPath));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        using TempDirectory temp = new("settings");
        AppSettings loaded = new SettingsStore(temp.Combine("settings.json")).Load();

        Assert.Equal(ThemeMode.System, loaded.Theme);
        Assert.False(loaded.StartWithWindows);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.CheckForUpdatesOnStartup);
        Assert.Equal("", loaded.DatabasePath);
        Assert.False(loaded.WindowPlacement.HasBounds);
    }

    [Fact]
    public void UnknownPropertiesAreIgnoredAndMissingOnesDefaulted()
    {
        using TempDirectory temp = new("settings");
        string path = temp.Combine("settings.json");
        File.WriteAllText(path, """{ "theme": "Light", "someFutureSetting": { "a": [1, 2] }, "anotherOne": 5 }""");

        SettingsStore store = new(path);
        AppSettings loaded = store.Load();

        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.True(loaded.CheckForUpdatesOnStartup);
        Assert.Null(store.LastCorruptCopy);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "theme": "Purple" }""")]
    public void CorruptFileIsRenamedAndDefaultsReturned(string content)
    {
        using TempDirectory temp = new("settings");
        string path = temp.Combine("settings.json");
        File.WriteAllText(path, content);

        SettingsStore store = new(path);
        AppSettings loaded = store.Load();

        Assert.Equal(ThemeMode.System, loaded.Theme);
        Assert.False(File.Exists(path));
        Assert.NotNull(store.LastCorruptCopy);
        Assert.Matches(@"settings\.corrupt-\d{8}-\d{6}(-\d+)?\.json$", store.LastCorruptCopy);
        Assert.Equal(content, File.ReadAllText(store.LastCorruptCopy!));
    }

    [Fact]
    public void SaveIsAtomicWhenInterruptedBetweenTempWriteAndReplace()
    {
        using TempDirectory temp = new("settings");
        string path = temp.Combine("settings.json");
        SettingsStore store = new(path);
        store.Save(new AppSettings { Theme = ThemeMode.Light });
        string before = File.ReadAllText(path);

        store.BeforeReplace = () => throw new IOException("simulated kill");
        Assert.Throws<IOException>(() => store.Save(new AppSettings { Theme = ThemeMode.Dark }));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(ThemeMode.Light, new SettingsStore(path).Load().Theme);
    }

    [Fact]
    public void FirstSaveWorksWithoutAnExistingFile()
    {
        using TempDirectory temp = new("settings");
        string path = temp.Combine("sub", "settings.json");
        SettingsStore store = new(path);

        store.Save(new AppSettings { MinimizeToTray = true });

        Assert.True(File.Exists(path));
        Assert.True(store.Load().MinimizeToTray);
    }
}
