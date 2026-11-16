using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>What one stderr line means (subplan 04, Log classification).</summary>
public enum LogSignalKind : byte
{
    Noise = 0,
    Benign = 1,
    OpenFailure = 2,
    Truncation = 3,
    DecodeError = 4,
    MinorDecode = 5,
    ConcealedFrame = 6,
    Checksum = 7,
    Timestamp = 8,
    Unsupported = 9,
    Unknown = 10,
}

public enum FfLogLevel : byte
{
    None = 0,
    Trace,
    Debug,
    Verbose,
    Info,
    Warning,
    Error,
    Fatal,
    Panic,
}

/// <summary>A parsed log line: its contexts (without the <c>@ address</c> part), level and message.</summary>
public sealed record LogLine(string Raw, IReadOnlyList<string> Contexts, FfLogLevel Level, string Message, LogSignalKind Kind, int? StreamIndex)
{
    /// <summary>The message with digit runs replaced by <c>N</c>, for counting.</summary>
    public string Normalized => LogClassifier.Normalize(Message);
}

/// <summary>
/// Turns FFmpeg stderr lines (written with <c>-loglevel repeat+level+warning</c>) into signals. Pattern
/// table from subplan 04, grown from the log corpus (subplan 15); first match wins, matching on message
/// plus context.
/// </summary>
public static partial class LogClassifier
{
    private static readonly (LogSignalKind Kind, Regex Pattern)[] Patterns =
    [
        // Benign first: some of these contain words that would otherwise match a damage pattern.
        (LogSignalKind.Benign, Re(@"deprecated pixel format used|Estimating duration from bitrate|Guessed Channel Layout|detected only with low score|Last message repeated|Could not find codec parameters for stream .*\(Attachment|Thread message queue blocking")),
        (LogSignalKind.Unsupported, Re(@"Decoder .* not found|Unsupported codec|is not implemented|Unknown decoder")),
        (LogSignalKind.OpenFailure, Re(@"moov atom not found|EBML header parsing failed|Failed to find two consecutive MPEG audio frames|Error opening input|Could not find codec parameters for stream .*unspecified (size|sample format)")),
        (LogSignalKind.Checksum, Re(@"CRC error|CRC mismatch|checksum mismatch")),
        (LogSignalKind.ConcealedFrame, Re(@"corrupt decoded frame")),
        (LogSignalKind.Truncation, Re(@"File ended prematurely|partial file|EOI missing|overread|chunk too big|filesize and duration do not match|Truncating packet of size|invalid new backstep|premature end")),
        (LogSignalKind.MinorDecode, Re(@"mmco: unref short failure|co located POCs unavailable|reference picture missing during reorder|Missing reference picture|Increasing reorder buffer")),
        (LogSignalKind.Timestamp, Re(@"Non-monotonic DTS|non monotonically increasing dts|Invalid timestamps|timestamp discontinuity|DTS .* out of order")),
        (LogSignalKind.DecodeError, Re(@"decode_frame\(\) failed|invalid residual|Header missing|error while decoding MB|Invalid NAL unit size|Error splitting the input into NAL units|missing picture in access unit|invalid band type|Prediction is not allowed|Number of bands|is not allocated|inflate returned error|error y=|error count:|no frame!|damaged|concealing|Error submitting packet to decoder|Decoding error|error dc|cabac decode of .* failed|invalid subframe padding|Input buffer exhausted before END element|Reserved bit set|bits_left=|not supported, maybe buggy stream")),
    ];

