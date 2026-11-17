using System.Diagnostics;

namespace MediaVerifier.UI;

/// <summary>Hand-offs to Windows Explorer and the clipboard (no shell: arguments go through ArgumentList).</summary>
internal static class ShellActions
{
    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ProcessStartInfo start = new("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select," + path);
        Start(start);
    }

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ProcessStartInfo start = new("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Start(start);
    }

    /// <summary>Opens a web page in the default browser (Explorer hands the address on).</summary>
    public static void OpenUrl(string url) => OpenFolder(url);

    public const string WebsiteUrl = "https://github.com/rockenrooster/MediaVerifier";
    public const string ReleasesUrl = "https://github.com/rockenrooster/MediaVerifier/releases";
    public const string NoticesUrl = "https://github.com/rockenrooster/MediaVerifier/blob/master/THIRD-PARTY-NOTICES.md";

    /// <summary>The help page for a rule (its section of docs/rules.md), or the help index.</summary>
    public static string HelpUrl(string? ruleId)
    {
        const string Rules = "https://github.com/rockenrooster/MediaVerifier/blob/master/docs/rules.md";
        if (ruleId is null || !Core.Rules.RuleCatalog.TryGet(ruleId, out Core.Rules.RuleDefinition rule))
        {
            return "https://github.com/rockenrooster/MediaVerifier#readme";
        }

        // GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens.
        string heading = (rule.Id + " " + rule.Title).ToLowerInvariant();
        System.Text.StringBuilder slug = new(heading.Length);
        foreach (char c in heading)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return Rules + "#" + slug;
    }

    public static void CopyText(IEnumerable<string> lines)
    {
        string text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private static void Start(ProcessStartInfo start)
    {
        try
        {
            using Process? process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer missing or refused: nothing to show.
        }
    }
}
