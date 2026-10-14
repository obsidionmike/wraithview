namespace WraithView.Preview;

// Remembers only clients left behind by accepted switches. Foreground events
// finish the work; no timer, foreground wait, or posted native minimize is used.
// All access, including the synchronous minimize callback, is on the UI thread.
internal sealed class WraithViewMinimizeState
{
    private readonly Dictionary<nint, EveClientWindow> _pending = new();
    private string _profileId = "";
    private bool _enabled;
    private long _revision;
    public EveClientWindow? Target { get; private set; }
    public bool FromHotkey { get; private set; }
    public bool HasPending => _pending.Count != 0;
    public long Revision => _revision;

    public void Configure(string profileId, bool enabled)
    {
        if (!enabled || _profileId != profileId) Clear();
        _profileId = profileId;
        _enabled = enabled;
    }

    public void Accept(EveClientWindow target, bool fromHotkey, IEnumerable<EveClientWindow> previousClients)
    {
        if (!_enabled) return;
        _revision++;
        Target = target;
        FromHotkey = fromHotkey;
        // A -> B -> A must cancel the outstanding minimize of A before it runs.
        _pending.Remove(target.Handle);
        foreach (var previous in previousClients)
            if (previous.Handle != target.Handle)
                _pending[previous.Handle] = previous;
    }

    public bool Drain(Func<nint> foreground, Func<EveClientWindow, bool> eligible, Action<EveClientWindow> minimize)
    {
        if (!HasPending || Target == null) return false;
        var target = Target.Handle;
        var revision = _revision;
        var minimized = false;
        foreach (var previous in _pending.Values.ToArray())
        {
            if (_revision != revision || foreground() != target) break;
            var canMinimize = eligible(previous);
            if (_revision != revision) break;
            if (!canMinimize)
            {
                _pending.Remove(previous.Handle);
                continue;
            }
            // Eligibility can inspect live window state. Check focus again at
            // dispatch, and never carry a native minimize across another switch.
            if (_revision != revision || foreground() != target) break;
            _pending.Remove(previous.Handle);
            minimize(previous);
            minimized = true;
        }
        return minimized;
    }

    public void Clear()
    {
        _revision++;
        _pending.Clear();
        Target = null;
    }
}
