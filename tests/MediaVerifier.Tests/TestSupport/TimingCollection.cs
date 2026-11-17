namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Tests that assert a time budget from the plans run outside the parallel phase: with every other test competing for
/// the processor, a budget such as "a compare image in under 2 s" (V-05) failed now and then for no fault of the code.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
