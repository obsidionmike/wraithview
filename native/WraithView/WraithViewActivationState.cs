namespace WraithView.Preview;

// Logical selection advances as soon as a hotkey request is accepted. A short
// observation grace prevents a late foreground event from rewinding rapid cycles.
// This never waits or retries activation; idle refresh still reconciles failed focus.
internal sealed class WraithViewActivationState
{
    internal const int ObservationGraceMs = 250;
    private long _reconcileAfter;
    public nint ActiveHandle { get; private set; }

    public void Accept(nint handle, long now, bool optimistic)
    {
        ActiveHandle = handle;
        _reconcileAfter = optimistic ? now + ObservationGraceMs : 0;
    }

    public bool TryObserve(nint foreground, long now)
    {
        if (foreground != ActiveHandle && now < _reconcileAfter) return false;
        ActiveHandle = foreground;
        _reconcileAfter = 0;
        return true;
    }

    public void Clear()
    {
        ActiveHandle = 0;
        _reconcileAfter = 0;
    }
}
