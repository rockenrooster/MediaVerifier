using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Integrity;

public enum ChangeKind : byte
{
    /// <summary>No baseline yet: take one.</summary>
    NoBaseline = 0,

    /// <summary>Stat and fingerprint unchanged.</summary>
    Verified = 1,

    /// <summary>Stat unchanged, fingerprint changed, stream hash changed or unavailable (INT-ROT).</summary>
    Rot = 2,

    /// <summary>Stat unchanged, fingerprint changed, stream hash identical (INT-META-DRIFT).</summary>
    MetaDrift = 3,

    /// <summary>Size or modified time differs: a legitimate edit. New baseline, full re-analysis.</summary>
    Modified = 4,
}

/// <summary>
/// The decision table of subplan 06 (task I-05), a pure function. "Stat" is size and modified time with
/// the tolerances of subplan 05: 2 s on the time unless the size also changed, and an exact one-hour
/// shift on FAT or exFAT (daylight saving) counts as unchanged.
/// </summary>
public static class ChangeClassifier
{
    public static readonly long TimeTolerance = TimeSpan.FromSeconds(2).Ticks;
    private static readonly long Hour = TimeSpan.FromHours(1).Ticks;

    public static bool StatSame(long sizeA, long mtimeA, long sizeB, long mtimeB, bool fatLikeVolume)
    {
        if (sizeA != sizeB)
        {
            return false;
        }

        long difference = Math.Abs(mtimeA - mtimeB);
        if (difference <= TimeTolerance)
        {
            return true;
        }

        return fatLikeVolume && Math.Abs(difference - Hour) <= TimeTolerance;
    }

    /// <summary>Classifies a re-read against the file's baseline.</summary>
    public static ChangeKind Classify(FileRecord? previous, long currentSize, long currentMtime, byte[] currentHash, byte[]? currentStreamHash, bool fatLikeVolume)
    {
        if (previous?.Hash is not { Length: > 0 } baselineHash || previous.HashSize is not long baselineSize || previous.HashMtimeUtc is not long baselineMtime)
        {
            return ChangeKind.NoBaseline;
        }

        if (!StatSame(baselineSize, baselineMtime, currentSize, currentMtime, fatLikeVolume))
        {
            return ChangeKind.Modified;
        }

        if (baselineHash.AsSpan().SequenceEqual(currentHash))
        {
            return ChangeKind.Verified;
        }

        if (previous.StreamHash is { Length: > 0 } baselineStream && currentStreamHash is { Length: > 0 } && baselineStream.AsSpan().SequenceEqual(currentStreamHash))
        {
            return ChangeKind.MetaDrift;
        }

        // Never auto-accept a changed hash when the stat is unchanged (subplan 06).
        return ChangeKind.Rot;
    }
}

/// <summary>A file due for verification, as the scrub policy sees it.</summary>
public readonly record struct ScrubCandidate(long FileId, string VolumeKey, string RelDir, string Name, long LastVerifiedUtcTicks, long Size);

/// <summary>
/// Scrub selection (task I-08): files whose last verification is older than the period, oldest first;
/// a time budget cuts the list using the expected read rate; what is chosen is then read grouped by
/// volume and in directory order within a volume to keep disk access sequential.
/// </summary>
public static class ScrubPolicy
{
    public static IReadOnlyList<ScrubCandidate> Select(IEnumerable<ScrubCandidate> candidates, DateTimeOffset nowUtc, TimeSpan period, TimeSpan? budget, double bytesPerSecond)
    {
        long cutoff = (nowUtc - period).UtcTicks;
        List<ScrubCandidate> due = [.. candidates.Where(c => c.LastVerifiedUtcTicks < cutoff).OrderBy(c => c.LastVerifiedUtcTicks).ThenBy(c => c.FileId)];
        if (budget is TimeSpan limit && bytesPerSecond > 0)
        {
            double allowedBytes = limit.TotalSeconds * bytesPerSecond;
            double used = 0;
            int keep = 0;
            foreach (ScrubCandidate candidate in due)
            {
                if (keep > 0 && used + candidate.Size > allowedBytes)
                {
                    break;
                }

                used += candidate.Size;
                keep++;
            }

            due.RemoveRange(keep, due.Count - keep);
        }

        return [.. due
            .OrderBy(c => c.VolumeKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.RelDir, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
