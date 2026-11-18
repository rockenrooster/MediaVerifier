using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The Issues data behind the list and the drawer (subplan 09, Issues and Detail drawer; subplan 12, Action catalogue):
/// which fixes a row offers, what the drawer shows for ignored findings, and the filter and group bookkeeping.
/// </summary>
public sealed class IssuesDataTests
{
    /// <summary>"Show problems found in this scan" is a filter: without it the list said "No problems found" while thousands were open.</summary>
    [Fact]
    public void Problems_found_in_one_scan_count_as_a_filter()
    {
        Assert.True(new IssuesQuery(new FindingFilter { FirstRun = 3 }).Filtered);
        Assert.False(new IssuesQuery(new FindingFilter()).Filtered);
    }

    /// <summary>
    /// The list's fixes follow the action catalogue for the file: a damaged photo was offered "Save a playable copy"
    /// (a byte-identical copy that keeps the zeros), a PNG "Rewrite FLAC", an ALAC file "Store as 16-bit", and the
    /// experimental de-clip was the recommended Ctrl+Enter action.
    /// </summary>
    [Fact]
    public void The_list_offers_only_fixes_that_apply_to_the_file()
    {
        RootRecord library = new() { Id = 1, Path = @"D:\Media" };
        RootRecord backup = new() { Id = 2, Path = @"E:\Backup", Role = RootRole.Backup };

        Assert.Equal([ActionIds.ExtractPreview], LiveUiData.ListFixes("GEN-ZEROFILL", MediaKind.Photo, "IMG_1.jpg", library, false, false));
        Assert.Equal([ActionIds.Salvage], LiveUiData.ListFixes("COR-TRUNCATED", MediaKind.Audio, "song.mp3", library, false, false));
        Assert.DoesNotContain(ActionIds.ReencodeFlac, LiveUiData.ListFixes("COR-CHECKSUM", MediaKind.Photo, "scan.png", library, false, true));
        Assert.Contains(ActionIds.ReencodeFlac, LiveUiData.ListFixes("COR-CHECKSUM", MediaKind.Audio, "song.flac", library, false, true));
        Assert.DoesNotContain(ActionIds.ReencodeFlac, LiveUiData.ListFixes("COR-CHECKSUM", MediaKind.Audio, "song.flac", library, false, false));
        Assert.Empty(LiveUiData.ListFixes("AUD-PADDED-BITS", MediaKind.Audio, "song.m4a", library, false, false));
        Assert.Equal([ActionIds.ShrinkBitDepth], LiveUiData.ListFixes("AUD-PADDED-BITS", MediaKind.Audio, "song.flac", library, false, false));
        Assert.Empty(LiveUiData.ListFixes("COR-TRUNCATED", MediaKind.Audio, "song.mp3", backup, false, false));

        // The de-clip can be chosen, but it is never the recommended fix.
        IssueRow clipped = new(1, 1, "AUD-CLIPPING", Severity.Medium, "a.flac", "x", @"D:\Media", 1, MediaKind.Audio, @"D:\Media\a.flac") { Fixes = LiveUiData.ListFixes("AUD-CLIPPING", MediaKind.Audio, "a.flac", library, false, false) };
        Assert.Contains(ActionIds.DeclipCopy, clipped.Fixes);
        Assert.Null(clipped.RecommendedFix);

        // Restore needs a copy; with one, it comes first.
        Assert.DoesNotContain(ActionIds.RestoreCopy, LiveUiData.ListFixes("INT-ROT", MediaKind.Audio, "a.flac", library, false, false));
        Assert.Equal(ActionIds.RestoreCopy, LiveUiData.ListFixes("INT-ROT", MediaKind.Audio, "a.flac", library, true, false)[0]);
    }

