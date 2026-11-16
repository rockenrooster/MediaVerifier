using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Drop shadows for dialogs, menus, toasts, tooltips and floating bars (subplan 08, Space, shape,
/// elevation): the element's rounded rectangle, offset 6 px down, Gaussian blur with a standard
/// deviation of 10 px, in the theme's Shadow colour. Rendered once per size, radius, DPI and colour.
/// UI thread only.
/// </summary>
internal static class ShadowCache
{
    private const float SigmaLogical = 10f;
    private const float OffsetLogical = 6f;
    private static readonly Dictionary<(int W, int H, int Radius, int Dpi, int Argb), Bitmap> Cache = [];

    /// <summary>Paints the shadow of an element whose bounds are <paramref name="element"/> (device pixels).</summary>
    public static void Draw(Graphics g, Rectangle element, int logicalRadius, int dpi, Color shadow)
    {
        if (shadow.A == 0 || element.Width <= 0 || element.Height <= 0)
        {
            return;
        }

        Bitmap bitmap = Get(element.Size, logicalRadius, dpi, shadow);
        int pad = Padding(dpi);
        int offset = Dpi.Scale((int)OffsetLogical, dpi);
        g.DrawImageUnscaled(bitmap, element.X - pad, element.Y - pad + offset);
    }

    public static void Clear()
    {
        foreach (Bitmap bitmap in Cache.Values)
        {
            bitmap.Dispose();
        }

        Cache.Clear();
    }

    /// <summary>The margin around an element that the blur reaches: three standard deviations.</summary>
    public static int Padding(int dpi) => (int)Math.Ceiling(3 * Dpi.ScaleF(SigmaLogical, dpi));

    private static Bitmap Get(Size size, int logicalRadius, int dpi, Color shadow)
    {
        (int, int, int, int, int) key = (size.Width, size.Height, logicalRadius, dpi, shadow.ToArgb());
        if (Cache.TryGetValue(key, out Bitmap? cached))
        {
            return cached;
        }

        if (Cache.Count > 64)
        {
            // Sizes change while a window is resized; keep the cache small.
            Clear();
        }

        Bitmap bitmap = Render(size, Dpi.ScaleF(logicalRadius, dpi), Dpi.ScaleF(SigmaLogical, dpi), Padding(dpi), shadow);
        Cache[key] = bitmap;
        return bitmap;
    }

    private static Bitmap Render(Size size, float radius, float sigma, int pad, Color shadow)
    {
        int width = size.Width + (2 * pad);
        int height = size.Height + (2 * pad);
        byte[] alpha = new byte[width * height];
        using (Bitmap mask = new(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(mask))
            using (GraphicsPath path = new())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                IconGeometry.AddRoundedRect(path, new RectangleF(pad, pad, size.Width, size.Height), radius);
                g.FillPath(Brushes.White, path);
            }

            BitmapData data = mask.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                    {
                        alpha[(y * width) + x] = row[(x * 4) + 3];
                    }
                }
            }
            finally
            {
                mask.UnlockBits(data);
            }
        }

        float[] blurred = Blur(alpha, width, height, sigma);
        Bitmap result = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData target = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int a = (int)Math.Round(blurred[(y * width) + x] / 255f * shadow.A);
                    int o = x * 4;
                    row[o] = shadow.B;
                    row[o + 1] = shadow.G;
                    row[o + 2] = shadow.R;
                    row[o + 3] = (byte)Math.Clamp(a, 0, 255);
                }

                Marshal.Copy(row, 0, target.Scan0 + (y * target.Stride), row.Length);
            }
        }
        finally
        {
            result.UnlockBits(target);
        }

        return result;
    }

    /// <summary>Separable Gaussian blur of an 8-bit mask, kernel radius three standard deviations.</summary>
    private static float[] Blur(byte[] source, int width, int height, float sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        float[] kernel = new float[(2 * radius) + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            float value = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = value;
            sum += value;
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        float[] horizontal = new float[source.Length];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int sx = x + k;
                    if (sx >= 0 && sx < width)
                    {
                        acc += source[rowStart + sx] * kernel[k + radius];
                    }
                }

                horizontal[rowStart + x] = acc;
            }
        }

        float[] result = new float[source.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int sy = y + k;
                    if (sy >= 0 && sy < height)
                    {
                        acc += horizontal[(sy * width) + x] * kernel[k + radius];
                    }
                }

                result[(y * width) + x] = acc;
            }
        }

        return result;
    }
}
