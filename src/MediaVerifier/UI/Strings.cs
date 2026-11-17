namespace MediaVerifier.UI;

/// <summary>
/// Every user-facing string (subplan 09, task UI-13). Wording, punctuation and capitalisation are
/// exactly as specified; nothing user-facing is written inline elsewhere. <c>{0}</c>-style placeholders
/// are filled with <see cref="UiFormat"/> values.
/// </summary>
internal static class Strings
{
    public const string ProductName = "MediaVerifier";

    /// <summary>Space, middle dot, space between facts on one line.</summary>
    public const string Separator = " · ";
    public const string And = " and ";
    public const string Dash = "—";

    // Tier names (subplan 09, Formats used in strings).
    public const string TierCritical = "Critical";
    public const string TierHigh = "High";
    public const string TierMedium = "Medium";
    public const string TierLow = "Low";
    public const string TierOk = "OK";

    public static class Units
    {
        public const string Bytes = "{0} bytes";
        public const string Kilobytes = "{0} KB";
        public const string Megabytes = "{0} MB";
        public const string Gigabytes = "{0} GB";
        public const string Terabytes = "{0} TB";
        public const string Seconds = "{0} s";
        public const string Minutes = "{0} min";
        public const string Hours = "{0} h";
        public const string HoursMinutes = "{0} h {1} min";
        public const string About = "About {0}";
        public const string AboutLower = "about {0}";
        public const string Rate = "{0} MB/s";
        public const string Days = "days";
        public const string MinutesUnit = "minutes";
        public const string SecondsUnit = "seconds";
    }

    public static class Dates
    {
        public const string Today = "Today";
        public const string Yesterday = "Yesterday";
        public const string Tomorrow = "Tomorrow";
    }

    public static class Depths
    {
        public const string Quick = "Quick";
        public const string Standard = "Standard";
        public const string Deep = "Deep";
    }

    public static class Kinds
    {
        public const string Photos = "Photos";
        public const string Videos = "Videos";
        public const string Music = "Music";
    }

    public static class Kit
    {
        public const string Browse = "Browse…";
        public const string Details = "Details";
        public const string MoreActions = "More actions";
        public const string Close = "Close";
        public const string CloseEsc = "Close (Esc)";
        public const string Cancel = "Cancel";
        public const string Save = "Save";
        public const string Reset = "Reset";
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
        public const string QuickScan = "Quick scan";
        public const string StandardScan = "Standard scan";
        public const string DeepScan = "Deep scan";
        public const string VerifyOnly = "Verify fingerprints only";
        public const string ScanFolder = "Scan a folder…";
        public const string AddFolderFirst = "Add a folder first";
        public const string AlreadyRunning = "A scan is already running";
        public const string ScheduledRunning = "A scheduled scan is running in the background";
        public const string EngineMissing = "The media engine is missing or damaged";
        public const string Scanning = "Scanning {0} of {1} files";
        public const string ScanningDetails = "· {0} · about {1} left";
        public const string ScanningEstimating = "· {0} · estimating time left";
        public const string ShortScanning = "{0} of {1} files · about {2} left";
        public const string ShortEstimating = "{0} of {1} files";
        public const string Finding = "Finding files… {0} so far";
        public const string Pausing = "Pausing, finishing {0} files";
        public const string Paused = "Paused · {0} of {1} files";
        public const string Finishing = "Finishing…";
        public const string StopTitle = "Stop this scan?";
        public const string StopText = "Finished files keep their results. You can continue later.";
        public const string StopButton = "Stop scan";
        public const string KeepScanning = "Keep scanning";
        public const string BadgeOverflow = "999+";
        public const string BellOverflow = "9+";
    }

