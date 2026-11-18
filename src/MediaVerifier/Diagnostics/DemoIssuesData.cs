using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Diagnostics;

/// <summary>The Issues rows and the drawer of the mockups (docs/design/issues-*.svg).</summary>
internal sealed partial class DemoUiData
{
    private static readonly IssueRow[] Rows =
    [
        Row(1, Severity.Critical, "INT-ROT", "IMG_2041.jpg", "Changed without being modified · intact on 14 Mar 2026", @"D:\Photos\2019\Rome", 4.2, MediaKind.Photo),
        Row(2, Severity.Critical, "INT-ROT", "Harbor Lights - 03 - Low Tide.flac", "Changed without being modified · intact on 2 Mar 2026", @"D:\Music\Harbor Lights\Low Tide", 28.7, MediaKind.Audio),
        Row(3, Severity.Critical, "COR-CONTAINER", "Birthday 2021.mp4", "Container is broken · index missing, recording was interrupted", @"D:\Videos\Family", 1.9 * 1024, MediaKind.Video),
        Row(4, Severity.High, "AUD-FAKE-LOSSLESS", "Night Drive - 07 - Overpass.flac", "Likely made from a lossy source · hard cutoff at 16.6 kHz", @"D:\Music\Night Drive\Overpass", 31.2, MediaKind.Audio),
        Row(5, Severity.High, "VID-UPSCALED", "Coastline 4K.mkv", "Likely upscaled · 2160p file with detail equivalent to 1080p", @"D:\Videos\Travel", 14.6 * 1024, MediaKind.Video),
        Row(6, Severity.High, "COR-CHECKSUM", "Sunday Session - 02.flac", "Embedded checksum mismatch · audio differs from the stored MD5", @"D:\Music\Sunday Session", 41.0, MediaKind.Audio),
        Row(7, Severity.High, "COR-DECODE", "Wedding - Speeches.mov", "Decoder errors · 14 errors, about 3 s affected", @"D:\Videos\Family", 6.3 * 1024, MediaKind.Video),
        Row(8, Severity.High, "COR-PARTIAL-IMAGE", "DSC_0412.jpg", "Image decodes only partly · data ends before the image does", @"D:\Photos\2016\Lisbon", 2.1, MediaKind.Photo),
        Row(9, Severity.Medium, "AUD-PADDED-BITS", "Glasshouse - 01 - Morning.flac", "Bit depth is padded · 24-bit file with 16 significant bits", @"D:\Music\Glasshouse", 88.4, MediaKind.Audio),
        Row(10, Severity.Medium, "COR-INDEX", "Road Trip Day 3.mkv", "Seek index missing · seeking will be slow or fail", @"D:\Videos\Travel", 3.8 * 1024, MediaKind.Video),
        Row(11, Severity.Medium, "AUD-CLIPPING", "Static Bloom - 05.flac", "Clipping · 212 clipped peaks per minute", @"D:\Music\Static Bloom", 36.9, MediaKind.Audio),
        Row(12, Severity.Medium, "AUD-CLIPPING", "Static Bloom - 06.flac", "Clipping · 148 clipped peaks per minute", @"D:\Music\Static Bloom", 34.2, MediaKind.Audio),
        Row(13, Severity.Medium, "COR-INDEX", "Road Trip Day 4.mkv", "Seek index missing · seeking will be slow or fail", @"D:\Videos\Travel", 4.1 * 1024, MediaKind.Video),
    ];

