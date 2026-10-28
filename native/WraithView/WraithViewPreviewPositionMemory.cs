using System.Drawing;

namespace WraithView.Preview;

internal readonly record struct PreviewClientIdentity(nint Handle, uint ProcessId)
{
    public static PreviewClientIdentity From(EveClientWindow client) => new(client.Handle, client.ProcessId);
}

internal sealed class WraithViewPreviewPositionMemory
{
    private readonly Dictionary<PreviewClientIdentity, Rectangle> _positions = new();
    // Clients the user moved or resized at character select. Their layout can't be saved until the
    // client has a character name, so it is carried over (and saved) when they log in.
    private readonly HashSet<PreviewClientIdentity> _adjustedWhileNameless = new();
    private string _profileId = "";
    private int _previewWidth;
    private int _previewHeight;
    private ScreenPixelInfo[] _monitorTopology = Array.Empty<ScreenPixelInfo>();
    private bool _hasContext;

    public bool MatchesContext(
        string profileId,
        int previewWidth,
        int previewHeight,
        IReadOnlyList<ScreenPixelInfo> monitorTopology)
    {
        return _hasContext
            && string.Equals(_profileId, profileId, StringComparison.OrdinalIgnoreCase)
            && _previewWidth == previewWidth
            && _previewHeight == previewHeight
            && _monitorTopology.SequenceEqual(monitorTopology);
    }

    public bool BeginContext(
        string profileId,
        int previewWidth,
        int previewHeight,
        IReadOnlyList<ScreenPixelInfo> monitorTopology)
    {
        var nextTopology = monitorTopology.ToArray();
        var changed = !MatchesContext(profileId, previewWidth, previewHeight, nextTopology);

        if (!changed) return false;

        _positions.Clear();
        _adjustedWhileNameless.Clear();
        _profileId = profileId;
        _previewWidth = previewWidth;
        _previewHeight = previewHeight;
        _monitorTopology = nextTopology;
        _hasContext = true;
        return true;
    }

    public void Remember(PreviewClientIdentity identity, Rectangle position)
    {
        _positions[identity] = position;
    }

    public bool TryGet(PreviewClientIdentity identity, out Rectangle position)
    {
        return _positions.TryGetValue(identity, out position);
    }

    public void PurgeExcept(IReadOnlySet<PreviewClientIdentity> liveIdentities)
    {
        foreach (var identity in _positions.Keys.Where(identity => !liveIdentities.Contains(identity)).ToArray())
        {
            _positions.Remove(identity);
        }

        _adjustedWhileNameless.RemoveWhere(identity => !liveIdentities.Contains(identity));
    }

    /// <summary>
    /// The default size for new previews changed because the user resized one. Keep the remembered
    /// positions: they are the user's own placements, not default-stack slots that need redoing.
    /// </summary>
    public void AdoptPreviewSize(int previewWidth, int previewHeight)
    {
        if (!_hasContext) return;
        _previewWidth = previewWidth;
        _previewHeight = previewHeight;
    }

    public void MarkAdjustedWhileNameless(PreviewClientIdentity identity) => _adjustedWhileNameless.Add(identity);

    /// <summary>True once per character-select adjustment, for the client that has just logged in.</summary>
    public bool TakeAdjustedWhileNameless(PreviewClientIdentity identity)
        => _positions.ContainsKey(identity) && _adjustedWhileNameless.Remove(identity);

    public static Rectangle Resolve(
        Rectangle? savedForCurrentKey,
        Rectangle? rememberedForClient,
        Rectangle? titleFallback,
        Rectangle defaultStack,
        bool preferRemembered = false)
    {
        if (preferRemembered && rememberedForClient != null) return rememberedForClient.Value;
        return savedForCurrentKey
            ?? rememberedForClient
            ?? titleFallback
            ?? defaultStack;
    }
}
