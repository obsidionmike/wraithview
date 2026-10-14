using System.Drawing;
using WraithView.Preview;
using Xunit;

namespace WraithView.Tests;

/// <summary>
/// Snapping as reported in testing: dragging one preview up under another snapped its top onto the other's
/// top (overlapping it) instead of stacking below; previews never snapped side by side; and previews couldn't
/// be parked half off the top of the screen.
/// </summary>
public class PreviewSnappingTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);
    private static readonly Rectangle[] OneMonitor = { Screen };
    private const int Snap = 12;

    private static Rectangle Move(Rectangle raw, params Rectangle[] others) =>
        PreviewSnapping.Move(raw, others, OneMonitor, Screen, snapEnabled: true, Snap);

    [Fact]
    public void ABottomPreviewDraggedUpStacksUnderTheOneAbove()
    {
        var top = new Rectangle(100, 100, 320, 180);
        // Dragged up so its top is 8 px below the other's bottom (280).
        var moved = Move(new Rectangle(100, 288, 320, 180), top);
        Assert.Equal(280, moved.Top);
        Assert.False(moved.IntersectsWith(top));
    }

    [Fact]
    public void PreviewsSnapSideBySide()
    {
        var left = new Rectangle(100, 100, 320, 180);
        var moved = Move(new Rectangle(428, 130, 320, 180), left);
        Assert.Equal(420, moved.Left); // left edge onto the other's right edge
        Assert.Equal(130, moved.Top);  // nothing to snap to vertically within 12 px
    }

    [Fact]
    public void MatchingEdgesStillLineUp()
    {
        var anchor = new Rectangle(100, 100, 320, 180);
        var moved = Move(new Rectangle(106, 285, 320, 180), anchor);
        Assert.Equal(100, moved.Left); // lined up with the left edge
        Assert.Equal(280, moved.Top);  // and stacked below
    }

    [Fact]
    public void TheClosestEdgeWinsNotTheLastOneChecked()
    {
        var near = new Rectangle(100, 100, 320, 180);   // bottom at 280: 3 px away
        var far = new Rectangle(300, 471, 320, 180);    // top at 471: 8 px below the moving preview's bottom (463)
        var moved = Move(new Rectangle(100, 283, 320, 180), near, far);
        Assert.Equal(280, moved.Top);
    }

    [Fact]
    public void PreviewsFarAwayOnTheOtherAxisAreIgnored()
    {
        // Same top line, but 900 px to the right: not beside it, so no vertical snap.
        var faraway = new Rectangle(1300, 100, 320, 180);
        var moved = Move(new Rectangle(100, 106, 320, 180), faraway);
        Assert.Equal(106, moved.Top);
    }

    [Fact]
    public void TheTopOfTheScreenHoldsThenLetsGo()
    {
        var breakout = PreviewSnapping.BreakoutFor(Snap);
        Assert.Equal(0, Move(new Rectangle(100, 6, 320, 180)).Top);             // near: snaps up
        Assert.Equal(0, Move(new Rectangle(100, -breakout, 320, 180)).Top);     // pushed: held
        Assert.Equal(-1, Move(new Rectangle(100, -breakout - 1, 320, 180)).Top); // through: follows smoothly
        Assert.Equal(-90, Move(new Rectangle(100, -90 - breakout, 320, 180)).Top); // half off the top
    }

    [Fact]
    public void SomeOfThePreviewAlwaysStaysOnScreen()
    {
        var moved = Move(new Rectangle(100, -5000, 320, 180));
        Assert.Equal(PreviewSnapping.MinVisible, moved.Bottom);
    }

    [Fact]
    public void WithSnappingOffPreviewsMoveFreelyButStayFindable()
    {
        var moved = PreviewSnapping.Move(new Rectangle(100, -50, 320, 180), Array.Empty<Rectangle>(), OneMonitor, Screen, snapEnabled: false, Snap);
        Assert.Equal(-50, moved.Top);
    }

    [Fact]
    public void ASeamBetweenMonitorsDoesNotHoldThePreview()
    {
        var left = new Rectangle(0, 0, 1920, 1080);
        var right = new Rectangle(1920, 0, 1920, 1080);
        var desktop = Rectangle.Union(left, right);
        // 60 px across the seam, which would be "held" at an outer edge.
        var moved = PreviewSnapping.Move(new Rectangle(1660, 300, 320, 180), Array.Empty<Rectangle>(), new[] { left, right }, desktop, true, Snap);
        Assert.Equal(1660, moved.Left);
        // Close to the seam it snaps onto it.
        var near = PreviewSnapping.Move(new Rectangle(1606, 300, 320, 180), Array.Empty<Rectangle>(), new[] { left, right }, desktop, true, Snap);
        Assert.Equal(1600, near.Left);
    }

    [Fact]
    public void ResizingSnapsTheDraggedEdgeWithoutMovingThePreview()
    {
        var neighbour = new Rectangle(500, 100, 320, 180);
        var resized = PreviewSnapping.Resize(new Rectangle(100, 100, 395, 175), new[] { neighbour }, OneMonitor, Screen, true, Snap, 16, 4000);
        Assert.Equal(100, resized.Left);
        Assert.Equal(400, resized.Width);   // right edge onto the neighbour's left edge (500)
        Assert.Equal(180, resized.Height);  // bottom onto the neighbour's bottom (280)
    }
}
