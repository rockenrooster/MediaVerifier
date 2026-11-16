using System.Globalization;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>One <c>-progress</c> block (subplan 04, Progress parsing).</summary>
public sealed record ProgressBlock(long? Frame, long? OutTimeMicroseconds, long? DupFrames, long? DropFrames, double? Speed, bool IsEnd)
{
    public double? OutTimeSeconds => OutTimeMicroseconds is long us ? us / 1_000_000.0 : null;
}

/// <summary>
/// Parses <c>key=value</c> progress lines into blocks ending with <c>progress=continue</c> or
/// <c>progress=end</c>. The final block gives the decoded frame count and duration. A stream cut in the
/// middle of a block keeps the last complete one.
/// </summary>
public sealed class ProgressParser(Action<ProgressBlock>? onBlock = null)
{
    private long? _frame;
    private long? _outTime;
    private long? _dup;
    private long? _drop;
    private double? _speed;

    /// <summary>The last complete block seen.</summary>
    public ProgressBlock? Final { get; private set; }

    public int Blocks { get; private set; }

    public static ProgressParser ParseAll(string text)
    {
        ProgressParser parser = new();
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            parser.AddLine(line);
        }

        parser.Complete();
        return parser;
    }

    public void AddLine(string line)
    {
        int equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return;
        }

        string key = line[..equals].Trim();
        string value = line[(equals + 1)..].Trim();
        switch (key)
        {
            case "frame":
                _frame = ParseLong(value);
                break;
            case "out_time_us":
                _outTime = ParseLong(value);
                break;
            case "out_time_ms" when _outTime is null:
                // Despite its name FFmpeg writes microseconds here too.
                _outTime = ParseLong(value);
                break;
            case "dup_frames":
                _dup = ParseLong(value);
                break;
            case "drop_frames":
                _drop = ParseLong(value);
                break;
            case "speed":
                _speed = ParseSpeed(value);
                break;
            case "progress":
                Final = new ProgressBlock(_frame, _outTime, _dup, _drop, _speed, string.Equals(value, "end", StringComparison.Ordinal));
                Blocks++;
                onBlock?.Invoke(Final);
                _frame = _outTime = _dup = _drop = null;
                _speed = null;
                break;
        }
    }

    /// <summary>Called at end of stream. Incomplete trailing lines are dropped.</summary>
    public void Complete()
    {
    }

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0 ? parsed : null;

    private static double? ParseSpeed(string value)
    {
        string trimmed = value.TrimEnd('x', 'X').Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) ? speed : null;
    }
}
