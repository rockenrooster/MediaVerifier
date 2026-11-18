using System.Diagnostics;
using System.IO.Hashing;
using System.Security.AccessControl;
using System.Security.Principal;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Engine;

public sealed class EngineUnitTests
{
    private static readonly FakeClock Clock = new(new DateTimeOffset(2026, 11, 16, 14, 20, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("photo.JPG", MediaKind.Photo)]
    [InlineData("holiday.final.jpeg", MediaKind.Photo)]
    [InlineData("archive.tar.mp3", MediaKind.Audio)]
    [InlineData("Clip.MKV", MediaKind.Video)]
    [InlineData("raw.CR3", MediaKind.Photo)]
    [InlineData("notes.txt", MediaKind.Unknown)]
    [InlineData("noextension", MediaKind.Unknown)]
    [InlineData("trailingdot.", MediaKind.Unknown)]
    public void ExtensionsClassifyCaseInsensitively(string name, MediaKind expected)
    {
        Assert.Equal(expected, MediaClassifier.Default.Classify(name));
    }

    [Fact]
    public void UserListsAreNormalised()
    {
        MediaClassifier custom = new(photo: [" .PNG", "*.Jpg", ""], video: ["mp4"], audio: []);
        Assert.Equal(MediaKind.Photo, custom.Classify("a.jpg"));
        Assert.Equal(MediaKind.Unknown, custom.Classify("a.flac"));
        Assert.Equal(["png", "jpg"], MediaClassifier.Normalize([" .PNG", "*.Jpg", "", "png", "a b"]));
        Assert.True(MediaClassifier.IsRaw("nef"));
    }

    [Fact]
    public void EnumeratorSkipsExcludedHiddenSystemAndJunctionsAndRecordsUnlistedDirectories()
    {
        using TempDirectory temp = new("enum");
        string root = temp.Path;
        Write(root, "a.jpg");
        Write(root, @"Sub\b.mp3");
        Write(root, @"Sub\notes.txt");
        Write(root, @"$RECYCLE.BIN\c.jpg");
        Write(root, @".Trash-1000\d.jpg");
        Write(root, @"@eaDir\e.jpg");
        string hidden = Write(root, "hidden.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        RunCmd("mklink", "/J", Path.Combine(root, "Loop"), root);
        string locked = Path.Combine(root, "Locked");
        Write(root, @"Locked\f.jpg");
        DenyList(locked);
        try
        {
            TreeEnumerator enumerator = new(WindowsFileSystem.Instance, MediaClassifier.Default, new EnumerationRules { InProgressAge = TimeSpan.Zero }, Clock);
            List<EnumeratedDirectory> dirs = [.. enumerator.Enumerate(root)];

            string[] files = [.. dirs.SelectMany(d => d.Files).Select(f => (f.RelDir.Length == 0 ? "" : f.RelDir + "\\") + f.Name).Order(StringComparer.Ordinal)];
            Assert.Equal(["Sub\\b.mp3", "a.jpg"], files);
            EnumeratedDirectory unlisted = Assert.Single(dirs, d => !d.Listed);
            Assert.Equal("Locked", unlisted.RelDir);
            Assert.DoesNotContain(dirs, d => d.RelDir.StartsWith("Loop", StringComparison.Ordinal));
        }
        finally
        {
            Allow(locked);
        }
    }

    [Fact]
    public void GlobsIncludeAndExcludeRelativePaths()
    {
        using TempDirectory temp = new("glob");
        Write(temp.Path, @"Keep\one.jpg");
        Write(temp.Path, @"Keep\Deep\two.jpg");
        Write(temp.Path, @"Skip\three.jpg");
        Write(temp.Path, @"Keep\four.mp3");
        TreeEnumerator enumerator = new(WindowsFileSystem.Instance, MediaClassifier.Default, new EnumerationRules
        {
            IncludeGlobs = ["Keep/**"],
            ExcludeGlobs = ["**/*.mp3"],
            InProgressAge = TimeSpan.Zero,
        }, Clock);

        string[] names = [.. enumerator.Enumerate(temp.Path).SelectMany(d => d.Files).Select(f => f.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["one.jpg", "two.jpg"], names);
        Assert.Matches(Glob.Compile(["*.jpg"])!, "a.jpg");
        Assert.DoesNotMatch(Glob.Compile(["*.jpg"])!, @"dir\a.jpg");
        Assert.Matches(Glob.Compile(["**\\*.jpg"])!, @"dir\sub\a.JPG");
        Assert.Null(Glob.Compile([]));
    }

    /// <summary>A repair's temporary output (name.mvtmp-id.ext) is never indexed as a library file.</summary>
    [Fact]
    public void RepairTemporaryOutputsAreNotListed()
    {
        using TempDirectory temp = new("mvtmp");
        Write(temp.Path, "song.mp3");
        Write(temp.Path, "song.mvtmp-42.mp3");
        TreeEnumerator enumerator = new(WindowsFileSystem.Instance, MediaClassifier.Default, new EnumerationRules { InProgressAge = TimeSpan.Zero }, Clock);

        string[] names = [.. enumerator.Enumerate(temp.Path).SelectMany(d => d.Files).Select(f => f.Name)];

        Assert.Equal(["song.mp3"], names);
    }

    [Fact]
    public void SkipRulesNeedNoIo()
    {
        TreeEnumerator enumerator = new(WindowsFileSystem.Instance, MediaClassifier.Default, new EnumerationRules { MaxFileSize = 1000 }, Clock);
        long now = Clock.UtcNow.UtcTicks;
        long old = now - TimeSpan.FromDays(1).Ticks;
        HashSet<string> siblings = new(StringComparer.OrdinalIgnoreCase) { "movie.mkv", "movie.mkv.part", "song.mp3" };

        Assert.Equal(SkipReason.CloudPlaceholder, enumerator.DecideSkip(new FsEntry("a.jpg", false, 10, old, (FileAttributes)0x400000), siblings, now));
        Assert.Equal(SkipReason.CloudPlaceholder, enumerator.DecideSkip(new FsEntry("a.jpg", false, 10, old, FileAttributes.Offline), siblings, now));
        Assert.Equal(SkipReason.TooLarge, enumerator.DecideSkip(new FsEntry("a.jpg", false, 5000, old, FileAttributes.Normal), siblings, now));
        Assert.Equal(SkipReason.InProgress, enumerator.DecideSkip(new FsEntry("a.jpg", false, 10, now - TimeSpan.FromSeconds(30).Ticks, FileAttributes.Normal), siblings, now));
        Assert.Equal(SkipReason.InProgress, enumerator.DecideSkip(new FsEntry("movie.mkv", false, 10, old, FileAttributes.Normal), siblings, now));
        Assert.Equal(SkipReason.None, enumerator.DecideSkip(new FsEntry("song.mp3", false, 10, old, FileAttributes.Normal), siblings, now));
        Assert.Equal(SkipReason.None, enumerator.DecideSkip(new FsEntry("future.jpg", false, 10, now + TimeSpan.FromDays(400).Ticks, FileAttributes.Normal), siblings, now));
    }

    public static TheoryData<string, ReconcileKind, bool, bool, bool, bool> ReconcileCases() => new()
    {
        // name, kind, returned, read, analyse, reevaluate
        { "new", ReconcileKind.New, false, true, true, false },
        { "unchanged", ReconcileKind.Unchanged, false, false, false, false },
        { "unchanged-1s-later", ReconcileKind.Unchanged, false, false, false, false },
        { "modified-size", ReconcileKind.Modified, false, true, true, false },
        { "modified-time", ReconcileKind.Modified, false, true, true, false },
        { "returned", ReconcileKind.Unchanged, true, false, false, false },
        { "shallow", ReconcileKind.Unchanged, false, true, true, false },
        { "old-detectors", ReconcileKind.Unchanged, false, false, false, true },
        { "verify-due", ReconcileKind.Unchanged, false, true, false, false },
        { "no-baseline", ReconcileKind.Unchanged, false, true, false, false },
        { "errored", ReconcileKind.Unchanged, false, true, true, false },
        { "skipped-now", ReconcileKind.Skipped, false, false, false, false },
    };

    [Theory]
    [MemberData(nameof(ReconcileCases))]
    public void ReconcilerFollowsTheTable(string name, ReconcileKind kind, bool returned, bool read, bool analyse, bool reevaluate)
    {
        DateTimeOffset now = Clock.UtcNow;
        long recent = (now - TimeSpan.FromDays(10)).UtcTicks;
        long mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        FileRecord Row(Func<FileRecord, FileRecord>? change = null)
        {
            FileRecord row = new()
            {
                Id = 1,
                Name = "x.jpg",
                Ext = "jpg",
                MediaKind = MediaKind.Photo,
                Size = 100,
                MtimeUtc = mtime,
                State = FileState.Ok,
                ScannedDepth = ScanDepth.Standard,
                DetectorSet = 42,
                Hash = new byte[16],
                HashSize = 100,
                HashMtimeUtc = mtime,
                HashUtc = recent,
                LastVerifiedUtc = recent,
            };
            return change is null ? row : change(row);
        }

        EnumeratedFile file = new("", "x.jpg", "jpg", MediaKind.Photo, 100, mtime, FileAttributes.Normal, SkipReason.None);
        (EnumeratedFile f, FileRecord? r) = name switch
        {
            "new" => (file, null),
            "unchanged" => (file, Row()),
            "unchanged-1s-later" => (file with { MtimeUtcTicks = mtime + TimeSpan.TicksPerSecond }, Row()),
            "modified-size" => (file with { Size = 101 }, Row()),
            "modified-time" => (file with { MtimeUtcTicks = mtime + TimeSpan.FromMinutes(5).Ticks }, Row()),
            "returned" => (file, Row(x => x with { State = FileState.Missing })),
            "shallow" => (file, Row(x => x with { ScannedDepth = ScanDepth.Quick })),
            "old-detectors" => (file, Row(x => x with { DetectorSet = 7 })),
            "verify-due" => (file, Row(x => x with { LastVerifiedUtc = (now - TimeSpan.FromDays(200)).UtcTicks })),
            "no-baseline" => (file, Row(x => x with { Hash = null })),
            "errored" => (file, Row(x => x with { State = FileState.Error })),
            _ => (file with { Skip = SkipReason.InProgress }, Row()),
        };
        Reconciler reconciler = new(ScanDepth.Standard, 42, now, TimeSpan.FromDays(90), false, false, true, RootRole.Library, false);

        ReconcileDecision decision = reconciler.Decide(f, r);

        Assert.Equal(kind, decision.Kind);
        Assert.Equal(returned, decision.Returned);
        Assert.Equal(read, decision.ReadContent);
        Assert.Equal(analyse, decision.Analyze);
        Assert.Equal(reevaluate, decision.ReevaluateOnly);
    }

    [Fact]
    public void VerifyOnlyAndBackupRootsNeverAnalyse()
    {
        EnumeratedFile file = new("", "x.jpg", "jpg", MediaKind.Photo, 1, 1, FileAttributes.Normal, SkipReason.None);
        Reconciler verifyOnly = new(ScanDepth.Standard, 1, Clock.UtcNow, TimeSpan.FromDays(90), true, true, true, RootRole.Library, false);
        Reconciler backup = new(ScanDepth.Deep, 1, Clock.UtcNow, TimeSpan.FromDays(90), false, false, true, RootRole.Backup, false);
        Assert.False(verifyOnly.Decide(file, null).Analyze);
        Assert.True(verifyOnly.Decide(file, null).ReadContent);
        Assert.False(backup.Decide(file, null).Analyze);
    }

    [Fact]
    public async Task CpuGateNeverExceedsCapacity()
    {
        CpuGate gate = new(capacity: 6);
        int inUse = 0;
        int peak = 0;
        Random random = new(5);
        int[] weights = [.. Enumerable.Range(0, 200).Select(_ => random.Next(1, 9))];
        await Task.WhenAll(weights.Select(async weight =>
        {
            using CpuGate.Lease lease = await gate.AcquireAsync(weight, CancellationToken.None);
            int now = Interlocked.Add(ref inUse, lease.Weight);
            int seen;
            do
            {
                seen = Volatile.Read(ref peak);
            }
            while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);

            await Task.Yield();
            Interlocked.Add(ref inUse, -lease.Weight);
        }));

        Assert.InRange(peak, 1, 6);
        Assert.Equal(0, gate.InUse);
        Assert.Equal(8, CpuGate.CapacityFor(PerformanceLevel.Balanced, 16));
        Assert.Equal(1, CpuGate.CapacityFor(PerformanceLevel.Low, 2));
        Assert.Equal(16, CpuGate.CapacityFor(PerformanceLevel.High, 16));
    }

    [Fact]
    public async Task CpuGateWaitersCanBeCancelled()
    {
        CpuGate gate = new(capacity: 1);
        using CpuGate.Lease held = await gate.AcquireAsync(1, CancellationToken.None);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AcquireAsync(1, cancel.Token));
    }

    [Fact]
    public void IoGatesUseTheDefaultReadersPerClass()
    {
        using IoGates gates = new(new ScanSettings().ReadersFor);
        Assert.Equal(1, gates.LimitFor(new VolumeInfo("hdd", StorageClass.Hdd)));
        Assert.Equal(4, gates.LimitFor(new VolumeInfo("ssd", StorageClass.Ssd)));
        Assert.Equal(2, gates.LimitFor(new VolumeInfo("nas", StorageClass.Network)));
        Assert.Equal(1, gates.LimitFor(new VolumeInfo("usb", StorageClass.Removable)));
        Assert.Equal(2, gates.LimitFor(new VolumeInfo("?", StorageClass.Unknown)));
        Assert.Same(gates.For(new VolumeInfo("SSD", StorageClass.Ssd)), gates.For(new VolumeInfo("ssd", StorageClass.Ssd)));
    }

    [Fact]
    [Trait("Category", "System")]
    public void TheSystemDriveHasAKnownStorageClass()
    {
        VolumeInfo volume = new WindowsVolumeResolver().Resolve(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        // A CI runner's virtual disk answers no seek-penalty query, so its class is honestly Unknown there.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            Assert.NotEqual(StorageClass.Unknown, volume.Class);
        }

        Assert.StartsWith(@"\\?\Volume{", volume.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StorageClass.Network, new WindowsVolumeResolver().Resolve(@"\\server\share\photos").Class);
    }

    [Fact]
    public async Task IntegrityReaderMatchesXxh3AndMapsZeroBlocks()
    {
        using TempDirectory temp = new("integrity");
        byte[] data = new byte[(3 * 1024 * 1024) + 1234];
        new Random(1).NextBytes(data);
        Array.Clear(data, 4096 * 10, 4096 * 20);
        string path = Path.Combine(temp.Path, "x.bin");
        File.WriteAllBytes(path, data);

        IntegrityResult result = await IntegrityReader.ReadAsync(path, WindowsFileSystem.Instance);

        Assert.Equal(IntegrityOutcome.Ok, result.Outcome);
        Assert.Equal(XxHash128.Hash(data), result.Hash);
        Assert.Equal(20, result.Zeros.ZeroBlocks);
        Assert.Equal(20, result.Zeros.LongestRunBlocks);
        Assert.Equal(4096 * 10, result.Zeros.LongestRunOffset);
        Assert.Equal([4096L * 10], result.Zeros.RunOffsets);
        Assert.NotNull(result.FileId);

        // Published XXH3-128 vector for empty input.
        Assert.Equal("99aa06d3014798d86001c324468d497f", Convert.ToHexStringLower(XxHash128.Hash([])));
        File.WriteAllBytes(path, []);
        IntegrityResult empty = await IntegrityReader.ReadAsync(path, WindowsFileSystem.Instance);
        Assert.Equal("99aa06d3014798d86001c324468d497f", Convert.ToHexStringLower(empty.Hash!));
    }

    [Theory]
    [InlineData(23, IntegrityOutcome.Unreadable)]
    [InlineData(1117, IntegrityOutcome.Unreadable)]
    [InlineData(1392, IntegrityOutcome.Unreadable)]
    [InlineData(32, IntegrityOutcome.Locked)]
    [InlineData(33, IntegrityOutcome.Locked)]
    [InlineData(5, IntegrityOutcome.AccessDenied)]
    [InlineData(21, IntegrityOutcome.DeviceGone)]
    [InlineData(1167, IntegrityOutcome.DeviceGone)]
    public async Task ReadErrorsMapToOutcomes(int win32, IntegrityOutcome expected)
    {
        FaultingFileSystem fileSystem = new(size: 200_000, failAt: 100_000, win32);

        IntegrityResult result = await IntegrityReader.ReadAsync(@"X:\fake.jpg", fileSystem);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(win32, result.Win32Error);
        Assert.Null(result.Hash);
        if (expected == IntegrityOutcome.Unreadable)
        {
            Assert.Equal(2, fileSystem.Opens);
            Assert.InRange(result.ErrorOffset!.Value, 0, 100_000);
        }
    }

    [Fact]
    public async Task AFileThatChangesDuringTheReadIsInProgress()
    {
        FaultingFileSystem fileSystem = new(size: 50_000, failAt: -1, win32: 0) { GrowAfterOpen = true };
        IntegrityResult result = await IntegrityReader.ReadAsync(@"X:\growing.mp4", fileSystem);
        Assert.Equal(IntegrityOutcome.ChangedDuringRead, result.Outcome);
        Assert.Null(result.Hash);
    }

    [Fact]
    public void ChangeClassifierFollowsTheDecisionTable()
    {
        byte[] hash = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] other = [.. hash.Select(b => (byte)(b + 1))];
        byte[] stream = [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9];
        long mtime = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc).Ticks;
        FileRecord baseline = new() { Name = "a", Ext = "flac", Hash = hash, HashSize = 100, HashMtimeUtc = mtime, StreamHash = stream };

