using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

public enum ReconcileKind : byte
{
    New,
    Unchanged,
    Modified,
    Skipped,
}

/// <summary>What reconciliation decided for one enumerated file.</summary>
public sealed record ReconcileDecision(EnumeratedFile File, FileRecord? Row, ReconcileKind Kind, bool Returned, bool ReadContent, bool Analyze, bool ReevaluateOnly, WorkPriority Priority)
{
    public bool HasWork => ReadContent || Analyze || ReevaluateOnly;
}

/// <summary>
/// Decides what work each file needs (task E-04, subplan 05 reconciliation table) by comparing the listing
/// with the index rows of the same directory. Pure: no I/O, no clock reads beyond the given instant.
/// </summary>
public sealed class Reconciler(
    ScanDepth runDepth,
    int detectorSet,
    DateTimeOffset nowUtc,
    TimeSpan scrubPeriod,
    bool verifyEverything,
    bool verifyOnly,
    bool verifyOnAnalysis,
    RootRole role,
    bool fatLikeVolume)
{
    public List<ReconcileDecision> Reconcile(IReadOnlyList<EnumeratedFile> files, IReadOnlyList<FileRecord> rows)
    {
        Dictionary<string, FileRecord> byName = new(rows.Count, StringComparer.Ordinal);
        foreach (FileRecord row in rows)
        {
            byName[row.Name] = row;
        }

        List<ReconcileDecision> decisions = new(files.Count);
        foreach (EnumeratedFile file in files)
        {
            byName.TryGetValue(file.Name, out FileRecord? row);
            decisions.Add(Decide(file, row));
        }

        return decisions;
    }

    public ReconcileDecision Decide(EnumeratedFile file, FileRecord? row)
    {
        bool analyse = !verifyOnly && role == RootRole.Library;
        bool returned = row?.State == FileState.Missing;
        if (file.Skip != SkipReason.None)
        {
            return new ReconcileDecision(file, row, ReconcileKind.Skipped, returned, false, false, false, WorkPriority.NewOrModified);
        }

        if (row is null)
        {
            return new ReconcileDecision(file, null, ReconcileKind.New, false, ReadContent: true, Analyze: analyse, false, WorkPriority.NewOrModified);
        }

        if (!ChangeClassifier.StatSame(row.Size, row.MtimeUtc, file.Size, file.MtimeUtcTicks, fatLikeVolume))
        {
            return new ReconcileDecision(file, row, ReconcileKind.Modified, returned, ReadContent: true, Analyze: analyse, false, WorkPriority.NewOrModified);
        }

        bool noBaseline = row.Hash is null;
        long lastVerified = row.LastVerifiedUtc ?? row.HashUtc ?? 0;
        bool verifyDue = verifyEverything || noBaseline || lastVerified < (nowUtc - scrubPeriod).UtcTicks;
        bool unfinished = row.State is FileState.Pending or FileState.Error or FileState.Skipped;
        bool needsDepth = analyse && (row.ScannedDepth < runDepth || unfinished);
        bool needsReevaluation = analyse && !needsDepth && row.DetectorSet != detectorSet;
        bool read = verifyDue || (needsDepth && verifyOnAnalysis);
        WorkPriority priority = verifyDue ? WorkPriority.VerificationDue : WorkPriority.Upgrade;
        if (unfinished)
        {
            priority = WorkPriority.NewOrModified;
        }

        return new ReconcileDecision(file, row, ReconcileKind.Unchanged, returned, read, needsDepth, needsReevaluation, priority);
    }
}
