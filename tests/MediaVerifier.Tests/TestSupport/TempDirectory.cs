using MediaVerifier.Core;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>A per-test folder under %TEMP%, deleted on dispose. Tests never use the real profile.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? prefix = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MediaVerifier.Tests",
            (prefix ?? "t") + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public AppPaths CreateAppPaths() => AppPaths.UnderBase(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
