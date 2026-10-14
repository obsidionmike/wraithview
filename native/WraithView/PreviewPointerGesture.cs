using System.Drawing;

namespace WraithView.Preview;

/// <summary>
/// Distinguishes a click from a drag for preview interactions.
///
/// Clicking a preview switches to that client, so the decision has to be forgiving of the small
/// cursor movement that happens during any real click. It is made on the net displacement
/// between press and release rather than on the largest movement seen along the way: a cursor
/// that wanders and comes back has not been dragged anywhere, and swallowing that click would
/// leave the user pressing a preview that never responds.
///
/// The tolerance is per axis, matching the system drag metrics (SM_CXDRAG / SM_CYDRAG) that
/// every other Windows application uses.
/// </summary>
internal static class PreviewPointerGesture
{
    public static bool IsClick(Point start, Point end, Size dragSize)
    {
        // Max(0, ...) so a zero or nonsensical metric degrades to "must not move at all"
        // rather than inverting the comparison and making every click impossible.
        return Math.Abs(end.X - start.X) <= Math.Max(0, dragSize.Width)
            && Math.Abs(end.Y - start.Y) <= Math.Max(0, dragSize.Height);
    }

    /// <summary>Within this distance of the press, a release still counts as a click, px per axis.</summary>
    public static readonly Size ClickSlop = new(10, 10);

    /// <summary>A press must be held this long before small movements start a drag.</summary>
    public const int DragHoldMs = 180;

    /// <summary>Movement that starts a drag once the hold time has passed, px.</summary>
    public const int HeldDragDistance = 6;

    /// <summary>Movement that starts a drag straight away (a deliberate flick), px.</summary>
    public const int FlickDragDistance = 24;

    /// <summary>
    /// Whether a press on a preview has turned into a drag. Clicking to switch clients often moves the
    /// mouse a few pixels; with only the system drag size (4 px) as the test, those clicks moved the preview
    /// instead. A drag now needs either a short hold first or a clearly deliberate movement.
    /// </summary>
    public static bool ShouldStartDrag(Point start, Point current, long heldMs)
    {
        var distance = Math.Max(Math.Abs(current.X - start.X), Math.Abs(current.Y - start.Y));
        return distance >= FlickDragDistance || (heldMs >= DragHoldMs && distance > HeldDragDistance);
    }

    /// <summary>
    /// With previews locked there is nothing to drag, so distance travelled during the press is
    /// meaningless — a cursor moving at ordinary speed covers tens of pixels in the time a real
    /// click takes, far past any drag-size tolerance, and net-displacement tests like
    /// <see cref="IsClick"/> would swallow the click just as the old peak-displacement latch did.
    /// The only question that matters when locked is where the button came back up: releasing
    /// over the same preview that was pressed activates it, releasing elsewhere is a cancelled
    /// click (the user pressed, changed their mind, and dragged off before letting go).
    /// </summary>
    public static bool IsLockedReleaseActivation(Rectangle pressedFrame, Point releasePointAbsolute)
    {
        return pressedFrame.Contains(releasePointAbsolute);
    }
}
