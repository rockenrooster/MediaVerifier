using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>An exact fraction such as a frame rate (<c>24000/1001</c>).</summary>
public readonly record struct Rational(long Numerator, long Denominator)
{
    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    public static Rational? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole) ? new Rational(whole, 1) : null;
        }

        if (long.TryParse(text.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            && long.TryParse(text.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long d)
            && d != 0)
        {
            return new Rational(n, d);
        }

        return null;
    }
}

/// <summary>One stream of a probe result (subplan 04, Probe model).</summary>
public sealed record ProbeStream(
    int Index,
    string CodecType,
    string? CodecName,
    string? Profile,
    int? Width,
    int? Height,
    string? PixFmt,
    string? FieldOrder,
    Rational? RFrameRate,
    Rational? AvgFrameRate,
    long? NbFrames,
    int? SampleRate,
    int? Channels,
    string? SampleFmt,
    int? BitsPerRawSample,
    double? DurationSeconds,
    long? BitRate,
    string? ColorTransfer,
    string? ColorPrimaries,
    string? ColorSpace,
    bool IsAttachedPicture,
    bool IsDefault,
    IReadOnlyDictionary<string, string> Tags)
{
    public bool IsVideo => CodecType == "video" && !IsAttachedPicture;

    public bool IsAudio => CodecType == "audio";
}

/// <summary>What ffprobe said about a file. <see cref="Opened"/> is decided from the streams and the log, never the exit code.</summary>
public sealed record ProbeResult(
    bool Opened,
    string? FormatName,
    int ProbeScore,
    double? DurationSeconds,
    long? SizeBytes,
    long? BitRate,
    IReadOnlyDictionary<string, string> FormatTags,
    IReadOnlyList<ProbeStream> Streams,
    LogSummary Log)
{
    public static ProbeResult NotOpened(LogSummary log) => new(false, null, 0, null, null, null, new Dictionary<string, string>(), [], log);

    /// <summary>The first real video stream (cover art excluded).</summary>
    public ProbeStream? Video => Streams.FirstOrDefault(s => s.IsVideo);

    /// <summary>The default audio stream, or the first one.</summary>
    public ProbeStream? Audio => Streams.FirstOrDefault(s => s.IsAudio && s.IsDefault) ?? Streams.FirstOrDefault(s => s.IsAudio);

    /// <summary>
    /// A probe score below 25 with no decodable stream means unrecognised, even when a format name is
    /// present (ffprobe names an empty .flac file "flac").
    /// </summary>
    public bool Unrecognised => !Opened || (ProbeScore < 25 && !Streams.Any(s => s.CodecName is not null && (s.IsAudio || s.CodecType == "video")));

    /// <summary>The media summary columns stored with the file row.</summary>
    public MediaSummary ToSummary()
    {
        ProbeStream? video = Video;
        ProbeStream? audio = Audio;
        long? durationMs = DurationSeconds is double d && double.IsFinite(d) ? (long)Math.Round(d * 1000) : null;
        int? fpsMilli = video?.AvgFrameRate is Rational avg && avg.Value > 0 ? (int)Math.Round(avg.Value * 1000)
            : video?.RFrameRate is Rational r && r.Value > 0 ? (int)Math.Round(r.Value * 1000) : null;
        int? bits = audio?.BitsPerRawSample is int raw && raw > 0 ? raw : BitsFromSampleFormat(audio?.SampleFmt);
        return new MediaSummary(
            FormatName?.Split(',')[0],
            video?.CodecName,
            audio?.CodecName,
            video?.Width ?? Streams.FirstOrDefault(s => s.CodecType == "video")?.Width,
            video?.Height ?? Streams.FirstOrDefault(s => s.CodecType == "video")?.Height,
            durationMs,
            BitRate,
            audio?.SampleRate,
            audio?.Channels,
            bits,
            fpsMilli);
    }

    private static int? BitsFromSampleFormat(string? format) => format switch
    {
        "s16" or "s16p" => 16,
        "u8" or "u8p" => 8,
        _ => null,
    };
}

