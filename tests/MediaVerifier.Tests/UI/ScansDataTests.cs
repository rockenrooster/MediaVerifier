using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>Scans history wording and estimates (task UI-07).</summary>
public sealed class ScansDataTests
{
    private static readonly RootRecord[] Roots =
    [
        new() { Id = 1, Path = @"D:\Music", Label = "Music" },
        new() { Id = 2, Path = @"D:\Photos", Label = "Photos" },
        new() { Id = 3, Path = @"E:\Backup", Label = "Backup", Role = RootRole.Backup },
    ];

    /// <summary>
    /// The Folders column names the scope; a subfolder scan is named by its folder (it read "All folders" or the whole
    /// root's name, because both branches of the partial-scope test were the same).
    /// </summary>
    [Theory]
    [InlineData("""[{"rootId":1,"dir":null,"files":null},{"rootId":2,"dir":null,"files":null}]""", "All folders")]
    [InlineData("""[{"rootId":2,"dir":null,"files":null}]""", "Photos")]
    [InlineData("""[{"rootId":1,"dir":"Album","files":null}]""", "Album")]
    [InlineData("""[{"rootId":1,"dir":"Night Drive\\Overpass","files":null},{"rootId":2,"dir":null,"files":null}]""", "Overpass and Photos")]
    [InlineData("""[{"rootId":1,"dir":null,"files":3},{"rootId":2,"dir":null,"files":2}]""", "5 files")]
    [InlineData("""[{"rootId":1,"dir":null,"files":1}]""", "1 file")]
    [InlineData("not json", "")]
    public void The_folders_column_names_the_scope(string scopeJson, string expected)
    {
        Assert.Equal(expected, ScansPage.AtLineStart(LiveUiData.ScopeText(scopeJson, Roots)));
    }

    /// <summary>In a sentence the scope stays lower case: the drawer read "Scan of All folders".</summary>
    [Fact]
    public void In_a_sentence_all_folders_stays_lower_case()
    {
        string scope = LiveUiData.ScopeText("""[{"rootId":1},{"rootId":2}]""", Roots);
        Assert.Equal("Scan of all folders", string.Format(System.Globalization.CultureInfo.CurrentCulture, MediaVerifier.UI.Strings.Scans.RunOf, scope));
    }

    [Fact]
    public void Results_have_their_wording_and_colours()
    {
        Assert.Equal("Time limit reached", ScansPage.ResultText(RunStatus.BudgetReached));
        Assert.Equal(Severity.Critical, ScansPage.ResultTier(RunStatus.Failed));
        Assert.Equal(Severity.None, ScansPage.ResultTier(RunStatus.Completed));
        Assert.Null(ScansPage.ResultTier(RunStatus.Running));
    }

    [Fact]
    public async Task Estimates_are_unknown_until_a_scan_has_been_timed_and_large_scopes_ask_first()
    {
        using TestDatabase db = new();
        await SyntheticLibrary.GenerateAsync(db.Database, new SyntheticLibraryOptions { Files = 12_000, Roots = 2 });
        LiveUiData data = new(db.Database, () => new ScanSettings(), db.Clock);
        List<long> roots = [.. db.Database.Roots.List().Select(r => r.Id)];

        ScanEstimate unknown = data.LoadScanEstimate(roots);
        Assert.Equal(12_000, unknown.Files);
        Assert.True(unknown.IsLarge);
        Assert.Null(unknown.Standard);

        EstimateAverages averages = new() { BytesPerSecond = 100 * 1024 * 1024 };
        averages.SecondsPerFile[EstimateAverages.Key(MediaKind.Audio, ScanDepth.Standard)] = 0.5;
        await db.Database.Meta.SetAsync(ScanEngine.EstimatesKey, Estimator.Serialize(new Dictionary<StorageClass, EstimateAverages> { [StorageClass.Unknown] = averages }));
        ScanEstimate known = data.LoadScanEstimate(roots);
        Assert.NotNull(known.Quick);
        Assert.True(known.Standard > TimeSpan.Zero);
        Assert.False(data.LoadScanEstimate([]).IsLarge);
    }
}
