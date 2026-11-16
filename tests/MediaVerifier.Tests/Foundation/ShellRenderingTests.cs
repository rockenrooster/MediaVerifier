using System.Drawing.Drawing2D;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Shell;

namespace MediaVerifier.Tests.Foundation;

public sealed class ShellRenderingTests
{
    [Fact]
    public void EveryIconHasGeometry()
    {
        Assert.Equal(50, IconGeometry.Count);
        foreach (IconName name in Enum.GetValues<IconName>())
        {
            IconShape shape = IconGeometry.Get(name);
            Assert.True(shape.Stroked.PointCount + shape.Filled.PointCount > 0, name.ToString());
            RectangleF bounds = shape.Stroked.PointCount > 0 ? shape.Stroked.GetBounds() : shape.Filled.GetBounds();
            Assert.InRange(bounds.Left, 0f, 16f);
            Assert.InRange(bounds.Right, 0f, 16f);
        }
    }

    [Fact]
    public void PathTokenizerSplitsCommandsFromNumbers()
    {
        Assert.Equal(["M", "4", "11.5", "V", "7.5", "C", "4", "5"], IconGeometry.Tokenize("M4 11.5 V7.5 C4 5"));
        using GraphicsPath path = new();
        IconGeometry.AddSvgPath(path, "M2 4.5 H6 L7.5 6 H14 V12.5 H2 Z");
        Assert.True(path.PointCount >= 6);
    }

    [Fact]
    public void RailPaintsWithoutErrors()
    {
        using NavigationRail rail = new() { Size = new Size(200, 800) };
        rail.SetIssuesBadge(128, MediaVerifier.Core.Model.Severity.Critical);
        rail.UpdateAvailable = true;
        using Bitmap bitmap = new(200, 800);
        rail.DrawToBitmap(bitmap, new Rectangle(0, 0, 200, 800));

        // The selected Dashboard item is filled with the Selected token.
        Color selected = bitmap.GetPixel(100, 70);
        Assert.Equal(MediaVerifier.UI.Theme.Theme.Tokens.Selected.ToArgb(), selected.ToArgb());
    }
}