    private static readonly Regex DigitRuns = new(@"\d+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StreamContext = new(@"^[av]ist#\d+:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string InvalidData = "Invalid data found when processing input";

    public static LogLine Classify(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!TryParse(raw, out List<string> contexts, out FfLogLevel level, out string message))
        {
            return new LogLine(raw, [], FfLogLevel.None, raw, LogSignalKind.Noise, null);
        }

        int? stream = null;
        foreach (string context in contexts)
        {
            Match match = StreamContext.Match(context);
            if (match.Success)
            {
                stream = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        LogSignalKind kind = Kind(contexts, level, message);
        return new LogLine(raw, contexts, level, message, kind, stream);
    }

    public static string Normalize(string message) => DigitRuns.Replace(message, "N");

    /// <summary>
    /// Splits <c>[ctx @ 0x..] [ctx2] [level] message</c>. Returns false for lines without a level
    /// (continuation lines and hints), which are Noise.
    /// </summary>
    internal static bool TryParse(string raw, out List<string> contexts, out FfLogLevel level, out string message)
    {
        contexts = [];
        level = FfLogLevel.None;
        message = "";
        int index = 0;
        while (index < raw.Length)
        {
            while (index < raw.Length && raw[index] == ' ')
            {
                index++;
            }

            if (index >= raw.Length || raw[index] != '[')
            {
                break;
            }

            int close = raw.IndexOf(']', index + 1);
            if (close < 0)
            {
                break;
            }

            string group = raw[(index + 1)..close];
            index = close + 1;
            FfLogLevel parsed = ParseLevel(group);
            if (parsed != FfLogLevel.None)
            {
                level = parsed;
                message = raw[index..].Trim();
                return true;
            }

            int at = group.IndexOf(" @ ", StringComparison.Ordinal);
            contexts.Add(at >= 0 ? group[..at] : group);
        }

        return false;
    }

    private static LogSignalKind Kind(List<string> contexts, FfLogLevel level, string message)
    {
        if (message.Contains(InvalidData, StringComparison.OrdinalIgnoreCase))
        {
            // Same text, different meaning: on a decoder line it is damage, at file level it means the
            // input could not be opened (subplan 04).
            bool decoderLine = contexts.Exists(c => c.StartsWith("dec:", StringComparison.Ordinal) || c.StartsWith("aist#", StringComparison.Ordinal) || c.StartsWith("vist#", StringComparison.Ordinal))
                || message.StartsWith("Error submitting packet", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("Decoding error", StringComparison.OrdinalIgnoreCase);
            return decoderLine ? LogSignalKind.DecodeError : LogSignalKind.OpenFailure;
        }

        foreach ((LogSignalKind kind, Regex pattern) in Patterns)
        {
            if (pattern.IsMatch(message))
            {
                return kind;
            }
        }

        return level >= FfLogLevel.Error ? LogSignalKind.Unknown : LogSignalKind.Noise;
    }

    private static FfLogLevel ParseLevel(string group) => group switch
    {
        "trace" => FfLogLevel.Trace,
        "debug" => FfLogLevel.Debug,
        "verbose" => FfLogLevel.Verbose,
        "info" => FfLogLevel.Info,
        "warning" => FfLogLevel.Warning,
        "error" => FfLogLevel.Error,
        "fatal" => FfLogLevel.Fatal,
        "panic" => FfLogLevel.Panic,
        _ => FfLogLevel.None,
    };

    private static Regex Re(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

/// <summary>Per-kind counts and samples from one process run.</summary>
public sealed record SignalSummary(
    LogSignalKind Kind,
    int Count,
    IReadOnlyList<(string Message, int Count)> TopMessages,
    string? FirstLine,
    string? LastLine,
    IReadOnlyList<int> Streams);

/// <summary>
/// The classified log of one run (subplan 04, LogSummary): per kind, its count, the top 20 distinct
/// normalised messages, first and last raw line and the streams named; plus the first 200 and last
/// 50 raw lines.
/// </summary>
public sealed class LogSummary
{
    public static LogSummary Empty { get; } = new(new Dictionary<LogSignalKind, SignalSummary>(), [], [], 0);

    public LogSummary(IReadOnlyDictionary<LogSignalKind, SignalSummary> kinds, IReadOnlyList<string> head, IReadOnlyList<string> tail, long totalLines)
    {
        Kinds = kinds;
        Head = head;
        Tail = tail;
        TotalLines = totalLines;
    }

    public IReadOnlyDictionary<LogSignalKind, SignalSummary> Kinds { get; }

    public IReadOnlyList<string> Head { get; }

    public IReadOnlyList<string> Tail { get; }

    public long TotalLines { get; }

    public int Count(LogSignalKind kind) => Kinds.TryGetValue(kind, out SignalSummary? summary) ? summary.Count : 0;

    public bool Has(LogSignalKind kind) => Count(kind) > 0;

    /// <summary>Decode errors with unknown error lines counted at half weight (subplan 04).</summary>
    public double WeightedDecodeErrors => Count(LogSignalKind.DecodeError) + (0.5 * Count(LogSignalKind.Unknown));

    /// <summary>Every damage-signal line, for the "Message / Count" evidence table.</summary>
    public IEnumerable<(string Message, int Count)> DamageMessages() =>
        Kinds.Values
            .Where(k => k.Kind is not (LogSignalKind.Noise or LogSignalKind.Benign or LogSignalKind.Unsupported))
            .SelectMany(k => k.TopMessages)
            .OrderByDescending(m => m.Count);

    public static LogSummary FromLines(IEnumerable<string> lines)
    {
        LogCollector collector = new();
        foreach (string line in lines)
        {
            collector.Add(line);
        }

        return collector.ToSummary();
    }
}

/// <summary>
/// Collects stderr as it arrives with bounded memory: a badly damaged file can emit millions of lines,
/// so only counts, the first 200 and last 50 raw lines and up to 64 distinct messages per kind are kept.
/// </summary>
public sealed class LogCollector
{
    private const int HeadLines = 200;
    private const int TailLines = 50;
    private const int MaxDistinctPerKind = 64;
    private const int MaxLineLength = 2048;

    private readonly List<string> _head = new(HeadLines);
    private readonly Queue<string> _tail = new(TailLines);
    private readonly Dictionary<LogSignalKind, KindState> _kinds = [];
    private readonly Lock _gate = new();
    private long _total;

    /// <summary>Called for each classified line (the integration tests' unknown-line gate listens here).</summary>
    public Action<LogLine>? OnLine { get; init; }

    public void Add(string raw)
    {
        if (raw.Length == 0)
        {
            return;
        }

        if (raw.Length > MaxLineLength)
        {
            raw = raw[..MaxLineLength];
        }

        LogLine line = LogClassifier.Classify(raw);
        lock (_gate)
        {
            _total++;
            if (_head.Count < HeadLines)
            {
                _head.Add(raw);
            }
            else
            {
                if (_tail.Count == TailLines)
                {
                    _tail.Dequeue();
                }

                _tail.Enqueue(raw);
            }

            if (!_kinds.TryGetValue(line.Kind, out KindState? state))
            {
                state = new KindState();
                _kinds[line.Kind] = state;
            }

            state.Count++;
            state.First ??= raw;
            state.Last = raw;
            if (line.StreamIndex is int stream)
            {
                state.Streams.Add(stream);
            }

            string normalized = line.Normalized;
            if (state.Messages.TryGetValue(normalized, out int count))
            {
                state.Messages[normalized] = count + 1;
            }
            else if (state.Messages.Count < MaxDistinctPerKind)
            {
                state.Messages[normalized] = 1;
            }
        }

        OnLine?.Invoke(line);
    }

    public LogSummary ToSummary()
    {
        lock (_gate)
        {
            Dictionary<LogSignalKind, SignalSummary> kinds = [];
            foreach ((LogSignalKind kind, KindState state) in _kinds)
            {
                kinds[kind] = new SignalSummary(
                    kind,
                    state.Count,
                    [.. state.Messages.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(20).Select(p => (p.Key, p.Value))],
                    state.First,
                    state.Last,
                    [.. state.Streams.Order()]);
            }

            return new LogSummary(kinds, [.. _head], [.. _tail], _total);
        }
    }

    private sealed class KindState
    {
        public int Count { get; set; }

        public string? First { get; set; }

        public string? Last { get; set; }

        public HashSet<int> Streams { get; } = [];

        public Dictionary<string, int> Messages { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Splits a character stream into lines on \r and \n, as FFmpeg uses both.</summary>
internal sealed class LineSplitter(Action<string> onLine)
{
    private readonly StringBuilder _current = new();

    public void Feed(ReadOnlySpan<char> chars)
    {
        foreach (char c in chars)
        {
            if (c is '\r' or '\n')
            {
                Flush();
            }
            else if (_current.Length < 8192)
            {
                _current.Append(c);
            }
        }
    }

    public void Flush()
    {
        if (_current.Length > 0)
        {
            onLine(_current.ToString());
            _current.Clear();
        }
    }
}