/// <summary>Parses ffprobe JSON defensively: every number may be a string, absent or <c>N/A</c>.</summary>
public static class ProbeParser
{
    public static ProbeResult Parse(string? json, LogSummary log)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ProbeResult.NotOpened(log);
        }

        FfprobeOutput? output;
        try
        {
            output = JsonSerializer.Deserialize(json, FfprobeJsonContext.Default.FfprobeOutput);
        }
        catch (JsonException)
        {
            return ProbeResult.NotOpened(log);
        }

        if (output is null)
        {
            return ProbeResult.NotOpened(log);
        }

        List<ProbeStream> streams = [];
        foreach (FfprobeStream s in output.Streams ?? [])
        {
            streams.Add(new ProbeStream(
                s.Index,
                s.CodecType ?? "unknown",
                Text(s.CodecName),
                Text(s.Profile),
                Int(s.Width),
                Int(s.Height),
                Text(s.PixFmt),
                Text(s.FieldOrder),
                Rational.Parse(Text(s.RFrameRate)),
                Rational.Parse(Text(s.AvgFrameRate)),
                Long(s.NbFrames),
                Int(s.SampleRate),
                Int(s.Channels),
                Text(s.SampleFmt),
                Int(s.BitsPerRawSample),
                Double(s.Duration),
                Long(s.BitRate),
                Text(s.ColorTransfer),
                Text(s.ColorPrimaries),
                Text(s.ColorSpace),
                s.Disposition?.AttachedPic == 1,
                s.Disposition?.Default == 1,
                s.Tags ?? new Dictionary<string, string>()));
        }

        FfprobeFormat? format = output.Format;
        bool fileLevelInvalid = log.Kinds.TryGetValue(LogSignalKind.OpenFailure, out SignalSummary? open) && open.Count > 0;
        bool opened = streams.Any(s => s.CodecName is not null) && !fileLevelInvalid;
        return new ProbeResult(
            opened,
            Text(format?.FormatName),
            Int(format?.ProbeScore) ?? 0,
            Double(format?.Duration),
            Long(format?.Size),
            Long(format?.BitRate),
            format?.Tags ?? new Dictionary<string, string>(),
            streams,
            log);
    }

    private static string? Text(JsonElement? element) => element is { ValueKind: JsonValueKind.String } e ? Clean(e.GetString()) : element is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : null;

    private static string? Text(string? value) => Clean(value);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) || value == "N/A" || value == "unknown" ? null : value;

    private static int? Int(JsonElement? element)
    {
        string? text = Text(element);
        return text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static long? Long(JsonElement? element)
    {
        string? text = Text(element);
        return text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    private static double? Double(JsonElement? element)
    {
        string? text = Text(element);
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }
}

internal sealed class FfprobeOutput
{
    [JsonPropertyName("streams")]
    public List<FfprobeStream>? Streams { get; set; }

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("format_name")]
    public string? FormatName { get; set; }

    [JsonPropertyName("probe_score")]
    public JsonElement? ProbeScore { get; set; }

    [JsonPropertyName("duration")]
    public JsonElement? Duration { get; set; }

    [JsonPropertyName("size")]
    public JsonElement? Size { get; set; }

    [JsonPropertyName("bit_rate")]
    public JsonElement? BitRate { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_type")]
    public string? CodecType { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("profile")]
    public JsonElement? Profile { get; set; }

    [JsonPropertyName("width")]
    public JsonElement? Width { get; set; }

    [JsonPropertyName("height")]
    public JsonElement? Height { get; set; }

    [JsonPropertyName("pix_fmt")]
    public string? PixFmt { get; set; }

    [JsonPropertyName("field_order")]
    public string? FieldOrder { get; set; }

    [JsonPropertyName("r_frame_rate")]
    public JsonElement? RFrameRate { get; set; }

    [JsonPropertyName("avg_frame_rate")]
    public JsonElement? AvgFrameRate { get; set; }

    [JsonPropertyName("nb_frames")]
    public JsonElement? NbFrames { get; set; }

    [JsonPropertyName("sample_rate")]
    public JsonElement? SampleRate { get; set; }

    [JsonPropertyName("channels")]
    public JsonElement? Channels { get; set; }

    [JsonPropertyName("sample_fmt")]
    public string? SampleFmt { get; set; }

    [JsonPropertyName("bits_per_raw_sample")]
    public JsonElement? BitsPerRawSample { get; set; }

    [JsonPropertyName("duration")]
    public JsonElement? Duration { get; set; }

    [JsonPropertyName("bit_rate")]
    public JsonElement? BitRate { get; set; }

    [JsonPropertyName("color_transfer")]
    public string? ColorTransfer { get; set; }

    [JsonPropertyName("color_primaries")]
    public string? ColorPrimaries { get; set; }

    [JsonPropertyName("color_space")]
    public string? ColorSpace { get; set; }

    [JsonPropertyName("disposition")]
    public FfprobeDisposition? Disposition { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
}

internal sealed class FfprobeDisposition
{
    [JsonPropertyName("default")]
    public int Default { get; set; }

    [JsonPropertyName("attached_pic")]
    public int AttachedPic { get; set; }
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FfprobeOutput))]
internal sealed partial class FfprobeJsonContext : JsonSerializerContext;
