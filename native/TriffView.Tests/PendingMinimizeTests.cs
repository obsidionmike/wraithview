using TriffView.Preview;

namespace TriffView.Tests;

public class PendingMinimizeTests
{
    private static readonly EveClientWindow A = Client(1);
    private static readonly EveClientWindow B = Client(2);
    private static readonly EveClientWindow C = Client(3);
    private static readonly EveClientWindow D = Client(4);

    [Fact]
    public void ReplacementArrivingAfterImmediateCheckStillMinimizesPrevious()
    {
        var state = Enabled();
        var minimized = new List<nint>();
        state.Accept(B, true, [A]);
        Assert.False(state.Drain(() => A.Handle, _ => true, item => minimized.Add(item.Handle)));
        Assert.True(state.HasPending);
        // A later foreground notification, with no delay/retry inside Drain.
        Assert.True(state.Drain(() => B.Handle, _ => true, item => minimized.Add(item.Handle)));
        Assert.Equal(new[] { A.Handle }, minimized);
        Assert.False(state.HasPending);
        Assert.False(state.Drain(() => B.Handle, _ => true, item => minimized.Add(item.Handle)));
    }

    [Fact]
    public void RapidSequenceRetainsEachDepartedClientDespiteStaleActualForeground()
    {
        var state = Enabled();
        var minimized = new List<nint>();
        state.Accept(B, true, [A]);
        state.Accept(C, true, [A, B]);
        state.Accept(D, true, [A, C]);
        Assert.False(state.Drain(() => B.Handle, _ => true, item => minimized.Add(item.Handle)));
        Assert.False(state.Drain(() => C.Handle, _ => true, item => minimized.Add(item.Handle)));
        Assert.True(state.Drain(() => D.Handle, _ => true, item => minimized.Add(item.Handle)));
        Assert.Equal(new[] { A.Handle, B.Handle, C.Handle }, minimized);
        Assert.DoesNotContain(D.Handle, minimized);
    }

    [Fact]
    public void RequestedClientIsRetainedWhenForegroundReconciliationRewindsLogicalSelection()
    {
        var state = Enabled();
        var selection = new TriffViewActivationState();
        selection.Accept(B.Handle, 1000, optimistic: true);
        state.Accept(B, true, [A]);
        Assert.True(selection.TryObserve(A.Handle, 1500));
        // Both actual and logical foreground now say A, but B was also restored.
        var priorRequest = state.Target!;
        state.Accept(C, true, [A, priorRequest]);
        var minimized = new List<nint>();
        state.Drain(() => C.Handle, _ => true, item => minimized.Add(item.Handle));
        Assert.Equal(new[] { A.Handle, B.Handle }, minimized);
    }

    [Fact]
    public void ReturningToClientCancelsItsPendingMinimize()
    {
        var state = Enabled();
        var minimized = new List<nint>();
        state.Accept(B, true, [A]);
        state.Accept(A, true, [B]);
        Assert.True(state.Drain(() => A.Handle, _ => true, item => minimized.Add(item.Handle)));
        Assert.Equal(new[] { B.Handle }, minimized);
    }

    [Fact]
    public void FocusChangingDuringDispatchStopsRemainingMinimizes()
    {
        var state = Enabled();
        state.Accept(D, true, [A, B, C]);
        var foreground = D.Handle;
        var minimized = new List<nint>();
        state.Drain(() => foreground, _ => true, item => { minimized.Add(item.Handle); foreground = C.Handle; });
        Assert.Equal(new[] { A.Handle }, minimized);
        Assert.True(state.HasPending);
        state.Accept(C, true, [D]);
        state.Drain(() => foreground, _ => true, item => minimized.Add(item.Handle));
        Assert.Equal(new[] { A.Handle, B.Handle, D.Handle }, minimized);
    }

    [Fact]
    public void FocusIsRecheckedAfterWindowEligibilityInspection()
    {
        var state = Enabled();
        state.Accept(B, true, [A]);
        var foreground = B.Handle;
        Assert.False(state.Drain(() => foreground, _ => { foreground = A.Handle; return true; },
            _ => throw new Exception("Must not minimize the foreground client")));
        Assert.True(state.HasPending);
    }

