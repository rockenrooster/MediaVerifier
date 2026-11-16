using MediaVerifier.Core.Analysis.Structure;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Task C-03: the FLAC header reader.</summary>
public sealed class FlacTests
{
    [Fact]
    public void StreamInfoValuesAndMd5AtOffset26()
    {
        byte[] file = Flac();
        StructureArtifact artifact = Read(file, "flac");
        Assert.Empty(artifact.Issues);
        FlacInfo flac = artifact.Flac!;
        Assert.Equal(44100, flac.SampleRate);
        Assert.Equal(2, flac.Channels);
        Assert.Equal(16, flac.BitsPerSample);
        Assert.Equal(882_000, flac.TotalSamples);
        Assert.Equal(8, flac.StreamInfoOffset);
        Assert.Equal("0123456789abcdeffedcba9876543210", flac.Md5Hex);
        Assert.Equal(Convert.ToHexStringLower(file.AsSpan(26, 16)), flac.Md5Hex);
        Assert.Null(artifact.DeclaredEnd);
    }

    [Fact]
    public void ExtremeStreamInfoValues()
    {
        byte[] file = Concat(
            Ascii("fLaC"),
            FlacBlock(0, true, StreamInfo(655_350, 8, 32, (1L << 36) - 1, SampleMd5)),
            [0xFF, 0xF9, 0x00, 0x00]);
        FlacInfo flac = Read(file, "flac").Flac!;
        Assert.Equal(655_350, flac.SampleRate);
        Assert.Equal(8, flac.Channels);
        Assert.Equal(32, flac.BitsPerSample);
        Assert.Equal((1L << 36) - 1, flac.TotalSamples);
    }

    [Fact]
    public void ZeroMd5MeansNotComputed() => Assert.Null(Read(Flac(md5: new byte[16]), "flac").Flac!.Md5Hex);

    [Fact]
    public void PaddingBlockIsRecorded()
    {
        byte[] file = Flac(paddingBytes: 4096);
        StructureArtifact artifact = Read(file, "flac");
        int padding = 4 + 4 + 34 + 4 + 12 + 4;
        Assert.Equal([new PaddingRegion(padding, 4096)], artifact.Padding);
    }

    [Fact]
    public void Id3v2PrefixedFlac()
    {
        byte[] tag = Id3v2(paddingBytes: 300);
        StructureArtifact artifact = Read(Concat(tag, Flac()), "flac");
        Assert.Equal(ContainerFormat.Flac, artifact.Sniff.Format);
        Assert.Equal(tag.Length, artifact.Sniff.Id3v2Length);
        Assert.Empty(artifact.Issues);
        Assert.Equal(tag.Length + 8, artifact.Flac!.StreamInfoOffset);
        Assert.Equal(44100, artifact.Flac.SampleRate);

        // The tag's padding and the FLAC PADDING block are both recorded.
        Assert.Equal(2, artifact.Padding.Count);
        Assert.Equal(new PaddingRegion(10 + 16, 300), artifact.Padding[0]);
    }

    [Fact]
    public void BrokenChainNoFrameSync()
    {
        // The last block (PADDING, 1024 bytes) claims 1022, so the chain ends inside it.
        byte[] file = Flac();
        int padding = 4 + 4 + 34 + 4 + 12;
        file[padding + 1] = 0x00;
        file[padding + 2] = 0x03;
        file[padding + 3] = 0xFE;
        StructureIssue issue = Assert.Single(Read(file, "flac").Issues);
        Assert.Equal(StructureIssueKind.ContainerBroken, issue.Kind);
        Assert.StartsWith("no frame sync", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenChainInvalidBlockType()
    {
        byte[] file = Flac();
        file[4 + 4 + 34] = 0x7F;
        StructureIssue issue = Assert.Single(Read(file, "flac").Issues);
        Assert.Equal(StructureIssueKind.ContainerBroken, issue.Kind);
        Assert.Contains("type 127", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamInfoMustComeFirst()
    {
        byte[] file = Concat(Ascii("fLaC"), FlacBlock(4, false, new byte[8]), FlacBlock(0, true, StreamInfo(44100, 2, 16, 10, SampleMd5)), [0xFF, 0xF8]);
        StructureArtifact artifact = Read(file, "flac");
        Assert.Equal(StructureIssueKind.ContainerBroken, Assert.Single(artifact.Issues).Kind);
        Assert.Null(artifact.Flac);
    }

    [Fact]
    public void TruncatedInsideTheMetadata()
    {
        byte[] file = Flac(paddingBytes: 4096);
        byte[] cut = file[..1000];
        StructureIssue issue = Assert.Single(Read(cut, "flac").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("PADDING", issue.Element);
        Assert.Equal(4 + 4 + 34 + 4 + 12 + 4 + 4096 - 1000, issue.MissingBytes);
    }

    [Fact]
    public void NoAudioAfterTheMetadata()
    {
        byte[] file = Concat(Ascii("fLaC"), FlacBlock(0, true, StreamInfo(44100, 2, 16, 1000, SampleMd5)));
        Assert.Equal(StructureIssueKind.Truncated, Assert.Single(Read(file, "flac").Issues).Kind);

        // An empty stream (no samples) has no frames to miss.
        Assert.Empty(Read(Concat(Ascii("fLaC"), FlacBlock(0, true, StreamInfo(44100, 2, 16, 0, new byte[16]))), "flac").Issues);
    }
}
