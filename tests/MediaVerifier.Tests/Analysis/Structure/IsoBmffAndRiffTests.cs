using MediaVerifier.Core.Analysis.Structure;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Task C-02: the ISO-BMFF and RIFF walkers.</summary>
public sealed class IsoBmffAndRiffTests
{
    [Fact]
    public void HealthyFastStartMp4()
    {
        byte[] file = FastStartMp4();
        StructureArtifact artifact = Read(file, "mp4");
        Assert.Equal(ContainerFormat.IsoBmff, artifact.Sniff.Format);
        Assert.Equal("isom", artifact.Sniff.Brand);
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
        Assert.Equal(file.Length, artifact.FileLength);
    }

    [Fact]
    public void FreePaddingIsRecorded()
    {
        byte[] head = Concat(Ftyp("isom"), Moov());
        byte[] file = Concat(head, Box("free", new byte[64]), Box("skip", new byte[10]), Mdat(1000));
        StructureArtifact artifact = Read(file, "mp4");
        Assert.Empty(artifact.Issues);
        Assert.Equal([new PaddingRegion(head.Length + 8, 64), new PaddingRegion(head.Length + 8 + 64 + 8, 10)], artifact.Padding);
    }

    [Fact]
    public void BoxTruncatedAtSixtyPercent()
    {
        byte[] full = FastStartMp4();
        byte[] cut = Truncate(full, 0.6);
        StructureArtifact artifact = Read(cut, "mp4");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("mdat", issue.Element);
        Assert.Equal(full.Length - cut.Length, issue.MissingBytes);
        Assert.Equal(full.Length - Mdat(50_000).Length, issue.Offset);
        Assert.Equal($"mdat box ends {full.Length - cut.Length} bytes past the end of the file", issue.Detail);
        Assert.Equal(full.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void MoovMissing()
    {
        StructureArtifact artifact = Read(Concat(Ftyp("isom"), Mdat(5000)), "mp4");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.ContainerBroken, issue.Kind);
        Assert.Equal("moov box not found", issue.Detail);
        Assert.Equal("moov", issue.Element);
    }

    [Fact]
    public void IndexAtTheEndTruncatedLosesTheIndex()
    {
        byte[] full = MoovAtEndMp4();
        StructureArtifact artifact = Read(Truncate(full, 0.6), "mov");
        Assert.Equal(2, artifact.Issues.Count);
        Assert.Equal("mdat", Single(artifact, StructureIssueKind.Truncated).Element);
        Assert.Equal("moov box not found", Single(artifact, StructureIssueKind.ContainerBroken).Detail);
    }

    [Fact]
    public void MdatBeforeMoovIsNormal() => Assert.Empty(Read(MoovAtEndMp4(), "mov").Issues);

    [Fact]
    public void TrailingJunk()
    {
        byte[] file = FastStartMp4(2000);
        StructureArtifact artifact = Read(Concat(file, Ascii("this is not a box, just junk")), "mp4");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.TrailingData, issue.Kind);
        Assert.Equal(file.Length, issue.Offset);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void JunkThatLooksLikeABoxHeaderIsTrailingNotTruncation()
    {
        // "Hell" reads as a 1.2 GB size and "o, w" as a printable type.
        StructureArtifact artifact = Read(Concat(FastStartMp4(2000), Ascii("Hello, world")), "mp4");
        Assert.Equal(StructureIssueKind.TrailingData, Assert.Single(artifact.Issues).Kind);
    }

    [Fact]
    public void TrailingZerosAndShortTailsAreJudgedSensibly()
    {
        Assert.Empty(Read(Concat(FastStartMp4(2000), new byte[5000]), "mp4").Issues);
        Assert.Equal(StructureIssueKind.TrailingData, Assert.Single(Read(Concat(FastStartMp4(2000), [1, 2, 3]), "mp4").Issues).Kind);
    }

    [Fact]
    public void SixtyFourBitBoxSize()
    {
        byte[] large = LargeBox("mdat", new ByteBuilder().Noise(20_000, 2).ToArray());
        byte[] file = Concat(Ftyp("isom"), Moov(), large);
        StructureArtifact artifact = Read(file, "mp4");
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);

        byte[] cut = file[..^5000];
        StructureIssue issue = Assert.Single(Read(cut, "mp4").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal(5000, issue.MissingBytes);
    }

    [Fact]
    public void SizeZeroRunsToTheEndOfTheFile()
    {
        byte[] file = Concat(Ftyp("isom"), Moov(), new ByteBuilder().U32BE(0).Ascii("mdat").Noise(3000, 1).ToArray());
        StructureArtifact artifact = Read(file, "mp4");
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void BareMediaSegment()
    {
        byte[] segment = Concat(Box("styp", Ascii("msdh"), new byte[4]), Box("moof", Box("mfhd", new byte[8])), Mdat(3000));
        StructureIssue issue = Assert.Single(Read(Concat(Ftyp("iso6"), segment), "mp4").Issues);
        Assert.Equal(StructureIssueKind.ContainerBroken, issue.Kind);
        Assert.StartsWith("bare media segment", issue.Detail, StringComparison.Ordinal);

        // A fragmented file keeps a moov at the front: healthy.
        Assert.Empty(Read(Concat(Ftyp("iso6"), Moov(), Box("moof", Box("mfhd", new byte[8])), Mdat(3000)), "mp4").Issues);
    }

    [Fact]
    public void HeifImagesNeedMetaNotMoov()
    {
        Assert.Empty(Read(Concat(Ftyp("heic"), Box("meta", new byte[200]), Mdat(3000)), "heic").Issues);
        StructureIssue issue = Assert.Single(Read(Concat(Ftyp("avif"), Mdat(3000)), "avif").Issues);
        Assert.Equal("meta box not found", issue.Detail);
    }

    [Fact]
    public void HealthyWav()
    {
        byte[] file = Wav();
        StructureArtifact artifact = Read(file, "wav");
        Assert.Equal(ContainerFormat.Wav, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
    }

    [Fact]
    public void TruncatedWav()
    {
        byte[] full = Wav();
        byte[] cut = Truncate(full, 0.6);
        StructureIssue issue = Assert.Single(Read(cut, "wav").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("RIFF", issue.Element);
        Assert.Equal(full.Length - cut.Length, issue.MissingBytes);
    }

    [Theory]
    [InlineData(0u, "RIFF size is 0; the writer never finalised the file")]
    [InlineData(0xFFFFFFFFu, "RIFF size is 0xFFFFFFFF; the writer never finalised the file")]
    public void WavNeverFinalised(uint riffSize, string detail)
    {
        byte[] file = Wav();
        BitConverter.TryWriteBytes(file.AsSpan(4, 4), riffSize);
        StructureArtifact artifact = Read(file, "wav");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.IndexNotFinalised, issue.Kind);
        Assert.Equal(detail, issue.Detail);
        Assert.Null(artifact.DeclaredEnd);
    }

    [Fact]
    public void MissingFinalPadByteIsNotTruncation()
    {
        // An odd-sized data chunk: the RIFF size counts the pad byte, the writer never wrote it.
        byte[] file = Wav(7999)[..^1];
        Assert.Empty(Read(file, "wav").Issues);
        Assert.Equal(StructureIssueKind.Truncated, Assert.Single(Read(file[..^1], "wav").Issues).Kind);
    }

    [Fact]
    public void DataSizeNeverFinalised()
    {
        byte[] file = Wav();
        int data = file.AsSpan().IndexOf("data"u8);
        BitConverter.TryWriteBytes(file.AsSpan(data + 4, 4), 0xFFFFFFFFu);
        StructureIssue issue = Assert.Single(Read(file, "wav").Issues);
        Assert.Equal(StructureIssueKind.IndexNotFinalised, issue.Kind);
        Assert.Equal("data", issue.Element);
    }

    [Fact]
    public void WavTrailers()
    {
        Assert.Empty(Read(Concat(Wav(), Id3v1()), "wav").Issues);
        Assert.Empty(Read(Concat(Wav(), Id3v2()), "wav").Issues);
        Assert.Empty(Read(Concat(Wav(), [0]), "wav").Issues);
        Assert.Empty(Read(Concat(Wav(), new byte[4096]), "wav").Issues);
        StructureIssue issue = Assert.Single(Read(Concat(Wav(), Ascii("some appended garbage")), "wav").Issues);
        Assert.Equal(StructureIssueKind.TrailingData, issue.Kind);
        Assert.Equal(Wav().Length, issue.Offset);
    }

    [Fact]
    public void HealthyAndTruncatedAvi()
    {
        byte[] file = Avi();
        StructureArtifact artifact = Read(file, "avi");
        Assert.Equal(ContainerFormat.Avi, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        PaddingRegion junk = Assert.Single(artifact.Padding);
        Assert.Equal(100, junk.Length);

        StructureIssue issue = Assert.Single(Read(Truncate(file, 0.6), "avi").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
    }

    [Fact]
    public void OpenDmlAvixChunks()
    {
        byte[] avix = Riff("AVIX", List("movi", Chunk("00dc", new ByteBuilder().Noise(3000, 8).ToArray())));
        byte[] file = Concat(Avi(), avix);
        StructureArtifact artifact = Read(file, "avi");
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);

        StructureIssue issue = Assert.Single(Read(file[..^1000], "avi").Issues);
        Assert.Equal("AVIX", issue.Element);
        Assert.Equal(1000, issue.MissingBytes);
    }

    [Fact]
    public void TruncatedWebP()
    {
        Assert.Empty(Read(WebP(), "webp").Issues);
        byte[] full = WebP();
        byte[] cut = Truncate(full, 0.6);
        StructureIssue issue = Assert.Single(Read(cut, "webp").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal(full.Length - cut.Length, issue.MissingBytes);
    }

    [Fact]
    public void Rf64SizesComeFromDs64()
    {
        byte[] file = Rf64();
        StructureArtifact artifact = Read(file, "wav");
        Assert.Equal(ContainerFormat.Rf64, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        Assert.Equal(file.Length, artifact.DeclaredEnd);

        StructureIssue cut = Assert.Single(Read(file[..^2000], "wav").Issues);
        Assert.Equal(StructureIssueKind.Truncated, cut.Kind);
        Assert.Equal(2000, cut.MissingBytes);

        Assert.Equal(StructureIssueKind.IndexNotFinalised, Assert.Single(Read(Rf64(riffSize: 0), "wav").Issues).Kind);
    }

    [Fact]
    public void Rf64WithoutDs64IsNotFinalised()
    {
        byte[] file = Concat(new ByteBuilder().Ascii("RF64").U32LE(0xFFFFFFFF).Ascii("WAVE").ToArray(), Chunk("fmt ", PcmFormat()), Chunk("data", new byte[100]));
        Assert.Equal(StructureIssueKind.IndexNotFinalised, Assert.Single(Read(file, "wav").Issues).Kind);
    }
}
