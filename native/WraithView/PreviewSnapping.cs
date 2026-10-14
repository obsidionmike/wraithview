using System.Drawing;

namespace WraithView.Preview;

/// <summary>
/// Where a preview lands while it's dragged or resized.
///
/// Snapping looks at each axis separately and takes the single closest edge within the snap distance, so the
/// last preview checked can't win over a nearer one. Against other previews, an edge snaps to the matching
/// edge (left to left, to line up) and to the opposite edge (top to bottom, to stack or sit side by side),
/// but only when the two previews are beside each other on the other axis.
///
/// Screen edges are sticky: a preview pushed into an edge holds there until the drag goes a further
/// <see cref="BreakoutFor"/> pixels, then follows the cursor off-screen. Some of it always stays on the virtual
/// desktop so it can't be lost.
/// </summary>
internal static class PreviewSnapping
{
    /// <summary>How much of a preview must stay on the desktop, in px.</summary>
    public const int MinVisible = 24;

    /// <summary>How far past a screen edge you push before the preview leaves the screen.</summary>
    public static int BreakoutFor(int snap) => Math.Max(40, snap * 3);

    /// <param name="dragStart">Where the preview was when the drag began. An edge it already sat past at
    /// that point doesn't hold it again (it was parked off-screen on purpose), so it moves freely there.</param>
    public static Rectangle Move(Rectangle raw, IReadOnlyList<Rectangle> others, IReadOnlyList<Rectangle> monitors, Rectangle desktop, bool snapEnabled, int snap, Rectangle? dragStart = null)
    {
        var screen = MonitorFor(raw, monitors, desktop);
        snap = snapEnabled ? Math.Max(0, snap) : 0;
        var breakout = BreakoutFor(snap);

        // An edge with another monitor beyond it is a seam: previews cross it freely (with a light snap).
        var cx = raw.Left + raw.Width / 2;
        var cy = raw.Top + raw.Height / 2;
        bool Beyond(int x, int y) => monitors.Any(m => m != screen && m.Contains(x, y));
        var leftOuter = !Beyond(screen.Left - 1, cy);
        var rightOuter = !Beyond(screen.Right, cy);
        var topOuter = !Beyond(cx, screen.Top - 1);
        var bottomOuter = !Beyond(cx, screen.Bottom);

        // 1. Screen edges first: held at an edge, or following the cursor past it.
        var start = dragStart ?? screen; // no start given: treat the drag as having begun on-screen
        var (sx, xEdge) = ScreenDelta(raw.Left, raw.Right, screen.Left, screen.Right, leftOuter, rightOuter, snap, breakout, snapEnabled,
            startedPastStart: start.Left < screen.Left, startedPastEnd: start.Right > screen.Right);
        var (sy, yEdge) = ScreenDelta(raw.Top, raw.Bottom, screen.Top, screen.Bottom, topOuter, bottomOuter, snap, breakout, snapEnabled,
            startedPastStart: start.Top < screen.Top, startedPastEnd: start.Bottom > screen.Bottom);
        var placed = raw;
        if (xEdge is EdgeState.Held or EdgeState.Past) placed.X += sx ?? 0;
        if (yEdge is EdgeState.Held or EdgeState.Past) placed.Y += sy ?? 0;

        // 2. Then other previews, measured from where the preview actually is. (Measuring from the raw cursor
        // position broke vertical snapping off-screen, where the two differ by the edge's hold distance.)
        var dx = BestPreviewDelta(placed.Left, placed.Right, others.Where(o => Overlaps(placed.Top, placed.Bottom, o.Top, o.Bottom, snap)).SelectMany(o => new[] { o.Left, o.Right }), snap);
        var dy = BestPreviewDelta(placed.Top, placed.Bottom, others.Where(o => Overlaps(placed.Left, placed.Right, o.Left, o.Right, snap)).SelectMany(o => new[] { o.Top, o.Bottom }), snap);

        placed.X += Pick(dx, sx, xEdge);
        placed.Y += Pick(dy, sy, yEdge);
        return KeepOnDesktop(placed, desktop);
    }

    public static Rectangle Resize(Rectangle raw, IReadOnlyList<Rectangle> others, IReadOnlyList<Rectangle> monitors, Rectangle desktop, bool snapEnabled, int snap, int minSize, int maxSize)
    {
        if (!snapEnabled || snap <= 0) return raw;
        var screen = MonitorFor(raw, monitors, desktop);
        var right = Closest(raw.Right, others.Where(o => Overlaps(raw.Top, raw.Bottom, o.Top, o.Bottom, snap)).SelectMany(o => new[] { o.Left, o.Right }).Append(screen.Right), snap);
        var bottom = Closest(raw.Bottom, others.Where(o => Overlaps(raw.Left, raw.Right, o.Left, o.Right, snap)).SelectMany(o => new[] { o.Top, o.Bottom }).Append(screen.Bottom), snap);
        var result = raw;
        if (right is int r) result.Width = Math.Clamp(r - raw.Left, minSize, maxSize);
        if (bottom is int b) result.Height = Math.Clamp(b - raw.Top, minSize, maxSize);
        return result;
    }

    /// <summary>Keeps at least <see cref="MinVisible"/> px of the preview on the desktop on each axis.</summary>
    public static Rectangle KeepOnDesktop(Rectangle rect, Rectangle desktop)
    {
        var keepX = Math.Min(MinVisible, rect.Width);
        var keepY = Math.Min(MinVisible, rect.Height);
        rect.X = Math.Clamp(rect.X, desktop.Left - rect.Width + keepX, desktop.Right - keepX);
        rect.Y = Math.Clamp(rect.Y, desktop.Top - rect.Height + keepY, desktop.Bottom - keepY);
        return rect;
    }