    public static class Dashboard
    {
        public const string Health = "HEALTH";
        public const string Protection = "PROTECTION";
        public const string Scans = "SCANS";
        public const string ByMediaType = "BY MEDIA TYPE";
        public const string Recommended = "RECOMMENDED NEXT STEPS";
        public const string Activity = "RECENT ACTIVITY";
        public const string Healthy = "healthy";
        public const string HealthyLine = "{0} of {1} files have no problems";
        public const string NeedAttention = "{0} files need attention";
        public const string NeedAttentionOne = "1 file needs attention";
        public const string Verified = "verified";
        public const string VerifiedLine = "Re-read and unchanged in {0} days";
        public const string Overdue = "{0} files are overdue";
        public const string OverdueOne = "1 file is overdue";
        public const string Last = "Last";
        public const string Next = "Next";
        public const string Scheduled = "Scheduled";
        public const string Manual = "Manual";
        public const string NewAndResolved = "{0} new problems, {1} resolved";
        public const string NewOneAndResolved = "1 new problem, {1} resolved";
        public const string NoNewProblems = "No new problems";
        public const string NoSchedule = "No schedule";
        public const string SinceLastScanUp = "+{0} since last scan";
        public const string SinceLastScanDown = "−{0} since last scan";
        public const string NoChange = "No change";
        public const string FilesAndSize = "{0} files · {1}";
        public const string Problems = "{0} problems";
        public const string OneProblem = "1 problem";
        public const string NoProblems = "No problems";
        public const string NothingToDo = "Nothing needs doing right now.";
        public const string Sparkline = "Open problems, last 30 scans";
        public const string NotScannedTitle = "Your folders have not been scanned yet";
        public const string NotScannedDetail = "A first Standard scan takes about {0}";
        public const string EngineMissingBar = "The media engine is missing or damaged. Reinstall MediaVerifier.";
        public const string OpenDownloadPage = "Open download page";
        public const string FirstRunHeadline = "Add a folder to start watching it";
        public const string FirstRunLine1 = "MediaVerifier checks photos, videos and music for damage, silent changes and fake quality.";
        public const string FirstRunLine2 = "It only reads your files. Nothing is changed unless you ask.";
        public const string AddFolder = "Add folder";
        public const string DropCaption = "or drop folders anywhere in this window";
        public const string Step1Title = "Add folders";
        public const string Step1Text = "Choose what to watch. Mark backup drives as Backup so damaged files can be restored from them.";
        public const string Step2Title = "Scan";
        public const string Step2Text = "A Standard scan decodes audio and photos fully and samples video. You see the estimated time first.";
        public const string Step3Title = "Stay protected";
        public const string Step3Text = "Create a schedule. Scans then run on their own and you hear about new problems only.";
    }

    public static class Activity
    {
        public const string ScheduledFinished = "Scheduled scan finished";
        public const string ScanFinished = "Scan finished";
        public const string StoppedByYou = "Scan stopped by you";
        public const string CouldNotRun = "Scan could not run";
        public const string Restored = "{0} files restored from backup";
        public const string RestoredOne = "1 file restored from backup";
        public const string Repaired = "{0} files repaired";
        public const string RepairedOne = "1 file repaired";
        public const string FolderAdded = "Folder added: {0}";
        public const string FolderRemoved = "Folder removed: {0}";
        public const string Updated = "Updated to version {0}";
    }

    public static class Recommendations
    {
        public const string RotTitle = "{0} files changed on their own";
        public const string RotTitleOne = "1 file changed on its own";
        public const string RotCopyAll = "A healthy copy exists in {0} for all {1}";
        public const string RotCopySome = "A healthy copy exists in {0} for {1} of {2}";
        public const string RotNoCopy = "No healthy copy was found";
        public const string Review = "Review";
        public const string IndexTitle = "{0} videos have no seek index";
        public const string IndexDetail = "Rebuilding is lossless and takes about {0}";
        public const string FixAll = "Fix all";
        public const string ExtensionTitle = "{0} files have the wrong extension";
        public const string ExtensionDetail = "Renaming is safe and can be undone";
        public const string FakeLosslessTitle = "{0} albums look converted from MP3";
        public const string CannotRepair = "They cannot be repaired, only replaced";
        public const string UpscaledTitle = "{0} videos have less detail than their resolution";
        public const string OverdueTitle = "{0} of files not verified in {1} days";
        public const string OverdueDetail = "At {0} per night a full pass takes {1} days";
        public const string VerifyNow = "Verify now";
        public const string NoBackupTitle = "No backup folder is set up";
        public const string NoBackupDetail = "With one, damaged files can be restored automatically";
        public const string AddFolder = "Add folder";
        public const string NoScheduleTitle = "Scans only run when you start them";
        public const string NoScheduleDetail = "A schedule keeps checking in the background";
        public const string CreateSchedule = "Create schedule";
        public const string ReadErrorsTitle = "Drive {0}: returned read errors on {1} files";
        public const string ReadErrorsDetail = "Copy what you can elsewhere, then check the drive";
        public const string WhatToDo = "What to do";
        public const string InterruptedTitle = "The last scan was interrupted";
        public const string InterruptedDetail = "{0} of {1} files were finished";
        public const string Continue = "Continue";
        public const string Scan = "Scan";
    }