    [Fact]
    public void NewSwitchDuringSynchronousDispatchInvalidatesOldWork()
    {
        var state = Enabled();
        state.Accept(C, true, [A, B]);
        var revision = state.Revision;
        var minimized = new List<nint>();
        state.Drain(() => C.Handle, _ => true, item =>
        {
            minimized.Add(item.Handle);
            state.Accept(B, true, [C]);
        });
        Assert.Equal(new[] { A.Handle }, minimized);
        Assert.NotEqual(revision, state.Revision);
        state.Drain(() => B.Handle, _ => true, item => minimized.Add(item.Handle));
        Assert.Equal(new[] { A.Handle, C.Handle }, minimized);
    }

    [Theory]
    [InlineData("profile", false)]
    [InlineData("other-profile", true)]
    public void DisableOrProfileChangeCancelsOutstandingMinimizes(string profile, bool enabled)
    {
        var state = Enabled();
        state.Accept(B, true, [A]);
        state.Configure(profile, enabled);
        Assert.False(state.HasPending);
        Assert.Null(state.Target);
        Assert.False(state.Drain(() => B.Handle, _ => true, _ => throw new Exception("Stale work")));
    }

    [Fact]
    public void LeavingEveSuspendingOrDisposingCanCancelWithoutLaterReplay()
    {
        var state = Enabled();
        state.Accept(B, true, [A]);
        state.Clear();
        state.Configure("profile", true);
        Assert.False(state.Drain(() => B.Handle, _ => true, _ => throw new Exception("Stale work")));
    }

    [Fact]
    public void NeverMinimizeClosedOrAlreadyMinimizedClientsAreRecheckedAtExecution()
    {
        var state = Enabled();
        state.Accept(D, true, [A, B, C]);
        var minimized = new List<nint>();
        // Policy/live-window validation can invalidate work recorded earlier.
        state.Drain(() => D.Handle, item => item.Handle == B.Handle, item => minimized.Add(item.Handle));
        Assert.Equal(new[] { B.Handle }, minimized);
        Assert.False(state.HasPending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MinimizeDisabledNeverQueuesWork(bool fromHotkey)
    {
        var state = new TriffViewMinimizeState();
        state.Configure("profile", false);
        state.Accept(B, fromHotkey, [A]);
        Assert.False(state.HasPending);
        Assert.Null(state.Target);
    }

    [Fact]
    public void TwoThousandAcceptedSwitchesWithDelayedFocusKeepOnlyInactiveVisitedClients()
    {
        var state = Enabled();
        var clients = Enumerable.Range(1, 18).Select(Client).ToArray();
        var logical = clients[0];
        var actual = logical;
        var minimized = new HashSet<nint>();
        for (int press = 1; press <= 2000; press++)
        {
            var target = clients[press % clients.Length];
            state.Accept(target, true, [actual, logical]);
            logical = target;
            minimized.Remove(target.Handle); // Accepted restore/activation.
            if (press % 7 == 0) actual = target; // Foreground trails the press stream.
            state.Drain(() => actual.Handle, item => !minimized.Contains(item.Handle), item =>
            {
                Assert.NotEqual(actual.Handle, item.Handle);
                Assert.NotEqual(logical.Handle, item.Handle);
                minimized.Add(item.Handle);
            });
        }
        actual = logical;
        state.Drain(() => actual.Handle, item => !minimized.Contains(item.Handle), item => minimized.Add(item.Handle));
        Assert.Equal(clients.Where(item => item.Handle != actual.Handle).Select(item => item.Handle).Order(), minimized.Order());
        Assert.False(state.HasPending);
    }

    private static TriffViewMinimizeState Enabled()
    {
        var state = new TriffViewMinimizeState();
        state.Configure("profile", true);
        return state;
    }

    private static EveClientWindow Client(int index) => new(index, $"EVE - Client {index}", $"Client {index}", (uint)index, false, false);
}
