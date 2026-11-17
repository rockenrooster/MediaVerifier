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

    /// <summary>Run notifications (subplan 13, Notification policy). No file names: toasts appear on lock screens.</summary>
    public static class Notify
    {
        public const string CouldNotRun = "Scheduled scan could not run";
        public const string NewProblem = "{0} new problem found";
        public const string NewProblems = "{0} new problems found";
        public const string NothingNew = "Scheduled scan finished";
        public const string Skipped = "Scheduled scan skipped";
        public const string OnBattery = "The computer was running on battery.";
        public const string FullScreen = "A full-screen program was running.";
        public const string CriticalCount = "{0} critical";
        public const string HighCount = "{0} high";
        public const string MediumCount = "{0} medium";
        public const string LowCount = "{0} low";
        public const string FinishedAt = "Scan finished {0}.";
    }

    /// <summary>Why an operation did not run or failed (subplan 12, safe-operation protocol).</summary>
    public static class Remediation
    {
        public const string BackupRoot = "The file is in a backup folder. Backup folders are only used as sources.";
        public const string Missing = "The file is no longer there.";
        public const string ChangedSinceScan = "The file changed since the last scan. Scan it again first.";
        public const string ReadOnly = "The file is read-only.";
        public const string InUse = "The file is in use by another program.";
        public const string NoSpace = "There is not enough free space on the drive.";
        public const string Cancelled = "Cancelled.";
        public const string Interrupted = "Interrupted. The original was left unchanged.";
        public const string CannotUndo = "This operation cannot be undone.";
        public const string OriginalGone = "The kept original is no longer there.";
        public const string NameTaken = "A file with the original name exists again.";
        public const string NoHealthyCopy = "No verified healthy copy was found.";
        public const string CopyChanged = "The copy no longer matches the file's fingerprint.";
        public const string StreamsDiffer = "The rebuilt file's streams differ from the original's.";
        public const string AudioDiffers = "The new file's audio differs from the original's.";
        public const string OutputUnreadable = "The new file could not be read back.";
        public const string NotNeeded = "The file already has the right extension.";
        public const string ToolFailed = "The media engine could not write the new file.";
        public const string NoPreview = "The file holds no intact embedded preview.";
        public const string NoRecycleBin = "This drive has no Recycle Bin.";
    }
}
