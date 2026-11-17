using System.Text.Json;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Calibration (subplan 15, step 4): with <c>MEDIAVERIFIER_CALIBRATION_DUMP</c> naming a file, the calibration tests
/// append every scanned variant's sampled frames (spatial information and SSIM per candidate rung) as JSON lines, so
/// that thresholds can be searched offline over exactly what the providers measured.
/// </summary>
public static class CalibrationDump
{
    public static void Append(EngineHarness harness, IEnumerable<(string Name, string Truth, bool Video, bool Original)> variants, string corpus)
    {
        ArgumentNullException.ThrowIfNull(harness);
        string? path = Environment.GetEnvironmentVariable("MEDIAVERIFIER_CALIBRATION_DUMP");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        using StreamWriter writer = File.AppendText(path);
        foreach ((string name, string truth, bool video, bool original) in variants)
        {
            FileRecord file = harness.AllFiles().Single(f => f.Name == name);
            if (!harness.Database.Artifacts.ForFile(file.Id).TryGetValue(ArtifactKind.FrameSamples, out (int, string Data) data)
                || new FrameSampleProvider().Deserialize(data.Data) is not FrameSamplesArtifact samples)
            {
                continue;
            }

            writer.WriteLine(JsonSerializer.Serialize(new
            {
                corpus,
                name,
                truth,
                video,
                original,
                candidates = samples.Candidates,
                height = samples.Height,
                frames = samples.Frames.Select(f => new { si = f.SpatialInfo, ssim = f.Ssim }),
                findings = harness.Database.Findings.ForFile(file.Id).Where(f => f.Status == FindingStatus.Open).Select(f => f.RuleId + "/" + f.Severity),
            }));
        }
    }
}
