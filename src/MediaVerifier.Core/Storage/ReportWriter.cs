using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;

namespace MediaVerifier.Core.Storage;

/// <summary>The formats of Tools → Create report (subplan 09).</summary>
public enum ReportFormat
{
    Html,
    Csv,
    Json,
}

/// <summary>
/// Writes findings to a report file (task UI-09): a self-contained web page, a spreadsheet file or JSON.
/// Numbers and dates use the invariant culture except in the web page, which is for reading.
/// </summary>
public static class ReportWriter
{
    private static readonly string[] Columns = ["Severity", "Rule", "Title", "Summary", "Confidence", "File", "Folder", "Size", "First seen (UTC)"];

    public static string Extension(ReportFormat format) => format switch
    {
        ReportFormat.Csv => ".csv",
        ReportFormat.Json => ".json",
        _ => ".html",
    };

    public static void Write(string path, ReportFormat format, IReadOnlyList<FindingRow> rows, DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string temp = path + ".tmp";
        using (StreamWriter writer = new(temp, false, new UTF8Encoding(format == ReportFormat.Csv)))
        {
            switch (format)
            {
                case ReportFormat.Csv:
                    WriteCsv(writer, rows);
                    break;
                case ReportFormat.Json:
                    WriteJson(writer, rows, createdUtc);
                    break;
                default:
                    WriteHtml(writer, rows, createdUtc);
                    break;
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string Title(FindingRow row) => RuleCatalog.TryGet(row.RuleId, out RuleDefinition rule) ? rule.Title : row.RuleId;

    private static string FullPath(FindingRow row) => row.RelDir.Length == 0 ? Path.Combine(row.RootPath, row.FileName) : Path.Combine(row.RootPath, row.RelDir, row.FileName);

    private static string Folder(FindingRow row) => row.RelDir.Length == 0 ? row.RootPath : Path.Combine(row.RootPath, row.RelDir);

    private static string Utc(long ticks) => new DateTimeOffset(ticks, TimeSpan.Zero).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static void WriteCsv(StreamWriter writer, IReadOnlyList<FindingRow> rows)
    {
        writer.WriteLine(string.Join(',', Columns));
        foreach (FindingRow row in rows)
        {
            string[] cells =
            [
                row.Severity.ToString(), row.RuleId, Title(row), row.Summary, row.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
                row.FileName, Folder(row), row.Size.ToString(CultureInfo.InvariantCulture), Utc(row.FirstUtc),
            ];
            writer.WriteLine(string.Join(',', cells.Select(Csv)));
        }
    }

    private static string Csv(string value)
    {
        // Leading = + - @ would start a formula in a spreadsheet: prefix an apostrophe.
        string safe = value.Length > 0 && "=+-@".Contains(value[0], StringComparison.Ordinal) ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    private static void WriteJson(StreamWriter writer, IReadOnlyList<FindingRow> rows, DateTimeOffset createdUtc)
    {
        using Utf8JsonWriter json = new(writer.BaseStream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("product", AppInfo.ProductName);
        json.WriteString("version", AppInfo.VersionText);
        json.WriteString("createdUtc", createdUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        json.WriteStartArray("findings");
        foreach (FindingRow row in rows)
        {
            json.WriteStartObject();
            json.WriteString("severity", row.Severity.ToString());
            json.WriteString("rule", row.RuleId);
            json.WriteString("title", Title(row));
            json.WriteString("summary", row.Summary);
            json.WriteNumber("confidence", Math.Round(row.Confidence, 2));
            json.WriteString("path", FullPath(row));
            json.WriteNumber("size", row.Size);
            json.WriteString("firstSeenUtc", new DateTimeOffset(row.FirstUtc, TimeSpan.Zero).UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(row.EvidenceJson))
            {
                json.WritePropertyName("evidence");
                using JsonDocument evidence = JsonDocument.Parse(row.EvidenceJson);
                evidence.RootElement.WriteTo(json);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    private static void WriteHtml(StreamWriter writer, IReadOnlyList<FindingRow> rows, DateTimeOffset createdUtc)
    {
        writer.WriteLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>MediaVerifier report</title>");
        writer.WriteLine("<style>body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #ddd;font-size:13px}th{color:#555}td.n{text-align:right}</style></head><body>");
        writer.WriteLine($"<h1>MediaVerifier report</h1><p>{WebUtility.HtmlEncode(AppInfo.WindowTitle)} · {rows.Count.ToString("N0", CultureInfo.CurrentCulture)} findings · {WebUtility.HtmlEncode(createdUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))}</p>");
        writer.WriteLine("<table><thead><tr>" + string.Concat(Columns.Select(c => "<th>" + c + "</th>")) + "</tr></thead><tbody>");
        foreach (FindingRow row in rows)
        {
            writer.WriteLine(
                "<tr><td>" + row.Severity + "</td><td>" + WebUtility.HtmlEncode(row.RuleId) + "</td><td>" + WebUtility.HtmlEncode(Title(row)) + "</td><td>" + WebUtility.HtmlEncode(row.Summary)
                + "</td><td class=\"n\">" + row.Confidence.ToString("0.00", CultureInfo.CurrentCulture) + "</td><td>" + WebUtility.HtmlEncode(row.FileName) + "</td><td>" + WebUtility.HtmlEncode(Folder(row))
                + "</td><td class=\"n\">" + row.Size.ToString("N0", CultureInfo.CurrentCulture) + "</td><td>" + Utc(row.FirstUtc) + "</td></tr>");
        }

        writer.WriteLine("</tbody></table></body></html>");
    }
}
