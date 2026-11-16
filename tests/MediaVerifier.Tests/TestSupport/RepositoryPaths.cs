namespace MediaVerifier.Tests.TestSupport;

/// <summary>Locates the repository root (the folder holding MediaVerifier.sln) from the test binaries.</summary>
public static class RepositoryPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>
    /// True when generated documents should be rewritten instead of compared
    /// (set <c>MEDIAVERIFIER_UPDATE_DOCS=1</c> and run the tests once).
    /// </summary>
    public static bool UpdateDocs =>
        string.Equals(Environment.GetEnvironmentVariable("MEDIAVERIFIER_UPDATE_DOCS"), "1", StringComparison.Ordinal);

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaVerifier.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("MediaVerifier.sln not found above " + AppContext.BaseDirectory);
    }
}