    /// <summary>The monitor a preview mostly sits on (by overlap, then by distance from its centre).</summary>
    public static Rectangle MonitorFor(Rectangle rect, IReadOnlyList<Rectangle> monitors, Rectangle desktop)
    {
        if (monitors.Count == 0) return desktop;
        Rectangle best = monitors[0];
        long bestArea = -1;
        foreach (var m in monitors)
        {
            var i = Rectangle.Intersect(rect, m);
            long area = i.IsEmpty ? 0 : (long)i.Width * i.Height;
            if (area > bestArea) (best, bestArea) = (m, area);
        }
        if (bestArea > 0) return best;
        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        return monitors.OrderBy(m => Distance(m, cx, cy)).First();
    }

    private static long Distance(Rectangle m, int x, int y)
    {
        long dx = x < m.Left ? m.Left - x : x > m.Right ? x - m.Right : 0;
        long dy = y < m.Top ? m.Top - y : y > m.Bottom ? y - m.Bottom : 0;
        return dx * dx + dy * dy;
    }

    private static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd, int slack) => aStart < bEnd + slack && bStart < aEnd + slack;

    /// <summary>Smallest move (within snap) that puts either of the preview's edges on one of the lines.</summary>
    private static int? BestPreviewDelta(int start, int end, IEnumerable<int> lines, int snap)
    {
        if (snap <= 0) return null;
        int? best = null;
        foreach (var line in lines)
        {
            foreach (var delta in new[] { line - start, line - end })
            {
                if (Math.Abs(delta) <= snap && (best is null || Math.Abs(delta) < Math.Abs(best.Value))) best = delta;
            }
        }
        return best;
    }

    private enum EdgeState
    {
        /// <summary>Not near a screen edge.</summary>
        None,
        /// <summary>Inside the screen, within snap distance of an edge.</summary>
        Near,
        /// <summary>Pushed past an edge but not yet by the breakout distance: the edge holds it.</summary>
        Held,
        /// <summary>Pushed through: the preview follows the cursor off-screen.</summary>
        Past,
    }

    /// <summary>Screen edge handling for one axis: the move to apply and what the edge is doing.</summary>
    private static (int? delta, EdgeState state) ScreenDelta(int start, int end, int edgeStart, int edgeEnd, bool startOuter, bool endOuter, int snap, int breakout, bool snapEnabled, bool startedPastStart = false, bool startedPastEnd = false)
    {
        var pastStart = edgeStart - start; // > 0: pushed past the start edge
        var pastEnd = end - edgeEnd;       // > 0: pushed past the end edge
        // Seams between monitors: snap onto the line from either side, never hold.
        if ((pastStart > 0 && !startOuter) || (pastEnd > 0 && !endOuter))
        {
            int? line = null;
            if (!startOuter && snap > 0 && Math.Abs(pastStart) <= snap) line = pastStart;
            if (!endOuter && snap > 0 && Math.Abs(pastEnd) <= snap && (line is null || Math.Abs(pastEnd) < Math.Abs(line.Value))) line = -pastEnd;
            return (line, line is null ? EdgeState.None : EdgeState.Near);
        }
        // Began this drag already parked past the edge: follow the cursor exactly while it stays out there.
        if ((pastStart > 0 && startedPastStart) || (pastEnd > 0 && startedPastEnd)) return (0, EdgeState.Past);
        if (pastStart > 0)
        {
            if (!snapEnabled) return (0, EdgeState.Past);
            return pastStart <= breakout ? (pastStart, EdgeState.Held) : (breakout, EdgeState.Past);
        }
        if (pastEnd > 0)
        {
            if (!snapEnabled) return (0, EdgeState.Past);
            return pastEnd <= breakout ? (-pastEnd, EdgeState.Held) : (-breakout, EdgeState.Past);
        }
        // Inside the screen: snap onto an edge that's close.
        int? best = null;
        if (snap > 0 && -pastStart <= snap) best = pastStart;
        if (snap > 0 && -pastEnd <= snap && (best is null || -pastEnd < Math.Abs(best.Value))) best = -pastEnd;
        return (best, best is null ? EdgeState.None : EdgeState.Near);
    }

    /// <summary>The extra move on one axis once the screen edge has been applied.</summary>
    private static int Pick(int? previewDelta, int? screenDelta, EdgeState edge) => edge switch
    {
        // Pushed into an edge: the edge holds it there.
        EdgeState.Held => 0,
        // Off-screen, following the cursor: other previews still snap (two parked previews line up).
        EdgeState.Past => previewDelta ?? 0,
        // Near an edge on-screen: whichever of the edge and a preview is closer.
        EdgeState.Near when screenDelta is int s && previewDelta is int p => Math.Abs(p) <= Math.Abs(s) ? p : s,
        EdgeState.Near => screenDelta ?? previewDelta ?? 0,
        _ => previewDelta ?? 0,
    };

    private static int? Closest(int value, IEnumerable<int> lines, int snap)
    {
        int? best = null;
        foreach (var line in lines)
        {
            if (Math.Abs(line - value) <= snap && (best is null || Math.Abs(line - value) < Math.Abs(best.Value - value))) best = line;
        }
        return best;
    }
}
