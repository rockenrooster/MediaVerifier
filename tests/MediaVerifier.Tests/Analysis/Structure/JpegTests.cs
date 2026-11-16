using MediaVerifier.Core.Analysis.Structure;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Task C-04: the JPEG marker walker.</summary>
public sealed class JpegTests
{
    [Fact]
    public void Baseline()
    {
        byte[] file = BaselineJpeg(640, 480);
        StructureArtifact artifact = Read(file, "jpg");
        Assert.Empty(artifact.Issues);
        JpegInfo jpeg = artifact.Jpeg!;
        Assert.Equal(640, jpeg.Width);
        Assert.Equal(480, jpeg.Height);
        Assert.False(jpeg.Progressive);
        Assert.Equal(1, jpeg.ScanCount);
        Assert.True(jpeg.HasEoi);
        Assert.Equal(file.Length - 2, jpeg.EoiOffset);
        Assert.Equal(0, jpeg.BytesAfterEoi);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void ProgressiveWithSeveralScans()
    {
        JpegInfo jpeg = Read(ProgressiveJpeg(scans: 5), "jpg").Jpeg!;
        Assert.True(jpeg.Progressive);
        Assert.Equal(5, jpeg.ScanCount);
        Assert.Equal(800, jpeg.Width);
        Assert.Equal(600, jpeg.Height);
        Assert.True(jpeg.HasEoi);
    }

    [Fact]
    public void RestartMarkersInsideEntropyData()
    {
        byte[] file = BaselineJpeg(restartMarkers: true);
        Assert.Contains(0xD3, Enumerable.Range(0, file.Length - 1).Where(i => file[i] == 0xFF).Select(i => (int)file[i + 1]));
        StructureArtifact artifact = Read(file, "jpg");
        Assert.Empty(artifact.Issues);
        Assert.Equal(1, artifact.Jpeg!.ScanCount);
        Assert.Equal(file.Length - 2, artifact.Jpeg.EoiOffset);
    }

    [Fact]
    public void FillBytesBeforeAMarker()
    {
        byte[] file = Concat(BaselineJpeg()[..^2], [0xFF, 0xFF, 0xFF, 0xD9]);
        StructureArtifact artifact = Read(file, "jpg");
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length - 2, artifact.Jpeg!.EoiOffset);
    }

    [Fact]
    public void LongRunsOfFillBytes()
    {
        byte[] file = Concat(BaselineJpeg()[..^2], new ByteBuilder().Fill(200_000, 0xFF).ToArray(), [0xD9]);
        StructureArtifact artifact = Read(file, "jpg");
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length - 2, artifact.Jpeg!.EoiOffset);

