namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Tests that paint or switch the process-wide theme run one at a time: the theme and the GDI caches
/// are shared by the whole process.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "UI";
}
