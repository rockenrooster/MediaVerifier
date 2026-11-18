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
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;

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

    /// <summary>
    /// Menus, lists and tooltips stay above every window while they show; a toast belongs to its window only, so it never
    /// floats over another program (Disk Monitor 2.0's toast is not topmost either).
    /// </summary>
    protected virtual bool TopMostPopup => true;

    /// <summary>A tooltip takes no mouse input at all: the pointer stays on the control it explains.</summary>
    protected virtual bool ClickThrough => false;

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
    /// <para>
    /// The shadow is part of the window, and a per-pixel-alpha window takes every click where its alpha is not 0: the
    /// shadow swallowed clicks on the lower part of a drop-down and on the status strip's Pause and Stop under a toast.
    /// Outside the content the window answers HTTRANSPARENT, so the click goes to the window beneath.
    /// </para>
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = MaNoActivate;
            return;
        }

        if (m.Msg == WmNcHitTest && (ClickThrough || !ContentScreenBounds.Contains(PointFromLParam(m.LParam))))
        {
            m.Result = HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
            if (TopMostPopup)
            {
                cp.ExStyle |= WsExTopMost;
            }

            return cp;
        }
    }

    /// <summary>
    /// Shows (or moves) the popup so that its content's top-left corner is at <paramref name="screen"/>: painted once
    /// before it shows, so it never shows a previous frame (the shared tooltip showed the last tip's text first).
    /// </summary>
    public void ShowAt(Point screen, IWin32Window? owner = null)
    {
        _contentScreenLocation = screen;
        if (!Visible && owner is Form ownerForm)
        {
            Owner = ownerForm;
        }

        if (!IsHandleCreated)
        {
            CreateHandle();
        }

        Render();
        if (!Visible)
        {
            Show();
        }
    }

    /// <summary>Moves the popup without painting it again (a toast following its window as it is dragged).</summary>
    public void MoveContentTo(Point screen)
    {
        int dx = screen.X - _contentScreenLocation.X;
        int dy = screen.Y - _contentScreenLocation.Y;
        _contentScreenLocation = screen;
        if (_frame is null || (dx == 0 && dy == 0))
        {
            return;
        }

        _frameLocation.Offset(dx, dy);
        if (IsHandleCreated)
        {
            // The layered image stays with the window when it moves.
            Location = _frameLocation;
        }
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

    /// <summary>The point packed in a mouse message's LPARAM (signed: monitors left of or above the primary are negative).</summary>
    internal static Point PointFromLParam(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
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

/// <summary>
/// A popup that closes itself: menus, drop-down lists and the notifications flyout (one copy of the rules, which both
/// used to implement). It never takes focus; while it is open it watches the thread's messages:
/// <list type="bullet">
/// <item>Esc and F4 close it; Tab and Alt with another key close it and go on (Alt+F4 still closes the window); Alt+Down
/// and Alt+Up only close it; every other key goes to <see cref="OnKey"/>, which by default swallows it.</item>
/// <item>A press outside closes it and goes on to what it was aimed at. A press on the button that opened it (its
/// toggle area) only closes it, and the release that follows is swallowed too, so the button does not open it again.</item>
/// <item>The wheel over it goes to <see cref="OnWheel"/>; elsewhere it closes it. The window moving or losing activation
/// closes it.</item>
/// </list>
/// What the user chose runs once it has closed, from a posted message (<see cref="CloseWith"/>): never inside the message
/// filter, where WinForms keeps calling the filters it started with, so the closed popup went on swallowing the keys of a
/// dialog the action opened, and an exception thrown by the action was dropped without a word.
/// </summary>
internal abstract class TransientPopup : LayeredPopup, IMessageFilter
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseFirst = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonDblClk = 0x0206;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonDblClk = 0x0209;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonDblClk = 0x020D;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcRButtonDown = 0x00A4;
    private const int WmNcMButtonDown = 0x00A7;
    private const int WmNcXButtonDown = 0x00AB;

    private Form? _ownerForm;
    private bool _filtering;
    private bool _closed;

    /// <summary>Raised when the popup closes, whatever closed it; before a chosen action runs.</summary>
    public event EventHandler? Dismissed;

    /// <summary>Screen area of the button that opened the popup: a left press inside it only closes the popup.</summary>
    internal Rectangle ToggleArea { get; set; }

    /// <summary>Closes the popup without choosing anything.</summary>
    public void Dismiss() => CloseWith(null);

    public bool PreFilterMessage(ref Message m)
    {
        // WinForms rebuilds its list of filters only between messages, so a closed popup can still be asked: it lets
        // everything through.
        if (!_filtering)
        {
            return false;
        }

        switch (m.Msg)
        {
            case WmKeyDown:
                return FilterKey((Keys)(int)m.WParam);
            case WmSysKeyDown:
                {
                    // Alt alone waits for the key that follows it.
                    Keys key = (Keys)(int)m.WParam;
                    if (key == Keys.Menu)
                    {
                        return false;
                    }

                    CloseWith(null);
                    return key is Keys.Down or Keys.Up;
                }

            case WmSysKeyUp:
                // Alt pressed and released on its own.
                if ((Keys)(int)m.WParam == Keys.Menu)
                {
                    CloseWith(null);
                }

                return false;
            case WmMouseWheel:
                {
                    // The wheel message goes to the window under the pointer or to the focused one; the pointer decides.
                    Point pointer = PointFromLParam(m.LParam);
                    if (ContentScreenBounds.Contains(pointer))
                    {
                        OnWheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF), pointer);
                        return true;
                    }

                    CloseWith(null);
                    return false;
                }

            case WmLButtonDown or WmLButtonDblClk or WmRButtonDown or WmRButtonDblClk or WmMButtonDown or WmMButtonDblClk
                or WmXButtonDown or WmXButtonDblClk or WmNcLButtonDown or WmNcRButtonDown or WmNcMButtonDown or WmNcXButtonDown:
                {
                    Point point = ScreenPointOf(m);
                    if (m.HWnd == Handle && ContentScreenBounds.Contains(point))
                    {
                        return false;
                    }

                    CloseWith(null);
                    if ((m.Msg is WmLButtonDown or WmLButtonDblClk) && ToggleArea.Contains(point))
                    {
                        ReleaseSwallower.Arm();
                        return true;
                    }

                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Shows the popup with its content's top-left corner at <paramref name="contentTopLeft"/>, over the window of
    /// <paramref name="anchor"/>, and starts watching. A left press inside <paramref name="toggleArea"/> (screen
    /// coordinates; the button that opened the popup) only closes it.
    /// </summary>
    protected void Watch(Control anchor, Point contentTopLeft, Rectangle toggleArea)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        _ownerForm = anchor.FindForm();
        ToggleArea = toggleArea;
        ShowAt(contentTopLeft, _ownerForm);
        if (_ownerForm is not null)
        {
            _ownerForm.Deactivate += OnOwnerChanged;
            _ownerForm.Move += OnOwnerChanged;
        }

        Application.AddMessageFilter(this);
        _filtering = true;
        KitToolTip.Hide();
    }

    /// <summary>A key pressed while the popup is open (Esc, F4 and Tab are handled already); true swallows it.</summary>
    protected virtual bool OnKey(Keys key) => true;

    /// <summary>The wheel turned over the popup (<paramref name="delta"/> positive away from the user).</summary>
    protected virtual void OnWheel(int delta, Point screen)
    {
    }

    /// <summary>
    /// Closes the popup (hides it and raises <see cref="Dismissed"/>), then runs <paramref name="then"/> and disposes the
    /// popup from a posted message, on the normal message loop: a dialog it opens gets its own keys, and an exception
    /// reaches the app's error report.
    /// </summary>
    protected void CloseWith(Action? then)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopWatching();
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
        if (IsHandleCreated)
        {
            BeginInvoke(() => Finish(then));
        }
        else
        {
            Finish(then);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            StopWatching();
        }

        base.Dispose(disposing);
    }

    private static Point ScreenPointOf(Message m)
    {
        Point point = PointFromLParam(m.LParam);
        if (m.Msg < WmMouseFirst)
        {
            // Non-client messages carry screen coordinates already.
            return point;
        }

        NativePoint native = new() { X = point.X, Y = point.Y };
        _ = ClientToScreen(m.HWnd, ref native);
        return new Point(native.X, native.Y);
    }

    private bool FilterKey(Keys key)
    {
        switch (key)
        {
            case Keys.Escape:
            case Keys.F4:
                CloseWith(null);
                return true;
            case Keys.Tab:
                CloseWith(null);
                return false;
            default:
                return OnKey(key);
        }
    }

    private void Finish(Action? then)
    {
        try
        {
            then?.Invoke();
        }
        finally
        {
            Dispose();
        }
    }

    private void OnOwnerChanged(object? sender, EventArgs e) => CloseWith(null);

    private void StopWatching()
    {
        if (_filtering)
        {
            Application.RemoveMessageFilter(this);
            _filtering = false;
        }

        if (_ownerForm is not null)
        {
            _ownerForm.Deactivate -= OnOwnerChanged;
            _ownerForm.Move -= OnOwnerChanged;
            _ownerForm = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    /// <summary>
    /// Swallows the release of a press that only closed a popup, so the button under it does not act on half a click
    /// (a drop-down opens on the release). It stands down at the next press, or when the pointer moves with the button
    /// up (the release went to another program).
    /// </summary>
    private sealed class ReleaseSwallower : IMessageFilter
    {
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonUp = 0x0202;
        private const long MkLButton = 0x0001;

        [ThreadStatic]
        private static ReleaseSwallower? t_armed;

        private bool _done;

        public static void Arm()
        {
            if (t_armed is null)
            {
                t_armed = new ReleaseSwallower();
                Application.AddMessageFilter(t_armed);
            }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (_done)
            {
                return false;
            }

            switch (m.Msg)
            {
                case WmLButtonUp:
                    StandDown();
                    return true;
                case WmMouseMove when (m.WParam.ToInt64() & MkLButton) == 0:
                case WmLButtonDown or WmLButtonDblClk or WmRButtonDown or WmRButtonDblClk or WmMButtonDown or WmMButtonDblClk
                    or WmXButtonDown or WmXButtonDblClk or WmNcLButtonDown or WmNcRButtonDown or WmNcMButtonDown or WmNcXButtonDown:
                    StandDown();
                    return false;
                default:
                    return false;
            }
        }

        private void StandDown()
        {
            _done = true;
            if (t_armed == this)
            {
                t_armed = null;
            }

            Application.RemoveMessageFilter(this);
        }
    }
}
