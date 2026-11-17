namespace MediaVerifier.UI;

/// <summary>
/// Every user-facing string (subplan 09, task UI-13). Wording, punctuation and capitalisation are
/// exactly as specified; nothing user-facing is written inline elsewhere.
/// </summary>
internal static class Strings
{
    public const string ProductName = "MediaVerifier";

    // Tier names (subplan 09, Formats used in strings).
    public const string TierCritical = "Critical";
    public const string TierHigh = "High";
    public const string TierMedium = "Medium";
    public const string TierLow = "Low";
    public const string TierOk = "OK";

    public static class Kit
    {
        public const string Browse = "Browse…";
        public const string Details = "Details";
    }

    public static class Pages
    {
        public const string Dashboard = "Dashboard";
        public const string Issues = "Issues";
        public const string Library = "Library";
        public const string Scans = "Scans";
        public const string Schedule = "Schedule";
        public const string Tools = "Tools";
        public const string Settings = "Settings";
        public const string UpdateAvailable = "Update available";
    }

    public static class Shell
    {
        public const string SearchPlaceholder = "Search files";
        public const string Notifications = "Notifications";
        public const string Scan = "Scan";
        public const string Pause = "Pause";
        public const string Resume = "Resume";
        public const string Stop = "Stop";
    }

    public static class Tray
    {
        public const string Open = "Open";
        public const string ScanNow = "Scan now";
        public const string Exit = "Exit";
    }

    public static class UnhandledError
    {
        public const string Title = "Something went wrong";
        public const string Body = "MediaVerifier will keep running. Details were saved to the log.";
        public const string OpenLog = "Open log";
        public const string Close = "Close";
    }
}
