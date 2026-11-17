using System.Text.Json;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Storage;

/// <summary>Tools → Create report files (task UI-09).</summary>
public sealed class ReportWriterTests
{
    private static readonly FindingRow[] Rows =
    [
        new(1, 10, "AUD-CLIPPING", FindingCategory.Quality, Severity.Medium, 0.9f, "=cmd, \"quoted\"", FindingStatus.Open, 0, 0, "a,b.flac", "Album", 1, @"D:\Music", MediaKind.Audio, 1234, """{"runs":12}"""),
        new(2, 11, "COR-DECODE", FindingCategory.Corruption, Severity.High, 1f, "<b>bad</b>", FindingStatus.Open, 0, 0, "c.mkv", "", 1, @"D:\Music", MediaKind.Video, 99, null),
    ];

    [Fact]
    public void Csv_escapes_separators_and_neutralises_formulas()
    {
        using TempDirectory temp = new("report");
        string path = temp.Combine("r.csv");
        ReportWriter.Write(path, ReportFormat.Csv, Rows, DateTimeOffset.UtcNow);
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"'=cmd, \"\"quoted\"\"\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"a,b.flac\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Json_carries_paths_and_evidence()
    {
        using TempDirectory temp = new("report");
        string path = temp.Combine("r.json");
        ReportWriter.Write(path, ReportFormat.Json, Rows, DateTimeOffset.UtcNow);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement first = document.RootElement.GetProperty("findings")[0];
        Assert.Equal(@"D:\Music\Album\a,b.flac", first.GetProperty("path").GetString());
        Assert.Equal(12, first.GetProperty("evidence").GetProperty("runs").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void Web_page_encodes_text()
    {
        using TempDirectory temp = new("report");
        string path = temp.Combine("r.html");
        ReportWriter.Write(path, ReportFormat.Html, Rows, DateTimeOffset.UtcNow);
        string html = File.ReadAllText(path);
        Assert.Contains("&lt;b&gt;bad&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bad</b>", html, StringComparison.Ordinal);
    }
}