        Assert.Equal(ChangeKind.NoBaseline, ChangeClassifier.Classify(baseline with { Hash = null }, 100, mtime, hash, null, false));
        Assert.Equal(ChangeKind.Verified, ChangeClassifier.Classify(baseline, 100, mtime + TimeSpan.TicksPerSecond, hash, null, false));
        Assert.Equal(ChangeKind.Rot, ChangeClassifier.Classify(baseline, 100, mtime, other, [.. stream.Select(b => (byte)0)], false));
        Assert.Equal(ChangeKind.Rot, ChangeClassifier.Classify(baseline, 100, mtime, other, null, false));
        Assert.Equal(ChangeKind.MetaDrift, ChangeClassifier.Classify(baseline, 100, mtime, other, stream, false));
        Assert.Equal(ChangeKind.Modified, ChangeClassifier.Classify(baseline, 101, mtime, other, null, false));
        Assert.Equal(ChangeKind.Modified, ChangeClassifier.Classify(baseline, 100, mtime + TimeSpan.FromSeconds(3).Ticks, hash, null, false));

        // FAT and exFAT: an exact one-hour daylight-saving shift with the same size is unchanged.
        Assert.True(ChangeClassifier.StatSame(100, mtime, 100, mtime + TimeSpan.FromHours(1).Ticks, fatLikeVolume: true));
        Assert.False(ChangeClassifier.StatSame(100, mtime, 100, mtime + TimeSpan.FromHours(1).Ticks, fatLikeVolume: false));
        Assert.False(ChangeClassifier.StatSame(100, mtime, 101, mtime, fatLikeVolume: true));
    }

    [Fact]
    public void ScrubTakesTheOldestFirstWithinTheBudgetAndReadsInDiskOrder()
    {
        DateTimeOffset now = Clock.UtcNow;
        long Day(int ago) => (now - TimeSpan.FromDays(ago)).UtcTicks;
        ScrubCandidate[] candidates =
        [
            new(1, "D", "b", "1.jpg", Day(200), 1000),
            new(2, "C", "z", "2.jpg", Day(300), 1000),
            new(3, "C", "a", "3.jpg", Day(150), 1000),
            new(4, "D", "a", "4.jpg", Day(10), 1000),
            new(5, "C", "m", "5.jpg", Day(100), 1000),
        ];

        IReadOnlyList<ScrubCandidate> all = ScrubPolicy.Select(candidates, now, TimeSpan.FromDays(90), null, 0);
        Assert.Equal([3L, 5, 2, 1], all.Select(c => c.FileId));

        IReadOnlyList<ScrubCandidate> budgeted = ScrubPolicy.Select(candidates, now, TimeSpan.FromDays(90), TimeSpan.FromSeconds(2), bytesPerSecond: 1000);
        Assert.Equal([2L, 1], budgeted.Select(c => c.FileId));
    }

    [Theory]
    [InlineData("empty.flac", 0, 0, 0, "GEN-EMPTY", Severity.Critical)]
    [InlineData("zeros.flac", 100, 100, 100, "GEN-ZEROFILL", Severity.Critical)]
    [InlineData("mostly.flac", 100, 60, 60, "GEN-ZEROFILL", Severity.Critical)]
    [InlineData("run.flac", 100, 16, 16, "GEN-ZEROFILL", Severity.Low)]
    [InlineData("run.wav", 100, 16, 16, null, Severity.None)]
    [InlineData("clean.flac", 100, 3, 3, null, Severity.None)]
    public void ZeroFillAndEmptyRules(string name, long blocks, long zeroBlocks, long longestRun, string? rule, Severity severity)
    {
        IntegrityArtifact artifact = new(IntegrityOutcome.Ok, blocks * 4096, zeroBlocks, blocks, longestRun, 8192, longestRun >= 16 ? [8192L] : [], null, null);
        FindingSink sink = new();
        DetectorInput input = new(@"C:\m\" + name, MediaClassifier.Extension(name), MediaKind.Audio, ScanDepth.Quick, RootRole.Library, null, new Dictionary<Type, IArtifact> { [typeof(IntegrityArtifact)] = artifact }, RuleSettings.Defaults);

        new IntegrityDetector().Evaluate(input, sink);

        if (rule is null)
        {
            Assert.Empty(sink.Findings);
        }
        else
        {
            Finding finding = Assert.Single(sink.Findings);
            Assert.Equal(rule, finding.RuleId);
            Assert.Equal(severity, finding.Severity);
        }
    }

    [Fact]
    public void UnreadableIsCriticalWithCodeAndOffset()
    {
        IntegrityArtifact artifact = new(IntegrityOutcome.Unreadable, 1000, 0, 0, 0, 0, [], 23, 4096);
        FindingSink sink = new();
        new IntegrityDetector().Evaluate(new DetectorInput(@"C:\a.jpg", "jpg", MediaKind.Photo, ScanDepth.Quick, RootRole.Library, null, new Dictionary<Type, IArtifact> { [typeof(IntegrityArtifact)] = artifact }, RuleSettings.Defaults), sink);
        Finding finding = Assert.Single(sink.Findings);
        Assert.Equal("GEN-UNREADABLE", finding.RuleId);
        Assert.Equal(23, finding.Evidence["win32Code"]);
    }

    [Theory]
    [InlineData(@"C:\Photos\2026\a.jpg", null)]
    [InlineData(@"C:\Photos\aux\a.jpg", "reservedName")]
    [InlineData(@"C:\Photos\AUX.txt\a.jpg", "reservedName")]
    [InlineData(@"C:\Photos\com1\a.jpg", "reservedName")]
    [InlineData(@"C:\Photos\holiday.\a.jpg", "trailingDotOrSpace")]
    [InlineData(@"C:\Photos\holiday \a.jpg", "trailingDotOrSpace")]
    [InlineData(@"C:\Photos\auxiliary\a.jpg", null)]
    public void PathChecks(string path, string? expected)
    {
        Assert.Equal(expected, PathDetector.Check(path).Condition);
    }

    [Fact]
    public void ALongPathIsFlagged()
    {
        string path = @"C:\" + new string('p', 290) + @"\a.jpg";
        Assert.Equal("tooLong", PathDetector.Check(path).Condition);
        FindingSink sink = new();
        new PathDetector().Evaluate(new DetectorInput(path, "jpg", MediaKind.Photo, ScanDepth.Quick, RootRole.Library, null, new Dictionary<Type, IArtifact>(), RuleSettings.Defaults), sink);
        Assert.Equal(Severity.Low, Assert.Single(sink.Findings).Severity);
    }

    private static string Write(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static void RunCmd(params string[] args)
    {
        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("/c");
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }

    private static void DenyList(string directory)
    {
        DirectoryInfo info = new(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static void Allow(string directory)
    {
        DirectoryInfo info = new(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.RemoveAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    /// <summary>A file system whose reads fail at a given offset with a Win32 error, or whose file grows.</summary>
    private sealed class FaultingFileSystem(long size, long failAt, int win32) : IMediaFileSystem
    {
        private int _opens;

        public int Opens => Volatile.Read(ref _opens);

        public bool GrowAfterOpen { get; init; }

        public IEnumerable<FsEntry> EnumerateDirectory(string path) => [];

        public bool DirectoryExists(string path) => true;

        public bool FileExists(string path) => true;

        public FileStat? Stat(string path) => new FileStat(Opens > 0 && GrowAfterOpen ? size + 10 : size, 1, FileAttributes.Normal, 77);

        public Stream OpenRead(string path, int bufferSize = 4096, bool sequential = true)
        {
            Interlocked.Increment(ref _opens);
            if (failAt == 0)
            {
                throw new IOException("open failed", unchecked((int)0x80070000) | win32);
            }

            return new FaultingStream(size, failAt, win32);
        }
    }

    private sealed class FaultingStream(long size, long failAt, int win32) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => size;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAt >= 0 && _position + count > failAt)
            {
                if (_position < failAt)
                {
                    int partial = (int)(failAt - _position);
                    _position += partial;
                    return partial;
                }

                throw new IOException("read failed", unchecked((int)0x80070000) | win32);
            }

            int n = (int)Math.Min(count, size - _position);
            Array.Fill(buffer, (byte)7, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
