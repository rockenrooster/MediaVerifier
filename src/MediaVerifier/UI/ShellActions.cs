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