    public static class Issues
    {
        public const string All = "All";
        public const string Category = "Category";
        public const string AllCategories = "All categories";
        public const string Damage = "Damage";
        public const string Integrity = "Integrity";
        public const string Authenticity = "Authenticity";
        public const string Quality = "Quality";
        public const string Structure = "Structure";
        public const string Rule = "Rule";
        public const string AllRules = "All rules";
        public const string Folder = "Folder";
        public const string AllFolders = "All folders";
        public const string Status = "Status: {0}";
        public const string Open = "Open";
        public const string Ignored = "Ignored";
        public const string Resolved = "Resolved";
        public const string GroupBy = "Group by";
        public const string Kind = "Kind";
        public const string GroupFormat = "Group: {0}";
        public const string BySeverity = "Severity";
        public const string ByFolder = "Folder";
        public const string ByRule = "Rule";
        public const string Selected = "{0} selected";
        public const string Fix = "Fix…";
        public const string Ignore = "Ignore";
        public const string ScanAgain = "Scan again";
        public const string Quarantine = "Quarantine";
        public const string NeedDifferentFixes = "These problems need different fixes";
        public const string NewResults = "{0} new problems · Show";
        public const string Showing = "Showing {0} of {1}";
        public const string ClearFilters = "Clear filters";
        public const string NoneHeadline = "No problems found";
        public const string NoneText = "{0} files checked. Last scan {1}.";
        public const string FilteredHeadline = "Nothing matches these filters";
        public const string NotScannedHeadline = "Scan your library to see results";
        public const string NoFoldersHeadline = "Add a folder to start watching it";
        public const string SortBy = "Sort by";
        public const string SortSeverity = "Severity";
        public const string SortName = "Name";
        public const string SortFolder = "Folder";
        public const string SortSize = "Size";
        public const string SortFirstSeen = "First seen";
        public const string Likely = "Likely";
        public const string Possibly = "Possibly";
        public const string Suspicious = "Suspicious";
    }

    /// <summary>Action names (subplan 12, action catalog).</summary>
    public static class Actions
    {
        public const string RestoreCopy = "Restore from healthy copy";
        public const string Remux = "Rebuild container and index";
        public const string FixExtension = "Rename to the correct extension";
        public const string ShrinkBitDepth = "Store as 16-bit (lossless)";
        public const string ReencodeFlac = "Rewrite FLAC";
        public const string Salvage = "Save a playable copy";
        public const string ExtractPreview = "Extract embedded preview";
        public const string DeclipCopy = "Make a de-clipped copy (experimental)";
        public const string Quarantine = "Move to quarantine";
        public const string Recycle = "Move to Recycle Bin";
        public const string AcceptBaseline = "Accept current content";
        public const string ExportList = "Export replacement list";
        public const string CheckDrive = "Check the drive";
        public const string RecoverIndexHelp = "How to recover a recording without an index";
    }

    public static class Menu
    {
        public const string Details = "Details";
        public const string ScanAgain = "Scan again";
        public const string Ignore = "Ignore";
        public const string IgnoreRuleHere = "Ignore this rule for this folder";
        public const string ShowInFolder = "Show in folder";
        public const string CopyPath = "Copy path";
        public const string MoveToQuarantine = "Move to quarantine";
        public const string VerifyNow = "Verify now";
        public const string ScanThisFolder = "Scan this folder";
        public const string VerifyThisFolder = "Verify this folder";
        public const string IgnoreRuleHereEllipsis = "Ignore a rule here…";
        public const string Edit = "Edit…";
        public const string OpenInExplorer = "Open in Explorer";
        public const string Remove = "Remove…";
        public const string Duplicate = "Duplicate";
        public const string Delete = "Delete…";
        public const string ShortcutEnter = "Enter";
        public const string ShortcutCtrlEnter = "Ctrl+Enter";
        public const string ShortcutShiftF5 = "Shift+F5";
        public const string ShortcutCtrlI = "Ctrl+I";
        public const string ShortcutAltEnter = "Alt+Enter";
        public const string ShortcutCtrlC = "Ctrl+C";
        public const string ShortcutDel = "Del";
    }