    /// <summary>
    /// A FLAC whose frames fail their checksums raises only COR-CHECKSUM (the errors are not decoder errors), so it
    /// counted as "decodes cleanly" and Rewrite FLAC stored the damaged audio under a fresh, valid checksum.
    /// </summary>
    [Fact]
    public void A_flac_with_frame_checksum_errors_is_not_offered_a_rewrite()
    {
        Dictionary<string, object> frameErrors = new() { ["md5"] = "Mismatch", ["checksumErrors"] = 2L };
        Dictionary<string, object> md5Only = new() { ["md5"] = "Mismatch" };
        Assert.False(LiveUiData.DecodesCleanly([("COR-CHECKSUM", frameErrors)]));
        Assert.True(LiveUiData.DecodesCleanly([("COR-CHECKSUM", md5Only)]));
        Assert.False(LiveUiData.DecodesCleanly([("COR-CHECKSUM", md5Only), ("COR-DECODE", md5Only)]));

        using TestDatabase db = new("issues-flac");
        long root = IssueFixtures.Root(db.Database, @"Z:\Music", "Music");
        long damaged = IssueFixtures.File(db.Database, root, "Album", "damaged.flac", MediaKind.Audio);
        long stale = IssueFixtures.File(db.Database, root, "Album", "stale.flac", MediaKind.Audio);
        long damagedFinding = IssueFixtures.Finding(db.Database, damaged, "COR-CHECKSUM", Severity.High, evidenceJson: """{"md5":"Mismatch","checksumErrors":3}""");
        long staleFinding = IssueFixtures.Finding(db.Database, stale, "COR-CHECKSUM", Severity.High, evidenceJson: """{"md5":"Mismatch"}""");
        LiveUiData data = IssueFixtures.Data(db);

        Dictionary<long, IssueRow> rows = data.LoadIssueRows([damagedFinding, staleFinding]).ToDictionary(r => r.Id);
        Assert.DoesNotContain(ActionIds.ReencodeFlac, rows[damagedFinding].Fixes);
        Assert.Contains(ActionIds.ReencodeFlac, rows[staleFinding].Fixes);
        Assert.DoesNotContain(ActionIds.ReencodeFlac, data.LoadFileDetail(damaged)!.FixActionIds!);
        Assert.Equal(ActionIds.ReencodeFlac, data.LoadFileDetail(stale)!.RecommendedActionId);
    }

    /// <summary>Fix… lists the fixes every selected finding can take (subplan 09); Library's rule-only fix is one that suits any file.</summary>
    [Fact]
    public void Common_fixes_are_those_every_finding_can_take()
    {
        Assert.Equal([ActionIds.Remux], Remediator.CommonFixes([[ActionIds.Remux, ActionIds.Salvage], [ActionIds.Salvage, ActionIds.Remux], [ActionIds.Remux]]));
        Assert.Empty(Remediator.CommonFixes([[ActionIds.ExtractPreview], [ActionIds.Salvage]]));
        Assert.Empty(Remediator.CommonFixes([]));

        Assert.Equal(ActionIds.FixExtension, Remediator.CommonFix(["GEN-EXT-MISMATCH"]));
        Assert.Null(Remediator.CommonFix(["GEN-ZEROFILL"]));
        Assert.Null(Remediator.CommonFix(["AUD-CLIPPING"]));
    }

    /// <summary>The list's groups always add up to its ids, so painting the last row never indexes past the list.</summary>
    [Fact]
    public void Group_sizes_always_add_up_to_the_ids()
    {
        IssueGroup[] groups = [new("High", "High", Severity.High, 3, 3), new("Low", "Low", Severity.Low, 4, 4)];
        Assert.Same(groups, LiveUiData.Reconcile(groups, 7));
        Assert.Equal([3, 2], LiveUiData.Reconcile(groups, 5).Select(g => g.Rows));
        Assert.Equal([3, 6], LiveUiData.Reconcile(groups, 9).Select(g => g.Rows));
        Assert.Equal([2], LiveUiData.Reconcile(groups, 2).Select(g => g.Rows));

        // The header keeps the counted number.
        Assert.Equal(4, LiveUiData.Reconcile(groups, 9)[1].Count);
    }

    /// <summary>
    /// A rotted file whose old fingerprint another file in the index still has can be restored from the list (the
    /// Dashboard's "a healthy copy exists" then Review, Select all, Fix… offered only "Save a playable copy").
    /// </summary>
    [Fact]
    public void Rows_carry_their_fixes_and_rot_with_an_indexed_copy_can_be_restored()
    {
        using TestDatabase db = new("issues-rows");
        long library = IssueFixtures.Root(db.Database, @"Z:\Media", "Media");
        long backup = IssueFixtures.Root(db.Database, @"Y:\Backup", "Backup", RootRole.Backup);
        byte[] fingerprint = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
        long rotted = IssueFixtures.File(db.Database, library, "2019", "IMG_1.jpg", MediaKind.Photo, hash: fingerprint);
        _ = IssueFixtures.File(db.Database, backup, "2019", "IMG_1.jpg", MediaKind.Photo, hash: fingerprint, state: FileState.Ok);
        long lonely = IssueFixtures.File(db.Database, library, "2019", "IMG_2.jpg", MediaKind.Photo, hash: [.. fingerprint.Reverse()]);
        long rot = IssueFixtures.Finding(db.Database, rotted, "INT-ROT", Severity.Critical);
        long otherRot = IssueFixtures.Finding(db.Database, lonely, "INT-ROT", Severity.Critical);
        long zeros = IssueFixtures.Finding(db.Database, lonely, "GEN-ZEROFILL", Severity.Low);

        Dictionary<long, IssueRow> rows = IssueFixtures.Data(db).LoadIssueRows([rot, otherRot, zeros]).ToDictionary(r => r.Id);
        Assert.Equal(ActionIds.RestoreCopy, rows[rot].RecommendedFix);
        Assert.DoesNotContain(ActionIds.RestoreCopy, rows[otherRot].Fixes);
        Assert.Equal([ActionIds.ExtractPreview], rows[zeros].Fixes);
        Assert.Equal(UiFormat.Size(4096), rows[rot].SizeText);
    }

