using System.Drawing;
using WraithView.Preview;
using Xunit;

namespace WraithView.Tests;

/// <summary>
/// Overlapping previews: a press used to pick the active client's preview, or else whichever came first in
/// the preview dictionary, so it often grabbed the one underneath. It must pick the one drawn on top.
/// </summary>
public class PreviewStackingTests
{
    private sealed record Item(string Name, Rectangle Frame, long Order);

    private static Item? Pick(Point point, params Item[] items)
        => PreviewStacking.TopmostAt(items, item => item.Frame, item => item.Order, point);

    [Fact]
    public void WhereTwoOverlapTheHigherStackOrderWins()
    {
        var below = new Item("below", new Rectangle(0, 0, 320, 200), 1);
        var above = new Item("above", new Rectangle(100, 100, 320, 200), 2);

        Assert.Same(above, Pick(new Point(150, 150), below, above));
        Assert.Same(above, Pick(new Point(150, 150), above, below));
    }

    [Fact]
    public void OutsideTheOverlapEachPreviewIsItsOwn()
    {
        var below = new Item("below", new Rectangle(0, 0, 320, 200), 1);
        var above = new Item("above", new Rectangle(100, 100, 320, 200), 2);

        Assert.Same(below, Pick(new Point(10, 10), below, above));
        Assert.Same(above, Pick(new Point(400, 280), below, above));
        Assert.Null(Pick(new Point(1000, 1000), below, above));
    }
}

/// <summary>
/// A preview moved or resized at character select can't be saved (no character name yet). On login it used
/// to jump to the character's old saved layout; now the character-select placement wins once and is saved.
/// </summary>
public class PreviewCharacterSelectCarryOverTests
{
    private static WraithViewPreviewPositionMemory NewMemory()
    {
        var memory = new WraithViewPreviewPositionMemory();
        memory.BeginContext("default", 320, 204, Array.Empty<ScreenPixelInfo>());
        return memory;
    }

    [Fact]
    public void AnAdjustedClientIsCarriedOverOnceAfterLogin()
    {
        var memory = NewMemory();
        var identity = new PreviewClientIdentity((nint)0x301, 61);
        memory.Remember(identity, new Rectangle(10, 10, 480, 270));
        memory.MarkAdjustedWhileNameless(identity);

        Assert.True(memory.TakeAdjustedWhileNameless(identity));
        Assert.False(memory.TakeAdjustedWhileNameless(identity));
    }

    [Fact]
    public void AnUntouchedClientIsNotCarriedOver()
    {
        var memory = NewMemory();
        var identity = new PreviewClientIdentity((nint)0x302, 62);
        memory.Remember(identity, new Rectangle(10, 10, 320, 204));

        Assert.False(memory.TakeAdjustedWhileNameless(identity));
    }

    [Fact]
    public void ClosingTheClientForgetsTheMark()
    {
        var memory = NewMemory();
        var identity = new PreviewClientIdentity((nint)0x303, 63);
        memory.Remember(identity, new Rectangle(10, 10, 480, 270));
        memory.MarkAdjustedWhileNameless(identity);
        memory.PurgeExcept(new HashSet<PreviewClientIdentity>());
        memory.Remember(identity, new Rectangle(10, 10, 480, 270));

        Assert.False(memory.TakeAdjustedWhileNameless(identity));
    }

    [Fact]
    public void TheCarriedOverPlacementBeatsTheSavedLayout()
    {
        var saved = new Rectangle(0, 0, 320, 204);
        var remembered = new Rectangle(50, 60, 480, 270);
        var fallback = new Rectangle(70, 80, 320, 204);

        Assert.Equal(saved, WraithViewPreviewPositionMemory.Resolve(saved, remembered, null, fallback));
        Assert.Equal(remembered, WraithViewPreviewPositionMemory.Resolve(saved, remembered, null, fallback, preferRemembered: true));
    }

    [Fact]
    public void ResizingChangesTheDefaultSizeWithoutForgettingPositions()
    {
        var memory = NewMemory();
        var identity = new PreviewClientIdentity((nint)0x304, 64);
        memory.Remember(identity, new Rectangle(10, 10, 480, 270));

        memory.AdoptPreviewSize(480, 270);

        Assert.True(memory.MatchesContext("default", 480, 270, Array.Empty<ScreenPixelInfo>()));
        Assert.True(memory.TryGet(identity, out _));
    }
}
