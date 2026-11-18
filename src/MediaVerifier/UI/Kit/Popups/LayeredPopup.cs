using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit.Popups;

/// <summary>
/// A top-level popup with per-pixel alpha (menus, drop-down lists, tooltips, toasts): the content is
/// painted opaque, clipped to its rounded rectangle with anti-aliased edges, and composited over the
/// cached Gaussian shadow (subplan 08, Space, shape, elevation). It never takes activation, so focus
/// stays where the user is working. A fade changes <see cref="PopupOpacity"/>, which the layered update
/// applies; never <see cref="Form.Opacity"/>, which switches the window to SetLayeredWindowAttributes, after
/// which the per-pixel image no longer shows and the popup is a blank white box.
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
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private Point _contentScreenLocation;
    private Bitmap? _frame;
    private Point _frameLocation;
    private byte _opacity = 255;

    protected LayeredPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    /// <summary>The DPI to paint at (the owner window's).</summary>
    public int PaintDpi { get; set; } = 96;

    /// <summary>0 (invisible) to 255 (opaque): the whole popup's opacity, applied without painting again.</summary>
    public byte PopupOpacity
    {
        get => _opacity;
        set
        {
            _opacity = value;
            Present();
        }
    }

    /// <summary>Corner radius of the content in logical pixels.</summary>
    protected virtual int Radius => 8;

    /// <summary>The content rectangle in screen coordinates.</summary>
    public Rectangle ContentScreenBounds => new(_contentScreenLocation, ContentSize);

    /// <summary>Size of the content (without the shadow margin) in device pixels.</summary>
    protected abstract Size ContentSize { get; }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// A click must not activate the popup: WS_EX_NOACTIVATE alone does not stop WinForms from activating the
    /// window on WM_MOUSEACTIVATE, and the owner losing activation closes menus and drop-down lists on the
    /// mouse-down, so the click fell through to the window beneath and the item never ran (Disk Monitor 2.0
    /// answers MA_NOACTIVATE the same way).
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = MaNoActivate;
            return;
        }

        base.WndProc(ref m);
    }

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
        Bitmap frame = new(total.Width, total.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);
            Rectangle contentRect = new(pad, pad, content.Width, content.Height);
            ShadowCache.Draw(g, contentRect, Radius, dpi, ThemeService.Tokens.Shadow);
            using Bitmap opaque = PaintOpaque(content, dpi);
            using TextureBrush texture = new(opaque, WrapMode.Clamp);
            texture.TranslateTransform(pad, pad);
            using GraphicsPath path = new();
            IconGeometry.AddRoundedRect(path, contentRect, Dpi.ScaleF(Radius, dpi));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(texture, path);
        }

        _frame?.Dispose();
        _frame = frame;
        _frameLocation = new Point(_contentScreenLocation.X - pad, _contentScreenLocation.Y - pad);
        Bounds = new Rectangle(_frameLocation, total);
        Present();
    }

    /// <summary>
    /// The content painted opaque, through a real GDI surface (a DIB section in a memory DC) as a window's own
    /// double buffer is. GDI text drawn on a GDI+ bitmap's Graphics blends its ClearType edges against an empty
    /// scratch copy instead of the background, which made menu text heavy and jagged (Disk Monitor 2.0 has the
    /// same fix).
    /// </summary>
    internal Bitmap PaintOpaque(Size content, int dpi)
    {
        Bitmap opaque = new(content.Width, content.Height, PixelFormat.Format32bppRgb);
        BitmapInfoHeader header = new() { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = content.Width, Height = -content.Height, Planes = 1, BitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr section = CreateDIBSection(memory, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr previous = section == IntPtr.Zero ? IntPtr.Zero : SelectObject(memory, section);
        try
        {
            if (section == IntPtr.Zero)
            {
                // No GDI surface to be had: paint on the bitmap (the text is less crisp, but it shows).
                using Graphics fallback = Graphics.FromImage(opaque);
                fallback.Clear(ThemeService.Tokens.Raised);
                PaintContent(fallback, new Rectangle(Point.Empty, content), dpi);
                return opaque;
            }

            using (Graphics g = Graphics.FromHdc(memory))
            {
                g.Clear(ThemeService.Tokens.Raised);
                PaintContent(g, new Rectangle(Point.Empty, content), dpi);
            }

            _ = GdiFlush();

            // Copy the pixels out: the DIB is freed below.
            BitmapData data = opaque.LockBits(new Rectangle(Point.Empty, content), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                int rowBytes = content.Width * 4;
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < content.Height; y++)
                {
                    Marshal.Copy(bits + (y * rowBytes), row, 0, rowBytes);
                    Marshal.Copy(row, 0, data.Scan0 + (y * data.Stride), rowBytes);
                }
            }
            finally
            {
                opaque.UnlockBits(data);
            }

            return opaque;
        }
        finally
        {
            if (section != IntPtr.Zero)
            {
                _ = SelectObject(memory, previous);
                _ = DeleteObject(section);
            }

            _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Paints the content opaque into <paramref name="bounds"/> (origin 0, 0), including its border.</summary>
    protected abstract void PaintContent(Graphics g, Rectangle bounds, int dpi);

    /// <summary>Converts a client point of the window to a point inside the content.</summary>
    protected Point ToContent(Point client)
    {
        int pad = ShadowCache.Padding(PaintDpi);
        return new Point(client.X - pad, client.Y - pad);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame?.Dispose();
            _frame = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>Shows the last painted image at the current opacity.</summary>
    private void Present()
    {
        if (_frame is not null && IsHandleCreated)
        {
            UpdateLayered(_frame, _frameLocation);
        }
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
            BlendFunction blend = new() { BlendOp = AcSrcOver, SourceConstantAlpha = _opacity, AlphaFormat = AcSrcAlpha };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
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

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, int usage, out IntPtr bits, IntPtr section, int offset);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
