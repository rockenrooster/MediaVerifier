namespace MediaVerifier.UI.Kit;

/// <summary>
/// Word wrapping for paragraphs (drawer explanations, dialog bodies): breaks at spaces using GDI
/// measurements, so lines match what <see cref="KitPaint.DrawText"/> draws.
/// </summary>
internal static class TextWrap
{
    /// <summary>The lines of <paramref name="text"/> that fit <paramref name="width"/> device pixels.</summary>
    public static List<string> Lines(string text, Font font, int width)
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

    /// <summary>Draws wrapped text from (x, top) with <paramref name="pitch"/> between line centres; returns the height used.</summary>
    public static int Draw(Graphics g, string text, Font font, Color color, int x, int top, int width, int pitch)
    {
        List<string> lines = Lines(text, font, width);
        for (int i = 0; i < lines.Count; i++)
        {
            KitPaint.DrawText(g, lines[i], font, color, x, top + (pitch / 2) + (i * pitch), width);
        }

        return lines.Count * pitch;
    }

    /// <summary>Height of wrapped text at a line pitch.</summary>
    public static int Measure(string text, Font font, int width, int pitch) => Lines(text, font, width).Count * pitch;
}