    /// <summary>Details in the Ignored view show the ignored finding (the drawer said "FINDINGS (0)"), with Stop ignoring.</summary>
    [Fact]
    public void Details_of_an_ignored_finding_show_it()
    {
        using TestDatabase db = new("issues-ignored");
        long root = IssueFixtures.Root(db.Database, @"Z:\Media", "Media");
        long file = IssueFixtures.File(db.Database, root, "Clips", "clip.mp4", MediaKind.Video);
        long open = IssueFixtures.Finding(db.Database, file, "COR-INDEX", Severity.Medium);
        long ignored = IssueFixtures.Finding(db.Database, file, "VID-UPSCALED", Severity.High, FindingStatus.Ignored);
        LiveUiData data = IssueFixtures.Data(db);

        Assert.Equal([open], data.LoadFileDetail(file)!.Findings.Select(f => f.Id));
        FileDetail detail = data.LoadFileDetail(file, FindingStatus.Ignored, findCopies: false)!;
        Assert.Equal([open, ignored], detail.Findings.Select(f => f.Id));
        Assert.Equal(FindingStatus.Ignored, detail.Findings[1].Status);

        // The fixes come from the open findings only.
        Assert.Equal(ActionIds.Remux, detail.RecommendedActionId);
        Assert.Equal(["Z:", "Media", "Clips"], detail.Breadcrumb);
        Assert.Equal(2, detail.RootSegments);
        Assert.Equal("Clips", detail.RelDir);
    }

    /// <summary>
    /// Opening a file's details looked for healthy copies of every file, stat-ing each copy, which waits for an offline
    /// backup drive. Only a file whose content changed can have a healthier copy, and the drawer opens before the search.
    /// </summary>
    [Fact]
    public void The_drawer_looks_for_copies_only_for_changed_content_and_after_it_opens()
    {
        using TestDatabase db = new("issues-copies");
        long root = IssueFixtures.Root(db.Database, @"Z:\Media", "Media");
        byte[] fingerprint = [.. Enumerable.Range(3, 16).Select(i => (byte)i)];
        long decode = IssueFixtures.File(db.Database, root, string.Empty, "a.mp3", MediaKind.Audio, hash: fingerprint);
        long rot = IssueFixtures.File(db.Database, root, string.Empty, "b.mp3", MediaKind.Audio, hash: [.. fingerprint.Reverse()]);
        IssueFixtures.Finding(db.Database, decode, "COR-DECODE", Severity.High);
        IssueFixtures.Finding(db.Database, rot, "INT-ROT", Severity.Critical);
        LiveUiData data = IssueFixtures.Data(db);

        Assert.False(data.LoadFileDetail(decode, FindingStatus.Open, findCopies: false)!.CopiesPending);
        FileDetail first = data.LoadFileDetail(rot, FindingStatus.Open, findCopies: false)!;
        Assert.True(first.CopiesPending);
        Assert.Equal(ActionIds.Salvage, first.RecommendedActionId);
        Assert.False(data.LoadFileDetail(rot, FindingStatus.Open, findCopies: true)!.CopiesPending);
    }

    /// <summary>
    /// The byte map draws the longest zero run over its whole length and reads INT-ROT's offsets too (a rot byte map was
    /// all grey, and a file 60 % zeros showed one red column).
    /// </summary>
    [Fact]
    public void Byte_map_regions_cover_the_longest_run_and_both_offset_keys()
    {
        Dictionary<string, object> zeroFill = new() { ["longestRunOffsetBytes"] = 4096L, ["longestRunBytes"] = 600_000L, ["runOffsetsBytes"] = new object[] { 4096L, 900_000L } };
        Assert.Equal([4096L, 600_000L, 4096L, 1L, 900_000L, 1L], LiveUiData.ZeroRuns(zeroFill)!);
        Dictionary<string, object> rot = new() { ["zeroRunOffsetsBytes"] = new object[] { 8192L } };
        Assert.Equal([8192L, 1L], LiveUiData.ZeroRuns(rot)!);
        Assert.Null(LiveUiData.ZeroRuns(new Dictionary<string, object>()));
    }

