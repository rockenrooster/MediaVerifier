using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>
/// The scan engine (subplan 05): enumerate, reconcile, then integrity, analysis, evaluation and
/// persistence per file, with per-volume I/O gates, a weighted CPU gate, bounded queues, pause, cancel,
/// budgets, checkpoints and a polled status snapshot. One run at a time.
/// </summary>
public sealed class ScanEngine : IDisposable
{
    public const long ReadOnceLimit = 256L * 1024 * 1024;
    /// <summary>Meta key of the per-storage-class timing averages (pre-scan estimates read them).</summary>
    public const string EstimatesKey = "estimates.v1";

    private readonly EngineServices _services;
    private readonly Lock _gate = new();
    private Run? _current;

    public ScanEngine(EngineServices services, string engineMutexName = NamedMutexLock.EngineName)
    {
        _services = services;
        EngineMutexName = engineMutexName;
    }

    public string EngineMutexName { get; }

    /// <summary>Raised on a pool thread when a run ends, whatever the outcome.</summary>
    public event Action<RunSummary>? RunCompleted;

    public EngineStatus Status
    {
        get
        {
            Run? run = Volatile.Read(ref _current);
            return run?.Snapshot() ?? EngineStatus.Idle;
        }
    }

    public bool IsRunning => Volatile.Read(ref _current) is not null;

    /// <summary>Completes when the current run ends (immediately when idle).</summary>
    public Task WaitForIdleAsync() => Volatile.Read(ref _current)?.Completion ?? Task.CompletedTask;

    /// <summary>
    /// Starts a run and returns its id once the run row exists; the run continues in the background.
    /// Throws <see cref="InvalidOperationException"/> when a run is active here or the engine mutex is held
    /// by another process.
    /// </summary>
    public async Task<long> StartAsync(ScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NamedMutexLock? engineLock = NamedMutexLock.TryAcquire(EngineMutexName, TimeSpan.Zero)
            ?? throw new InvalidOperationException("Another MediaVerifier process is scanning.");
        Run run;
        lock (_gate)
        {
            if (_current is not null)
            {
                engineLock.Dispose();
                throw new InvalidOperationException("A scan is already running.");
            }

            run = new Run(this, request, engineLock);
            _current = run;
        }

        try
        {
            await run.BeginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _current = null;
            }

            engineLock.Dispose();
            throw;
        }

