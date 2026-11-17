using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit.Popups;

/// <summary>
/// A top-level popup with per-pixel alpha (menus, drop-down lists, tooltips, toasts): the content is
/// painted opaque, clipped to its rounded rectangle with anti-aliased edges, and composited over the
/// cached Gaussian shadow (subplan 08, Space, shape, elevation). It never takes activation, so focus
/// stays where the user is working.
/// </summary>
internal abstract class LayeredPopup : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;
    private const int UlwAlpha = 2;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;

    private Point _contentScreenLocation;

    protected LayeredPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    /// <summary>The DPI to paint at (the owner window's).</summary>
    public int PaintDpi { get; set; } = 96;

    /// <summary>Corner radius of the content in logical pixels.</summary>
    protected virtual int Radius => 8;

    /// <summary>The content rectangle in screen coordinates.</summary>
    public Rectangle ContentScreenBounds => new(_contentScreenLocation, ContentSize);

    /// <summary>Size of the content (without the shadow margin) in device pixels.</summary>
    protected abstract Size ContentSize { get; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopMost;
            return cp;
        }
    }

    /// <summary>Shows (or moves) the popup so that its content's top-left corner is at <paramref name="screen"/>.</summary>
    public void ShowAt(Point screen, IWin32Window? owner = null)
    {
        _contentScreenLocation = screen;
        if (!Visible)
        {
            if (owner is Form ownerForm)
            {
                Owner = ownerForm;
            }

            CreateControl();
            Render();
            Show();
        }

        Render();
    }

    /// <summary>Repaints the popup after its content changed.</summary>
    public void Render()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        Size content = ContentSize;
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        int dpi = PaintDpi;
        int pad = ShadowCache.Padding(dpi);
        int offset = Dpi.Scale(6, dpi);
        Size total = new(content.Width + (2 * pad), content.Height + (2 * pad) + offset);
        using Bitmap final = new(total.Width, total.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(final))
        {
            g.Clear(Color.Transparent);
            Rectangle contentRect = new(pad, pad, content.Width, content.Height);
            ShadowCache.Draw(g, contentRect, Radius, dpi, ThemeService.Tokens.Shadow);
            using Bitmap opaque = new(content.Width, content.Height, PixelFormat.Format24bppRgb);
            using (Graphics cg = Graphics.FromImage(opaque))
            {
                cg.Clear(ThemeService.Tokens.Raised);
                PaintContent(cg, new Rectangle(Point.Empty, content), dpi);
            }

            using TextureBrush texture = new(opaque, WrapMode.Clamp);
            texture.TranslateTransform(pad, pad);
            using GraphicsPath path = new();
            IconGeometry.AddRoundedRect(path, contentRect, Dpi.ScaleF(Radius, dpi));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(texture, path);
        }

        Point windowLocation = new(_contentScreenLocation.X - pad, _contentScreenLocation.Y - pad);
        Bounds = new Rectangle(windowLocation, total);
        UpdateLayered(final, windowLocation);
    }

    /// <summary>Paints the content opaque into <paramref name="bounds"/> (origin 0, 0), including its border.</summary>
    protected abstract void PaintContent(Graphics g, Rectangle bounds, int dpi);

    /// <summary>Converts a client point of the window to a point inside the content.</summary>
    protected Point ToContent(Point client)
    {
        int pad = ShadowCache.Padding(PaintDpi);
        return new Point(client.X - pad, client.Y - pad);
    }

    private void UpdateLayered(Bitmap bitmap, Point location)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memory, hbitmap);
        try
        {
            NativePoint destination = new(location.X, location.Y);
            NativeSize size = new(bitmap.Width, bitmap.Height);
            NativePoint source = new(0, 0);
            BlendFunction blend = new() { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(hbitmap);
            DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pprSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
