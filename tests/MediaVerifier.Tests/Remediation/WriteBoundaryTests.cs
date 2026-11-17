using System.Text.RegularExpressions;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>
/// Subplan 12, exit criteria: no code path outside the remediation module writes to a library root. Every call in
/// MediaVerifier.Core that can change, move or delete a file must sit in a file on this list, each with the reason it
/// never touches user media. A new call elsewhere fails here until it is routed through the operation runner or
/// added with its reason.
/// </summary>
public sealed partial class WriteBoundaryTests
{
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Remediation\"] = "The operation runner and its actions: the only writer of user media (safe-operation protocol)",
        [@"Storage\"] = "The library database, its backups and user-chosen report files, all outside library roots",
        [@"Logging\"] = "Log files in the data folder",
        [@"Settings\"] = "settings.json in the roaming data folder",
        [@"Updates\"] = "Downloaded installers in the data folder's Updates folder",
        [@"Ffmpeg\"] = "Per-job scratch folders under the data folder's temp folder",
        ["AppPaths.cs"] = "Creates the data folders",
        [@"Analysis\Audio\AudioEvidence.cs"] = "The evidence cache in the data folder",
        [@"Analysis\Pictures.cs"] = "The picture cache in the data folder",
        [@"Engine\FileContext.cs"] = "Deletes the file's job scratch folder in the data folder",
        [@"Analysis\Providers\FrameSampleProvider.cs"] = "Filter statistics files in the job's scratch folder in the data folder",
    };

    [Fact]
    public void Only_listed_code_can_change_files()
    {
        string source = RepositoryPaths.Combine("src", "MediaVerifier.Core");
        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || Allowed.Keys.Any(k => k.EndsWith('\\') ? relative.StartsWith(k, StringComparison.OrdinalIgnoreCase) : relative.Equals(k, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("///", StringComparison.Ordinal) && Mutating().IsMatch(line))
                {
                    violations.Add($"{relative}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(violations.Count == 0, "File changes outside the allowed code:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void The_scan_recognises_file_changes() =>
        Assert.All(
            ["File.Move(a, b);", "File.Delete(path);", "Directory.Delete(folder, true);", "File.WriteAllBytes(p, data);", "new FileStream(p, FileMode.Create)", "stream.SetLength(0);", "info.MoveTo(target);", "File.SetLastWriteTimeUtc(p, t);", "File.Open(p, FileMode.Open, FileAccess.ReadWrite)"],
            line => Assert.Matches(Mutating(), line));

    [GeneratedRegex(@"\b(File|Directory)\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create\w*|OpenWrite|SetLastWriteTime\w*|SetCreationTime\w*|SetAttributes|Encrypt|Decrypt)\s*\(|\bnew\s+FileStream\s*\([^)]*(FileMode\.(Create|CreateNew|OpenOrCreate|Append|Truncate)|FileAccess\.(Write|ReadWrite))|\bFile\.Open\s*\([^)]*FileAccess\.(Write|ReadWrite)|\.(SetLength|MoveTo)\s*\(")]
    private static partial Regex Mutating();
}