    public static class Drawer
    {
        public const string Findings = "FINDINGS ({0})";
        public const string WhatItMeans = "What this means";
        public const string WhyItMatters = "Why it matters";
        public const string WhatYouCanDo = "What you can do";
        public const string Evidence = "Evidence";
        public const string History = "History";
        public const string TechnicalDetails = "Technical details";
        public const string CopyDetails = "Copy details";
        public const string Unchanged = "Unchanged since {0} · verified {1} times, last {2}";
        public const string UnchangedOnce = "Unchanged since {0} · verified once, last {1}";
        public const string NeverVerified = "Fingerprint recorded {0} · not verified since";
        public const string NoFingerprint = "No fingerprint yet";
        public const string ChangedSince = "Changed since {0}";
        public const string Message = "Message";
        public const string Count = "Count";
        public const string Mono = "mono";
        public const string Stereo = "stereo";
        public const string Surround = "5.1";
        public const string Kilohertz = "{0} kHz";
        public const string Bits = "{0}-bit";
        public const string Progressive = "{0}p";
        public const string Fps = "{0} fps";
        public const string Dimensions = "{0} × {1}";
        public const string ZeroHz = "0 Hz";
        public const string TopOfScale = "Top of scale: {0} kHz";
        public const string FileFingerprint = "File fingerprint (XXH3-128)  ";
        public const string StreamFingerprint = "Stream fingerprint (MD5)  ";
    }

    public static class Events
    {
        public const string FirstSeen = "First seen";
        public const string Baselined = "Fingerprint recorded";
        public const string Verified = "Verified unchanged";
        public const string Modified = "Modified";
        public const string Rot = "Changed without being modified";
        public const string MetaDrift = "Tags changed";
        public const string Moved = "Moved from {0}";
        public const string Missing = "Went missing";
        public const string CameBack = "Came back";
        public const string Repaired = "Repaired: {0}";
        public const string Restored = "Restored from {0}";
    }

    public static class Library
    {
        public const string Folders = "Folders";
        public const string Browse = "Browse";
        public const string AddFolder = "Add folder";
        public const string LibraryPill = "Library";
        public const string BackupPill = "Backup";
        public const string Scan = "Scan";
        public const string FilesAndSize = "{0} files · {1}";
        public const string Problems = "{0} problems";
        public const string OneProblem = "1 problem";
        public const string NoProblems = "No problems";
        public const string Scanned = "Scanned {0} · {1}";
        public const string NotScanned = "Not scanned yet · {0}";
        public const string NotConnected = "Not connected. Its files are left untouched.";
        public const string LastSeen = "Last seen {0} · {1}";
        public const string FingerprintsRecorded = "Fingerprints recorded {0} · {1}";
        public const string Ssd = "SSD";
        public const string HardDrive = "Hard drive";
        public const string Network = "Network";
        public const string Removable = "Removable";
        public const string DropArea = "Drop folders here, or press Ctrl+N to add one";
        public const string RemoveTitle = "Stop watching {0}?";
        public const string RemoveText = "Its index, fingerprints and history are deleted from MediaVerifier. Your files are not touched.";
        public const string StopWatching = "Stop watching";
        public const string AddTitle = "Add folder";
        public const string EditTitle = "Edit folder";
        public const string FolderLabel = "Folder";
        public const string NameLabel = "Name";
        public const string UseAs = "Use as";
        public const string UseLibrary = "Library";
        public const string UseLibraryText = "Scan everything here and report problems";
        public const string UseBackup = "Backup";
        public const string UseBackupText = "Only record fingerprints, so damaged files can be restored from here";
        public const string Contains = "Contains";
        public const string Skip = "Skip";
        public const string SkipHelper = "Folder or file patterns, separated by semicolons";
        public const string Counting = "Counting files…";
        public const string Found = "{0} media files found · {1}";
        public const string NoneFound = "No media files found here";
        public const string ErrorMissing = "This folder does not exist";
        public const string ErrorWatched = "This folder is already being watched";
        public const string ErrorInside = "This folder is inside {0}, which is already being watched";
        public const string AddedToast = "{0} added";
        public const string WaitingToast = "{0} files are waiting to be scanned";
        public const string ScanNow = "Scan now";
        public const string ChipAll = "All";
        public const string ChipOk = "OK";
        public const string ChipProblems = "Problems";
        public const string ColumnName = "Name";
        public const string ColumnFormat = "Format";
        public const string ColumnSize = "Size";
        public const string ColumnScanned = "Scanned";
        public const string ColumnVerified = "Verified";
        public const string NotScannedYet = "Not scanned yet";
        public const string Skipped = "Skipped: {0}";
        public const string SkipCloud = "online-only cloud file";
        public const string SkipInProgress = "still being written";
        public const string SkipLocked = "in use by another program";
        public const string SkipDenied = "no permission to read";
        public const string SkipTooLarge = "larger than the size limit";
        public const string SkipUnsupported = "format not supported";
    }

