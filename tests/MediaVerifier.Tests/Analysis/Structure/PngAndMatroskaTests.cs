using MediaVerifier.Core.Analysis.Structure;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Tasks C-05 and C-13: the PNG chunk walker and the Matroska header, segment and Cues checks.</summary>
public sealed class PngAndMatroskaTests
{
    [Fact]
    public void HealthyPng()
    {
        byte[] file = Png();
        StructureArtifact artifact = Read(file, "png");
        Assert.Equal(ContainerFormat.Png, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void TruncatedPngWithMissingByteCount()
    {
        byte[] full = Png(idatBytes: 1000);
        byte[] cut = Truncate(full, 0.6);
        StructureIssue issue = Assert.Single(Read(cut, "png").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("IDAT", issue.Element);

        // The rest of IDAT (data and CRC) plus the 12-byte IEND chunk.
        Assert.Equal(full.Length - cut.Length, issue.MissingBytes);
        Assert.Equal(8 + 25, issue.Offset);
    }

    [Fact]
    public void PngWithoutIend()
    {
        byte[] full = Png();
        StructureIssue issue = Assert.Single(Read(full[..^12], "png").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("IEND", issue.Element);
        Assert.Equal(12, issue.MissingBytes);

        StructureIssue partial = Assert.Single(Read(full[..^7], "png").Issues);
        Assert.Equal(StructureIssueKind.Truncated, partial.Kind);
        Assert.Null(partial.MissingBytes);
    }

    [Fact]
    public void PngTrailingData()
    {
        Assert.Empty(Read(Concat(Png(), new byte[100]), "png").Issues);
        StructureIssue issue = Assert.Single(Read(Concat(Png(), Ascii("extra")), "png").Issues);
        Assert.Equal(StructureIssueKind.TrailingData, issue.Kind);
        Assert.Equal(Png().Length, issue.Offset);
    }

    [Fact]
    public void HealthyMatroska()
    {
        byte[] file = Mkv();
        StructureArtifact artifact = Read(file, "mkv");
        Assert.Equal(ContainerFormat.Matroska, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        MatroskaInfo mkv = artifact.Matroska!;
        Assert.Equal("matroska", mkv.DocType);
        Assert.True(mkv.HasCues);
        Assert.True(mkv.HasVideoTrack);
        Assert.Equal(file.Length - EbmlHeader("matroska").Length - 4 - EbmlSize(mkv.SegmentSize!.Value).Length, mkv.SegmentSize);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
        Assert.Equal(40, Assert.Single(artifact.Padding).Length);
    }

    [Fact]
    public void WebMDocType() => Assert.Equal("webm", Read(Mkv(docType: "webm"), "webm").Matroska!.DocType);

    [Fact]
    public void TruncatedSegment()
    {
        byte[] full = Mkv();
        byte[] cut = Truncate(full, 0.6);
        StructureArtifact artifact = Read(cut, "mkv");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("Segment", issue.Element);
        Assert.Equal(full.Length - cut.Length, issue.MissingBytes);
        Assert.Equal(EbmlHeader("matroska").Length, issue.Offset);
        Assert.Equal(full.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void UnknownSegmentSizeIsFine()
    {
        StructureArtifact artifact = Read(Mkv(unknownSize: true), "mkv");
        Assert.Empty(artifact.Issues);
        Assert.Null(artifact.Matroska!.SegmentSize);
        Assert.Null(artifact.DeclaredEnd);
        Assert.True(artifact.Matroska.HasCues);
    }

    [Fact]
    public void CuesAbsentWithVideo()
    {
        StructureArtifact artifact = Read(Mkv(cues: false, seekToCues: false), "mkv");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.IndexMissingOrWrong, issue.Kind);
        Assert.Equal("Cues", issue.Element);
        Assert.False(artifact.Matroska!.HasCues);
        Assert.True(artifact.Matroska.HasVideoTrack);
    }

    [Fact]
    public void CuesAbsentWithoutVideoIsFine()
    {
        StructureArtifact artifact = Read(Mkv(cues: false, seekToCues: false, trackTypes: [2]), "mka");
        Assert.Empty(artifact.Issues);
        Assert.False(artifact.Matroska!.HasVideoTrack);
    }

    [Fact]
    public void CuesFoundByWalkingWithoutSeekHead()
    {
        StructureArtifact artifact = Read(Mkv(seekHead: false), "mkv");
        Assert.Empty(artifact.Issues);
        Assert.True(artifact.Matroska!.HasCues);
    }

    [Fact]
    public void CuesFoundThroughSeekHead()
    {
        // Damage between the Cluster and the Cues stops the walk: only the SeekHead can reach the Cues.
        byte[] info = Info();
        byte[] tracks = Tracks(1);
        byte[] cluster = Cluster(2000, 1);
        byte[] damage = new byte[16];
        long cuesAt = SeekHead((CuesId, 0)).Length + info.Length + tracks.Length + cluster.Length + damage.Length;
        byte[] Build(long position) =>
            Concat(EbmlHeader("matroska"), Element(SegmentId, SeekHead((CuesId, position)), info, tracks, cluster, damage, Cues()));

        StructureArtifact artifact = Read(Build(cuesAt), "mkv");
        Assert.True(artifact.Matroska!.HasCues);
        Assert.Empty(artifact.Issues);

        // A SeekHead entry that does not point at a Cues element does not count.
        Assert.False(Read(Build(cuesAt - 3), "mkv").Matroska!.HasCues);
    }

    [Fact]
    public void CuesFoundThroughASecondSeekHead()
    {
        byte[] info = Info();
        byte[] tracks = Tracks(1);
        byte[] cluster = Cluster(2000, 1);
        byte[] damage = new byte[16];
        byte[] cues = Cues();
        int head = SeekHead((SeekHeadId, 0)).Length;
        long cuesAt = head + info.Length + tracks.Length + cluster.Length + damage.Length;
        long secondAt = cuesAt + cues.Length;
        byte[] file = Concat(
            EbmlHeader("matroska"),
            Element(SegmentId, SeekHead((SeekHeadId, secondAt)), info, tracks, cluster, damage, cues, SeekHead((CuesId, cuesAt))));
        Assert.True(Read(file, "mkv").Matroska!.HasCues);
    }

    [Fact]
    public void CuesAtTheFrontWithoutSeekHead()
    {
        byte[] children = Concat(Info(), Tracks(1), Cues(), Cluster(2000, 1), Cluster(2000, 2));
        StructureArtifact artifact = Read(Concat(EbmlHeader("matroska"), Element(SegmentId, children)), "mkv");
        Assert.Empty(artifact.Issues);
        Assert.True(artifact.Matroska!.HasCues);
    }

    [Fact]
    public void UnfinalisedLiveRecordingHasNoCues()
    {
        byte[] children = Concat(Info(), Tracks(1, 2), UnknownSizeElement(ClusterId, UInt(0xE7, 0), Element(0xA3, new byte[500])));
        StructureArtifact artifact = Read(Concat(EbmlHeader("matroska"), UnknownSizeElement(SegmentId, children)), "mkv");
        Assert.Equal(StructureIssueKind.IndexMissingOrWrong, Assert.Single(artifact.Issues).Kind);
    }

    [Fact]
    public void TruncatedFileDoesNotAlsoReportMissingCues()
    {
        byte[] full = Mkv(unknownSize: true);
        StructureArtifact artifact = Read(Truncate(full, 0.7), "mkv");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("Cluster", issue.Element);
    }

    [Fact]
    public void BadEbmlHeader()
    {
        byte[] file = Mkv();
        file[4] = 0x00;
        StructureIssue issue = Assert.Single(Read(file, "mkv").Issues);
        Assert.Equal(StructureIssueKind.ContainerBroken, issue.Kind);
        Assert.Equal("EBML", issue.Element);
    }

    [Fact]
    public void MissingSegment()
    {
        StructureIssue issue = Assert.Single(Read(Concat(EbmlHeader("matroska"), Ascii("garbage instead of a segment")), "mkv").Issues);
        Assert.Equal(StructureIssueKind.ContainerBroken, issue.Kind);
        Assert.Equal("Segment", issue.Element);

        Assert.Equal(StructureIssueKind.Truncated, Assert.Single(Read(EbmlHeader("matroska"), "mkv").Issues).Kind);
    }

    [Fact]
    public void BytesAfterTheSegment()
    {
        Assert.Empty(Read(Concat(Mkv(), new byte[64]), "mkv").Issues);
        Assert.Empty(Read(Concat(Mkv(), Mkv()), "mkv").Issues);
        Assert.Equal(StructureIssueKind.TrailingData, Assert.Single(Read(Concat(Mkv(), Ascii("tail")), "mkv").Issues).Kind);
    }
}
