using MediaVerifier.Core.Analysis.Structure;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Task C-13: the MP3 VBR header check.</summary>
public sealed class Mp3Tests
{
    [Fact]
    public void XingCountRight()
    {
        byte[] file = Mp3(200, "Xing");
        StructureArtifact artifact = Read(file, "mp3");
        Assert.Empty(artifact.Issues);
        Mp3Info mp3 = artifact.Mp3!;
        int id3 = Id3v2().Length;
        Assert.Equal(id3, mp3.AudioStart);
        Assert.Equal(201 * FrameLength, mp3.AudioBytes);
        Assert.Equal(200, mp3.DeclaredFrames);
        Assert.Equal(201 * FrameLength, mp3.DeclaredBytes);
        Assert.Equal("Xing", mp3.VbrHeader);
        Assert.Equal(128_000, mp3.Bitrate);
        Assert.Equal(44100, mp3.SampleRate);
        Assert.Equal(2, mp3.Channels);
        Assert.Equal(file.Length, artifact.DeclaredEnd);
        Assert.Equal(id3, artifact.Sniff.Id3v2Length);
    }

    [Fact]
    public void TooFewBytesIsTruncation()
    {
        byte[] full = Mp3(200, "Xing");
        byte[] cut = Truncate(full, 0.55);
        StructureIssue issue = Assert.Single(Read(cut, "mp3").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("Xing", issue.Element);
        Assert.Equal(full.Length - cut.Length, issue.MissingBytes);
    }

    [Fact]
    public void TooManyBytesIsAWrongIndex()
    {
        // Two files joined byte for byte: the first header still declares only its own frames.
        byte[] joined = Concat(Mp3(100, "Xing"), Mp3(100, "Xing", id3: false));
        StructureIssue issue = Assert.Single(Read(joined, "mp3").Issues);
        Assert.Equal(StructureIssueKind.IndexMissingOrWrong, issue.Kind);
        Assert.Contains("longer than the Xing header declares", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WithinOnePercentIsFine()
    {
        Assert.Empty(Read(Mp3(200, "Xing", declaredBytes: (uint)(201 * FrameLength) + 500), "mp3").Issues);
        Assert.Empty(Read(Mp3(200, "Xing", declaredBytes: (uint)(201 * FrameLength) - 500), "mp3").Issues);
    }

    [Fact]
    public void Id3v1AndApeTagsAreSubtracted()
    {
        byte[] audio = Mp3(200, "Xing");
        byte[] file = Concat(audio, ApeTag(3000), Id3v1());
        StructureArtifact artifact = Read(file, "mp3");
        Assert.Empty(artifact.Issues);
        Assert.Equal(201 * FrameLength, artifact.Mp3!.AudioBytes);

        // Without subtraction the 3 KiB APE tag would exceed the tolerance.
        Assert.True(3000 > 0.01 * 201 * FrameLength);
        Assert.Equal(201 * FrameLength, Read(Concat(audio, Id3v1()), "mp3").Mp3!.AudioBytes);
        Assert.Equal(201 * FrameLength, Read(Concat(audio, ApeTag(3000)), "mp3").Mp3!.AudioBytes);
    }

    [Fact]
    public void CbrInfoHeaderWithAWrongFrameCount()
    {
        StructureIssue issue = Assert.Single(Read(Mp3(200, "Info", declaredFrames: 90), "mp3").Issues);
        Assert.Equal(StructureIssueKind.IndexMissingOrWrong, issue.Kind);
        Assert.Equal("Info", issue.Element);
        Assert.Empty(Read(Mp3(200, "Info"), "mp3").Issues);
    }

    [Fact]
    public void CbrInfoHeaderWithFramesOnly()
    {
        Assert.Empty(Read(Mp3(200, "Info", withBytes: false), "mp3").Issues);
        Assert.Equal(StructureIssueKind.Truncated, Assert.Single(Read(Truncate(Mp3(200, "Info", withBytes: false), 0.5), "mp3").Issues).Kind);

        // A VBR (Xing) header with frames only cannot be checked without walking every frame.
        Assert.Empty(Read(Truncate(Mp3(200, "Xing", withBytes: false), 0.5), "mp3").Issues);
    }

    [Fact]
    public void VbriHeader()
    {
        byte[] file = Mp3(150, "VBRI");
        Mp3Info mp3 = Read(file, "mp3").Mp3!;
        Assert.Equal("VBRI", mp3.VbrHeader);
        Assert.Equal(150, mp3.DeclaredFrames);
        Assert.Equal(151 * FrameLength, mp3.DeclaredBytes);
        Assert.Equal(StructureIssueKind.Truncated, Assert.Single(Read(Truncate(file, 0.5), "mp3").Issues).Kind);
    }

    [Fact]
    public void CbrWithoutVbrHeaderHasNothingToCompare()
    {
        byte[] file = Concat([Id3v2(), .. Enumerable.Range(0, 50).Select(_ => Mp3Frame())]);
        StructureArtifact artifact = Read(Truncate(file, 0.5), "mp3");
        Assert.Empty(artifact.Issues);
        Assert.Null(artifact.Mp3!.VbrHeader);
        Assert.Null(artifact.Mp3.DeclaredBytes);
        Assert.Null(artifact.DeclaredEnd);
    }

    [Fact]
    public void JunkBetweenTheTagAndTheFirstFrame()
    {
        byte[] tag = Id3v2();
        byte[] file = Concat(tag, new byte[300], Mp3(100, "Xing", id3: false));
        StructureArtifact artifact = Read(file, "mp3");
        Assert.Empty(artifact.Issues);
        Assert.Equal(tag.Length + 300, artifact.Mp3!.AudioStart);
    }

    [Fact]
    public void LeadingJunkWithoutATagNeedsTheExtension()
    {
        byte[] file = Concat(Ascii("junk before the audio"), Mp3(100, "Xing", id3: false));
        Assert.Equal(ContainerFormat.Unknown, Read(file, "bin").Sniff.Format);
        StructureArtifact artifact = Read(file, "MP3");
        Assert.Equal(ContainerFormat.Mp3, artifact.Sniff.Format);
        Assert.Equal(21, artifact.Mp3!.AudioStart);
    }

    [Fact]
    public void Id3TagPastTheEndOfTheFile()
    {
        byte[] tag = Id3v2(paddingBytes: 2000);
        StructureArtifact artifact = Read(tag[..500], "mp3");
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("ID3v2", issue.Element);
        Assert.Equal(tag.Length - 500, issue.MissingBytes);
    }

    [Fact]
    public void Id3v2PaddingIsRecorded()
    {
        byte[] tag = Id3v2(paddingBytes: 4000);
        StructureArtifact artifact = Read(Concat(tag, Mp3(20, id3: false)), "mp3");
        Assert.Equal([new PaddingRegion(10 + 16, 4000)], artifact.Padding);
    }

    [Fact]
    public void FirstFrameWithoutAudioIsNotAnIssue()
    {
        StructureArtifact artifact = Read(Concat(Id3v2(), Ascii("no audio frames here at all")), "mp3");
        Assert.Equal(ContainerFormat.Mp3, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        Assert.Null(artifact.Mp3);
    }
}