        run.Start();
        return run.RunId;
    }

    public void Pause() => Volatile.Read(ref _current)?.Pause();

    public void Resume() => Volatile.Read(ref _current)?.Resume();

    public void Cancel() => Volatile.Read(ref _current)?.Cancel();

    /// <summary>
    /// Re-evaluates stored artifacts after a rule setting changed (subplan 05: no file I/O). Only rules in
    /// <paramref name="ruleIds"/> are judged; other findings are untouched. Returns what changed.
    /// </summary>
    public async Task<ReevaluationResult> ReevaluateAsync(IReadOnlyCollection<string> ruleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        RuleSettings settings = _services.RuleSettings();
        HashSet<MediaKind> kinds = [];
        foreach (string ruleId in ruleIds)
        {
            RuleDefinition rule = RuleCatalog.Get(ruleId);
            foreach (MediaKind kind in new[] { MediaKind.Photo, MediaKind.Video, MediaKind.Audio })
            {
                if (rule.AppliesTo.Includes(kind))
                {
                    kinds.Add(kind);
                }
            }
        }

        long[] fileIds = _services.Database.Queries.FileIds(new FileFilter { Kinds = kinds, States = [FileState.Ok, FileState.Issues] });
        Dictionary<long, Dictionary<string, Severity>> before = OpenSeverities(fileIds, ruleIds);
        Dictionary<long, RootRecord> roots = _services.Database.Roots.List().ToDictionary(r => r.Id);
        int changed = 0;
        int appeared = 0;
        int gone = 0;
        foreach (ArraySegment<long> chunk in SqlChunks(fileIds, 200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (FileRecord row in _services.Database.Files.GetMany(chunk.ToArray()))
            {
                if (!roots.TryGetValue(row.RootId, out RootRecord? root))
                {
                    continue;
                }

                Dictionary<ArtifactKind, (int, string)> stored = _services.Database.Artifacts.ForFile(row.Id);
                using FileContext context = new(_services, root, row.RelDir ?? "", row.Name, row.MediaKind, new FileStat(row.Size, row.MtimeUtc, FileAttributes.Normal, row.NtfsFileId), row, row.ScannedDepth, 0, reevaluateOnly: true, stored, cancellationToken);
                Evaluation evaluation = await Evaluator.EvaluateAsync(context, _services.Detectors, settings, onlyRules: ruleIds).ConfigureAwait(false);
                await _services.Database.Writer.ExecuteAsync(c => FindingStore.ReplaceForFile(c, row.Id, row.LastSeenRun, evaluation.EvaluatedRules, evaluation.Findings, contentChanged: false), ChangeFlags.Findings | ChangeFlags.Files, cancellationToken).ConfigureAwait(false);
                Dictionary<string, Severity> was = before.GetValueOrDefault(row.Id) ?? [];
                foreach (Finding finding in evaluation.Findings)
                {
                    if (!was.TryGetValue(finding.RuleId, out Severity previous))
                    {
                        appeared++;
                    }
                    else if (previous != finding.Severity)
                    {
                        changed++;
                    }
                }

                gone += was.Keys.Count(rule => evaluation.EvaluatedRules.Contains(rule) && evaluation.Findings.All(f => f.RuleId != rule));
            }
        }

        return new ReevaluationResult(changed, appeared, gone);
    }

    public void Dispose()
    {
        Run? run = Volatile.Read(ref _current);
        if (run is not null)
        {
            run.Cancel();
            try
            {
                run.Completion.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
            }
        }
    }

    internal static IEnumerable<ArraySegment<long>> SqlChunks(long[] ids, int size)
    {
        for (int offset = 0; offset < ids.Length; offset += size)
        {
            yield return new ArraySegment<long>(ids, offset, Math.Min(size, ids.Length - offset));
        }
    }

    private Dictionary<long, Dictionary<string, Severity>> OpenSeverities(long[] fileIds, IReadOnlyCollection<string> ruleIds)
    {
        Dictionary<long, Dictionary<string, Severity>> map = [];
        long[] findingIds = _services.Database.Queries.FindingIds(new FindingFilter { RuleIds = [.. ruleIds] });
        foreach (FindingRow row in _services.Database.Queries.FindingRows(findingIds))
        {
            if (!map.TryGetValue(row.FileId, out Dictionary<string, Severity>? rules))
            {
                rules = new Dictionary<string, Severity>(StringComparer.Ordinal);
                map[row.FileId] = rules;
            }

            rules[row.RuleId] = row.Severity;
        }

        return map;
    }

    private void Finished(Run run, RunSummary summary)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, run))
            {
                _current = null;
            }
        }

        try
        {
            RunCompleted?.Invoke(summary);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("RunCompleted handler", ex);
        }
    }

    /// <summary>One run: its state, queues, workers and bookkeeping.</summary>
    private sealed class Run
    {
        private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(10);

        private readonly ScanEngine _engine;
        private readonly EngineServices _s;
        private readonly ScanRequest _request;
        private readonly NamedMutexLock _engineLock;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly WorkQueue _queue = new(capacity: 4096);
        private readonly ConcurrentDictionary<long, InFlight> _inFlight = new();
        private readonly ConcurrentBag<RunAlert> _alerts = [];
        private readonly ConcurrentQueue<WorkItem> _retry = new();
        private readonly ConcurrentDictionary<long, RootRun> _roots = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int[] _newBySeverity = new int[5];
        private readonly ConcurrentDictionary<MediaKind, long> _donePerKind = new();
        private readonly ConcurrentDictionary<MediaKind, long> _queuedPerKind = new();
        private readonly ScanSettings _settings;
        private readonly RuleSettings _rules;
        private readonly CpuGate _cpu;
        private readonly IoGates _io;
        private readonly Estimator _estimator;
        private TaskCompletionSource _resume = NewResumeSignal(completed: true);
        private volatile bool _paused;
        private volatile bool _budgetReached;
        private volatile bool _enumerating = true;
        private volatile bool _finishing;
        private long _seen;
        private long _queued;
        private long _done;
        private long _skipped;
        private long _failed;
        private long _analysed;
        private long _verified;
        private long _bytes;
        private long _queuedBytes;
        private long _newFindings;
        private long _resolvedFindings;
        private long _errors;
        private long _readErrors;
        private long _scrubTicks;
        private long _runStartUtc;

        public Run(ScanEngine engine, ScanRequest request, NamedMutexLock engineLock)
        {
            _engine = engine;
            _s = engine._services;
            _request = request;
            _engineLock = engineLock;
            _settings = _s.Settings();
            _rules = _s.RuleSettings();
            _cpu = new CpuGate(CpuGate.CapacityFor(_settings.Speed, Environment.ProcessorCount));
            _io = new IoGates(_settings.ReadersFor);
            Dictionary<StorageClass, EstimateAverages> averages = Estimator.Deserialize(_s.Database.Meta.Get(EstimatesKey));
            _estimator = new Estimator(averages.Values.FirstOrDefault());
        }

        public long RunId { get; private set; }

        public Task Completion => _completion.Task;

        public async Task BeginAsync(CancellationToken cancellationToken)
        {
            _runStartUtc = _s.Clock.UtcNow.UtcTicks;
            string scope = JsonSerializer.Serialize(_request.Scopes.Select(s => new Dictionary<string, object?> { ["rootId"] = s.RootId, ["dir"] = s.RelativeDirectory, ["files"] = s.FileIds?.Count }).ToList());
            RunId = await _s.Database.Runs.BeginAsync(_request.Trigger, _request.Depth, scope, _request.PlanId, cancellationToken).ConfigureAwait(false);
        }

        public void Start() => _ = Task.Run(ExecuteAsync);

        public void Pause()
        {
            if (_paused || _finishing)
            {
                return;
            }

            _paused = true;
            Interlocked.Exchange(ref _resume, NewResumeSignal(completed: false));
            _ = Task.Run(async () =>
            {
                // In-flight files get 10 seconds to finish, then are cancelled and queued again.
                Stopwatch wait = Stopwatch.StartNew();
                while (!_inFlight.IsEmpty && wait.Elapsed < PauseGrace && _paused)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                if (_paused)
                {
                    foreach (InFlight item in _inFlight.Values)
                    {
                        item.PauseCancel.Cancel();
                    }
                }
            });
        }

        public void Resume()
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            _resume.TrySetResult();
        }

        public void Cancel()
        {
            _paused = false;
            _resume.TrySetResult();
            _cancel.Cancel();
        }

        public EngineStatus Snapshot()
        {
            EnginePhase phase = _finishing ? EnginePhase.Finishing
                : _paused ? (_inFlight.IsEmpty ? EnginePhase.Paused : EnginePhase.Pausing)
                : _enumerating && Interlocked.Read(ref _done) == 0 && _inFlight.IsEmpty ? EnginePhase.Enumerating
                : EnginePhase.Scanning;
            long done = Interlocked.Read(ref _done);
            Dictionary<MediaKind, long> remainingFiles = [];
            foreach ((MediaKind kind, long queued) in _queuedPerKind)
            {
                remainingFiles[kind] = Math.Max(0, queued - _donePerKind.GetValueOrDefault(kind));
            }

            long remainingBytes = Math.Max(0, Interlocked.Read(ref _queuedBytes) - Interlocked.Read(ref _bytes));
            TimeSpan? remaining = _enumerating ? null : _estimator.Remaining(remainingBytes, remainingFiles, _request.Depth, Math.Max(1, _cpu.Capacity), _clock.Elapsed, done);
            return new EngineStatus(
                phase,
                RunId,
                _request.Depth,
                Interlocked.Read(ref _seen),
                Interlocked.Read(ref _queued) - done,
                done,
                Interlocked.Read(ref _skipped),
                Interlocked.Read(ref _failed),
                Interlocked.Read(ref _bytes),
                _estimator.BytesPerSecond,
                new Dictionary<MediaKind, long>(_donePerKind),
                new SeverityCounts(Volatile.Read(ref _newBySeverity[4]), Volatile.Read(ref _newBySeverity[3]), Volatile.Read(ref _newBySeverity[2]), Volatile.Read(ref _newBySeverity[1])),
                [.. _inFlight.Values.Take(8).Select(i => new InFlightItem(i.Item.FullPath, i.Stage, i.Fraction, i.Item.Kind))],
                _clock.Elapsed,
                remaining,
                remaining is null,
                _paused ? _inFlight.Count : 0,
                _request.Trigger);
        }

        private static TaskCompletionSource NewResumeSignal(bool completed)
        {
            TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (completed)
            {
                signal.TrySetResult();
            }

            return signal;
        }

        private async Task ExecuteAsync()
        {
            RunStatus status = RunStatus.Completed;
            string? error = null;
            CancellationToken token = _cancel.Token;
            using Timer checkpoint = new(_ => Checkpoint(), null, CheckpointInterval, CheckpointInterval);
            try
            {
                _s.Paths.EnsureCreated();
                int workers = Math.Max(2, _cpu.Capacity + 2);
                Task[] workerTasks = [.. Enumerable.Range(0, workers).Select(_ => Task.Run(() => WorkerAsync(token)))];
                Task producer = Task.Run(() => ProduceAsync(token));
                try
                {
                    await producer.ConfigureAwait(false);
                }
                finally
                {
                    _enumerating = false;
                    _queue.Complete();
                }

                await Task.WhenAll(workerTasks).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // Locked and in-progress files get one more try at the end of the run (subplan 05).
                while (_retry.TryDequeue(out WorkItem? retry) && !token.IsCancellationRequested && !_budgetReached)
                {
                    await ProcessWithGuardAsync(retry with { Previous = _s.Database.Files.Get(retry.FileId) ?? retry.Previous }, token, isRetry: true).ConfigureAwait(false);
                }

                _finishing = true;
                await FinishRootsAsync(token).ConfigureAwait(false);
                status = _budgetReached ? RunStatus.BudgetReached : RunStatus.Completed;
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                status = RunStatus.Cancelled;
            }
            catch (Exception ex)
            {
                status = RunStatus.Failed;
                error = ex.Message;
                ErrorLog.Write("Scan run", ex);
            }

            _finishing = true;
            RunSummary summary = await EndAsync(status, error).ConfigureAwait(false);
            _io.Dispose();
            _engineLock.Dispose();
            _completion.TrySetResult();
            _engine.Finished(this, summary);
        }

        private async Task<RunSummary> EndAsync(RunStatus status, string? error)
        {
            RunCounters counters = Counters();
            List<RunAlert> alerts = [.. _alerts];
            if (Interlocked.Read(ref _readErrors) > 0)
            {
                alerts.Add(new RunAlert("RUN-READ-ERRORS", Severity.Critical, null, string.Create(CultureInfo.InvariantCulture, $"{Interlocked.Read(ref _readErrors)} files had read errors")));
            }

            SeverityCounts newFindings = new(_newBySeverity[4], _newBySeverity[3], _newBySeverity[2], _newBySeverity[1]);
            string alertsJson = JsonSerializer.Serialize(alerts.Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["severity"] = (int)a.Severity, ["rootId"] = a.RootId, ["detail"] = a.Detail }).ToList());
            string summaryJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["newCritical"] = newFindings.Critical,
                ["newHigh"] = newFindings.High,
                ["newMedium"] = newFindings.Medium,
                ["newLow"] = newFindings.Low,
                ["seconds"] = Math.Round(_clock.Elapsed.TotalSeconds, 1),
                ["error"] = error,
            });
            try
            {
                await _s.Database.Runs.EndAsync(RunId, status, counters, alertsJson, summaryJson).ConfigureAwait(false);
                await _s.Database.Meta.SetAsync(EstimatesKey, Estimator.Serialize(new Dictionary<StorageClass, EstimateAverages> { [StorageClass.Unknown] = _estimator.Snapshot() })).ConfigureAwait(false);
                await _s.Database.CheckpointAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("End of run", ex);
            }

            AppLog.Shared?.Info(string.Create(CultureInfo.InvariantCulture, $"Run {RunId} {status}: {counters.FilesSeen} seen, {counters.FilesAnalysed} analysed, {counters.FilesVerified} verified, {counters.NewFindings} new findings, {counters.Errors} errors in {_clock.Elapsed.TotalSeconds:F1} s"));
            return new RunSummary(RunId, status, _request.Trigger, _request.PlanId, counters, newFindings, alerts, _clock.Elapsed, error);
        }

        private RunCounters Counters() => new(
            Interlocked.Read(ref _seen),
            Interlocked.Read(ref _analysed),
            Interlocked.Read(ref _verified),
            Interlocked.Read(ref _bytes),
            Interlocked.Read(ref _newFindings),
            Interlocked.Read(ref _resolvedFindings),
            Interlocked.Read(ref _errors));

        private void Checkpoint()
        {
            try
            {
                _ = _s.Database.Runs.CheckpointAsync(RunId, Counters()).AsTask();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        // ----- Producer: enumerate and reconcile -----
        private async Task ProduceAsync(CancellationToken token)
        {
            Dictionary<long, RootRecord> roots = _s.Database.Roots.List().Where(r => r.Enabled).ToDictionary(r => r.Id);
            List<(RootRecord Root, ScanScope Scope)> work = [];
            foreach (ScanScope scope in _request.Scopes)
            {
                if (roots.TryGetValue(scope.RootId, out RootRecord? root))
                {
                    work.Add((root, scope));
                }
            }

            // Roots on the same volume are enumerated one after another; different volumes in parallel.
            IEnumerable<IGrouping<string, (RootRecord Root, ScanScope Scope)>> byVolume = work.GroupBy(w => _s.Volumes.Resolve(w.Root.Path).Key, StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(byVolume.Select(group => Task.Run(async () =>
            {
                foreach ((RootRecord root, ScanScope scope) in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (_budgetReached)
                    {
                        break;
                    }

                    try
                    {
                        await ProduceRootAsync(root, scope, token).ConfigureAwait(false);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (RootRemoved(ex, root.Id))
                    {
                        // Stop watching during the scan: the folder's rows are gone, and its part of the run with them.
                        AppLog.Shared?.Info(string.Create(CultureInfo.InvariantCulture, $"Run {RunId}: folder {root.Id} was removed while it was listed; its files are skipped"));
                    }
                }
            }, token))).ConfigureAwait(false);
        }

        /// <summary>
        /// A foreign-key failure because the folder stopped being watched while it was scanned (Library → Stop watching):
        /// its directory, file and finding rows went with it.
        /// </summary>
        private bool RootRemoved(Microsoft.Data.Sqlite.SqliteException ex, long rootId) =>
            ex.SqliteErrorCode == 19 && _s.Database.Roots.List().All(r => r.Id != rootId);

        private async Task ProduceRootAsync(RootRecord root, ScanScope scope, CancellationToken token)
        {
            VolumeInfo volume = _s.Volumes.Resolve(root.Path);
            RootRun state = _roots.GetOrAdd(root.Id, _ => new RootRun(root, scope));
            if (!_s.FileSystem.DirectoryExists(root.Path))
            {
                state.Offline = true;
                _alerts.Add(new RunAlert("RUN-ROOT-OFFLINE", Severity.Medium, root.Id, root.Path));
                return;
            }

            state.PresentBefore = _s.Database.Files.CountPresent(root.Id);
            await _s.Database.Roots.MarkOnlineAsync(root.Id, _s.Clock.UtcNow, volume.Key, volume.Class, token).ConfigureAwait(false);
            bool fatLike = FileSystemFormat.IsFatLike(root.Path);
            ScanOptions options = _request.Options ?? new ScanOptions();
            Reconciler reconciler = new(
                _request.Depth,
                _s.Detectors.DetectorSet,
                _s.Clock.UtcNow,
                TimeSpan.FromDays(_settings.ScrubPeriodDays),
                options.VerifyEverything,
                options.VerifyOnly,
                _settings.VerifyOnAnalysis,
                root.Role,
                fatLike);
            TreeEnumerator enumerator = new(_s.FileSystem, new MediaClassifier(_settings.PhotoExtensions, _settings.VideoExtensions, _settings.AudioExtensions), new EnumerationRules
            {
                ExcludedDirectories = _settings.AlwaysSkip,
                IncludeGlobs = ParseGlobs(root.IncludePatternsJson),
                ExcludeGlobs = ParseGlobs(root.ExcludePatternsJson),
                ScanCloudPlaceholders = _settings.ScanCloudFiles,
                MaxFileSize = _settings.SkipFilesLargerThan,
            }, _s.Clock);

            HashSet<long>? fileFilter = scope.FileIds is { Count: > 0 } ids ? [.. ids] : null;
            bool holdNew = state.PresentBefore > 0;
            List<EnumeratedFile> heldNew = [];
            try
            {
                foreach (EnumeratedDirectory directory in enumerator.Enumerate(root.Path, scope.RelativeDirectory ?? "", token))
                {
                    token.ThrowIfCancellationRequested();
                    if (state.Offline)
                    {
                        return;
                    }

                    if (!directory.Listed)
                    {
                        if (directory.RelDir.Length == 0 && !_s.FileSystem.DirectoryExists(root.Path))
                        {
                            MarkOffline(state, "enumeration failed");
                            return;
                        }

                        state.Unlisted.Add(directory.RelDir);
                        Interlocked.Increment(ref _errors);
                        continue;
                    }

                    List<FileRecord> rows = _s.Database.Files.InDirectory(root.Id, directory.RelDir);
                    List<ReconcileDecision> decisions = reconciler.Reconcile(directory.Files, rows);
                    await HandleDirectoryAsync(root, volume, decisions, fileFilter, holdNew ? heldNew : null, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MarkOffline(state, ex.Message);
                return;
            }

            state.EnumeratedFully = true;
            if (heldNew.Count > 0)
            {
                await ReleaseHeldNewAsync(root, volume, heldNew, token).ConfigureAwait(false);
            }
        }

        private async Task HandleDirectoryAsync(RootRecord root, VolumeInfo volume, List<ReconcileDecision> decisions, HashSet<long>? fileFilter, List<EnumeratedFile>? holdNew, CancellationToken token)
        {
            List<long> seen = [];
            List<FileUpsert> inserts = [];
            List<ReconcileDecision> newDecisions = [];
            foreach (ReconcileDecision decision in decisions)
            {
                Interlocked.Increment(ref _seen);
                if (decision.Row is FileRecord row)
                {
                    seen.Add(row.Id);
                    if (fileFilter is not null && !fileFilter.Contains(row.Id))
                    {
                        continue;
                    }

                    if (decision.Kind == ReconcileKind.Skipped)
                    {
                        long skippedId = row.Id;
                        SkipReason reason = decision.File.Skip;
                        await _s.Database.Writer.PostAsync(c => FileIndex.MarkSkipped(c, skippedId, reason, RunId), ChangeFlags.Files, token).ConfigureAwait(false);
                        Interlocked.Increment(ref _skipped);
                        continue;
                    }

                    if (decision.Returned)
                    {
                        long returnedId = row.Id;
                        await _s.Database.Writer.PostAsync(c => FileEventStore.Add(c, returnedId, RunId, FileEventKind.Returned), ChangeFlags.Events, token).ConfigureAwait(false);
                    }

                    if (decision.HasWork)
                    {
                        await EnqueueAsync(MakeWork(root, volume, decision, row.Id), token).ConfigureAwait(false);
                    }
                }
                else if (fileFilter is null)
                {
                    if (holdNew is not null && decision.Kind == ReconcileKind.New)
                    {
                        holdNew.Add(decision.File);
                        continue;
                    }

                    inserts.Add(ToUpsert(root, decision.File));
                    newDecisions.Add(decision);
                }
            }

            if (seen.Count > 0)
            {
                long[] seenIds = [.. seen];
                await _s.Database.Writer.PostAsync(c => FileIndex.MarkSeen(c, seenIds, RunId), ChangeFlags.None, token).ConfigureAwait(false);
            }

            if (inserts.Count > 0)
            {
                await InsertAndQueueAsync(root, volume, inserts, newDecisions, token).ConfigureAwait(false);
            }
        }

        private async Task InsertAndQueueAsync(RootRecord root, VolumeInfo volume, List<FileUpsert> inserts, List<ReconcileDecision> decisions, CancellationToken token)
        {
            FileUpsertResult[] ids = await _s.Database.Files.UpsertAsync(inserts, token).ConfigureAwait(false);
            for (int i = 0; i < ids.Length; i++)
            {
                ReconcileDecision decision = decisions[i];
                long fileId = ids[i].FileId;
                if (decision.Kind == ReconcileKind.Skipped)
                {
                    SkipReason reason = decision.File.Skip;
                    await _s.Database.Writer.PostAsync(c => FileIndex.MarkSkipped(c, fileId, reason, RunId), ChangeFlags.Files, token).ConfigureAwait(false);
                    Interlocked.Increment(ref _skipped);
                    continue;
                }

                await _s.Database.Writer.PostAsync(c => FileEventStore.Add(c, fileId, RunId, FileEventKind.FirstSeen), ChangeFlags.Events, token).ConfigureAwait(false);
                await EnqueueAsync(MakeWork(root, volume, decision, fileId), token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// New files of a root that already had rows wait until its enumeration completes, then are matched
        /// against rows that went unseen (same NTFS file id, size and modified time): a match is a move.
        /// </summary>
        private async Task ReleaseHeldNewAsync(RootRecord root, VolumeInfo volume, List<EnumeratedFile> held, CancellationToken token)
        {
            List<FileRecord> unseen = _s.Database.Files.NotSeen(root.Id, RunId);
            Dictionary<long, FileRecord> byNtfs = [];
            foreach (FileRecord row in unseen)
            {
                if (row.NtfsFileId is long id && Within(row.RelDir ?? "", _roots[root.Id].Scope.RelativeDirectory ?? ""))
                {
                    byNtfs.TryAdd(id, row);
                }
            }

            List<FileUpsert> inserts = [];
            List<ReconcileDecision> decisions = [];
            foreach (EnumeratedFile file in held)
            {
                string fullPath = file.RelDir.Length == 0 ? Path.Combine(root.Path, file.Name) : Path.Combine(root.Path, file.RelDir, file.Name);
                if (byNtfs.Count > 0 && _s.FileSystem.Stat(fullPath) is FileStat stat && stat.FileId is long ntfs
                    && byNtfs.TryGetValue(ntfs, out FileRecord? moved) && moved.Size == file.Size
                    && ChangeClassifier.StatSame(moved.Size, moved.MtimeUtc, file.Size, file.MtimeUtcTicks, fatLikeVolume: false))
                {
                    byNtfs.Remove(ntfs);
                    string from = moved.RelDir ?? "";
                    long movedId = moved.Id;
                    string detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["from"] = from.Length == 0 ? moved.Name : from + "\\" + moved.Name });
                    await _s.Database.Writer.ExecuteAsync(
                        c =>
                        {
                            FileIndex.Move(c, movedId, root.Id, file.RelDir, file.Name);
                            FileIndex.MarkSeen(c, [movedId], RunId);
                            FileEventStore.Add(c, movedId, RunId, FileEventKind.Moved, detail);
                        },
                        ChangeFlags.Files | ChangeFlags.Events,
                        token).ConfigureAwait(false);
                    continue;
                }

                inserts.Add(ToUpsert(root, file));
                decisions.Add(new ReconcileDecision(file, null, ReconcileKind.New, false, true, !(_request.Options?.VerifyOnly ?? false) && root.Role == RootRole.Library, false, WorkPriority.NewOrModified));
                if (inserts.Count >= 1000)
                {
                    await InsertAndQueueAsync(root, volume, inserts, decisions, token).ConfigureAwait(false);
                    inserts = [];
                    decisions = [];
                }
            }

            if (inserts.Count > 0)
            {
                await InsertAndQueueAsync(root, volume, inserts, decisions, token).ConfigureAwait(false);
            }
        }

        private FileUpsert ToUpsert(RootRecord root, EnumeratedFile file) =>
            new(root.Id, file.RelDir, file.Name, file.Extension, file.Kind, file.Size, file.MtimeUtcTicks, null, RunId);

        private WorkItem MakeWork(RootRecord root, VolumeInfo volume, ReconcileDecision decision, long fileId) => new()
        {
            FileId = fileId,
            Root = root,
            Volume = volume,
            RelDir = decision.File.RelDir,
            Name = decision.File.Name,
            Kind = decision.Row?.MediaKind is MediaKind known && known != MediaKind.Unknown ? known : decision.File.Kind,
            Stat = new FileStat(decision.File.Size, decision.File.MtimeUtcTicks, decision.File.Attributes, null),
            Previous = decision.Row,
            Priority = decision.Priority,
            ReadContent = decision.ReadContent,
            Analyze = decision.Analyze,
            ReevaluateOnly = decision.ReevaluateOnly && !decision.Analyze,
            IsNew = decision.Kind == ReconcileKind.New,
            Modified = decision.Kind == ReconcileKind.Modified,
            Returned = decision.Returned,
            Depth = root.Role == RootRole.Backup ? ScanDepth.Quick : _request.Depth,
        };

        private async Task EnqueueAsync(WorkItem item, CancellationToken token)
        {
            Interlocked.Increment(ref _queued);
            _queuedPerKind.AddOrUpdate(item.Kind, 1, (_, n) => n + 1);
            if (item.ReadContent)
            {
                Interlocked.Add(ref _queuedBytes, item.Stat.Size);
            }

            await _queue.AddAsync(item, token).ConfigureAwait(false);
        }

        private void MarkOffline(RootRun state, string reason)
        {
            if (!state.Offline)
            {
                state.Offline = true;
                _alerts.Add(new RunAlert("RUN-ROOT-OFFLINE", Severity.Medium, state.Root.Id, reason));
            }
        }

        private static IReadOnlyList<string>? ParseGlobs(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool Within(string relDir, string scope) => FileIndex.Within(relDir, FileIndex.NormalizeRelDir(scope));

        // ----- Workers -----
        private async Task WorkerAsync(CancellationToken token)
        {
            while (true)
            {
                await _resume.Task.WaitAsync(token).ConfigureAwait(false);
                if (_budgetReached)
                {
                    _queue.Drain();
                    return;
                }

                WorkItem? item = await _queue.TakeAsync(token).ConfigureAwait(false);
                if (item is null)
                {
                    return;
                }

                if (_paused)
                {
                    await _queue.AddAsync(item, token).ConfigureAwait(false);
                    continue;
                }

                if (_request.Budget is TimeSpan budget && _clock.Elapsed >= budget)
                {
                    _budgetReached = true;
                    continue;
                }

                await ProcessWithGuardAsync(item, token, isRetry: false).ConfigureAwait(false);
            }
        }

        private async Task ProcessWithGuardAsync(WorkItem item, CancellationToken token, bool isRetry)
        {
            using CancellationTokenSource pause = new();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, pause.Token);
            InFlight entry = new(item, pause);
            _inFlight[item.FileId] = entry;
            try
            {
                await ProcessAsync(item, entry, linked.Token, isRetry).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pause.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // Paused: put it back to run again after Resume.
                await _queue.AddAsync(item, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (RootRemoved(ex, item.Root.Id))
            {
                // Its folder stopped being watched during the scan: the file's row went with it, so it is skipped. It
                // was counted as an error, with a stack trace in error.log for each remaining file.
                Interlocked.Increment(ref _skipped);
            }
            catch (Exception ex)
            {
                // A provider bug: the file is marked Error, the run goes on (subplan 05).
                Interlocked.Increment(ref _errors);
                Interlocked.Increment(ref _failed);
                ErrorLog.Write("Scanning " + item.FullPath, ex);
                long id = item.FileId;
                try
                {
                    await _s.Database.Writer.PostAsync(c => FileIndex.ApplyUpdate(c, new FileUpdate(id) { State = FileState.Error, LastScannedUtc = c.NowTicks }), ChangeFlags.Files, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                _inFlight.TryRemove(item.FileId, out _);
            }

            Interlocked.Increment(ref _done);
            _donePerKind.AddOrUpdate(item.Kind, 1, (_, n) => n + 1);
        }

        private async Task ProcessAsync(WorkItem item, InFlight entry, CancellationToken token, bool isRetry)
        {
            Stopwatch watch = Stopwatch.StartNew();
            RootRun? root = _roots.GetValueOrDefault(item.Root.Id);
            if (root?.Offline == true)
            {
                return;
            }

            bool analyse = item.Analyze || item.ReevaluateOnly;
            bool scrubOnly = item.ReadContent && !analyse && item.Priority == WorkPriority.VerificationDue;
            TimeSpan? scrubBudget = (_request.Options?.ScrubBudget) ?? (_request.Trigger == RunTrigger.Scheduled ? TimeSpan.FromMinutes(_settings.ScrubBudgetMinutes) : null);
            if (scrubOnly && !(_request.Options?.VerifyEverything ?? false) && scrubBudget is TimeSpan limit && TimeSpan.FromTicks(Interlocked.Read(ref _scrubTicks)) >= limit)
            {
                // The verification budget is spent; the next run continues from state.
                return;
            }

            IntegrityResult? integrity = null;
            ChangeKind change = ChangeKind.NoBaseline;
            CpuGate.Lease cpuLease = default;
            bool holdingCpu = false;
            SemaphoreSlim ioGate = _io.For(item.Volume);
            bool holdingIo = false;
            int weight = item.Kind == MediaKind.Video ? (item.Depth == ScanDepth.Deep ? 8 : 4) : 1;
            bool large = item.Stat.Size > ReadOnceLimit;
            FileContext? context = null;
            FileContext? deeper = null;
            try
            {
                if (item.ReadContent)
                {
                    entry.Stage = "Reading";
                    if (large && analyse)
                    {
                        // CPU first, then I/O: never hold the I/O gate while waiting for the CPU gate.
                        cpuLease = await _cpu.AcquireAsync(weight, token).ConfigureAwait(false);
                        holdingCpu = true;
                    }

                    await ioGate.WaitAsync(token).ConfigureAwait(false);
                    holdingIo = true;
                    Stopwatch read = Stopwatch.StartNew();
                    integrity = await IntegrityReader.ReadAsync(item.FullPath, _s.FileSystem, token).ConfigureAwait(false);
                    read.Stop();
                    Interlocked.Add(ref _bytes, integrity.BytesRead);
                    _estimator.ObserveRead(integrity.BytesRead, read.Elapsed);
                    if (scrubOnly)
                    {
                        Interlocked.Add(ref _scrubTicks, read.Elapsed.Ticks);
                    }

                    if (!large || !analyse)
                    {
                        ioGate.Release();
                        holdingIo = false;
                    }

                    switch (integrity.Outcome)
                    {
                        case IntegrityOutcome.DeviceGone:
                            if (root is not null)
                            {
                                MarkOffline(root, "the drive or share went away during the scan");
                            }

                            return;
                        case IntegrityOutcome.NotFound:
                            return;
                        case IntegrityOutcome.Locked:
                        case IntegrityOutcome.ChangedDuringRead:
                            if (!isRetry)
                            {
                                _retry.Enqueue(item);
                                return;
                            }

                            await SkipAsync(item, integrity.Outcome == IntegrityOutcome.Locked ? SkipReason.Locked : SkipReason.InProgress, token).ConfigureAwait(false);
                            return;
                        case IntegrityOutcome.AccessDenied:
                            await SkipAsync(item, SkipReason.AccessDenied, token).ConfigureAwait(false);
                            return;
                        case IntegrityOutcome.Unreadable:
                            Interlocked.Increment(ref _readErrors);
                            analyse = false;
                            break;
                    }

                    if (integrity.Succeeded)
                    {
                        change = ChangeClassifier.Classify(item.Previous, integrity.Size, integrity.LastWriteUtcTicks, integrity.Hash!, null, FileSystemFormat.IsFatLike(item.Root.Path));
                        Interlocked.Increment(ref _verified);
                    }
                }

                // Stored artifacts describe the old content: use them only when the content is the same.
                bool contentSame = !item.IsNew && !item.Modified && change is not (ChangeKind.Rot or ChangeKind.Modified or ChangeKind.MetaDrift);
                Dictionary<ArtifactKind, (int, string)>? stored = item.Previous is not null && contentSame ? _s.Database.Artifacts.ForFile(item.FileId) : null;

                // A stored artifact from an older provider means the file is analysed again (subplan 05).
                bool reevaluateOnly = item.ReevaluateOnly && !item.ReadContent;
                if (reevaluateOnly && stored is not null && _s.Providers.All.Any(p => stored.TryGetValue(p.Kind, out (int Version, string) kept) && kept.Version < p.Version))
                {
                    reevaluateOnly = false;
                }

                context = new FileContext(_s, item.Root, item.RelDir, item.Name, item.Kind, item.Stat, item.Previous, item.Depth, RunId, reevaluateOnly, stored, token);
                if (integrity is not null && integrity.Outcome is IntegrityOutcome.Ok or IntegrityOutcome.Unreadable)
                {
                    context.Put(IntegrityArtifact.From(integrity), _s.Providers.For(ArtifactKind.Integrity));
                }

                // A rot finding is described at Standard depth at least (subplan 06).
                ScanDepth depth = item.Depth;
                List<Finding> integrityFindings = [];
                List<string> integrityEvaluated = [];
                byte[]? currentStreamHash = null;
                if (integrity?.Succeeded == true && item.Previous?.Hash is not null && change is ChangeKind.Rot
                    && item.Previous.StreamHash is not null && item.Kind is MediaKind.Audio or MediaKind.Video
                    && _s.Providers.For(ArtifactKind.StreamHash) is IArtifactProvider streamProvider && streamProvider.Applies(context))
                {
                    entry.Stage = "Comparing streams";
                    if (await streamProvider.ProduceAsync(context).ConfigureAwait(false) is IStreamHashSource source)
                    {
                        currentStreamHash = source.StreamHash;
                        change = ChangeClassifier.Classify(item.Previous, integrity.Size, integrity.LastWriteUtcTicks, integrity.Hash!, currentStreamHash, FileSystemFormat.IsFatLike(item.Root.Path));
                    }
                }

                if (integrity?.Succeeded == true && item.Previous?.Hash is not null)
                {
                    integrityEvaluated.Add("INT-ROT");
                    integrityEvaluated.Add("INT-META-DRIFT");
                    if (change == ChangeKind.Rot)
                    {
                        integrityFindings.Add(RotFinding(item.Previous, integrity, currentStreamHash is not null));
                        if (item.Root.Role == RootRole.Library && !(_request.Options?.VerifyOnly ?? false))
                        {
                            depth = depth < ScanDepth.Standard ? ScanDepth.Standard : depth;
                            analyse = true;
                        }
                    }
                    else if (change == ChangeKind.MetaDrift)
                    {
                        integrityFindings.Add(new Finding("INT-META-DRIFT", Severity.Low, 1f, "Tags changed; the picture and sound are unchanged", new Dictionary<string, object> { ["baselineUtc"] = item.Previous.HashUtc ?? 0 }));
                    }
                }

                if (analyse || item.ReadContent)
                {
                    if (!holdingCpu)
                    {
                        cpuLease = await _cpu.AcquireAsync(analyse ? weight : 1, token).ConfigureAwait(false);
                        holdingCpu = true;
                    }

                    entry.Stage = analyse ? "Analysing" : "Checking";
                    FileContext evalContext = context;
                    if (depth != item.Depth)
                    {
                        deeper = new FileContext(_s, item.Root, item.RelDir, item.Name, item.Kind, item.Stat, item.Previous, depth, RunId, false, null, token);
                        if (context.Available.Values.OfType<IntegrityArtifact>().FirstOrDefault() is IntegrityArtifact integrityArtifact)
                        {
                            deeper.Put(integrityArtifact, _s.Providers.For(ArtifactKind.Integrity));
                        }

                        evalContext = deeper;
                    }

                    IReadOnlyCollection<string>? onlyRules = analyse ? null : IntegrityOnly;
                    Evaluation evaluation = await Evaluator.EvaluateAsync(evalContext, _s.Detectors, _rules, onlyRules, integrityEvaluated, integrityFindings).ConfigureAwait(false);
                    await PersistAsync(item, evalContext, integrity, change, evaluation, analyse, depth, currentStreamHash, token).ConfigureAwait(false);
                }

                if (analyse)
                {
                    Interlocked.Increment(ref _analysed);
                    _estimator.ObserveAnalysis(item.Kind, depth, watch.Elapsed);
                }
            }
            finally
            {
                deeper?.Dispose();
                context?.Dispose();
                if (holdingIo)
                {
                    ioGate.Release();
                }

                if (holdingCpu)
                {
                    cpuLease.Dispose();
                }
            }
        }

        private static readonly IReadOnlyCollection<string> IntegrityOnly = ["GEN-EMPTY", "GEN-ZEROFILL", "GEN-UNREADABLE", "INT-ROT", "INT-META-DRIFT"];

        private async Task PersistAsync(WorkItem item, FileContext context, IntegrityResult? integrity, ChangeKind change, Evaluation evaluation, bool analysed, ScanDepth depth, byte[]? currentStreamHash, CancellationToken token)
        {
            long now = _s.Clock.UtcNow.UtcTicks;
            MediaSummary? summary = null;
            MediaKind? probedKind = null;
            byte[]? streamHash = null;
            bool unsupported = false;
            foreach (IArtifact artifact in context.Available.Values)
            {
                if (artifact is IMediaSummarySource source)
                {
                    summary ??= source.Summary;
                    probedKind ??= source.ProbedKind;
                }

                if (artifact is IStreamHashSource hashSource && hashSource.StreamHash is { Length: > 0 } hash)
                {
                    streamHash ??= hash;
                }

                if (artifact is IUnsupportedSource { Unsupported: true })
                {
                    unsupported = true;
                }
            }

            bool newBaseline = integrity?.Succeeded == true && change is ChangeKind.NoBaseline or ChangeKind.Modified or ChangeKind.MetaDrift;
            Baseline? baseline = newBaseline ? new Baseline(integrity!.Hash!, integrity.Size, integrity.LastWriteUtcTicks, now) : null;
            byte[]? storeStreamHash = newBaseline ? (currentStreamHash ?? streamHash) : (item.Previous?.StreamHash is null ? streamHash : null);
            FileUpdate update = new(item.FileId)
            {
                State = unsupported ? FileState.Skipped : FileState.Ok,
                SkipReason = unsupported ? SkipReason.UnsupportedByFfmpeg : SkipReason.None,
                ScannedDepth = analysed ? (ScanDepth)Math.Max((int)(item.Previous?.ScannedDepth ?? ScanDepth.None), (int)depth) : null,
                DetectorSet = analysed ? _s.Detectors.DetectorSet : null,
                LastScannedUtc = analysed ? now : null,
                Size = item.Stat.Size,
                MtimeUtc = item.Stat.LastWriteUtcTicks,
                MediaKind = probedKind,
                Media = summary,
                Baseline = baseline,
                StreamHash = storeStreamHash,
                NtfsFileId = integrity?.FileId,
                VerifiedUtc = integrity?.Succeeded == true && change == ChangeKind.Verified ? now : null,
            };
            IReadOnlyList<(IArtifactProvider Provider, IArtifact Artifact)> produced = context.Produced;
            bool contentChanged = change is ChangeKind.Modified || item.Modified;
            FindingChange findings = await _s.Database.Writer.ExecuteAsync(
                c =>
                {
                    FileIndex.ApplyUpdate(c, update);
                    foreach ((IArtifactProvider provider, IArtifact artifact) in produced)
                    {
                        ArtifactStore.Put(c, item.FileId, provider.Kind, provider.Version, provider.Serialize(artifact));
                    }

                    FindingChange result = FindingStore.ReplaceForFile(c, item.FileId, RunId, evaluation.EvaluatedRules, evaluation.Findings, contentChanged);
                    switch (change)
                    {
                        case ChangeKind.NoBaseline when integrity?.Succeeded == true:
                            FileEventStore.Add(c, item.FileId, RunId, FileEventKind.Baselined);
                            break;
                        case ChangeKind.Modified:
                            FileEventStore.Add(c, item.FileId, RunId, FileEventKind.Modified);
                            FileEventStore.Add(c, item.FileId, RunId, FileEventKind.Baselined);
                            break;
                        case ChangeKind.Verified:
                            FileEventStore.ReplaceVerified(c, item.FileId, RunId);
                            break;
                        case ChangeKind.Rot:
                            FileEventStore.Add(c, item.FileId, RunId, FileEventKind.Rot);
                            break;
                        case ChangeKind.MetaDrift:
                            FileEventStore.Add(c, item.FileId, RunId, FileEventKind.MetaDrift);
                            break;
                    }

                    return result;
                },
                ChangeFlags.Files | ChangeFlags.Findings | ChangeFlags.Artifacts | ChangeFlags.Events,
                token).ConfigureAwait(false);
            Interlocked.Add(ref _newFindings, findings.New);
            Interlocked.Add(ref _resolvedFindings, findings.Resolved);
            Interlocked.Add(ref _newBySeverity[4], findings.NewCritical);
            Interlocked.Add(ref _newBySeverity[3], findings.NewHigh);
            Interlocked.Add(ref _newBySeverity[2], findings.NewMedium);
            Interlocked.Add(ref _newBySeverity[1], findings.NewLow);
        }

        private async Task SkipAsync(WorkItem item, SkipReason reason, CancellationToken token)
        {
            Interlocked.Increment(ref _skipped);
            long id = item.FileId;
            await _s.Database.Writer.PostAsync(c => FileIndex.MarkSkipped(c, id, reason, RunId), ChangeFlags.Files, token).ConfigureAwait(false);
        }

        private static Finding RotFinding(FileRecord previous, IntegrityResult current, bool streamCompared)
        {
            DateTimeOffset baselineDate = Ticks.To(previous.HashUtc ?? previous.FirstSeenUtc);
            string summary = previous.VerifyCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Content changed since {baselineDate:yyyy-MM-dd} without the file being modified. Verified intact {previous.VerifyCount} times before.")
                : string.Create(CultureInfo.InvariantCulture, $"Content changed since {baselineDate:yyyy-MM-dd} without the file being modified.");
            Dictionary<string, object> evidence = new()
            {
                ["oldFingerprint"] = Convert.ToHexStringLower(previous.Hash!),
                ["newFingerprint"] = Convert.ToHexStringLower(current.Hash!),
                ["baselineUtc"] = previous.HashUtc ?? previous.FirstSeenUtc,
                ["lastVerifiedUtc"] = previous.LastVerifiedUtc ?? 0,
                ["verifyCount"] = previous.VerifyCount,
                ["sizeBytes"] = current.Size,
                ["zeroBlocks"] = current.Zeros.ZeroBlocks,
                ["zeroRunOffsetsBytes"] = current.Zeros.RunOffsets.ToArray(),
                ["streamCompared"] = streamCompared,
            };
            return new Finding("INT-ROT", Severity.Critical, 1f, summary, evidence);
        }

        // ----- End of run -----
        private async Task FinishRootsAsync(CancellationToken token)
        {
            foreach (RootRun state in _roots.Values)
            {
                if (state.Offline || !state.EnumeratedFully || state.Scope.FileIds is { Count: > 0 })
                {
                    // Never mark files missing when a root is offline or enumeration failed part-way.
                    continue;
                }

                string scope = state.Scope.RelativeDirectory ?? "";
                List<string> unlisted = [.. state.Unlisted];
                long runId = RunId;
                long rootId = state.Root.Id;

                // Fingerprint fallback for moves: a missing row whose fingerprint equals a file first seen in
                // this run is the same file moved; keep the old row (and its history) at the new path.
                foreach (FileRecord gone in _s.Database.Files.NotSeen(rootId, runId))
                {
                    if (gone.Hash is null || !FileIndex.Within(gone.RelDir ?? "", FileIndex.NormalizeRelDir(scope)))
                    {
                        continue;
                    }

                    FileRecord? arrived = _s.Database.Files.NewWithHash(rootId, gone.Hash, _runStartUtc).FirstOrDefault(r => r.Id != gone.Id && r.Size == gone.Size);
                    if (arrived is null)
                    {
                        continue;
                    }

                    string from = (gone.RelDir ?? "").Length == 0 ? gone.Name : gone.RelDir + "\\" + gone.Name;
                    string detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["from"] = from });
                    long goneId = gone.Id;
                    await _s.Database.Writer.ExecuteAsync(
                        c =>
                        {
                            FileIndex.Delete(c, arrived.Id);
                            FileIndex.Move(c, goneId, rootId, arrived.RelDir ?? "", arrived.Name);
                            FileIndex.MarkSeen(c, [goneId], runId);
                            FileEventStore.Add(c, goneId, runId, FileEventKind.Moved, detail);
                        },
                        ChangeFlags.Files | ChangeFlags.Events,
                        token).ConfigureAwait(false);
                }

                List<long> missing = await _s.Database.Writer.ExecuteAsync(
                    c =>
                    {
                        List<long> ids = FileIndex.MarkMissingInScope(c, rootId, runId, scope, unlisted);
                        foreach (long id in ids)
                        {
                            FileEventStore.Add(c, id, runId, FileEventKind.Missing);
                        }

                        return ids;
                    },
                    ChangeFlags.Files | ChangeFlags.Events,
                    token).ConfigureAwait(false);

                double share = _rules.Value("RUN-MASS-MISSING", "percent") / 100.0;
                long minimum = (long)_rules.Value("RUN-MASS-MISSING", "minFiles");
                if (state.PresentBefore > 0 && missing.Count >= minimum && missing.Count >= state.PresentBefore * share)
                {
                    _alerts.Add(new RunAlert("RUN-MASS-MISSING", Severity.High, rootId, string.Create(CultureInfo.InvariantCulture, $"{missing.Count} of {state.PresentBefore} files disappeared from {state.Root.Path}")));
                }
            }
        }

        private sealed class RootRun(RootRecord root, ScanScope scope)
        {
            public RootRecord Root { get; } = root;

            public ScanScope Scope { get; } = scope;

            public volatile bool Offline;

            public volatile bool EnumeratedFully;

            public long PresentBefore { get; set; }

            public ConcurrentBag<string> Unlisted { get; } = [];
        }

        private sealed class InFlight(WorkItem item, CancellationTokenSource pauseCancel)
        {
            public WorkItem Item { get; } = item;

            public CancellationTokenSource PauseCancel { get; } = pauseCancel;

            public string Stage { get; set; } = "Queued";

            public double Fraction { get; set; }
        }
    }
}

/// <summary>What a re-evaluation changed (Settings → Rules info bar).</summary>
public sealed record ReevaluationResult(int ChangedTier, int Appeared, int NoLongerApply);

/// <summary>Three priority lanes with one shared capacity: a slow stage applies back-pressure.</summary>
internal sealed class WorkQueue(int capacity)
{
    private readonly ConcurrentQueue<WorkItem>[] _lanes = [new(), new(), new()];
    private readonly SemaphoreSlim _items = new(0);
    private readonly SemaphoreSlim _space = new(capacity, capacity);
    private volatile bool _completed;

    public async Task AddAsync(WorkItem item, CancellationToken token)
    {
        await _space.WaitAsync(token).ConfigureAwait(false);
        _lanes[(int)item.Priority].Enqueue(item);
        _items.Release();
    }

    /// <summary>The next item by priority, or null once completed and empty.</summary>
    public async Task<WorkItem?> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            if (_completed && _items.CurrentCount == 0 && _lanes.All(l => l.IsEmpty))
            {
                return null;
            }

            if (!await _items.WaitAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false))
            {
                continue;
            }

            foreach (ConcurrentQueue<WorkItem> lane in _lanes)
            {
                if (lane.TryDequeue(out WorkItem? item))
                {
                    _space.Release();
                    return item;
                }
            }
        }
    }

    public void Complete() => _completed = true;

    public void Drain()
    {
        foreach (ConcurrentQueue<WorkItem> lane in _lanes)
        {
            while (lane.TryDequeue(out _))
            {
                _space.Release();
            }
        }
    }
}

/// <summary>The file-system format of a volume: FAT and exFAT store local times with 2 s or 10 ms resolution.</summary>
public static class FileSystemFormat
{
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsFatLike(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        return Cache.GetOrAdd(root, r =>
        {
            try
            {
                string format = new DriveInfo(r).DriveFormat;
                return format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || format.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }
}
