using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;

namespace MediaVerifier.Tests.UI;

/// <summary>Kit scrolling, stroke weights and recent-date wording (tasks UK-07, UK-11, UI-05).</summary>
[Collection(UiCollection.Name)]
public sealed class ScrollingTests
{
    [Theory]
    [InlineData(0, 500, 540, 400, 140)]
    [InlineData(300, 100, 140, 400, 100)]
    [InlineData(100, 150, 200, 400, 100)]
    [InlineData(100, 600, 1200, 400, 600)]
    public void Offset_to_show_moves_the_least_needed(int offset, int top, int bottom, int viewport, int expected)
    {
        Assert.Equal(expected, KitScrolling.OffsetToShow(offset, top, bottom, viewport));
    }

    [Fact]
    public void Scroll_panel_clamps_and_moves_its_content()
    {
        using Control content = new() { Height = 1000 };
        using ScrollPanel panel = new(content) { Size = new Size(300, 300) };
        panel.PerformLayout();

        Assert.Equal(300, content.Width);
        Assert.Equal(700, panel.ScrollMax);
        panel.ScrollOffset = 2000;
        Assert.Equal(700, panel.ScrollOffset);
        Assert.Equal(-700, content.Top);

        panel.EnsureVisible(new Rectangle(0, 100, 10, 40));
        Assert.Equal(100, panel.ScrollOffset);
        panel.ScrollToTop();
        Assert.Equal(0, content.Top);

        content.Height = 200;
        Assert.Equal(0, panel.ScrollMax);
    }

    [Fact]
    public void A_wheel_turn_over_the_content_scrolls_the_panel_once()
    {
        using Form form = new() { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(400, 400) };
        using Control content = new() { Height = 2000 };
        using ScrollPanel panel = new(content) { Bounds = new Rectangle(0, 0, 300, 300) };
        form.Controls.Add(panel);

        // Create the windows parent first so the content is a child window of the panel.
        _ = form.Handle;
        _ = panel.Handle;
        _ = content.Handle;
        panel.PerformLayout();

        // The wheel message goes to the content; Windows passes it on to the parent when it is not handled.
        Point screen = content.PointToScreen(new Point(20, 20));
        nint wParam = unchecked((nint)(-120 << 16));
        nint lParam = (screen.Y << 16) | (screen.X & 0xFFFF);
        SendMessageW(content.Handle, 0x020A, wParam, lParam);

        Assert.Equal(KitScrolling.WheelStep(-120, panel.DeviceDpi), panel.ScrollOffset);
    }

    [Theory]
    [InlineData(1.5f, 1.501f)]
    [InlineData(1.3f, 1.501f)]
    [InlineData(1.2f, 1.2f)]
    [InlineData(1f, 1f)]
    [InlineData(2.25f, 2.25f)]
    public void Strokes_at_the_gdi_threshold_are_nudged_past_it(float requested, float expected)
    {
        Assert.Equal(expected, GdiCache.StrokeWidth(requested));
    }

    [Fact]
    public void A_cached_one_and_a_half_pixel_pen_draws_one_and_a_half_pixels_of_ink()
    {
        using Bitmap bitmap = new(20, 20);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawLine(GdiCache.RoundPen(Color.White, 1.5f), 2, 10, 18, 10);
        }

        float ink = 0;
        for (int y = 0; y < 20; y++)
        {
            ink += bitmap.GetPixel(10, y).R / 255f;
        }

        Assert.InRange(ink, 1.4f, 1.6f);
    }

    [Fact]
    public void Recent_dates_carry_the_year_when_older_than_half_a_year()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        try
        {
            DateTimeOffset now = Local(2026, 11, 16, 14, 20);
            Assert.Equal("Today 03:12", UiFormat.RecentDate(Local(2026, 11, 16, 3, 12), now));
            Assert.Equal("Yesterday 03:00", UiFormat.RecentDate(Local(2026, 11, 15, 3, 0), now));
            Assert.Equal("Thu", UiFormat.RecentDay(Local(2026, 11, 12, 9, 0), now));
            Assert.Equal("8 Nov 03:00", UiFormat.RecentDate(Local(2026, 11, 8, 3, 0), now));
            Assert.Equal("12 Oct", UiFormat.RecentDay(Local(2026, 10, 12, 3, 0), now));
            Assert.Equal("14 Mar 2026", UiFormat.RecentDay(Local(2026, 3, 14, 10, 0), now));
            Assert.Equal("20 Dec 2025", UiFormat.RecentDay(Local(2025, 12, 20, 10, 0), now));
            Assert.Equal("scanned today 03:12", "scanned " + UiFormat.RecentDate(Local(2026, 11, 16, 3, 12), now, inSentence: true));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        DateTime local = new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);
}