    /// <summary>Technical details hold the streams and the decoder's messages with their counts, not only the fingerprints.</summary>
    [Fact]
    public void Technical_details_list_streams_and_decoder_messages()
    {
        FileRecord file = new()
        {
            Name = "clip.mkv",
            Ext = "mkv",
            MediaKind = MediaKind.Video,
            Hash = [1, 2, 3],
            Media = new MediaSummary("matroska", "h264", "aac", 1920, 1080, 60_000, null, 48_000, 2, null, 25_000),
        };
        Dictionary<string, object> evidence = new() { ["messages"] = new object[] { "Invalid NAL unit size" }, ["messageCounts"] = new object[] { 14L } };
        FindingDetail decode = new(1, "COR-DECODE", Severity.High, 1f, "Decoder errors", "s", "w", "w", "w", EvidenceView.Messages, evidence, "c");
        string text = LiveUiData.TechnicalText(file, [decode, decode with { Id = 2, RuleId = "COR-CONCEALED" }]);
        Assert.Contains(Strings.Drawer.TechContainer + "matroska", text, StringComparison.Ordinal);
        Assert.Contains(Strings.Drawer.TechVideo + "h264", text, StringComparison.Ordinal);
        Assert.Contains(Strings.Drawer.TechAudio + "aac", text, StringComparison.Ordinal);
        Assert.Single(text.Split(Environment.NewLine), line => line.EndsWith("Invalid NAL unit size", StringComparison.Ordinal));
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, Strings.Drawer.TechMessage, "14", "Invalid NAL unit size"), text, StringComparison.Ordinal);
        Assert.Contains(Strings.Drawer.FileFingerprint + "010203", text, StringComparison.Ordinal);
    }
}

/// <summary>Watched folders, files and findings written straight into a test library.</summary>
internal static class IssueFixtures
{
    public static LiveUiData Data(TestDatabase db) => new(db.Database, () => new ScanSettings(), db.Clock);

    public static long Root(Database database, string path, string label, RootRole role = RootRole.Library) =>
        database.Roots.AddAsync(new RootRecord { Path = path, Label = label, Role = role, MediaMask = MediaKindMask.All, AddedUtc = database.Clock.UtcNow }).GetAwaiter().GetResult();

    public static long File(Database database, long root, string relDir, string name, MediaKind kind, long size = 4096, long mtimeUtc = 0, byte[]? hash = null, FileState state = FileState.Issues) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                long dir = FileIndex.InternDir(context, root, relDir);
                SqliteCommand insert = context.Command(
                    "INSERT INTO files(root_id, dir_id, name, ext, media_kind, size, mtime_utc, state, worst_severity, open_findings, scanned_depth, first_seen_utc, last_seen_run, " +
                    "hash, hash_size, hash_mtime_utc, hash_utc, verify_count) VALUES ($root, $dir, $name, $ext, $kind, $size, $mtime, $state, 0, 0, 2, $now, 1, $hash, $hashSize, $mtime, $now, 0)");
                insert.Parameters.AddWithValue("$root", root);
                insert.Parameters.AddWithValue("$dir", dir);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$ext", Path.GetExtension(name).TrimStart('.').ToLowerInvariant());
                insert.Parameters.AddWithValue("$kind", (int)kind);
                insert.Parameters.AddWithValue("$size", size);
                insert.Parameters.AddWithValue("$mtime", mtimeUtc);
                insert.Parameters.AddWithValue("$state", (int)state);
                insert.Parameters.AddWithValue("$now", context.NowTicks);
                insert.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
                insert.Parameters.AddWithValue("$hashSize", hash is null ? DBNull.Value : size);
                return context.Insert(insert);
            },
            ChangeFlags.Files).GetAwaiter().GetResult();

    public static long Finding(Database database, long file, string ruleId, Severity severity, FindingStatus status = FindingStatus.Open, string evidenceJson = "{}") =>
        database.Writer.ExecuteAsync(
            context =>
            {
                RuleDefinition rule = RuleCatalog.Get(ruleId);
                SqliteCommand insert = context.Command(
                    "INSERT INTO findings(file_id, rule_id, category, severity, confidence, summary, evidence_json, status, first_run, last_run, first_utc, last_utc, media_kind, file_name) " +
                    "SELECT $file, $rule, $category, $severity, 1.0, $summary, $evidence, $status, 1, 1, $now, $now, media_kind, name FROM files WHERE id = $file");
                insert.Parameters.AddWithValue("$file", file);
                insert.Parameters.AddWithValue("$rule", ruleId);
                insert.Parameters.AddWithValue("$category", (int)rule.Category);
                insert.Parameters.AddWithValue("$severity", (int)severity);
                insert.Parameters.AddWithValue("$summary", rule.Title);
                insert.Parameters.AddWithValue("$evidence", evidenceJson);
                insert.Parameters.AddWithValue("$status", (int)status);
                insert.Parameters.AddWithValue("$now", context.NowTicks);
                long id = context.Insert(insert);
                FindingStore.RecomputeFileSummary(context, file);
                return id;
            },
            ChangeFlags.Findings).GetAwaiter().GetResult();
}
