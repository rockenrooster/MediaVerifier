using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Settings;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

/// <summary>Scan settings as scans use them: the folders always skipped, and extension lists that never end up empty.</summary>
public sealed class ScanSettingsTests
{
    /// <summary>An Always skip list edited down to the user's own patterns still skips the quarantine (it indexed originals).</summary>
    [Fact]
    public void The_quarantine_and_recycle_bin_are_always_skipped()
    {
        using TempDirectory temp = new("scan-settings-skip");
        string path = temp.Combine("settings.json");
        File.WriteAllText(path, """{ "scanning": { "alwaysSkip": [ "Temp", "Backup", "$recycle.bin" ] } }""");

        List<string> skip = new SettingsStore(path).Load().Scanning.AlwaysSkip;

        Assert.Contains(OperationRunner.QuarantineFolder, skip);
        Assert.Contains("System Volume Information", skip);
        Assert.Single(skip, p => p.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Temp", skip);
        Assert.Contains("Backup", skip);
        Assert.Equal(["Temp"], ScanSettings.WithProtectedSkip(["Temp", " ", ".MediaVerifier-Quarantine"]).Where(p => !ScanSettings.IsProtectedSkip(p)));
    }

    [Fact]
    public void Extension_lists_split_at_spaces_and_fall_back_to_the_defaults()
    {
        Assert.Equal(["jpg", "png", "gif", "heic"], ScanSettings.ExtensionsOrDefaults(["jpg png  gif", ".HEIC"], MediaClassifier.DefaultPhoto));
        Assert.Equal(MediaClassifier.DefaultVideo, ScanSettings.ExtensionsOrDefaults(["...", " ", "*"], MediaClassifier.DefaultVideo));
        Assert.Equal(MediaClassifier.DefaultAudio, ScanSettings.ExtensionsOrDefaults(null, MediaClassifier.DefaultAudio));
    }

    /// <summary>A settings file whose photo list holds nothing usable scans photos as usual (it used to index none).</summary>
    [Fact]
    public void An_unusable_extension_list_in_the_file_gives_the_defaults()
    {
        using TempDirectory temp = new("scan-settings-ext");
        string path = temp.Combine("settings.json");
        File.WriteAllText(path, """{ "scanning": { "photoExtensions": [ "..." ], "scrubBudgetMinutes": 0 } }""");

        ScanSettings scan = new SettingsStore(path).Load().Scanning;

        Assert.Equal(MediaClassifier.DefaultPhoto, scan.PhotoExtensions);
        Assert.Equal(ScanSettings.MinScrubBudgetMinutes, scan.ScrubBudgetMinutes);
    }
}
