namespace MediaVerifier.Core;

/// <summary>
/// User-facing text produced by the engine (notifications, run alerts). Rule texts live in the rule
/// catalog; everything shown by the window lives in the app's string table.
/// </summary>
public static class CoreStrings
{
    public const string DatabaseRecoveredTitle = "The library database was damaged";

    /// <summary>{0}: where the damaged copy was moved.</summary>
    public const string DatabaseRecoveredBody = "A fresh one was started. The damaged copy is at {0}.";
}
