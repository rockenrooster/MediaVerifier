using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MediaVerifier.UI;

/// <summary>Hand-offs to Windows Explorer and the clipboard (no command shell: arguments go through ArgumentList).</summary>
internal static class ShellActions
{
    /// <summary>
    /// Opens Explorer on the file's folder with the file selected, through the shell API. A command line
    /// <c>explorer /select,…</c> fails for paths with spaces once the whole argument is quoted. A file that is
    /// gone opens its folder.
    /// </summary>
    public static void ShowInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            OpenFolder(Path.GetDirectoryName(path));
            return;
        }

        if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr item, 0, out _) < 0 || item == IntPtr.Zero)
        {
            OpenFolder(Path.GetDirectoryName(path));
            return;
        }

        try
        {
            // With no child items, the one item list names the item to select in its parent folder.
            if (SHOpenFolderAndSelectItems(item, 0, null, 0) < 0)
            {
                OpenFolder(Path.GetDirectoryName(path));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    /// <summary>Opens a file in the program Windows has for its type (Explorer hands it on); nothing when it is gone.</summary>
    public static void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        ProcessStartInfo start = new("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[]? items, uint flags);
}