    public static class Scans
    {
        public const string Scanning = "Scanning {0}";
        public const string PausedScope = "Paused · {0}";
        public const string FindingIn = "Finding files in {0}";
        public const string AllFolders = "all folders";
        public const string FilesScope = "{0} files";
        public const string SubLineYou = "{0} depth · started {1} · started by you";
        public const string SubLineSchedule = "{0} depth · started {1} · started by schedule \"{2}\"";
        public const string Files = "Files";
        public const string DataRead = "Data read";
        public const string Speed = "Speed";
        public const string TimeLeft = "Time left";
        public const string OfTotal = "{0} of {1}";
        public const string Estimating = "Estimating…";
        public const string FoundSoFar = "Found so far";
        public const string ErrorsCount = "{0} files could not be read";
        public const string WorkingOn = "WORKING ON";
        public const string NoScan = "No scan is running";
        public const string LastScan = "Last scan {0}.";
        public const string History = "HISTORY";
        public const string ColStarted = "Started";
        public const string ColStartedBy = "Started by";
        public const string ColDepth = "Depth";
        public const string ColFolders = "Folders";
        public const string ColDuration = "Duration";
        public const string ColFiles = "Files";
        public const string ColNew = "New";
        public const string ColResolved = "Resolved";
        public const string ColResult = "Result";
        public const string You = "You";
        public const string ScheduleBy = "Schedule";
        public const string Completed = "Completed";
        public const string Stopped = "Stopped";
        public const string TimeLimit = "Time limit reached";
        public const string Interrupted = "Interrupted";
        public const string CouldNotRun = "Could not run";
        public const string Skipped = "Skipped";
        public const string StageReading = "Reading";
        public const string StageDecoding = "Decoding";
        public const string StageAnalysing = "Decoding and analysing";
        public const string StageSampling = "Sampling frames";
        public const string StageEveryFrame = "Decoding every frame";
        public const string RunOf = "Scan of {0}";
        public const string ShowProblems = "Show problems found in this scan";
        public const string LargeTitle = "Scan {0} files?";
        public const string LargeNote = "Estimates come from earlier scans on these drives. You can pause or stop at any time, and a stopped scan continues where it left off.";
        public const string StartScan = "Start scan";
        public const string QuickText = "Checks structure and fingerprints new files";
        public const string StandardText = "Decodes audio and photos fully, samples video";
        public const string DeepText = "Also decodes every video frame";
        public const string NotKnown = "Not known yet";
        public const string InterruptedBar = "The last scan was interrupted.";
        public const string Continue = "Continue";
    }

