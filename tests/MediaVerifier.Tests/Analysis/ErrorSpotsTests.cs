using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;

namespace MediaVerifier.Tests.Analysis;

/// <summary>Where decoder errors were: merging, placing between progress reports, and the wording (owner request).</summary>
public sealed class ErrorSpotsTests
{
    [Fact]
    public void Errors_within_two_seconds_of_a_spot_belong_to_it()
    {
        Assert.Equal([1.2, 5.0, 12.4], ErrorSpots.Merge([5.0, 1.2, 1.9, 3.1, 6.9, 12.4, 13.0, double.NaN]));
        Assert.Equal(ErrorSpots.Max, ErrorSpots.Merge(Enumerable.Range(0, 200).Select(i => i * 3.0)).Length);
        Assert.Empty(ErrorSpots.Merge([]));
    }

    [Fact]
    public void An_error_is_placed_between_the_progress_reports_around_it()
    {
        // Reports at 0.1 s (12 s of media) and 0.2 s (30 s): an error at 0.15 s is half way, at 21 s.
        (double, double)[] marks = [(0.1, 12), (0.2, 30), (0.3, 40)];
        List<double> positions = ErrorSpots.Interpolate([0.05, 0.15, 0.3, 0.9], marks);
        Assert.Equal(4, positions.Count);
        Assert.Equal(6, positions[0], 3);
        Assert.Equal(21, positions[1], 3);
        Assert.Equal(40, positions[2], 3);
        Assert.Equal(40, positions[3], 3);
        Assert.Equal([0.0], ErrorSpots.Interpolate([0.5], []));
    }

    [Fact]
    public void The_finding_says_around_where()
    {
        Assert.Equal(string.Empty, EvidenceText.Around(null));
        Assert.Equal(string.Empty, EvidenceText.Around([]));
        Assert.Equal(", around 0:11", EvidenceText.Around([11.2]));
        Assert.Equal(", around 1:23 and 4:05", EvidenceText.Around([83, 245]));
        Assert.Equal(", around 1:23, 4:05 and 12:40", EvidenceText.Around([83, 245, 760]));
        Assert.Equal(", around 1:23, 4:05, 12:40 and 1 more place", EvidenceText.Around([83, 245, 760, 900]));
        Assert.Equal(", around 1:23, 4:05, 12:40 and 2 more places", EvidenceText.Around([83, 245, 760, 900, 3700]));
    }
}
