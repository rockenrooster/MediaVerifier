namespace MediaVerifier.UI.Kit;

/// <summary>
/// Word wrapping for paragraphs (drawer explanations, dialog bodies): breaks at spaces using GDI
/// measurements, so lines match what <see cref="KitPaint.DrawText"/> draws. Wrapped lines are kept for the
/// last texts wrapped, so painting the same paragraph again allocates nothing. UI thread only.
/// </summary>
internal static class TextWrap
{
    private static readonly Entry?[] Cache = new Entry?[64];
    private static int s_next;

    /// <summary>The lines of <paramref name="text"/> that fit <paramref name="width"/> device pixels.</summary>
    public static IReadOnlyList<string> Lines(string text, Font font, int width) => Cached(text, font, width, 0);

    /// <summary>At most <paramref name="maxLines"/> lines: the last takes the rest, cut with an ellipsis when drawn.</summary>
    public static IReadOnlyList<string> Lines(string text, Font font, int width, int maxLines) => Cached(text, font, width, Math.Max(1, maxLines));

    /// <summary>Draws wrapped text from (x, top) with <paramref name="pitch"/> between line centres; returns the height used.</summary>
    public static int Draw(Graphics g, string text, Font font, Color color, int x, int top, int width, int pitch) =>
        DrawLines(g, Lines(text, font, width), font, color, x, top, width, pitch);

    /// <summary>Height of wrapped text at a line pitch.</summary>
    public static int Measure(string text, Font font, int width, int pitch) => Lines(text, font, width).Count * pitch;

    /// <summary>Draws at most <paramref name="maxLines"/> wrapped lines from (x, top); returns the height used.</summary>
    public static int Draw(Graphics g, string text, Font font, Color color, int x, int top, int width, int pitch, int maxLines) =>
        DrawLines(g, Lines(text, font, width, maxLines), font, color, x, top, width, pitch);

    private static int DrawLines(Graphics g, IReadOnlyList<string> lines, Font font, Color color, int x, int top, int width, int pitch)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            KitPaint.DrawText(g, lines[i], font, color, x, top + (pitch / 2) + (i * pitch), width);
        }

        return lines.Count * pitch;
    }

    private static IReadOnlyList<string> Cached(string text, Font font, int width, int maxLines)
    {
        foreach (Entry? entry in Cache)
        {
            if (entry is not null && entry.Matches(text, font, width, maxLines))
            {
                return entry.Lines;
            }
        }

        List<string> lines = Wrap(text, font, width);
        if (maxLines > 0 && lines.Count > maxLines)
        {
            List<string> kept = lines.GetRange(0, maxLines - 1);
            kept.Add(string.Join(' ', lines.Skip(maxLines - 1)));
            lines = kept;
        }

        Cache[s_next] = new Entry(text, font, width, maxLines, lines);
        s_next = (s_next + 1) % Cache.Length;
        return lines;
    }

    private static List<string> Wrap(string text, Font font, int width)
    {
        List<string> lines = [];
        foreach (string paragraph in text.Split('\n'))
        {
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string current = string.Empty;
            foreach (string word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && KitPaint.MeasureWidth(candidate, font) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    private sealed class Entry(string text, Font font, int width, int maxLines, List<string> lines)
    {
        public IReadOnlyList<string> Lines { get; } = lines;

        public bool Matches(string other, Font otherFont, int otherWidth, int otherMaxLines) =>
            otherWidth == width && otherMaxLines == maxLines && ReferenceEquals(otherFont, font)
            && (ReferenceEquals(other, text) || (other.Length == text.Length && string.Equals(other, text, StringComparison.Ordinal)));
    }
}