    public static class Schedule
    {
        public const string Intro = "Scans run on their own, even when MediaVerifier is closed.";
        public const string NewSchedule = "New schedule";
        public const string EditSchedule = "Edit schedule";
        public const string NextRun = "Next run";
        public const string LastResult = "Last result";
        public const string Off = "Off";
        public const string NeverRun = "Never run";
        public const string RunNow = "Run now";
        public const string Edit = "Edit";
        public const string UpToHours = "up to {0} h";
        public const string UpToMinutes = "up to {0} min";
        public const string NoTimeLimit = "no time limit";
        public const string EveryDay = "Every day at {0}";
        public const string EveryWeekday = "Every {0} at {1}";
        public const string DayOfMonth = "Day {0} of every month at {1}";
        public const string LastDayOfMonth = "Last day of every month at {0}";
        public const string EveryNDays = "Every {0} days at {1}";
        public const string EmptyHeadline = "No schedules yet";
        public const string EmptyText = "A schedule keeps your library checked without you thinking about it.";
        public const string Nightly = "Nightly verification";
        public const string Weekly = "Weekly check";
        public const string Verification = "VERIFICATION";
        public const string VerificationLine1 = "Every file should be re-read and compared with its fingerprint regularly.";
        public const string VerificationLine2 = "That is how silent damage is found while a healthy copy still exists.";
        public const string ReReadEvery = "Re-read every file at least every";
        public const string SpendUpTo = "Spend up to";
        public const string MinutesEachRun = "minutes on this in each scheduled scan";
        public const string FullPass = "At {0} per night a full pass takes {1} days.";
        public const string OverdueCaption = "{0} files are overdue. Raise the limit or ";
        public const string VerifyNowLink = "verify now";
        public const string Name = "Name";
        public const string Folders = "Folders";
        public const string AllFolders = "All folders";
        public const string Depth = "Depth";
        public const string QuickHelper = "Checks structure and fingerprints new files.";
        public const string StandardHelper = "Decodes audio and photos fully, and samples video.";
        public const string DeepHelper = "Also decodes every video frame. Slow.";
        public const string Repeat = "Repeat";
        public const string Daily = "Daily";
        public const string Weekly2 = "Weekly";
        public const string Monthly = "Monthly";
        public const string EveryDays = "Every … days";
        public const string On = "On";
        public const string At = "At";
        public const string TimeLimitLabel = "Time limit";
        public const string NoLimit = "No limit";
        public const string Minutes30 = "30 minutes";
        public const string Hour1 = "1 hour";
        public const string Hours2 = "2 hours";
        public const string Hours4 = "4 hours";
        public const string Hours8 = "8 hours";
        public const string Conditions = "Conditions";
        public const string PluggedIn = "Only when plugged in";
        public const string WaitFullScreen = "Wait if a game or full-screen app is running";
        public const string Wake = "Wake the computer to run";
        public const string Summary = "{0}, {1} scan of {2}.";
        public const string SummaryNext = "Next run: {0}";
        public const string DayN = "Day {0}";
        public const string LastDay = "Last day";
        public const string ErrorName = "Give the schedule a name";
        public const string ErrorDays = "Choose at least one day";
        public const string ErrorFolders = "Choose at least one folder";
        public const string ErrorWindows = "Windows would not accept this schedule: {0}";
    }

    public static class Tools
    {
        public const string Quarantine = "Quarantine";
        public const string QuarantineText = "Originals kept after a repair, in case you want them back.";
        public const string Items = "Items";
        public const string Size = "Size";
        public const string OldestRemovedIn = "Oldest is removed in";
        public const string OpenQuarantine = "Open quarantine";
        public const string Operations = "Operations";
        public const string OperationsText = "Everything MediaVerifier changed, newest first. Most can be undone.";
        public const string Last = "Last";
        public const string When = "When";
        public const string CanBeUndone = "Can be undone";
        public const string OpenJournal = "Open journal";
        public const string Reports = "Reports";
        public const string ReportsText = "Save findings as a web page, a spreadsheet file, or JSON.";
        public const string OpenProblems = "Open problems";
        public const string LastReport = "Last report";
        public const string CreateReport = "Create report";
        public const string MediaEngine = "Media engine";
        public const string MediaEngineText = "FFmpeg is built in. It decodes files to check them and performs repairs.";
        public const string Version = "Version";
        public const string Licence = "Licence";
        public const string ViewLicences = "View licences";
        public const string Database = "Database";
        public const string DatabaseText = "The index of your library, its fingerprints and history.";
        public const string Location = "Location";
        public const string LastBackup = "Last backup";
        public const string Manage = "Manage";
        public const string BackToTools = "Back to Tools";
        public const string Never = "Never";
        public const string Yes = "Yes";
        public const string No = "No";
        public const string DaysValue = "{0} days";
        public const string FfmpegVersion = "FFmpeg version";
        public const string Build = "Build";
        public const string Integrity = "Integrity";
        public const string IntegrityVerified = "Verified";
        public const string IntegrityChanged = "Changed";
        public const string IntegrityMissing = "Missing";
        public const string EngineChangedBar = "The media engine files were changed. Results may differ from a standard installation.";
        public const string EngineNotice = "MediaVerifier runs FFmpeg as a separate program. Its exact source is published with each release.";
        public const string OpenLicencesFolder = "Open licences folder";
        public const string WhereSource = "Where to get the source";
        public const string FilesIndexed = "Files indexed";
        public const string BackUpNow = "Back up now";
        public const string BackUpNowText = "Saves a copy next to the database";
        public const string Compact = "Compact";
        public const string CompactText = "Reclaims unused space";
        public const string Check = "Check";
        public const string CheckText = "Looks for damage in the database itself";
        public const string Move = "Move…";
        public const string MoveText = "Use a different folder";
        public const string ResetEllipsis = "Reset…";
        public const string ResetText = "Forget everything and start again";
        public const string ResetTitle = "Reset the library?";
        public const string ResetBody = "Folders, fingerprints, findings and history are deleted. Your files are not touched. This cannot be undone.";
        public const string Reset = "Reset";
        public const string QuarantineEmpty = "Quarantine is empty";
        public const string QuarantineEmptyText = "Originals appear here after a repair.";
        public const string OperationsEmpty = "Nothing has been changed yet";
        public const string ColFile = "File";
        public const string ColWasIn = "Was in";
        public const string ColSize = "Size";
        public const string ColMoved = "Moved";
        public const string ColRemovedIn = "Removed in";
        public const string ColWhen = "When";
        public const string ColWhat = "What";
        public const string ColFiles = "Files";
        public const string ColResult = "Result";
        public const string Restore = "Restore";
        public const string DeleteForGood = "Delete for good";
        public const string OpenFolder = "Open folder";
        public const string DeleteAll = "Delete all…";
        public const string Undo = "Undo";
        public const string Done = "Done";
        public const string Failed = "Failed";
        public const string Undone = "Undone";
        public const string Cancelled = "Cancelled";
        public const string ReportWhat = "What";
        public const string ReportCurrent = "Open problems (current filters)";
        public const string ReportAll = "All open problems";
        public const string ReportLastScan = "Last scan";
        public const string ReportFormat = "Format";
        public const string ReportHtml = "Web page";
        public const string ReportHtmlText = "One file you can open in a browser or print";
        public const string ReportCsv = "Spreadsheet (CSV)";
        public const string ReportCsvText = "Opens in Excel";
        public const string ReportJson = "JSON";
        public const string ReportJsonText = "For other programs";
        public const string SaveTo = "Save to";
        public const string Create = "Create";
        public const string ReportSaved = "Report saved";
        public const string Open = "Open";
    }