        // Fill bytes between segments, too.
        byte[] segments = Concat(Soi, new ByteBuilder().Fill(10_000, 0xFF).ToArray(), App0()[1..], Sof(0xC0, 8, 8), Sos(), Entropy(100, 1), EoiMarker);
        Assert.Equal(8, Read(segments, "jpg").Jpeg!.Width);
    }

    [Fact]
    public void StrayBytesBetweenSegmentsAreSkipped()
    {
        byte[] file = Concat(Soi, App0(), Ascii("stray"), Dqt((0, 0, Table(1))), Sof(0xC0, 32, 16), Dht(), Sos(), Entropy(500, 3), EoiMarker);
        StructureArtifact artifact = Read(file, "jpg");
        Assert.Empty(artifact.Issues);
        Assert.Equal(32, artifact.Jpeg!.Width);
    }

    [Fact]
    public void NoEoi()
    {
        StructureArtifact artifact = Read(Truncate(BaselineJpeg(), 0.6), "jpg");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("EOI", issue.Element);
        Assert.False(artifact.Jpeg!.HasEoi);
        Assert.Equal(-1, artifact.Jpeg.EoiOffset);
        Assert.Null(artifact.DeclaredEnd);
    }

    [Fact]
    public void SegmentLengthPastTheEnd()
    {
        byte[] head = Concat(Soi, App0());
        byte[] dqt = Dqt((0, 0, Table(1)), (0, 1, Table(2)));
        byte[] file = Concat(head, dqt[..40]);
        StructureIssue issue = Assert.Single(Read(file, "jpg").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("DQT", issue.Element);
        Assert.Equal(head.Length, issue.Offset);
        Assert.Equal(dqt.Length - 40, issue.MissingBytes);
    }

    [Fact]
    public void TrailingGarbage()
    {
        byte[] image = BaselineJpeg();
        byte[] garbage = Ascii("appended by something, not a picture");
        StructureArtifact artifact = Read(Concat(image, garbage), "jpg");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.TrailingData, issue.Kind);
        Assert.Equal(image.Length, issue.Offset);
        Assert.Equal(garbage.Length, artifact.Jpeg!.BytesAfterEoi);
        Assert.Equal(image.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void MultiPictureTrailer()
    {
        byte[] image = BaselineJpeg();
        byte[] second = BaselineJpeg(160, 120);
        StructureArtifact artifact = Read(Concat(image, second), "jpg");
        Assert.Empty(artifact.Issues);
        Assert.Equal(second.Length, artifact.Jpeg!.BytesAfterEoi);
        Assert.Equal(640, artifact.Jpeg.Width);
    }

    [Fact]
    public void MotionPhotoTrailer()
    {
        byte[] video = FastStartMp4(5000);
        StructureArtifact artifact = Read(Concat(BaselineJpeg(), video), "jpg");
        Assert.Empty(artifact.Issues);
        Assert.Equal(video.Length, artifact.Jpeg!.BytesAfterEoi);
    }

    [Fact]
    public void PaddingAndSamsungTrailers()
    {
        Assert.Empty(Read(Concat(BaselineJpeg(), new byte[3000]), "jpg").Issues);
        Assert.Empty(Read(Concat(BaselineJpeg(), new ByteBuilder().Fill(100, 0xFF).Fill(100).ToArray()), "jpg").Issues);
        Assert.Empty(Read(Concat(BaselineJpeg(), Ascii("Image_UTC_Data1700000000000SEFHxxxxSEFT")), "jpg").Issues);
        Assert.Empty(Read(Concat(BaselineJpeg(), Ascii("MotionPhoto_Data"), FastStartMp4(500)), "jpg").Issues);
    }

    [Fact]
    public void QuantisationTablesEightAndSixteenBit()
    {
        int[] wide = [.. Enumerable.Range(0, 64).Select(i => 256 + (i * 3))];
        byte[] file = Concat(Soi, Dqt((0, 0, Table(1)), (1, 1, wide)), Dqt((0, 0, Table(90))), Sof(0xC0, 16, 16), Dht(), Sos(), Entropy(200, 1), EoiMarker);
        JpegInfo jpeg = Read(file, "jpg").Jpeg!;
        Assert.Equal(2, jpeg.QuantTables.Count);
        Assert.Equal(Table(1), jpeg.QuantTables[0]);
        Assert.Equal(wide, jpeg.QuantTables[1]);
    }

    [Fact]
    public void QuantisationTablesAreIndexedById()
    {
        byte[] file = Concat(Soi, Dqt((0, 2, Table(5))), Sof(0xC0, 16, 16), Dht(), Sos(), Entropy(200, 1), EoiMarker);
        JpegInfo jpeg = Read(file, "jpg").Jpeg!;
        Assert.Equal(3, jpeg.QuantTables.Count);
        Assert.Empty(jpeg.QuantTables[0]);
        Assert.Empty(jpeg.QuantTables[1]);
        Assert.Equal(Table(5), jpeg.QuantTables[2]);

        Assert.Empty(Read(Concat(Soi, Sof(0xC0, 16, 16), Sos(), Entropy(100, 2), EoiMarker), "jpg").Jpeg!.QuantTables);
    }

    [Fact]
    public void SofVariantsGiveDimensions()
    {
        foreach (byte marker in new byte[] { 0xC1, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF })
        {
            byte[] file = Concat(Soi, Sof(marker, 123, 45), Sos(), Entropy(100, marker), EoiMarker);
            JpegInfo jpeg = Read(file, "jpg").Jpeg!;
            Assert.Equal(123, jpeg.Width);
            Assert.Equal(45, jpeg.Height);
            Assert.Equal(marker is 0xC6 or 0xCA or 0xCE, jpeg.Progressive);
        }
    }
}
