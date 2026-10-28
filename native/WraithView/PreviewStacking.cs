using System.Drawing;

namespace WraithView.Preview;

/// <summary>
/// Which preview is on top where previews overlap. DWM draws thumbnails registered later above earlier ones,
/// so each preview carries the order its thumbnail was registered in, and the highest one under the cursor is
/// the one you see. Picking by anything else (the active client, dictionary order) grabbed the preview
/// underneath.
/// </summary>
internal static class PreviewStacking
{
    public static T? TopmostAt<T>(IEnumerable<T> previews, Func<T, Rectangle> frame, Func<T, long> stackOrder, Point point)
        where T : class
    {
        T? top = null;
        var topOrder = long.MinValue;
        foreach (var preview in previews)
        {
            if (!frame(preview).Contains(point)) continue;
            var order = stackOrder(preview);
            if (top != null && order <= topOrder) continue;
            top = preview;
            topOrder = order;
        }

        return top;
    }
}
