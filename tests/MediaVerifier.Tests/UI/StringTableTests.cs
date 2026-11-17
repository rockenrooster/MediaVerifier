using System.Text.RegularExpressions;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Task UI-13: every user-facing string lives in the string table (UI/Strings.cs). The test looks for
/// literals with words handed to the places users read: control texts and tooltips, painted text,
/// menu items, dialogs and toasts, and format patterns.
/// </summary>
public sealed partial class StringTableTests
{
    [Fact]
    public void No_user_facing_literal_lives_outside_the_string_table()
    {
        string source = RepositoryPaths.Combine("src", "MediaVerifier");
        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Diagnostics", StringComparison.OrdinalIgnoreCase) || relative.EndsWith("Strings.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (UserFacing().IsMatch(line))
                {
                    violations.Add($"{relative}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0, "User-facing literals outside UI/Strings.cs:\n" + string.Join('\n', violations));
    }

    [GeneratedRegex("""(\b(Text|ToolTipText|Headline|Body|Placeholder|Description|AccessibleName|AccessibleDescription|Title|Caption|Label|Action|Note)\s*=\s*\$?"[^"]*[A-Za-z]{2}[^"]*")|(DrawText\w*\(\s*\w+\s*,\s*\$?"[^"]*[A-Za-z]{2})|(new PopupItem\(\s*\$?"[^"]*[A-Za-z]{2})|(MessageDialog\.(Confirm|Show)\([^,]*,\s*\$?"[^"]*[A-Za-z]{2})|(string\.Format\([^,]+,\s*"[^"]*[A-Za-z]{2})|(MessageBox\.Show\(\s*\$?"[^"]*[A-Za-z]{2})|(ToolStripMenuItem\(\s*\$?"[^"]*[A-Za-z]{2})|(new ToastContent\([^,]+,\s*\$?"[^"]*[A-Za-z]{2})""")]
    private static partial Regex UserFacing();
}