    public static class Settings
    {
        public const string General = "General";
        public const string GeneralIntro = "How MediaVerifier looks and starts.";
        public const string Theme = "Theme";
        public const string ThemeSystem = "System";
        public const string ThemeLight = "Light";
        public const string ThemeDark = "Dark";
        public const string StartWithWindows = "Start with Windows";
        public const string StartWithWindowsHelper = "Starts hidden in the notification area";
        public const string KeepRunning = "Keep running in the notification area when closed";
        public const string DefaultDepth = "Default scan depth";
        public const string AskLarge = "Ask before large scans";
        public const string AskLargeHelper = "Shows the estimated time first";
        public const string Scanning = "Scanning";
        public const string ScanningIntro = "How much of the computer scans may use, and what they look at.";
        public const string Speed = "Speed";
        public const string SpeedLow = "Low";
        public const string SpeedBalanced = "Balanced";
        public const string SpeedHigh = "High";
        public const string SpeedHelper = "Share of processor cores: 25 %, 50 %, 100 %";
        public const string ReadersHdd = "Readers per hard drive";
        public const string ReadersSsd = "Readers per SSD";
        public const string ReadersNetwork = "Readers per network folder";
        public const string FileTypes = "File types";
        public const string EditEllipsis = "Edit…";
        public const string RestoreDefaults = "Restore defaults";
        public const string AlwaysSkip = "Always skip";
        public const string CloudFiles = "Scan online-only cloud files";
        public const string CloudFilesHelper = "Downloads them first. Can use a lot of data";
        public const string SkipLarger = "Skip files larger than";
        public const string NoLimit = "No limit";
        public const string Gb10 = "10 GB";
        public const string Gb50 = "50 GB";
        public const string Gb100 = "100 GB";
        public const string StallSeconds = "Stop a file that makes no progress for";
        public const string Upscaling = "Check photos for upscaling";
        public const string Strict = "Strict decoding in Deep scans";
        public const string StrictHelper = "Reports files that break the format rules even if they play";
        public const string Rules = "Rules";
        public const string RulesIntro = "Turn checks on or off, cap how serious they can be, and tune their limits. Changes apply at once, with no rescan.";
        public const string Integrity = "Integrity";
        public const string IntegrityIntro = "How often files are re-read to catch silent changes.";
        public const string ReReadEvery = "Re-read every file at least every";
        public const string TimeEachRun = "Time for this in each scheduled scan";
        public const string VerifyOnAnalysis = "Also verify whenever a file is analysed";
        public const string KeepOriginals = "Keep originals after a repair in";
        public const string QuarantineFolder = "Quarantine folder";
        public const string RecycleBin = "Recycle Bin";
        public const string AnotherFolder = "Another folder…";
        public const string EmptyQuarantine = "Empty quarantine after";
        public const string Notifications = "Notifications";
        public const string NotificationsIntro = "When MediaVerifier tells you something outside its window.";
        public const string ShowWindowsNotifications = "Show Windows notifications";
        public const string NotifyAbout = "Notify about";
        public const string CriticalOnly = "Critical only";
        public const string HighAndAbove = "High and above";
        public const string MediumAndAbove = "Medium and above";
        public const string Everything = "Everything";
        public const string QuietHours = "Quiet hours";
        public const string From = "From";
        public const string To = "To";
        public const string TestNotification = "Send a test notification";
        public const string Updates = "Updates";
        public const string UpdatesIntro = "MediaVerifier updates itself from signed releases on GitHub.";
        public const string VersionLine = "Version {0}";
        public const string CheckNow = "Check now";
        public const string DownloadInstall = "Download and install";
        public const string ReleaseNotes = "Release notes";
        public const string CheckOnStart = "Check for updates when MediaVerifier starts";
        public const string NotChecked = "Update status has not been checked.";
        public const string Checking = "Checking…";
        public const string UpToDate = "MediaVerifier is up to date.";
        public const string Available = "Version {0} is available.";
        public const string Downloading = "Downloading {0}%";
        public const string NotVerified = "Update found, but it could not be verified. Nothing was installed.";
        public const string CheckFailed = "Could not check for updates. {0}";
        public const string Advanced = "Advanced";
        public const string AdvancedIntro = "For troubleshooting.";
        public const string DatabaseLocation = "Database location";
        public const string Change = "Change…";
        public const string Logs = "Logs";
        public const string OpenLogs = "Open logs folder";
        public const string Diagnostics = "Diagnostics";
        public const string RunSelfCheck = "Run self-check";
        public const string CopyReport = "Copy system report";
        public const string About = "About";
        public const string Website = "Website";
        public const string Licences = "Licences";
        public const string FilterRules = "Filter rules";
        public const string ChipAll = "All";
        public const string ChipDamage = "Damage";
        public const string ChipAuthenticity = "Authenticity";
        public const string ChipQuality = "Quality";
        public const string ChipStructure = "Structure";
        public const string RuleMeta = "{0} · {1} · {2} depth";
        public const string UpToCritical = "Up to Critical";
        public const string UpToHigh = "Up to High";
        public const string UpToMedium = "Up to Medium";
        public const string UpToLow = "Up to Low";
        public const string DefaultIs = "Default is {0}";
        public const string ChangedTier = "{0} findings changed tier with this value.";
        public const string Appeared = "{0} findings appeared.";
        public const string NoLongerApply = "{0} findings no longer apply.";
        public const string NoFindingsChanged = "No findings changed.";
    }

    public static class Dialogs
    {
        public const string AcceptTitle = "Accept this file as it is now?";
        public const string AcceptText = "MediaVerifier will treat the current content as correct and stop reporting the change.";
        public const string AcceptCheck = "I changed this file myself";
        public const string Accept = "Accept";
        public const string IgnoreRuleTitle = "Ignore \"{0}\" in {1}?";
        public const string IgnoreRuleText = "Existing and future findings of this kind in this folder and its subfolders are hidden. You can undo this in Settings → Rules.";
        public const string Ignore = "Ignore";
        public const string InstallTitle = "Install the update now?";
        public const string InstallText = "A scan is running. It will be interrupted and can continue after the update.";
        public const string Install = "Install";
        public const string NotNow = "Not now";
        public const string DeleteTitle = "Delete {0} files for good?";
        public const string DeleteText = "These are the originals kept in quarantine, {0}. This cannot be undone.";
        public const string DeleteForGood = "Delete for good";
        public const string KeepThem = "Keep them";
        public const string NotificationsTitle = "Notifications";
        public const string MarkAllRead = "Mark all read";
        public const string ClearAll = "Clear all";
        public const string NoNotifications = "No notifications";
        public const string Undo = "Undo";
        public const string Show = "Show";
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
