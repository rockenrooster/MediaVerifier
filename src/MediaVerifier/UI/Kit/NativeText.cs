using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Text through GDI with cached HFONTs (subplan 08, Rendering rules: ClearType, no padding, no prefix,
/// end ellipsis). TextRenderer allocates on every call; this path allocates nothing, so lists scroll
/// without garbage. The same GDI text engine as TextRenderer draws and measures, so results match.
/// UI thread only.
/// </summary>
internal static class NativeText
{
    private const int Transparent = 1;
    private static readonly ConditionalWeakTable<Font, FontHandle> Handles = [];
    private static IntPtr s_measureDc;

    /// <summary>Line height of a font in device pixels (cached with its handle).</summary>
    public static int LineHeight(Font font) => Handle(font).LineHeight;

    /// <summary>Width of a single line of text in device pixels.</summary>
    public static int Measure(string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (s_measureDc == IntPtr.Zero)
        {
            s_measureDc = CreateCompatibleDC(IntPtr.Zero);
        }

        IntPtr old = SelectObject(s_measureDc, Handle(font).Hfont);
        _ = GetTextExtentPoint32W(s_measureDc, text, text.Length, out NativeSize size);
        SelectObject(s_measureDc, old);
        return size.Cx;
    }

    /// <summary>
    /// Draws text into a rectangle with TextFormatFlags-compatible DT_* flags. The device context GDI+ hands out does not
    /// carry the Graphics' clip, so a clip that cuts the text (rows scrolled under a list's header) is applied here.
    /// </summary>
    public static void Draw(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
    {
        RectangleF clip = g.ClipBounds;
        bool clipped = !clip.Contains(bounds);
        if (clipped && !clip.IntersectsWith(bounds))
        {
            return;
        }

        IntPtr hdc = g.GetHdc();
        try
        {
            int saved = 0;
            if (clipped)
            {
                saved = SaveDC(hdc);
                _ = IntersectClipRect(hdc, (int)Math.Floor(clip.Left), (int)Math.Floor(clip.Top), (int)Math.Ceiling(clip.Right), (int)Math.Ceiling(clip.Bottom));
            }

            Draw(hdc, text, font, color, bounds, flags);
            if (clipped)
            {
                _ = RestoreDC(hdc, saved);
            }
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    /// <summary>Draws text on a device context (the text box's placeholder is drawn on its native edit).</summary>
    public static void Draw(IntPtr hdc, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
    {
        IntPtr old = SelectObject(hdc, Handle(font).Hfont);
        int oldMode = SetBkMode(hdc, Transparent);
        int oldColor = SetTextColor(hdc, color.R | (color.G << 8) | (color.B << 16));
        NativeRect rect = new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        _ = DrawTextW(hdc, text, text.Length, ref rect, (int)flags);
        SetTextColor(hdc, oldColor);
        SetBkMode(hdc, oldMode);
        SelectObject(hdc, old);
    }

    private static FontHandle Handle(Font font)
    {
        if (!Handles.TryGetValue(font, out FontHandle? handle))
        {
            handle = new FontHandle(font.ToHfont(), font.Height);
            Handles.Add(font, handle);
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    /// <summary>An HFONT owned for the font's lifetime (fonts come from TypeRamp and live as long as the process).</summary>
    private sealed class FontHandle(IntPtr hfont, int lineHeight)
    {
        public IntPtr Hfont { get; } = hfont;

        public int LineHeight { get; } = lineHeight;

        ~FontHandle()
        {
            DeleteObject(Hfont);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int length, ref NativeRect rect, int format);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out NativeSize size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int SaveDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool RestoreDC(IntPtr hdc, int saved);

    [DllImport("gdi32.dll")]
    private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
