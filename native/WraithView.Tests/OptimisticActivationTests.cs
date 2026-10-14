using WraithView.Preview;

namespace WraithView.Tests;

public class OptimisticActivationTests
{
    [Fact]
    public void LaggingForegroundDoesNotRepeatTargetsDuringRapidCycling()
    {
        var state = new WraithViewActivationState();
        nint[] clients = [1, 2, 3, 4];
        state.TryObserve(1, 0);
        var selected = new List<nint>();
        for (int press = 0; press < 12; press++)
        {
            var now = press * 30L;
            var next = clients[WraithViewCycleState.NextIndex(clients, 1, state.ActiveHandle, 1, 0)];
            state.Accept(next, now, optimistic: true);
            selected.Add(next);
            state.TryObserve(1, now + 10);
        }
        Assert.Equal(new nint[] { 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 1 }, selected);
    }

    [Fact]
    public void FailedFocusReconcilesOnLaterObservationWithoutRetrying()
    {
        var state = new WraithViewActivationState();
        state.Accept(2, 1000, optimistic: true);
        Assert.False(state.TryObserve(1, 1100));
        Assert.Equal((nint)2, state.ActiveHandle);
        Assert.True(state.TryObserve(1, 1000 + WraithViewActivationState.ObservationGraceMs));
        Assert.Equal((nint)1, state.ActiveHandle);
    }

    [Fact]
    public void NewRequestProtectsItsOwnPositionFromEarlierForegroundEvents()
    {
        var state = new WraithViewActivationState();
        state.Accept(2, 1000, optimistic: true);
        state.Accept(3, 1200, optimistic: true);
        Assert.False(state.TryObserve(2, 1300));
        Assert.Equal((nint)3, state.ActiveHandle);
        Assert.True(state.TryObserve(3, 1310));
        // Once observed, a subsequent manual switch is adopted immediately.
        Assert.True(state.TryObserve(4, 1320));
        Assert.Equal((nint)4, state.ActiveHandle);
    }

    [Fact]
    public void ThumbnailActivationAndClosedTargetClearPendingObservationGrace()
    {
        var state = new WraithViewActivationState();
        state.Accept(2, 1000, optimistic: true);
        state.Accept(3, 1010, optimistic: false);
        Assert.True(state.TryObserve(4, 1020));
        state.Accept(2, 1030, optimistic: true);
        state.Clear();
        Assert.Equal(nint.Zero, state.ActiveHandle);
        Assert.True(state.TryObserve(4, 1040));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(-1, 8)]
    public void LaggingForegroundInAnotherGroupDoesNotChangeRememberedResume(int direction, int expected)
    {
        nint[] group = Enumerable.Range(1, 10).Select(value => (nint)value).ToArray();
        var state = new WraithViewActivationState();
        state.Accept(15, 1000, optimistic: true);
        Assert.False(state.TryObserve(16, 1010));
        var index = WraithViewCycleState.NextIndex(group, direction, state.ActiveHandle, 16, 9, true);
        Assert.Equal((nint)expected, group[index]);
    }
}