    public IssuesData LoadIssues(IssuesQuery query)
    {
        IEnumerable<IssueRow> rows = Rows;
        if (query.Filter.Severities is { Count: > 0 } tiers)
        {
            rows = rows.Where(r => tiers.Contains(r.Severity));
        }

        if (query.Filter.Kinds is { Count: > 0 } kinds)
        {
            rows = rows.Where(r => kinds.Contains(r.Kind));
        }

        IssueRow[] shown = [.. rows];
        IssueGroup[] groups =
        [
            .. new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low }
                .Where(t => query.Filter.Severities is not { Count: > 0 } s || s.Contains(t))
                .Select(t => new IssueGroup(t.ToString(), UI.UiFormat.TierName(t), t, t switch { Severity.Critical => 3, Severity.High => 14, Severity.Medium => 96, _ => 212 }, shown.Count(r => r.Severity == t))),
        ];
        return new IssuesData(
            query,
            [.. shown.Select(r => r.Id)],
            groups,
            new SeverityCounts(3, 14, 96, 212),
            new Dictionary<string, int>(StringComparer.Ordinal),
            [(1, "Music"), (2, "Photos"), (3, "Videos"), (4, "Backup (E:)")],
            325,
            true,
            true,
            12_507,
            At(16, 3, 12));
    }

    public IReadOnlyList<IssueRow> LoadIssueRows(IReadOnlyList<long> ids, bool withFixes = true) => [.. ids.Select(id => Rows.First(r => r.Id == id))];

    public SeverityCounts CountIssues(FindingFilter filter) => LoadIssues(new IssuesQuery(filter)).TierCounts;

    public long? FileOfFinding(long findingId) => Rows.FirstOrDefault(r => r.Id == findingId)?.FileId;

    public FileDetail? LoadFileDetail(long fileId) => LoadFileDetail(fileId, FindingStatus.Open, findCopies: true);

    public FileDetail? LoadFileDetail(long fileId, FindingStatus status, bool findCopies)
    {
        IssueRow row = Rows.FirstOrDefault(r => r.FileId == fileId) ?? Rows[3];
        RuleDefinition fake = RuleCatalog.Get("AUD-FAKE-LOSSLESS");
        RuleDefinition dc = RuleCatalog.Get("AUD-DC");
        Dictionary<string, object> evidence = new() { ["cutoffHz"] = 16600, ["dropDb"] = 67, ["sampleRateHz"] = 44100 };
        return new FileDetail(
            row.FileId,
            row.Name,
            row.FullPath,
            ["D:", "Music", "Night Drive", "Overpass"],
            MediaKind.Audio,
            "FLAC · 44.1 kHz · 16-bit · stereo · 4:12 · 31.2 MB",
            FingerprintState.Unchanged,
            Local(3, 2, 10, 0),
            3,
            Local(10, 12, 3, 0),
            [
                new FindingDetail(4, fake.Id, Severity.High, 0.85f, fake.Title, "hard cutoff at 16.6 kHz", RuleText.Render(fake.WhatItMeans, evidence), RuleText.Render(fake.WhyItMatters, evidence), RuleText.Render(fake.WhatYouCanDo, evidence), EvidenceView.Spectrogram, evidence, "Cutoff 16.6 kHz · drop 67 dB · only rounding noise above"),
                new FindingDetail(14, dc.Id, Severity.Low, 1f, dc.Title, "offset of 0.4 %", RuleText.Render(dc.WhatItMeans, null), RuleText.Render(dc.WhyItMatters, null), RuleText.Render(dc.WhatYouCanDo, null), EvidenceView.None, new Dictionary<string, object>(), string.Empty),
            ],
            [("Verified unchanged", Local(10, 12, 3, 0)), ("Fingerprint recorded", Local(3, 2, 10, 0)), ("First seen", Local(3, 2, 10, 0))],
            "File fingerprint (XXH3-128)  9f2c1e07b4d3a8c56e0f1d2b3a4c5d6e\nStream fingerprint (MD5)  218d8bdbaa101df51f07a726ac401ee5\n",
            (long)(31.2 * 1024 * 1024),
            [],
            UI.Strings.Actions.ExportList);
    }

    /// <summary>
    /// The spectrogram of the issues-detail reference: the level falls from the bottom through the palette's
    /// stops to black at 16.6 kHz, and each column is a little darker or lighter, as in the mockup.
    /// </summary>
    public AudioEvidence? LoadAudioEvidence(FileDetail detail, CancellationToken token)
    {
        const int columns = 125;
        const double knee = 16_600.0 / 22_050;
        (double At, double Level)[] stops = [(0, 1), (0.12, 0.9), (0.3, 0.75), (0.5, 0.5), (0.72, 0.25), (knee, 0)];
        Random random = new(41);
        byte[] spectrogram = new byte[columns * AudioEvidence.Rows];
        for (int c = 0; c < columns; c++)
        {
            double shade = 1 - (0.01 + (random.NextDouble() * 0.19));
            for (int r = 0; r < AudioEvidence.Rows; r++)
            {
                double h = (r + 0.5) / AudioEvidence.Rows;
                double level = 0;
                for (int i = 1; i < stops.Length; i++)
                {
                    if (h <= stops[i].At)
                    {
                        double f = (h - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
                        level = stops[i - 1].Level + ((stops[i].Level - stops[i - 1].Level) * f);
                        break;
                    }
                }

                spectrogram[(c * AudioEvidence.Rows) + r] = (byte)Math.Round(level * shade * 255);
            }
        }

        return new AudioEvidence
        {
            SampleRate = 44_100,
            Channels = 2,
            Seconds = 252,
            Spectrogram = spectrogram,
            WaveMin = [],
            WaveMax = [],
            Marks = [],
            MarkWindows = [],
            WindowChannels = 2,
        };
    }

    public byte[]? LoadThumbnail(FileDetail detail, CancellationToken token) => null;

    public byte[]? LoadFrameStrip(FileDetail detail, FindingDetail finding, CancellationToken token) => null;

    public byte[]? LoadCompare(FileDetail detail, FindingDetail finding, double time, CancellationToken token) => null;

    /// <summary>A demo row; <paramref name="line2"/> is "{title} · {detail}" as the mockups write it, the detail starting a sentence here.</summary>
    private static IssueRow Row(long id, Severity severity, string rule, string name, string line2, string folder, double megabytes, MediaKind kind)
    {
        string[] parts = line2.Split(" · ", 2);
        string detail = parts.Length > 1 ? char.ToUpper(parts[1][0], System.Globalization.CultureInfo.InvariantCulture) + parts[1][1..] : line2;
        return new(id, id, rule, severity, name, detail, folder, (long)(megabytes * 1024 * 1024), kind, Path.Combine(folder, name), parts[0])
        {
            Fixes = LiveUiData.ListFixes(rule, kind, name, new RootRecord { Path = folder[..3] }, hasHealthyCopy: false, decodesCleanly: false),
        };
    }
}
