using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using WraithView.Alerts;
using Forms = System.Windows.Forms;

namespace WraithView.Preview;

internal static class WraithViewPreviewDimensions
{
    public const int Minimum = 16;
    public const int Maximum = 32767;
}

internal sealed class WraithViewController : IDisposable
{
    private const int SwitchSettleBeforeMinimizeMs = 10;
    private readonly Dispatcher _dispatcher;
    private readonly Action<object> _postToHud;
    private readonly Action _reassertHudTopmost;
    private readonly Action<bool> _applySettingsAlwaysOnTop;
    private readonly EveWindowTracker _tracker = new();
    private readonly object _trackerGate = new();
    private readonly WraithAlertsService _alerts = new();
    private readonly ConcurrentQueue<WraithAlertEvent> _pendingAlerts = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _switchStateTimer;
    private readonly WraithViewOverlayForm _overlay;
    private readonly WraithViewNativeMethods.WinEventProc _foregroundWinEventProc;
    private bool _settingsPanelOpen;
    private bool _nativeMenuOpen;
    private bool _disposed;
    private IReadOnlyList<EveClientWindow> _clients = Array.Empty<EveClientWindow>();
    private string _lastPostedStateJson = "";
    private string _autoRestoredProfileId = "";
    private readonly HashSet<string> _autoRestoredClientKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly WraithViewActivationState _activationState = new();
    private readonly WraithViewMinimizeState _minimizeState = new();
    private int _alertDispatchScheduled;
    private int _periodicRefreshInProgress;
    private string _lastClientTopologySignature = "";
    private string _lastClientStateSignature = "";
    private readonly Dictionary<string, nint> _cycleGroupCursors = new(StringComparer.OrdinalIgnoreCase);
    private nint _foregroundWinEventHook;
    private bool _hasObservedForeground;
    private bool _lastObservedForegroundWasEve;

    public WraithViewSettings Settings { get; }
    public bool SettingsPanelOpen => _settingsPanelOpen;
    public event Action<WraithAlertEvent>? AlertNotificationRequested;

    public WraithViewController(Dispatcher dispatcher, Action<object> postToHud, Action reassertHudTopmost, Action<bool> applySettingsAlwaysOnTop)
    {
        _dispatcher = dispatcher;
        _postToHud = postToHud;
        _reassertHudTopmost = reassertHudTopmost;
        _applySettingsAlwaysOnTop = applySettingsAlwaysOnTop;
        _foregroundWinEventProc = OnForegroundWinEvent;
        Settings = WraithViewSettings.Load();
        _overlay = new WraithViewOverlayForm();
        _overlay.ActivateRequested += ActivateClient;
        _overlay.MinimizeRequested += MinimizeClient;
        _overlay.PreviewLayoutChanged += SavePreviewLayout;
        _overlay.HotkeyPressed += HandleHotkey;
        _overlay.HotkeyStatusChanged += () => PostState();
        _alerts.AlertTriggered += OnAlertTriggered;
        _alerts.UpdateSettings(Settings.Alerts);

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _timer.Tick += (_, _) => QueuePeriodicRefresh();

        _switchStateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _switchStateTimer.Tick += (_, _) =>
        {
            _switchStateTimer.Stop();
            PostState();
        };
    }

    public void Start()
    {
        _applySettingsAlwaysOnTop(Settings.SettingsWindowAlwaysOnTop);

        if (Settings.Enabled)
        {
            _overlay.SizeToVirtualDesktop();
            Refresh(showOverlayAfterRefresh: true);
            _timer.Start();
        }

        StartForegroundTracking();
        PostState();
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        switch (type)
        {
            case "wraithview:get-state":
                PostState(force: true);
                return true;
            case "wraithview:set-enabled":
                SetEnabled(message?["enabled"]?.GetValue<bool>() == true);
                return true;
            case "wraithview:set-hotkeys-suspended":
                SetHotkeysSuspended(message?["suspended"]?.GetValue<bool>() == true);
                return true;
            case "wraithview:set-settings-open":
                SetSettingsPanelOpen(message?["open"]?.GetValue<bool>() == true);
                return true;
            case "wraithview:set-settings-window-always-on-top":
                SetSettingsWindowAlwaysOnTop(message?["alwaysOnTop"]?.GetValue<bool>() != false);
                return true;
            case "wraithview:set-guide-completed":
                SetGuideCompleted(message?["completed"]?.GetValue<bool>() == true);
                return true;
            case "wraithview:update-profile":
                ApplyProfilePatch(message?["patch"] as JsonObject);
                return true;
            case "wraithalerts:update-settings":
                ApplyAlertsPatch(message?["patch"] as JsonObject);
                return true;
            case "wraithalerts:update-event":
                ApplyAlertEventPatch(message?["eventType"]?.GetValue<string>(), message?["patch"] as JsonObject);
                return true;
            case "wraithalerts:test":
                TestAlert(message?["eventType"]?.GetValue<string>(), message?["characterName"]?.GetValue<string>());
                return true;
            case "wraithalerts:clear-history":
                _alerts.ClearHistory();
                PostState(force: true);
                return true;
            case "wraithview:set-profile":
                SelectProfile(message?["profileId"]?.GetValue<string>());
                return true;
            case "wraithview:add-profile":
                AddProfile(message?["name"]?.GetValue<string>());
                return true;
            case "wraithview:import-eveo-profile":
                ImportEveOPreviewProfile();
                return true;
            case "wraithview:import-evex-profile":
                ImportEveXPreviewProfiles();
                return true;
            case "wraithview:export-settings-backup":
                ExportSettingsBackup();
                return true;
            case "wraithview:restore-settings-backup":
                RestoreSettingsBackup();
                return true;
            case "wraithview:delete-profile":
                DeleteProfile(message?["profileId"]?.GetValue<string>());
                return true;
            case "wraithview:save-preview-layout":
                SaveCurrentPreviewLayouts();
                return true;
            case "wraithview:save-client-layouts":
                SaveClientLayouts();
                return true;
            case "wraithview:restore-client-layouts":
                RestoreClientLayouts();
                return true;
            case "wraithview:close-clients":
                CloseClients();
                return true;
            case "wraithview:hide-all":
                SetEnabled(false);
                return true;
            default:
                return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (Settings.Enabled == enabled)
        {
            PostState();
            return;
        }

        Settings.Enabled = enabled;
        Settings.Save();

        if (enabled)
        {
            _timer.Start();
            _overlay.SizeToVirtualDesktop();
            Refresh(showOverlayAfterRefresh: true);
        }
        else
        {
            _minimizeState.Clear();
            _timer.Stop();
            _overlay.Hide();
            _overlay.ClearThumbnails();
        }

        PostState();
    }

    public void SetHotkeysSuspended(bool suspended)
    {
        if (suspended) _minimizeState.Clear();
        Settings.HotkeysSuspended = suspended;
        Settings.Save();
        _overlay.ConfigureHotkeys(Settings.ActiveProfile(), _clients, Settings.HotkeysSuspended);
        PostState();
    }

    public void SetSettingsPanelOpen(bool open)
    {
        if (_settingsPanelOpen == open)
        {
            ApplyTopmostPolicy(force: true);
            return;
        }

        _settingsPanelOpen = open;
        ApplyTopmostPolicy(force: true);
        _reassertHudTopmost();
    }

    private void SetSettingsWindowAlwaysOnTop(bool alwaysOnTop)
    {
        if (Settings.SettingsWindowAlwaysOnTop == alwaysOnTop)
        {
            _applySettingsAlwaysOnTop(alwaysOnTop);
            PostState();
            return;
        }

        Settings.SettingsWindowAlwaysOnTop = alwaysOnTop;
        Settings.Save();
        _applySettingsAlwaysOnTop(alwaysOnTop);
        PostState(force: true);
    }

    private void SetGuideCompleted(bool completed)
    {
        Settings.GuideCompleted = completed;
        Settings.GuideVersion = completed ? WraithViewSettings.CurrentGuideVersion : "";
        Settings.Save();
        PostState(force: true);
    }

    public void SetNativeMenuOpen(bool open)
    {
        _nativeMenuOpen = open;
        if (!open)
        {
            ApplyTopmostPolicy(force: true);
        }
    }

    public void BringToTop()
    {
        ApplyTopmostPolicy(force: true);
    }

    public void ResizeToVirtualDesktop()
    {
        _overlay.SizeToVirtualDesktop();
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _minimizeState.Clear();
        _timer.Stop();
        _switchStateTimer.Stop();
        StopForegroundTracking();
        _alerts.Dispose();
        _overlay.Dispose();
    }

    private void ShowOverlay()
    {
        _overlay.SizeToVirtualDesktop();
        if (!_overlay.Visible) _overlay.Show();
        ApplyTopmostPolicy(force: true);
    }

    private void ApplyTopmostPolicy(bool force = false)
    {
        if (_disposed) return;
        if (_nativeMenuOpen) return;

        _overlay.AllowTopmost = Settings.Enabled;
        if (_overlay.Visible) _overlay.ApplyTopmostPolicy(force);
    }

    private void Refresh(bool showOverlayAfterRefresh = false)
    {
        if (_disposed || !Settings.Enabled) return;

        try
        {
            var foreground = WraithViewNativeMethods.GetForegroundWindow();
            ApplyClientRefresh(GetTrackedClients(foreground), foreground, showOverlayAfterRefresh, forceFullRefresh: true);
        }
        catch (Exception ex)
        {
            PostError("refresh", ex.Message);
        }
    }

    private async void QueuePeriodicRefresh()
    {
        if (_disposed || !Settings.Enabled) return;
        if (Interlocked.CompareExchange(ref _periodicRefreshInProgress, 1, 0) != 0) return;

        try
        {
            var observedForeground = WraithViewNativeMethods.GetForegroundWindow();
            var clients = await Task.Run(() => GetTrackedClients(observedForeground));
            if (_disposed || !Settings.Enabled) return;
            var foreground = WraithViewNativeMethods.GetForegroundWindow();
            ApplyClientRefresh(clients, foreground, showOverlayAfterRefresh: false, forceFullRefresh: false);
        }
        catch (Exception ex)
        {
            if (!_disposed) PostError("refresh", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _periodicRefreshInProgress, 0);
        }
    }

    private IReadOnlyList<EveClientWindow> GetTrackedClients(nint foreground)
    {
        lock (_trackerGate)
        {
            return _tracker.GetClients(foreground);
        }
    }

    private void ApplyClientRefresh(
        IReadOnlyList<EveClientWindow> discoveredClients,
        nint foreground,
        bool showOverlayAfterRefresh,
        bool forceFullRefresh)
    {
        var profile = Settings.ActiveProfileFast();
        var clients = SortClients(discoveredClients, profile).ToArray();
        _minimizeState.Configure(profile.Id, Settings.Enabled && profile.MinimizeInactiveClients);

        if (clients.Any(client => client.Handle == foreground))
        {
            _activationState.TryObserve(foreground, Environment.TickCount64);
        }

        if (_activationState.ActiveHandle != nint.Zero
            && clients.All(client => client.Handle != _activationState.ActiveHandle)
            && !WraithViewNativeMethods.IsWindow(_activationState.ActiveHandle))
        {
            RemoveCycleCursorHandle(_activationState.ActiveHandle);
            _activationState.Clear();
        }

        var activeHandle = _activationState.ActiveHandle != nint.Zero ? _activationState.ActiveHandle : foreground;
        clients = clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();

        var topologySignature = ClientTopologySignature(clients);
        var stateSignature = ClientStateSignature(clients, foreground);
        var topologyChanged = forceFullRefresh
            || !string.Equals(topologySignature, _lastClientTopologySignature, StringComparison.Ordinal)
            || _overlay.HasPositionContextChanged(profile);
        var stateChanged = !string.Equals(stateSignature, _lastClientStateSignature, StringComparison.Ordinal);

        _clients = clients;
        _lastClientTopologySignature = topologySignature;
        _lastClientStateSignature = stateSignature;
        _alerts.SetActiveCharacters(_clients.Select(client => client.CharacterName));
        ObserveForegroundTransition(foreground, _clients);

        if (topologyChanged)
        {
            AutoRestoreClientLayouts(profile, _clients);
            _overlay.SetClients(_clients, profile, foreground, activeHandle: activeHandle);
        }
        else
        {
            _overlay.SyncClientStates(_clients, foreground, activeHandle);
        }

        _overlay.ConfigureHotkeys(profile, _clients, Settings.HotkeysSuspended);

        if (showOverlayAfterRefresh) ShowOverlay();
        if (topologyChanged || stateChanged) PostState();
    }

    private void StartForegroundTracking()
    {
        if (_foregroundWinEventHook != nint.Zero) return;
        _foregroundWinEventHook = WraithViewNativeMethods.SetWinEventHook(
            WraithViewNativeMethods.EventSystemForeground,
            WraithViewNativeMethods.EventSystemForeground,
            nint.Zero,
            _foregroundWinEventProc,
            0,
            0,
            WraithViewNativeMethods.WinEventOutOfContext);
    }

    private void StopForegroundTracking()
    {
        if (_foregroundWinEventHook == nint.Zero) return;
        WraithViewNativeMethods.UnhookWinEvent(_foregroundWinEventHook);
        _foregroundWinEventHook = nint.Zero;
    }

    private void OnForegroundWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || eventType != WraithViewNativeMethods.EventSystemForeground || hwnd == nint.Zero) return;
        try
        {
            _dispatcher.InvokeAsync(
                () => ObserveForegroundTransition(hwnd, _clients),
                DispatcherPriority.Send);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can reject callbacks while the app is shutting down.
        }
    }

    private void ObserveForegroundTransition(nint foreground, IReadOnlyList<EveClientWindow> clients)
    {
        if (_disposed) return;

        var liveForeground = WraithViewNativeMethods.GetForegroundWindow();
        if (foreground != liveForeground) return;

        var foregroundIsEve = clients.Any(client => client.Handle == foreground);
        var returnedToEve = _hasObservedForeground && !_lastObservedForegroundWasEve && foregroundIsEve;
        _hasObservedForeground = true;
        _lastObservedForegroundWasEve = foregroundIsEve;

        if (!foregroundIsEve) _minimizeState.Clear();

        if (!foregroundIsEve || !Settings.Enabled || !_overlay.Visible) return;

        if (_activationState.TryObserve(foreground, Environment.TickCount64))
        {
            _overlay.MarkActiveClient(foreground);
            RememberCycleCursorForActiveGroups(Settings.ActiveProfileFast(), foreground);
        }
        CompletePendingMinimizes(Settings.ActiveProfileFast());
        if (!returnedToEve) return;

        ApplyTopmostPolicy(force: true);
        _reassertHudTopmost();
    }

    private static string ClientTopologySignature(IReadOnlyList<EveClientWindow> clients)
    {
        return string.Join(";", clients.Select(client => $"{client.Handle:X}|{client.ProcessId}|{client.Title}|{client.CharacterName}"));
    }

    private static string ClientStateSignature(IReadOnlyList<EveClientWindow> clients, nint foreground)
    {
        return $"{foreground:X}:" + string.Join(";", clients.Select(client => $"{client.Handle:X}|{client.IsMinimized}|{client.IsForeground}"));
    }

    private static IEnumerable<EveClientWindow> SortClients(IReadOnlyList<EveClientWindow> clients, WraithViewProfile profile)
    {
        var order = profile.CharacterOrder
            .Select((name, index) => (Name: name.Trim(), Index: index))
            .Where(item => item.Name.Length > 0)
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        return clients
            .Where(client => !profile.HiddenClients.Contains(client.StableKey, StringComparer.OrdinalIgnoreCase)
                && !profile.HiddenClients.Contains(client.CharacterName, StringComparer.OrdinalIgnoreCase)
                && !profile.HiddenClients.Contains(client.Title, StringComparer.OrdinalIgnoreCase))
            .OrderBy(client => order.TryGetValue(client.CharacterName, out var index) ? index : int.MaxValue)
            .ThenBy(client => client.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.Title, StringComparer.OrdinalIgnoreCase);
    }

    private void ActivateClient(EveClientWindow client)
    {
        TryActivateClient(client, Settings.ActiveProfileFast());
    }

    private bool TryActivateClient(EveClientWindow client, WraithViewProfile profile, bool fromHotkey = false)
    {
        try
        {
            var previousClient = ResolvePreviousActiveClient(client.Handle);
            var previousSelection = _clients.FirstOrDefault(item => item.Handle == _activationState.ActiveHandle);
            bool Activate() => fromHotkey
                ? _overlay.ActivateFromHotkey(client.Handle, profile.AlwaysMaximizeClients)
                : ActivateWindow(client, profile);
            if (!Activate()) return false;

            _activationState.Accept(client.Handle, Environment.TickCount64, optimistic: fromHotkey);
            _minimizeState.Configure(profile.Id, Settings.Enabled && profile.MinimizeInactiveClients);
            _minimizeState.Accept(client, fromHotkey,
                // Foreground reconciliation can replace the logical selection
                // before a slow restore completes. Retain that requested client too.
                new[] { previousClient, previousSelection, _minimizeState.Target }
                    .OfType<EveClientWindow>()
                    .Where(previous => ShouldMinimizePreviousClient(profile, previous, client.Handle)));
            RememberCycleCursorForActiveGroups(profile, client.Handle);
            MarkActiveClient(client.Handle);

            if (_minimizeState.HasPending)
            {
                if (SwitchSettleBeforeMinimizeMs > 0)
                {
                    Thread.Sleep(SwitchSettleBeforeMinimizeMs);
                }

                // If foreground is still catching up, its event completes this
                // work later. No polling wait and no native minimize is queued.
                CompletePendingMinimizes(profile);
            }

            return true;
        }
        catch (Exception ex)
        {
            PostError("activate", ex.Message);
            return false;
        }
    }

    private static void MinimizeClient(EveClientWindow client)
    {
        WraithViewNativeMethods.ShowWindow(client.Handle, WraithViewNativeMethods.SwMinimize);
    }

    private void SavePreviewLayout(string key, WraithViewRect rect)
    {
        var profile = Settings.ActiveProfile();
        profile.PreviewLayouts[key] = rect;
        Settings.Save();
        PostState();
    }

    private void SaveCurrentPreviewLayouts()
    {
        var layouts = _overlay.CurrentPreviewLayouts();
        var profile = Settings.ActiveProfile();
        foreach (var layout in layouts)
        {
            profile.PreviewLayouts[layout.Key] = layout.Value;
        }

        Settings.Save();
        PostState();
    }

    private void SaveClientLayouts()
    {
        var profile = Settings.ActiveProfile();
        foreach (var client in GetTrackedClients(WraithViewNativeMethods.GetForegroundWindow()))
        {
            if (!WraithViewNativeMethods.GetWindowPlacement(client.Handle, out var placement)) continue;
            var normal = placement.NormalPosition;
            profile.ClientPlacements[client.StableKey] = new WraithViewClientPlacement
            {
                Title = client.Title,
                CharacterName = client.CharacterName,
                X = normal.Left,
                Y = normal.Top,
                Width = Math.Max(1, normal.Right - normal.Left),
                Height = Math.Max(1, normal.Bottom - normal.Top),
                Maximized = placement.ShowCmd == WraithViewNativeMethods.SwMaximize,
            };
        }

        Settings.Save();
        PostState();
    }

    private void RestoreClientLayouts()
    {
        var profile = Settings.ActiveProfile();
        foreach (var client in GetTrackedClients(WraithViewNativeMethods.GetForegroundWindow()))
        {
            RestoreClientLayout(profile, client);
        }

        ResetAutoRestoreTracking(profile.Id);
        Refresh();
    }

    private void AutoRestoreClientLayouts(WraithViewProfile profile, IReadOnlyList<EveClientWindow> clients)
    {
        if (!profile.AutoRestoreClientLayouts)
        {
            ResetAutoRestoreTracking(profile.Id);
            return;
        }

        if (!string.Equals(_autoRestoredProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            ResetAutoRestoreTracking(profile.Id);
        }

        foreach (var client in clients)
        {
            var restoreKey = $"{client.StableKey}:{client.Handle}";
            if (_autoRestoredClientKeys.Contains(restoreKey)) continue;
            if (!RestoreClientLayout(profile, client)) continue;
            _autoRestoredClientKeys.Add(restoreKey);
        }

        foreach (var key in _autoRestoredClientKeys.Where(key => clients.All(client => key != $"{client.StableKey}:{client.Handle}")).ToArray())
        {
            _autoRestoredClientKeys.Remove(key);
        }
    }

    private void ResetAutoRestoreTracking(string profileId)
    {
        _autoRestoredProfileId = profileId;
        _autoRestoredClientKeys.Clear();
    }

    private static bool RestoreClientLayout(WraithViewProfile profile, EveClientWindow client)
    {
        if (!profile.ClientPlacements.TryGetValue(client.StableKey, out var placement)) return false;

        WraithViewNativeMethods.ShowWindow(client.Handle, WraithViewNativeMethods.SwRestore);
        WraithViewNativeMethods.MoveWindow(
            client.Handle,
            placement.X,
            placement.Y,
            Math.Max(1, placement.Width),
            Math.Max(1, placement.Height),
            true
        );

        if (placement.Maximized)
        {
            WraithViewNativeMethods.ShowWindow(client.Handle, WraithViewNativeMethods.SwMaximize);
        }

        return true;
    }

    private void CloseClients()
    {
        foreach (var client in GetTrackedClients(WraithViewNativeMethods.GetForegroundWindow()))
        {
            WraithViewNativeMethods.PostMessage(client.Handle, WraithViewNativeMethods.WmClose, nint.Zero, nint.Zero);
        }

        Refresh();
    }

    private EveClientWindow? ResolvePreviousActiveClient(nint nextHandle)
    {
        var foreground = WraithViewNativeMethods.GetForegroundWindow();
        var foregroundClient = _clients.FirstOrDefault(client => client.Handle == foreground && client.Handle != nextHandle);
        if (foregroundClient != null) return foregroundClient;

        return _clients.FirstOrDefault(client => client.Handle == _activationState.ActiveHandle && client.Handle != nextHandle);
    }

    private static bool ShouldMinimizePreviousClient(WraithViewProfile profile, EveClientWindow? previousClient, nint activeHandle)
    {
        if (!profile.MinimizeInactiveClients || previousClient == null) return false;
        if (previousClient.Handle == activeHandle) return false;

        return !ShouldSkipInactiveMinimize(profile, previousClient);
    }

    /// <summary>
    /// Minimise a client without waiting for it. This used to be SendMessage, which blocks until EVE has
    /// finished minimising (it does real graphics work), so every switch with "minimise inactive clients" on
    /// stalled until the client you were leaving had gone down.
    /// </summary>
    private static void PostMinimizeClient(nint handle)
    {
        WraithViewNativeMethods.PostMessage(handle, WraithViewNativeMethods.WmSysCommand, WraithViewNativeMethods.ScMinimize, nint.Zero);
    }

    private const int MinimizeFocusCheckMs = 150;

    /// <summary>
    /// After a posted minimise, check once that the client we switched to still has focus, and bring it back
    /// only if something took it (and no newer switch has happened since).
    /// </summary>
    private void ScheduleFocusCheckAfterMinimize(EveClientWindow target, WraithViewProfile profile, bool fromHotkey, long revision)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(MinimizeFocusCheckMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed || _minimizeState.Revision != revision || !IsCurrentClient(target)) return;
            if (WraithViewNativeMethods.GetForegroundWindow() == target.Handle) return;
            if (fromHotkey) _overlay.ActivateFromHotkey(target.Handle, profile.AlwaysMaximizeClients);
            else ActivateWindow(target, profile);
        };
        timer.Start();
    }

    private void CompletePendingMinimizes(WraithViewProfile profile)
    {
        _minimizeState.Configure(profile.Id, Settings.Enabled && profile.MinimizeInactiveClients);
        var target = _minimizeState.Target;
        if (target == null || !_minimizeState.HasPending) return;
        if (!IsCurrentClient(target))
        {
            _minimizeState.Clear();
            return;
        }
        var fromHotkey = _minimizeState.FromHotkey;
        var revision = _minimizeState.Revision;
        try
        {
            var minimized = _minimizeState.Drain(
                WraithViewNativeMethods.GetForegroundWindow,
                previous => IsCurrentClient(previous)
                    && ShouldMinimizePreviousClient(profile, previous, target.Handle)
                    && !WraithViewNativeMethods.IsIconic(previous.Handle),
                previous => PostMinimizeClient(previous.Handle));
            // Minimising a background client shouldn't move focus, but if it does, put it back, once,
            // for the latest target only. A newer accepted switch cancels the check.
            if (minimized && _minimizeState.Revision == revision)
            {
                ScheduleFocusCheckAfterMinimize(target, profile, fromHotkey, revision);
            }
        }
        catch (Exception ex)
        {
            PostError("minimize", ex.Message);
        }
    }

    private bool IsCurrentClient(EveClientWindow client)
    {
        return _clients.Any(current => current.Handle == client.Handle
                && current.ProcessId == client.ProcessId && current.StableKey == client.StableKey)
            && WraithViewNativeMethods.GetWindowThreadProcessId(client.Handle, out var pid) != 0
            && pid == client.ProcessId;
    }

    private static bool ShouldSkipInactiveMinimize(WraithViewProfile profile, EveClientWindow client)
    {
        var neverMinimize = profile.NeverMinimizeClients;
        return neverMinimize.Contains(client.StableKey, StringComparer.OrdinalIgnoreCase)
            || neverMinimize.Contains(client.CharacterName, StringComparer.OrdinalIgnoreCase)
            || neverMinimize.Contains(client.Title, StringComparer.OrdinalIgnoreCase);
    }

    private void HandleHotkey(WraithViewHotkeyCommand command)
    {
        var profile = Settings.ActiveProfileFast();
        if (Settings.HotkeysSuspended) return;
        var foreground = WraithViewNativeMethods.GetForegroundWindow();
        if (profile.HotkeysRequireEveForeground
            && _clients.All(client => client.Handle != foreground))
        {
            return;
        }

        if (command.Kind == WraithViewHotkeyKind.Direct)
        {
            var characterNames = command.CharacterNames.Count > 0
                ? command.CharacterNames
                : new[] { command.CharacterName };
            var target = _clients.FirstOrDefault(client => characterNames.Any(characterName =>
                string.Equals(client.CharacterName, characterName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.StableKey, characterName, StringComparison.OrdinalIgnoreCase)));
            if (target != null) TryActivateClient(target, profile, fromHotkey: true);
            return;
        }

        if (command.Kind == WraithViewHotkeyKind.Cycle)
        {
            CycleClient(profile, command.GroupId, command.Direction);
        }
    }

    private void CycleClient(WraithViewProfile profile, string groupId, int direction)
    {
        var group = profile.CycleGroups.FirstOrDefault(item => string.Equals(item.Id, groupId, StringComparison.OrdinalIgnoreCase));
        var cycleNames = group?.Characters.Count > 0 ? group.Characters : profile.CharacterOrder;
        if (cycleNames.Count == 0) return;

        var candidates = ResolveCycleCandidates(cycleNames);
        if (candidates.Count == 0) return;

        var cursorKey = CycleCursorKey(profile.Id, group?.Id ?? groupId);
        _cycleGroupCursors.TryGetValue(cursorKey, out var cursorHandle);
        var liveForeground = WraithViewNativeMethods.GetForegroundWindow();
        var candidateHandles = candidates.Select(client => client.Handle).ToArray();
        var nextIndex = WraithViewCycleState.NextIndex(
            candidateHandles,
            direction,
            _activationState.ActiveHandle,
            liveForeground,
            cursorHandle,
            profile.RememberCycleGroupPositions);
        if (nextIndex < 0) return;

        var target = candidates[nextIndex];
        var previousCursor = cursorHandle;
        if (profile.RememberCycleGroupPositions)
        {
            _cycleGroupCursors[cursorKey] = target.Handle;
        }
        if (TryActivateClient(target, profile, fromHotkey: true)) return;

        if (!profile.RememberCycleGroupPositions) return;

        var restoredCursor = WraithViewCycleState.CursorAfterFailedActivation(
            candidateHandles,
            previousCursor);
        if (restoredCursor != nint.Zero)
        {
            _cycleGroupCursors[cursorKey] = restoredCursor;
        }
        else
        {
            _cycleGroupCursors.Remove(cursorKey);
        }
    }

    private void RememberCycleCursorForActiveGroups(WraithViewProfile profile, nint handle)
    {
        if (!profile.RememberCycleGroupPositions || handle == nint.Zero) return;

        foreach (var group in profile.ActiveCycleGroups())
        {
            var cycleNames = group.Characters.Count > 0 ? group.Characters : profile.CharacterOrder;
            if (cycleNames.Count == 0) continue;
            if (ResolveCycleCandidates(cycleNames).All(client => client.Handle != handle)) continue;
            _cycleGroupCursors[CycleCursorKey(profile.Id, group.Id)] = handle;
        }
    }

    private void RemoveCycleCursorHandle(nint handle)
    {
        foreach (var key in _cycleGroupCursors
                     .Where(item => item.Value == handle)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _cycleGroupCursors.Remove(key);
        }
    }

    internal static string CycleCursorKey(string profileId, string groupId)
    {
        return $"{profileId}\u001f{groupId}";
    }

    internal static bool ApplyRememberCycleGroupPositionsSetting(
        WraithViewProfile profile,
        bool enabled,
        IDictionary<string, nint> cursors)
    {
        if (profile.RememberCycleGroupPositions == enabled) return false;
        profile.RememberCycleGroupPositions = enabled;
        cursors.Clear();
        return true;
    }

    private List<EveClientWindow> ResolveCycleCandidates(IReadOnlyList<string> cycleNames)
    {
        var candidates = new List<EveClientWindow>();
        var usedHandles = new HashSet<nint>();

        foreach (var name in cycleNames)
        {
            var client = _clients.FirstOrDefault(item =>
                string.Equals(item.CharacterName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.StableKey, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Title, name, StringComparison.OrdinalIgnoreCase));
            if (client == null || !usedHandles.Add(client.Handle)) continue;
            candidates.Add(client);
        }

        return candidates;
    }

    private void ApplyProfilePatch(JsonObject? patch)
    {
        if (patch == null) return;
        var profile = Settings.ActiveProfile();
        var previewSizeChanged = false;
        var cycleCursorMemoryChanged = false;

        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "name":
                    profile.Name = value?.GetValue<string>()?.Trim() ?? profile.Name;
                    break;
                case "previewWidth":
                    var previewWidth = ClampInt(
                        value,
                        WraithViewPreviewDimensions.Minimum,
                        WraithViewPreviewDimensions.Maximum,
                        profile.PreviewWidth);
                    previewSizeChanged |= previewWidth != profile.PreviewWidth;
                    profile.PreviewWidth = previewWidth;
                    break;
                case "previewHeight":
                    var previewHeight = ClampInt(
                        value,
                        WraithViewPreviewDimensions.Minimum,
                        WraithViewPreviewDimensions.Maximum,
                        profile.PreviewHeight);
                    previewSizeChanged |= previewHeight != profile.PreviewHeight;
                    profile.PreviewHeight = previewHeight;
                    break;
                case "opacity":
                    profile.Opacity = ClampDouble(value, 0.2, 1, profile.Opacity);
                    break;
                case "showLabels":
                    profile.ShowLabels = value?.GetValue<bool>() == true;
                    break;
                case "showActiveHighlight":
                    profile.ShowActiveHighlight = value?.GetValue<bool>() == true;
                    break;
                case "showInactiveBorders":
                    profile.ShowInactiveBorders = value?.GetValue<bool>() == true;
                    break;
                case "edgeOutline":
                    profile.EdgeOutline = value?.GetValue<bool>() == true;
                    break;
                case "lockPreviews":
                    profile.LockPreviews = value?.GetValue<bool>() == true;
                    break;
                case "borderThickness":
                    profile.BorderThickness = ClampInt(value, 1, 16, profile.BorderThickness);
                    break;
                case "snapEnabled":
                    profile.SnapEnabled = value?.GetValue<bool>() == true;
                    break;
                case "snapDistance":
                    profile.SnapDistance = ClampInt(value, 0, 80, profile.SnapDistance);
                    break;
                case "hideActivePreview":
                    profile.HideActivePreview = value?.GetValue<bool>() == true;
                    break;
                case "hideOnLostFocus":
                    profile.HideOnLostFocus = value?.GetValue<bool>() == true;
                    break;
                case "minimizeInactiveClients":
                    profile.MinimizeInactiveClients = value?.GetValue<bool>() == true;
                    break;
                case "alwaysMaximizeClients":
                    profile.AlwaysMaximizeClients = value?.GetValue<bool>() == true;
                    break;
                case "autoRestoreClientLayouts":
                    profile.AutoRestoreClientLayouts = value?.GetValue<bool>() == true;
                    ResetAutoRestoreTracking(profile.Id);
                    break;
                case "hotkeysRequireEveForeground":
                    profile.HotkeysRequireEveForeground = value?.GetValue<bool>() == true;
                    break;
                case "rememberCycleGroupPositions":
                    cycleCursorMemoryChanged |= ApplyRememberCycleGroupPositionsSetting(
                        profile,
                        value?.GetValue<bool>() != false,
                        _cycleGroupCursors);
                    break;
                case "activeBorderColor":
                    profile.ActiveBorderColor = CleanColor(value, profile.ActiveBorderColor);
                    break;
                case "inactiveBorderColor":
                    profile.InactiveBorderColor = CleanColor(value, profile.InactiveBorderColor);
                    break;
                case "labelTextColor":
                    profile.LabelTextColor = CleanColor(value, profile.LabelTextColor);
                    break;
                case "labelBackgroundColor":
                    profile.LabelBackgroundColor = CleanColor(value, profile.LabelBackgroundColor);
                    break;
                case "labelBackgroundTransparent":
                    profile.LabelBackgroundTransparent = value?.GetValue<bool>() == true;
                    break;
                case "labelPosition":
                    profile.LabelPosition = value?.GetValue<string>()?.Trim() ?? profile.LabelPosition;
                    break;
                case "labelFontSize":
                    profile.LabelFontSize = ClampInt(value, 8, 32, profile.LabelFontSize);
                    break;
                case "previewLabels":
                    profile.PreviewLabels = ParseStringMap(value as JsonObject);
                    break;
                case "characterOrderText":
                    profile.CharacterOrder = SplitLines(value?.GetValue<string>());
                    break;
                case "hiddenClientsText":
                    profile.HiddenClients = SplitLines(value?.GetValue<string>());
                    break;
                case "neverMinimizeClientsText":
                    profile.NeverMinimizeClients = SplitLines(value?.GetValue<string>());
                    break;
                case "directHotkeysText":
                    profile.DirectHotkeys = ParseDirectHotkeys(value?.GetValue<string>());
                    break;
                case "cycleGroups":
                    profile.CycleGroups = ParseCycleGroups(value as JsonArray);
                    break;
                case "cycleGroupsText":
                    if (!patch.ContainsKey("cycleGroups"))
                    {
                        profile.CycleGroups = ParseCycleGroups(value?.GetValue<string>());
                    }
                    break;
                case "selectedCycleGroupId":
                    profile.SelectedCycleGroupId = value?.GetValue<string>()?.Trim() ?? "";
                    break;
            }
        }

        profile.Normalize();
        if (previewSizeChanged)
        {
            ApplyProfilePreviewSizeToSavedLayouts(profile);
        }
        Settings.Save();
        Refresh();
        if (cycleCursorMemoryChanged)
        {
            _cycleGroupCursors.Clear();
        }
    }

    private void ApplyAlertsPatch(JsonObject? patch)
    {
        if (patch == null) return;
        var alerts = Settings.Alerts;
        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "enabled":
                    alerts.Enabled = value?.GetValue<bool>() == true;
                    break;
                case "pveMode":
                    alerts.PveMode = value?.GetValue<bool>() == true;
                    break;
                case "masterVolume":
                    alerts.MasterVolume = ClampDouble(value, 0, 1, alerts.MasterVolume);
                    break;
            }
        }

        alerts.Normalize();
        Settings.Save();
        _alerts.UpdateSettings(Settings.Alerts);
        PostState(force: true);
    }

    private void ApplyAlertEventPatch(string? eventType, JsonObject? patch)
    {
        if (string.IsNullOrWhiteSpace(eventType) || patch == null) return;
        var alerts = Settings.Alerts;
        alerts.Normalize();
        if (!alerts.Events.TryGetValue(eventType.Trim(), out var config)) return;

        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "enabled":
                    config.Enabled = value?.GetValue<bool>() == true;
                    break;
                case "severity":
                    config.Severity = WraithAlertSeverity.Normalize(value?.GetValue<string>(), config.Severity);
                    break;
                case "cooldownSeconds":
                    config.CooldownSeconds = ClampInt(value, 0, 120, config.CooldownSeconds);
                    break;
                case "flashEnabled":
                    config.FlashEnabled = value?.GetValue<bool>() == true;
                    break;
                case "flashColor":
                    config.FlashColor = CleanColor(value, config.FlashColor);
                    break;
                case "flashThickness":
                    config.FlashThickness = ClampInt(value, 1, 24, config.FlashThickness);
                    break;
                case "flashDurationMs":
                    config.FlashDurationMs = ClampInt(value, 250, 15000, config.FlashDurationMs);
                    break;
                case "flashPulseCount":
                    config.FlashPulseCount = ClampInt(value, 1, 16, config.FlashPulseCount);
                    break;
                case "sound":
                    config.Sound = (value?.GetValue<string>() ?? "none").Trim().ToLowerInvariant();
                    break;
                case "trayNotification":
                    config.TrayNotification = value?.GetValue<bool>() == true;
                    break;
            }
        }

        alerts.Normalize();
        Settings.Save();
        _alerts.UpdateSettings(Settings.Alerts);
        PostState(force: true);
    }

    private void TestAlert(string? eventType, string? characterName)
    {
        var target = characterName;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = _clients.FirstOrDefault()?.CharacterName;
        }

        _alerts.TestAlert(eventType, target);
        PostState(force: true);
    }

    private void OnAlertTriggered(object? sender, WraithAlertEvent alert)
    {
        if (_disposed) return;
        _pendingAlerts.Enqueue(alert);
        SchedulePendingAlertDispatch();
    }

    private void SchedulePendingAlertDispatch()
    {
        if (Interlocked.CompareExchange(ref _alertDispatchScheduled, 1, 0) != 0) return;
        _dispatcher.InvokeAsync(ProcessPendingAlerts, DispatcherPriority.Input);
    }

    private void ProcessPendingAlerts()
    {
        try
        {
            if (_disposed) return;

            while (_pendingAlerts.TryDequeue(out var alert))
            {
                var config = Settings.Alerts.Events.TryGetValue(alert.Type, out var configured)
                    ? configured
                    : Settings.Alerts.Event(alert.Type);
                if (config.FlashEnabled)
                {
                    _overlay.ShowAlert(alert.CharacterName, new WraithViewPreviewAlert(
                        config.SeverityRank,
                        config.FlashColor,
                        config.FlashThickness,
                        config.FlashDurationMs,
                        config.FlashPulseCount
                    ));
                }

                if (config.TrayNotification)
                {
                    AlertNotificationRequested?.Invoke(alert);
                }
            }

            ScheduleSwitchStatePost();
        }
        finally
        {
            Interlocked.Exchange(ref _alertDispatchScheduled, 0);
            if (!_pendingAlerts.IsEmpty && !_disposed) SchedulePendingAlertDispatch();
        }
    }

    private static void ApplyProfilePreviewSizeToSavedLayouts(WraithViewProfile profile)
    {
        foreach (var layout in profile.PreviewLayouts.Values)
        {
            if (!layout.IsUsable) continue;
            layout.Width = profile.PreviewWidth;
            layout.Height = profile.PreviewHeight;
        }
    }

    private void SelectProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        if (Settings.Profiles.All(profile => !string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))) return;
        Settings.SelectedProfileId = profileId;
        Settings.Save();
        Refresh();
    }

    private void AddProfile(string? name)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim();
        var profile = WraithViewProfile.CreateDefault(cleanName);
        Settings.Profiles.Add(profile);
        Settings.SelectedProfileId = profile.Id;
        Settings.Save();
        Refresh();
    }

    private void ExportSettingsBackup()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export WraithView settings backup",
                Filter = "WraithView backup JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"wraithview-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, Settings.ToJson());
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("export-settings-backup", ex.Message);
        }
    }

    private void RestoreSettingsBackup()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Restore WraithView settings backup",
                Filter = "WraithView backup JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true) return;

            var confirm = Forms.MessageBox.Show(
                "Restoring this backup will overwrite all current WraithView data, including every profile, preview layout, client layout, color, hotkey, and enable/suspend setting.\n\nContinue?",
                "Restore WraithView backup",
                Forms.MessageBoxButtons.YesNo,
                Forms.MessageBoxIcon.Warning,
                Forms.MessageBoxDefaultButton.Button2
            );
            if (confirm != Forms.DialogResult.Yes) return;

            var imported = WraithViewSettings.FromJson(File.ReadAllText(dialog.FileName));
            ReplaceSettings(imported);
        }
        catch (Exception ex)
        {
            PostError("restore-settings-backup", ex.Message);
        }
    }

    private void ReplaceSettings(WraithViewSettings imported)
    {
        _timer.Stop();
        _overlay.ConfigureHotkeys(Settings.ActiveProfile(), _clients, suspended: true);

        Settings.Enabled = imported.Enabled;
        Settings.HotkeysSuspended = imported.HotkeysSuspended;
        Settings.SettingsWindowAlwaysOnTop = imported.SettingsWindowAlwaysOnTop;
        Settings.GuideCompleted = imported.GuideCompleted;
        Settings.GuideVersion = imported.GuideVersion;
        Settings.SelectedProfileId = imported.SelectedProfileId;
        Settings.Profiles = imported.Profiles;
        Settings.Alerts = imported.Alerts;
        Settings.Save();
        _alerts.UpdateSettings(Settings.Alerts);

        if (Settings.Enabled)
        {
            _overlay.SizeToVirtualDesktop();
            Refresh(showOverlayAfterRefresh: true);
            _timer.Start();
        }
        else
        {
            _clients = Array.Empty<EveClientWindow>();
            _overlay.Hide();
            _overlay.ClearThumbnails();
        }

        PostState(force: true);
    }

    private void ImportEveOPreviewProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import EVE-O Preview settings",
                Filter = "EVE-O Preview JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true) return;

            var root = JsonNode.Parse(File.ReadAllText(dialog.FileName)) as JsonObject;
            if (root == null)
            {
                PostError("import-eveo-profile", "The selected file is not a valid EVE-O Preview JSON file.");
                return;
            }

            var profile = BuildEveOProfile(root);
            Settings.Profiles.Add(profile);
            Settings.SelectedProfileId = profile.Id;
            Settings.Save();
            Refresh();
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("import-eveo-profile", ex.Message);
        }
    }

    private void ImportEveXPreviewProfiles()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import EVE-X Preview settings",
                Filter = "EVE-X Preview JSON (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true) return;

            var root = JsonNode.Parse(File.ReadAllText(dialog.FileName)) as JsonObject;
            if (root?["_Profiles"] is not JsonObject profilesRoot || profilesRoot.Count == 0)
            {
                PostError("import-evex-profile", "The selected file is not a valid EVE-X Preview JSON file.");
                return;
            }

            var globalSettings = root["global_Settings"] as JsonObject;
            var lastUsedProfile = EveXString(globalSettings?["LastUsedProfile"]);
            var importedProfiles = new List<(string SourceName, WraithViewProfile Profile)>();

            foreach (var (profileName, node) in profilesRoot)
            {
                if (node is not JsonObject sourceProfile) continue;
                importedProfiles.Add((profileName, BuildEveXProfile(profileName, sourceProfile, globalSettings)));
            }

            if (importedProfiles.Count == 0)
            {
                PostError("import-evex-profile", "No EVE-X profiles were found in the selected file.");
                return;
            }

            Settings.Profiles.AddRange(importedProfiles.Select(item => item.Profile));
            var selected = importedProfiles.FirstOrDefault(item => string.Equals(item.SourceName, lastUsedProfile, StringComparison.OrdinalIgnoreCase)).Profile
                ?? importedProfiles[^1].Profile;
            Settings.SelectedProfileId = selected.Id;
            Settings.Save();
            Refresh();
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("import-evex-profile", ex.Message);
        }
    }

    private WraithViewProfile BuildEveOProfile(JsonObject root)
    {
        var profile = WraithViewProfile.CreateDefault(UniqueProfileName("Imported from EVE-O"));
        profile.HotkeysRequireEveForeground = false;

        if (TryParseSize(JsonString(root, "ThumbnailSize"), out var previewSize))
        {
            profile.PreviewWidth = Math.Clamp(
                previewSize.Width,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
            profile.PreviewHeight = Math.Clamp(
                previewSize.Height,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
        }

        profile.Opacity = Math.Max(0.2, Math.Min(1, JsonDouble(root, "ThumbnailsOpacity", profile.Opacity)));
        profile.SnapEnabled = JsonBool(root, "EnableThumbnailSnap", profile.SnapEnabled);
        profile.HideActivePreview = JsonBool(root, "HideActiveClientThumbnail", profile.HideActivePreview);
        profile.HideOnLostFocus = JsonBool(root, "HideThumbnailsOnLostFocus", profile.HideOnLostFocus);
        profile.MinimizeInactiveClients = JsonBool(root, "MinimizeInactiveClients", profile.MinimizeInactiveClients);
        profile.ShowLabels = JsonBool(root, "ShowThumbnailOverlays", profile.ShowLabels);
        profile.ShowInactiveBorders = JsonBool(root, "ShowThumbnailFrames", profile.ShowInactiveBorders);
        profile.ShowActiveHighlight = JsonBool(root, "EnableActiveClientHighlight", profile.ShowActiveHighlight);
        profile.BorderThickness = Math.Max(1, Math.Min(16, JsonInt(root, "ActiveClientHighlightThickness", profile.BorderThickness)));
        profile.ActiveBorderColor = ColorNameToHex(JsonString(root, "ActiveClientHighlightColor"), profile.ActiveBorderColor);

        ImportPreviewLayouts(root["FlatLayout"] as JsonObject, profile);
        ImportHiddenPreviews(root["DisableThumbnail"] as JsonObject, profile);
        profile.DirectHotkeys = ImportDirectHotkeys(root["ClientHotkey"] as JsonObject);
        profile.CycleGroups = ImportCycleGroups(root);

        if (profile.CycleGroups.Count > 0)
        {
            profile.SelectedCycleGroupId = profile.CycleGroups[0].Id;
            profile.CharacterOrder = profile.CycleGroups[0].Characters.ToList();
        }

        profile.Normalize();
        return profile;
    }

    private WraithViewProfile BuildEveXProfile(string sourceName, JsonObject root, JsonObject? globalSettings)
    {
        var cleanSourceName = string.IsNullOrWhiteSpace(sourceName) ? "Profile" : sourceName.Trim();
        var profile = WraithViewProfile.CreateDefault(UniqueProfileName($"Imported from EVE-X - {cleanSourceName}"));
        var thumbnailSettings = root["Thumbnail Settings"] as JsonObject;
        var clientSettings = root["Client Settings"] as JsonObject;

        profile.HotkeysRequireEveForeground = !EveXBool(globalSettings?["Global_Hotkeys"], fallback: true);
        profile.SnapEnabled = EveXBool(globalSettings?["ThumbnailSnap"], profile.SnapEnabled);
        profile.SnapDistance = EveXInt(globalSettings?["ThumbnailSnap_Distance"], profile.SnapDistance);

        if (TryReadEveXRect(root["Thumbnail Positions"] as JsonObject, out var firstPreviewRect)
            || TryParseEveXRect(globalSettings?["ThumbnailStartLocation"] as JsonObject, out firstPreviewRect))
        {
            profile.PreviewWidth = Math.Clamp(
                firstPreviewRect.Width,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
            profile.PreviewHeight = Math.Clamp(
                firstPreviewRect.Height,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
        }

        if (thumbnailSettings != null)
        {
            profile.Opacity = NormalizeEveXOpacity(thumbnailSettings["ThumbnailOpacity"], profile.Opacity);
            profile.HideOnLostFocus = EveXBool(thumbnailSettings["HideThumbnailsOnLostFocus"], profile.HideOnLostFocus);
            profile.ShowLabels = EveXBool(thumbnailSettings["ShowThumbnailTextOverlay"], profile.ShowLabels);
            profile.ShowActiveHighlight = EveXBool(thumbnailSettings["ShowClientHighlightBorder"], profile.ShowActiveHighlight);
            profile.ShowInactiveBorders = EveXBool(thumbnailSettings["ShowAllColoredBorders"], profile.ShowInactiveBorders);
            profile.BorderThickness = Math.Max(1, Math.Min(16, EveXInt(thumbnailSettings["ClientHighligtBorderthickness"], profile.BorderThickness)));
            profile.ActiveBorderColor = CleanImportedColor(EveXString(thumbnailSettings["ClientHighligtColor"]), profile.ActiveBorderColor);
            profile.InactiveBorderColor = CleanImportedColor(EveXString(thumbnailSettings["InactiveClientBorderColor"]), profile.InactiveBorderColor);
            profile.LabelTextColor = CleanImportedColor(EveXString(thumbnailSettings["ThumbnailTextColor"]), profile.LabelTextColor);
            profile.LabelFontSize = Math.Max(8, Math.Min(32, EveXInt(thumbnailSettings["ThumbnailTextSize"], profile.LabelFontSize)));
        }

        var backgroundColor = CleanImportedColor(EveXString(globalSettings?["ThumbnailBackgroundColor"]), "");
        if (!string.IsNullOrWhiteSpace(backgroundColor))
        {
            profile.LabelBackgroundColor = backgroundColor;
        }

        if (clientSettings != null)
        {
            profile.AlwaysMaximizeClients = EveXBool(clientSettings["AlwaysMaximize"], profile.AlwaysMaximizeClients);
            profile.MinimizeInactiveClients = EveXBool(clientSettings["MinimizeInactiveClients"], profile.MinimizeInactiveClients);
            profile.AutoRestoreClientLayouts = EveXBool(clientSettings["TrackClientPossitions"], profile.AutoRestoreClientLayouts);
            profile.NeverMinimizeClients = EveXStringList(clientSettings["Dont_Minimize_Clients"]);
        }

        ImportEveXPreviewLayouts(root["Thumbnail Positions"] as JsonObject, profile);
        ImportEveXClientPlacements(root["Client Possitions"] as JsonObject, profile);
        ImportEveXHiddenPreviews(root["Thumbnail Visibility"] as JsonObject, profile);
        profile.DirectHotkeys = ImportEveXDirectHotkeys(root["Hotkeys"] as JsonArray);
        profile.CycleGroups = ImportEveXCycleGroups(root["Hotkey Groups"] as JsonObject);

        if (profile.CycleGroups.Count > 0)
        {
            profile.SelectedCycleGroupId = profile.CycleGroups[0].Id;
            profile.CharacterOrder = profile.CycleGroups[0].Characters.ToList();
        }
        else
        {
            profile.CharacterOrder = profile.PreviewLayouts.Keys
                .Where(name => !string.Equals(name, "EVE", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        profile.Normalize();
        return profile;
    }

    private string UniqueProfileName(string baseName)
    {
        if (Settings.Profiles.All(profile => !string.Equals(profile.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (Settings.Profiles.All(profile => !string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{baseName} {DateTime.Now:yyyyMMdd-HHmmss}";
    }

    private static void ImportPreviewLayouts(JsonObject? layouts, WraithViewProfile profile)
    {
        if (layouts == null) return;

        foreach (var (title, node) in layouts)
        {
            var characterName = NormalizeEveWindowTitle(title);
            if (string.IsNullOrWhiteSpace(characterName)) continue;
            if (!TryParsePoint(node?.GetValue<string>(), out var point)) continue;

            profile.PreviewLayouts[characterName] = new WraithViewRect
            {
                X = point.X,
                Y = point.Y,
                Width = profile.PreviewWidth,
                Height = profile.PreviewHeight,
            };
        }
    }

    private static void ImportHiddenPreviews(JsonObject? hidden, WraithViewProfile profile)
    {
        if (hidden == null) return;

        profile.HiddenClients = hidden
            .Where(item => item.Value?.GetValue<bool>() == true)
            .Select(item => NormalizeEveWindowTitle(item.Key))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ImportEveXPreviewLayouts(JsonObject? layouts, WraithViewProfile profile)
    {
        if (layouts == null) return;

        foreach (var (title, node) in layouts)
        {
            var characterName = NormalizeEveWindowTitle(title);
            if (string.IsNullOrWhiteSpace(characterName)) continue;
            if (!TryParseEveXRect(node as JsonObject, out var rect)) continue;

            profile.PreviewLayouts[characterName] = new WraithViewRect
            {
                X = rect.X,
                Y = rect.Y,
                Width = Math.Clamp(
                    rect.Width,
                    WraithViewPreviewDimensions.Minimum,
                    WraithViewPreviewDimensions.Maximum),
                Height = Math.Clamp(
                    rect.Height,
                    WraithViewPreviewDimensions.Minimum,
                    WraithViewPreviewDimensions.Maximum),
            };
        }
    }

    private static void ImportEveXClientPlacements(JsonObject? placements, WraithViewProfile profile)
    {
        if (placements == null) return;

        foreach (var (title, node) in placements)
        {
            var characterName = NormalizeEveWindowTitle(title);
            if (string.IsNullOrWhiteSpace(characterName)) continue;
            if (!TryParseEveXRect(node as JsonObject, out var rect)) continue;

            profile.ClientPlacements[characterName] = new WraithViewClientPlacement
            {
                Title = characterName,
                CharacterName = characterName,
                X = rect.X,
                Y = rect.Y,
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Maximized = EveXBool((node as JsonObject)?["IsMaximized"], fallback: false),
            };
        }
    }

    private static void ImportEveXHiddenPreviews(JsonObject? hidden, WraithViewProfile profile)
    {
        if (hidden == null) return;

        profile.HiddenClients = hidden
            .Where(item => EveXBool(item.Value, fallback: false))
            .Select(item => NormalizeEveWindowTitle(item.Key))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WraithViewHotkeyBinding> ImportDirectHotkeys(JsonObject? hotkeys)
    {
        if (hotkeys == null) return new List<WraithViewHotkeyBinding>();

        return hotkeys
            .Select(item => new WraithViewHotkeyBinding
            {
                CharacterName = NormalizeEveWindowTitle(item.Key),
                Gestures = GestureList(item.Value),
                Enabled = true,
            })
            .Where(binding => binding.CharacterName.Length > 0 && binding.Gestures.Count > 0)
            .ToList();
    }

    private static List<WraithViewCycleGroup> ImportCycleGroups(JsonObject root)
    {
        var groups = new List<WraithViewCycleGroup>();

        for (var index = 1; index <= 8; index++)
        {
            var order = root[$"CycleGroup{index}ClientsOrder"] as JsonObject;
            if (order == null || order.Count == 0) continue;

            var group = new WraithViewCycleGroup
            {
                Name = index == 1 ? "Imported Main" : $"Imported Group {index}",
                ForwardGestures = GestureList(root[$"CycleGroup{index}ForwardHotkeys"]),
                BackwardGestures = GestureList(root[$"CycleGroup{index}BackwardHotkeys"]),
                Characters = OrderedNamesFromObject(order),
                Enabled = true,
            };
            group.Normalize();
            groups.Add(group);
        }

        return groups;
    }

    private static List<string> OrderedNamesFromObject(JsonObject order)
    {
        return order
            .Select((item, ordinal) => new
            {
                Name = NormalizeEveWindowTitle(item.Key),
                Order = item.Value?.GetValue<int>() ?? int.MaxValue,
                Ordinal = ordinal,
            })
            .Where(item => item.Name.Length > 0)
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Ordinal)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GestureList(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            return CleanGestureList(array.Select(gesture => gesture?.GetValue<string>() ?? ""));
        }

        return CleanGestureList(new[] { value?.GetValue<string>() ?? "" });
    }

    private static string NormalizeEveWindowTitle(string title)
    {
        var clean = (title ?? "").Trim();
        return clean.StartsWith("EVE - ", StringComparison.OrdinalIgnoreCase) ? clean[6..].Trim() : clean;
    }

    private static string NormalizeGesture(string gesture)
    {
        return (gesture ?? "").Trim().Replace("Ctrl+", "Control+", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CleanGestureList(IEnumerable<string>? gestures)
    {
        return (gestures ?? Array.Empty<string>())
            .SelectMany(AhkGesture.SplitList)
            .Select(NormalizeGesture)
            .Where(gesture => gesture.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WraithViewHotkeyBinding> ImportEveXDirectHotkeys(JsonArray? hotkeys)
    {
        if (hotkeys == null) return new List<WraithViewHotkeyBinding>();

        var result = new List<WraithViewHotkeyBinding>();
        foreach (var node in hotkeys)
        {
            if (node is not JsonObject item) continue;
            foreach (var (characterName, gestureNode) in item)
            {
                var gestures = CleanGestureList(new[] { NormalizeEveXGesture(EveXString(gestureNode)) });
                if (gestures.Count == 0) continue;

                result.Add(new WraithViewHotkeyBinding
                {
                    CharacterName = NormalizeEveWindowTitle(characterName),
                    Gestures = gestures,
                    Enabled = true,
                });
            }
        }

        return result
            .Where(binding => binding.CharacterName.Length > 0 && binding.Gestures.Count > 0)
            .ToList();
    }

    private static string? JsonString(JsonObject root, string key)
    {
        return root.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() : null;
    }

    private static bool JsonBool(JsonObject root, string key, bool fallback)
    {
        return root.TryGetPropertyValue(key, out var node) && node != null ? node.GetValue<bool>() : fallback;
    }

    private static int JsonInt(JsonObject root, string key, int fallback)
    {
        return root.TryGetPropertyValue(key, out var node) && node != null ? node.GetValue<int>() : fallback;
    }

    private static double JsonDouble(JsonObject root, string key, double fallback)
    {
        return root.TryGetPropertyValue(key, out var node) && node != null ? node.GetValue<double>() : fallback;
    }

    private static bool TryParseSize(string? value, out Size size)
    {
        size = Size.Empty;
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height)) return false;
        size = new Size(width, height);
        return true;
    }

    private static bool TryParsePoint(string? value, out Point point)
    {
        point = Point.Empty;
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return false;
        point = new Point(x, y);
        return true;
    }

    private static string ColorNameToHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.StartsWith("#", StringComparison.Ordinal)) return value;

        var color = Color.FromName(value.Trim());
        if (color.A == 0 && !string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase)) return fallback;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    internal static string NormalizeEveXGesture(string gesture) => gesture.Trim();

    private static string EveXString(JsonNode? node)
    {
        if (node == null) return "";
        try
        {
            return node.GetValue<string>()?.Trim() ?? "";
        }
        catch
        {
            return node.ToJsonString().Trim('"').Trim();
        }
    }

    private static bool EveXBool(JsonNode? node, bool fallback)
    {
        if (node == null) return fallback;
        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            var value = EveXString(node);
            if (int.TryParse(value, out var numeric)) return numeric != 0;
            if (bool.TryParse(value, out var boolean)) return boolean;
            return fallback;
        }
    }

    private static int EveXInt(JsonNode? node, int fallback)
    {
        var value = EveXString(node);
        return int.TryParse(value, out var numeric) ? numeric : fallback;
    }

    private static double EveXDouble(JsonNode? node, double fallback)
    {
        var value = EveXString(node);
        return double.TryParse(value, out var numeric) ? numeric : fallback;
    }

    private static double NormalizeEveXOpacity(JsonNode? node, double fallback)
    {
        var value = EveXDouble(node, fallback);
        if (value > 1) value /= 100.0;
        return Math.Max(0.2, Math.Min(1, value));
    }

    private static List<string> EveXStringList(JsonNode? node)
    {
        if (node is not JsonArray array) return new List<string>();

        return array
            .Select(item => EveXString(item))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryReadEveXRect(JsonObject? source, out WraithViewRect rect)
    {
        rect = new WraithViewRect();
        if (source == null) return false;

        foreach (var (_, node) in source)
        {
            if (TryParseEveXRect(node as JsonObject, out rect)) return true;
        }

        return false;
    }

    private static bool TryParseEveXRect(JsonObject? source, out WraithViewRect rect)
    {
        rect = new WraithViewRect();
        if (source == null) return false;

        var width = EveXInt(source["width"], 0);
        var height = EveXInt(source["height"], 0);
        if (width <= 0 || height <= 0) return false;

        rect = new WraithViewRect
        {
            X = EveXInt(source["x"], 0),
            Y = EveXInt(source["y"], 0),
            Width = width,
            Height = height,
        };
        return true;
    }

    private static string CleanImportedColor(string value, string fallback)
    {
        var clean = (value ?? "").Trim();
        if (clean.Length == 0) return fallback;
        if (clean.StartsWith("#", StringComparison.Ordinal)) return clean;
        if (clean.Length == 6 && clean.All(Uri.IsHexDigit)) return $"#{clean}";
        return ColorNameToHex(clean, fallback);
    }

    private void DeleteProfile(string? profileId)
    {
        if (Settings.Profiles.Count <= 1 || string.IsNullOrWhiteSpace(profileId)) return;
        var removed = Settings.Profiles.RemoveAll(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (removed <= 0) return;
        Settings.SelectedProfileId = Settings.Profiles[0].Id;
        Settings.Save();
        Refresh();
    }

    private void PostState(bool force = false)
    {
        var profile = Settings.ActiveProfile();
        var payload = new
        {
            type = "wraithview:state",
            enabled = Settings.Enabled,
            hotkeysSuspended = Settings.HotkeysSuspended,
            settingsWindowAlwaysOnTop = Settings.SettingsWindowAlwaysOnTop,
            guideCompleted = Settings.GuideCompleted,
            guideVersion = Settings.GuideVersion,
            selectedProfileId = Settings.SelectedProfileId,
            profiles = Settings.Profiles.Select(profileItem => new
            {
                id = profileItem.Id,
                name = profileItem.Name,
            }).ToArray(),
            profile = profile.ToState(),
            clients = _clients.Select(client => new
            {
                handle = client.Handle.ToString("X"),
                title = client.Title,
                characterName = client.CharacterName,
                previewLabel = profile.PreviewLabelFor(client),
                processId = client.ProcessId,
                minimized = client.IsMinimized,
                foreground = client.IsForeground,
                key = client.StableKey,
            }).ToArray(),
            alerts = Settings.Alerts.ToState(),
            alertHistory = _alerts.History.Select(alert => alert.ToState()).ToArray(),
            hotkeyFailures = _overlay.HotkeyFailures,
            dwmAvailable = _overlay.DwmAvailable,
        };

        var json = JsonSerializer.Serialize(payload);
        if (!force && string.Equals(json, _lastPostedStateJson, StringComparison.Ordinal)) return;
        _lastPostedStateJson = json;
        _postToHud(payload);
    }

    private void MarkActiveClient(nint activeHandle)
    {
        if (_clients.Count == 0) return;

        _clients = _clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();
        _lastClientStateSignature = ClientStateSignature(_clients, activeHandle);

        _overlay.MarkActiveClient(activeHandle);
        ScheduleSwitchStatePost();
    }

    private void ScheduleSwitchStatePost()
    {
        _switchStateTimer.Stop();
        _switchStateTimer.Start();
    }

    private static bool ActivateWindow(EveClientWindow client, WraithViewProfile profile)
    {
        // The overlay is WS_EX_NOACTIVATE + ShowWithoutActivation, so WraithView's process never
        // holds the foreground; Windows refuses SetForegroundWindow from a background process
        // unless its thread's input queue is attached to the current foreground thread's. Without
        // this, a held/auto-repeating key (the reported trigger was push-to-talk) keeps feeding
        // input to the other foreground process and clicking a preview does nothing.
        var foregroundWindow = WraithViewNativeMethods.GetForegroundWindow();
        var currentThreadId = WraithViewNativeMethods.GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow != nint.Zero
            ? WraithViewNativeMethods.GetWindowThreadProcessId(foregroundWindow, out _)
            : 0;

        var attachedForeground = false;
        if (foregroundWindow != nint.Zero && foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
        {
            attachedForeground = WraithViewNativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
        }

        // The attach above is not always enough. On a reporting user's machine
        // SPI_GETFOREGROUNDLOCKTIMEOUT read back as 2147483647 ms -- roughly 25 days,
        // evidently set by some other tool -- which makes Windows refuse foreground
        // *stealing* outright for the life of the session. AttachThreadInput gets through
        // that refusal only because it shares input state rather than stealing: with the
        // queues joined, the target is no longer a foreground change from an unrelated
        // process. Sharing with the outgoing foreground thread alone was measured to cover
        // 28 of 46 real activations; the remaining 18 needed the target's own thread joined
        // as well, and none needed anything beyond that.
        //
        // Rejected alternatives, both worse than the convolution: writing
        // SPI_SETFOREGROUNDLOCKTIMEOUT changes a system-wide setting that outlives the
        // process, and the synthetic-ALT trick injects a keypress into a live game client.
        var targetThreadId = WraithViewNativeMethods.GetWindowThreadProcessId(client.Handle, out _);
        var attachedTarget = false;

        try
        {
            WraithViewNativeMethods.SetForegroundWindow(client.Handle);
            WraithViewNativeMethods.SetFocus(client.Handle);

            // Every branch below is gated on the observed foreground rather than on
            // SetForegroundWindow's return value: the traces contain attempts where it
            // returned true with the foreground unchanged. The calls are still made for
            // their side effect, but only GetForegroundWindow is trusted as a verdict.
            if (profile.AlwaysMaximizeClients)
            {
                WraithViewNativeMethods.ShowWindowAsync(client.Handle, WraithViewNativeMethods.SwMaximize);
                WraithViewNativeMethods.SetForegroundWindow(client.Handle);
                if (WraithViewNativeMethods.GetForegroundWindow() == client.Handle) return true;

                RetryWithTargetThreadAttached(
                    client.Handle,
                    currentThreadId,
                    targetThreadId,
                    attachedForeground ? foregroundThreadId : 0,
                    ref attachedTarget);
                return WraithViewNativeMethods.GetForegroundWindow() == client.Handle;
            }

            if (WraithViewNativeMethods.IsIconic(client.Handle))
            {
                WraithViewNativeMethods.ShowWindowAsync(client.Handle, WraithViewNativeMethods.SwRestore);
                WraithViewNativeMethods.SetForegroundWindow(client.Handle);
            }

            if (WraithViewNativeMethods.GetForegroundWindow() == client.Handle) return true;

            RetryWithTargetThreadAttached(
                client.Handle,
                currentThreadId,
                targetThreadId,
                attachedForeground ? foregroundThreadId : 0,
                ref attachedTarget);
            return WraithViewNativeMethods.GetForegroundWindow() == client.Handle;
        }
        finally
        {
            // Reverse order of attaching. Every AttachThreadInput must be matched by exactly
            // one detach; an unbalanced pair tangles input queues for the whole process.
            if (attachedTarget)
            {
                WraithViewNativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (attachedForeground)
            {
                WraithViewNativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    /// <summary>
    /// Second activation attempt, with the target window's thread joined to ours as well.
    /// Only reached when the foreground-thread attach did not put the target in front.
    /// </summary>
    /// <param name="attachedForegroundThreadId">
    /// The foreground thread already joined to ours, or 0 if none. Attaching to it twice
    /// would leave the detach in <c>TryActivateWindow</c> unbalanced.
    /// </param>
    /// <remarks>
    /// Returns nothing: callers judge success from <c>GetForegroundWindow</c>, not from
    /// <c>SetForegroundWindow</c>'s return value, which cannot be trusted (see the caller).
    /// </remarks>
    private static void RetryWithTargetThreadAttached(
        nint handle,
        uint currentThreadId,
        uint targetThreadId,
        uint attachedForegroundThreadId,
        ref bool attachedTarget)
    {
        if (targetThreadId == 0 || targetThreadId == currentThreadId || targetThreadId == attachedForegroundThreadId)
        {
            return;
        }

        attachedTarget = WraithViewNativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
        if (attachedTarget)
        {
            WraithViewNativeMethods.SetForegroundWindow(handle);
        }
    }

    private void PostError(string action, string message)
    {
        _postToHud(new
        {
            type = "wraithview:error",
            action,
            message,
        });
    }

    private static int ClampInt(JsonNode? node, int min, int max, int fallback)
    {
        var value = node?.GetValue<int>() ?? fallback;
        return Math.Max(min, Math.Min(max, value));
    }

    private static double ClampDouble(JsonNode? node, double min, double max, double fallback)
    {
        var value = node?.GetValue<double>() ?? fallback;
        return Math.Max(min, Math.Min(max, value));
    }

    private static string CleanColor(JsonNode? node, string fallback)
    {
        var color = node?.GetValue<string>()?.Trim() ?? "";
        if (color.Length == 7 && color[0] == '#') return color;
        if (color.Length == 9 && color[0] == '#') return color;
        return fallback;
    }

    private static List<string> SplitLines(string? value)
    {
        return (value ?? "")
            .Split(new[] { "\r\n", "\n", "\r", "," }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> ParseStringMap(JsonObject? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value == null) return result;

        foreach (var (key, node) in value)
        {
            var cleanKey = key.Trim();
            var cleanValue = node?.GetValue<string>()?.Trim() ?? "";
            if (cleanKey.Length == 0 || cleanValue.Length == 0) continue;
            result[cleanKey] = cleanValue;
        }

        return result;
    }

    private static List<WraithViewHotkeyBinding> ParseDirectHotkeys(string? value)
    {
        return (value ?? "")
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new WraithViewHotkeyBinding
            {
                CharacterName = parts[0],
                Gestures = CleanGestureList(new[] { parts[1] }),
                Enabled = true,
            })
            .Where(binding => binding.Gestures.Count > 0)
            .ToList();
    }

    private static List<WraithViewCycleGroup> ParseCycleGroups(string? value)
    {
        var groups = new List<WraithViewCycleGroup>();
        foreach (var line in (value ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;
            var name = parts[0].Length > 0 ? parts[0] : "Cycle group";
            groups.Add(new WraithViewCycleGroup
            {
                Id = WraithViewCycleGroup.IdFromName(name),
                Name = name,
                ForwardGestures = CleanGestureList(new[] { parts[1] }),
                BackwardGestures = CleanGestureList(new[] { parts[2] }),
                Characters = parts.Length >= 4 ? SplitLines(parts[3]) : new List<string>(),
                Enabled = true,
            });
        }

        return groups;
    }

    internal static List<WraithViewCycleGroup> ParseCycleGroups(JsonArray? value)
    {
        if (value == null) return new List<WraithViewCycleGroup>();

        var groups = new List<WraithViewCycleGroup>();
        foreach (var node in value)
        {
            if (node is not JsonObject item) continue;
            var name = JsonString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Cycle group";

            var characters = item["characters"] is JsonArray
                ? EveXStringList(item["characters"])
                : SplitLines(JsonString(item, "charactersText"));
            groups.Add(new WraithViewCycleGroup
            {
                Id = JsonString(item, "id")?.Trim() ?? WraithViewCycleGroup.IdFromName(name),
                Name = name,
                ForwardGestures = GestureList(item["forwardGestures"] ?? item["forwardGesture"]),
                BackwardGestures = GestureList(item["backwardGestures"] ?? item["backwardGesture"]),
                Characters = characters,
                Enabled = JsonBool(item, "enabled", true),
            });
        }

        return groups;
    }

    private static List<WraithViewCycleGroup> ImportEveXCycleGroups(JsonObject? sourceGroups)
    {
        if (sourceGroups == null) return new List<WraithViewCycleGroup>();

        var groups = new List<WraithViewCycleGroup>();
        foreach (var (groupName, node) in sourceGroups)
        {
            if (node is not JsonObject sourceGroup) continue;

            var group = new WraithViewCycleGroup
            {
                Name = string.IsNullOrWhiteSpace(groupName) ? "Imported Group" : groupName.Trim(),
                ForwardGestures = CleanGestureList(new[] { NormalizeEveXGesture(EveXString(sourceGroup["ForwardsHotkey"])) }),
                BackwardGestures = CleanGestureList(new[] { NormalizeEveXGesture(EveXString(sourceGroup["BackwardsHotkey"])) }),
                Characters = EveXStringList(sourceGroup["Characters"])
                    .Select(NormalizeEveWindowTitle)
                    .Where(name => name.Length > 0)
                    .ToList(),
                Enabled = true,
            };
            group.Normalize();
            if (group.ForwardGestures.Count > 0 || group.BackwardGestures.Count > 0 || group.Characters.Count > 0)
            {
                groups.Add(group);
            }
        }

        return groups;
    }
}

internal sealed record WraithViewPreviewAlert(
    int SeverityRank,
    string Color,
    int Thickness,
    int DurationMs,
    int PulseCount
);

internal sealed class ActivePreviewAlert
{
    public ActivePreviewAlert(int severityRank, string color, int thickness, int durationMs, int pulseCount, DateTime startedUtc, DateTime expiresUtc)
    {
        SeverityRank = severityRank;
        Color = color;
        Thickness = thickness;
        DurationMs = durationMs;
        PulseCount = pulseCount;
        StartedUtc = startedUtc;
        ExpiresUtc = expiresUtc;
    }

    public int SeverityRank { get; }
    public string Color { get; }
    public int Thickness { get; }
    public int DurationMs { get; }
    public int PulseCount { get; }
    public DateTime StartedUtc { get; }
    public DateTime ExpiresUtc { get; set; }
}

internal sealed class WraithViewOverlayForm : Forms.Form
{
    private const int ResizeHitSize = 16;
    private readonly Dictionary<nint, PreviewState> _previews = new();
    private readonly WraithViewPreviewPositionMemory _positionMemory = new();
    private readonly WraithViewAhkHotkeys _ahkHotkeys = new(new AhkWindowTransport());
    private readonly Forms.Timer _alertTimer = new() { Interval = 80 };
    private readonly WraithViewLabelOverlayForm _labelOverlay = new();
    private string _windowRegionSignature = "";
    private PreviewState? _mousePreview;
    private MouseMode _mouseMode = MouseMode.None;
    private Point _mouseDownPoint;
    private long _mouseDownTicks;
    private Rectangle _mouseStartRect;
    private WraithViewProfile _profile = WraithViewProfile.CreateDefault("Default");
    private nint _foreground;
    private Rectangle _virtualDesktop;
    private IReadOnlyList<EveClientWindow> _clients = Array.Empty<EveClientWindow>();
    private bool? _appliedTopmost;
    private bool _suppressLabelOverlay;

    public event Action<EveClientWindow>? ActivateRequested;
    public event Action<EveClientWindow>? MinimizeRequested;
    public event Action<string, WraithViewRect>? PreviewLayoutChanged;
    public event Action<WraithViewHotkeyCommand>? HotkeyPressed;
    public event Action? HotkeyStatusChanged;

    public bool AllowTopmost { get; set; } = true;
    public bool DwmAvailable { get; private set; } = true;
    public IReadOnlyList<string> HotkeyFailures => _ahkHotkeys.Failures;

    public WraithViewOverlayForm()
    {
        _ahkHotkeys.Pressed += command => HotkeyPressed?.Invoke(command);
        _ahkHotkeys.StatusChanged += () => HotkeyStatusChanged?.Invoke();
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = Color.FromArgb(5, 7, 11);
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _labelOverlay.Owner = this;
        _alertTimer.Tick += (_, _) => TickAlertFlashes();
        SizeToVirtualDesktop();
        UpdateWindowRegion(forceEmpty: true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WraithViewNativeMethods.WsExToolWindow | WraithViewNativeMethods.WsExNoActivate;
            if (AllowTopmost) cp.ExStyle |= WraithViewNativeMethods.WsExTopmost;
            return cp;
        }
    }

    public void SizeToVirtualDesktop()
    {
        _virtualDesktop = ScreenGeometry.VirtualDesktopPixels();
        if (Bounds != _virtualDesktop)
        {
            SetBounds(_virtualDesktop.Left, _virtualDesktop.Top, _virtualDesktop.Width, _virtualDesktop.Height);
        }

        _labelOverlay.SetVirtualDesktop(_virtualDesktop);
    }

    public void ApplyTopmostPolicy(bool force = false)
    {
        if (!force && _appliedTopmost == AllowTopmost)
        {
            return;
        }

        _appliedTopmost = AllowTopmost;
        TopMost = AllowTopmost;
        WraithViewNativeMethods.SetWindowPos(
            Handle,
            AllowTopmost ? WraithViewNativeMethods.HwndTopmost : WraithViewNativeMethods.HwndNotTopmost,
            0,
            0,
            0,
            0,
            WraithViewNativeMethods.SwpNoMove | WraithViewNativeMethods.SwpNoSize | WraithViewNativeMethods.SwpNoActivate
        );
        _labelOverlay.AllowTopmost = AllowTopmost;
        _labelOverlay.ApplyTopmostPolicy(force);
    }

    public void SetClients(IReadOnlyList<EveClientWindow> clients, WraithViewProfile profile, nint foreground, bool suppressLostFocusHide = false, nint activeHandle = default)
    {
        _clients = clients;
        _profile = profile;
        _foreground = foreground;
        SizeToVirtualDesktop();

        var highlightHandle = activeHandle != nint.Zero ? activeHandle : foreground;
        DwmAvailable = WraithViewNativeMethods.DwmIsCompositionEnabled(out var compositionEnabled) == 0 && compositionEnabled;
        var liveIdentities = clients
            .Select(PreviewClientIdentity.From)
            .ToHashSet();
        var memoryInvalidated = _positionMemory.BeginContext(
            profile.Id,
            profile.PreviewWidth,
            profile.PreviewHeight,
            ScreenGeometry.MonitorTopologyPixels());
        if (!memoryInvalidated)
        {
            foreach (var state in _previews.Values.Where(state => liveIdentities.Contains(state.Identity)))
            {
                _positionMemory.Remember(state.Identity, state.FrameRect);
            }
        }

        _positionMemory.PurgeExcept(liveIdentities);

        var visibleIdentities = clients
            .Where(client => !(profile.HideActivePreview && client.Handle == highlightHandle))
            .Select(PreviewClientIdentity.From)
            .ToHashSet();

        foreach (var handle in _previews.Keys.Where(handle => !visibleIdentities.Contains(_previews[handle].Identity)).ToArray())
        {
            _previews[handle].Thumbnail.Dispose();
            _previews.Remove(handle);
        }

        var visibleIndex = 0;
        foreach (var client in clients)
        {
            if (profile.HideActivePreview && client.Handle == highlightHandle) continue;

            var identity = PreviewClientIdentity.From(client);
            if (!_previews.TryGetValue(client.Handle, out var state) || state.Identity != identity)
            {
                if (state != null) state.Thumbnail.Dispose();
                state = new PreviewState(client, CreateThumbnail(client.Handle));
                _previews[client.Handle] = state;
            }

            state.Client = client;
            state.FrameRect = ResolveFrameRect(client, visibleIndex++);
            _positionMemory.Remember(identity, state.FrameRect);
            state.Active = client.Handle == highlightHandle;
            state.Visible = DwmAvailable;
            UpdateThumbnail(state);
        }

        var shouldHideForLostFocus = profile.HideOnLostFocus && !suppressLostFocusHide && clients.All(client => client.Handle != foreground);
        Opacity = shouldHideForLostFocus ? 0 : Math.Max(0.2, Math.Min(1, profile.Opacity));
        _suppressLabelOverlay = shouldHideForLostFocus;
        UpdateWindowRegion(shouldHideForLostFocus);
        RefreshLabelOverlay();
        Invalidate();
    }

    public bool HasPositionContextChanged(WraithViewProfile profile)
    {
        return !_positionMemory.MatchesContext(
            profile.Id,
            profile.PreviewWidth,
            profile.PreviewHeight,
            ScreenGeometry.MonitorTopologyPixels());
    }

    public IReadOnlyDictionary<string, WraithViewRect> CurrentPreviewLayouts()
    {
        // Nameless clients (character select) key on their window handle in hex, which would be an
        // orphan the moment the window closes. Save only layouts that can be matched again.
        return _previews.Values
            .Where(state => !string.IsNullOrWhiteSpace(state.Client.CharacterName))
            .ToDictionary(
                state => state.Client.StableKey,
                state => WraithViewRect.FromRectangle(state.FrameRect),
                StringComparer.OrdinalIgnoreCase
            );
    }

    public void MarkActiveClient(nint activeHandle)
    {
        if (activeHandle == nint.Zero) return;
        _foreground = activeHandle;
        _clients = _clients
            .Select(client => client with { IsForeground = client.Handle == activeHandle })
            .ToArray();
        var restoreFromLostFocus = _suppressLabelOverlay;
        if (restoreFromLostFocus)
        {
            Opacity = Math.Max(0.2, Math.Min(1, _profile.Opacity));
            _suppressLabelOverlay = false;
        }

        if (_profile.HideActivePreview)
        {
            SyncHiddenActivePreview(activeHandle);
            return;
        }

        foreach (var state in _previews.Values)
        {
            var active = state.Client.Handle == activeHandle;
            if (state.Active == active && state.Client.IsForeground == active) continue;

            state.Active = active;
            state.Client = state.Client with { IsForeground = active };
            Invalidate(ToClientRect(state.FrameRect));
        }

        if (restoreFromLostFocus)
        {
            UpdateWindowRegion();
            RefreshLabelOverlay();
        }
    }

    public void SyncClientStates(IReadOnlyList<EveClientWindow> clients, nint foreground, nint activeHandle)
    {
        _clients = clients;
        _foreground = foreground;

        var shouldHideForLostFocus = _profile.HideOnLostFocus && clients.All(client => client.Handle != foreground);
        var lostFocusVisibilityChanged = _suppressLabelOverlay != shouldHideForLostFocus;
        if (lostFocusVisibilityChanged)
        {
            Opacity = shouldHideForLostFocus ? 0 : Math.Max(0.2, Math.Min(1, _profile.Opacity));
            _suppressLabelOverlay = shouldHideForLostFocus;
        }

        if (_profile.HideActivePreview)
        {
            SyncHiddenActivePreview(activeHandle);
            return;
        }

        var clientsByHandle = clients.ToDictionary(client => client.Handle);
        foreach (var state in _previews.Values)
        {
            if (!clientsByHandle.TryGetValue(state.Client.Handle, out var client)) continue;
            var active = state.Client.Handle == activeHandle;
            var changed = state.Active != active || state.Client != client;
            state.Client = client;
            state.Active = active;
            if (changed) Invalidate(ToClientRect(state.FrameRect));
        }

        if (!lostFocusVisibilityChanged) return;
        UpdateWindowRegion(shouldHideForLostFocus);
        RefreshLabelOverlay();
    }

    private void SyncHiddenActivePreview(nint activeHandle)
    {
        foreach (var handle in _previews.Keys.Where(handle => handle == activeHandle).ToArray())
        {
            var state = _previews[handle];
            var liveClient = _clients.FirstOrDefault(client => PreviewClientIdentity.From(client) == state.Identity);
            if (liveClient != null) _positionMemory.Remember(state.Identity, state.FrameRect);
            _previews[handle].Thumbnail.Dispose();
            _previews.Remove(handle);
        }

        var liveIdentities = _clients.Select(PreviewClientIdentity.From).ToHashSet();
        _positionMemory.PurgeExcept(liveIdentities);
        var visibleIndex = 0;
        foreach (var client in _clients)
        {
            if (client.Handle == activeHandle) continue;

            var identity = PreviewClientIdentity.From(client);
            if (!_previews.TryGetValue(client.Handle, out var state) || state.Identity != identity)
            {
                if (state != null) state.Thumbnail.Dispose();
                state = new PreviewState(client, CreateThumbnail(client.Handle))
                {
                    FrameRect = ResolveFrameRect(client, visibleIndex),
                };
                _previews[client.Handle] = state;
            }

            state.Client = client;
            _positionMemory.Remember(identity, state.FrameRect);
            state.Active = false;
            state.Visible = DwmAvailable;
            UpdateThumbnail(state);
            visibleIndex++;
        }

        UpdateWindowRegion(_suppressLabelOverlay);
        RefreshLabelOverlay();
        Invalidate();
    }

    public void ShowAlert(string characterName, WraithViewPreviewAlert alert)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return;
        var now = DateTime.UtcNow;
        var matched = false;

        foreach (var state in _previews.Values)
        {
            if (!MatchesAlertTarget(state, characterName)) continue;
            state.SetAlert(alert, now);
            matched = true;
        }

        if (!matched) return;
        UpdateAlertTimer();
        Invalidate();
    }

    public void ConfigureHotkeys(WraithViewProfile profile, IReadOnlyList<EveClientWindow> clients, bool suspended)
        => _ahkHotkeys.Configure(profile, clients, suspended);

    public bool ActivateFromHotkey(nint handle, bool maximize) => _ahkHotkeys.Activate(handle, maximize);

    public void ClearThumbnails()
    {
        foreach (var state in _previews.Values)
        {
            state.Thumbnail.Dispose();
        }

        _previews.Clear();
        _windowRegionSignature = "";
        _alertTimer.Stop();
        _labelOverlay.SetItems(Array.Empty<WraithViewLabelOverlayItem>());
        UpdateWindowRegion();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ahkHotkeys.Dispose();
            ClearThumbnails();
            _alertTimer.Dispose();
            _labelOverlay.Dispose();
            Region?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            RefreshLabelOverlay();
        }
        else
        {
            _labelOverlay.SetItems(Array.Empty<WraithViewLabelOverlayItem>());
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        foreach (var state in _previews.Values)
        {
            if (!state.Visible) continue;
            DrawPreviewChrome(e.Graphics, state);
        }
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        var state = HitPreview(e.Location);
        if (state == null) return;

        _mousePreview = state;
        _mouseDownPoint = e.Location;
        _mouseDownTicks = Environment.TickCount64;
        _mouseStartRect = state.FrameRect;
        Capture = true;

        if (e.Button == Forms.MouseButtons.Middle)
        {
            MinimizeRequested?.Invoke(state.Client);
            _mouseMode = MouseMode.None;
            return;
        }

        var absolutePoint = new Point(e.Location.X + _virtualDesktop.Left, e.Location.Y + _virtualDesktop.Top);
        if (!_profile.LockPreviews && HitResizeHandle(state.FrameRect, absolutePoint))
        {
            _mouseMode = MouseMode.Resize;
            return;
        }

        _mouseMode = e.Button == Forms.MouseButtons.Right && !_profile.LockPreviews ? MouseMode.Move : MouseMode.PendingClick;
    }

    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        if (_mousePreview == null || _mouseMode == MouseMode.None) return;

        var deltaX = e.Location.X - _mouseDownPoint.X;
        var deltaY = e.Location.Y - _mouseDownPoint.Y;

        // Locked previews cannot be dragged, so there is nothing for this displacement check to
        // decide: a press stays PendingClick no matter how far the cursor travels before release,
        // and OnMouseUp judges the click by where the button came back up instead. Reclassifying
        // to Move/None here on distance alone is exactly what used to swallow clicks made while
        // the mouse was in motion — a real click covers far more than SM_CXDRAG at ordinary speed.
        if (_mouseMode == MouseMode.PendingClick
            && !_profile.LockPreviews
            && PreviewPointerGesture.ShouldStartDrag(_mouseDownPoint, e.Location, Environment.TickCount64 - _mouseDownTicks))
        {
            _mouseMode = MouseMode.Move;
        }

        if (_mouseMode == MouseMode.Move)
        {
            var next = _mouseStartRect;
            next.Offset(deltaX, deltaY);
            _mousePreview.FrameRect = PreviewSnapping.Move(next, OtherPreviewFrames(_mousePreview), MonitorBounds(), _virtualDesktop, _profile.SnapEnabled, _profile.SnapDistance, _mouseStartRect);
            ApplyDragFrame(_mousePreview);
        }
        else if (_mouseMode == MouseMode.Resize)
        {
            var next = _mouseStartRect;
            next.Width = Math.Clamp(
                next.Width + deltaX,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
            next.Height = Math.Clamp(
                next.Height + deltaY,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
            _mousePreview.FrameRect = PreviewSnapping.Resize(
                next,
                OtherPreviewFrames(_mousePreview),
                MonitorBounds(),
                _virtualDesktop,
                _profile.SnapEnabled,
                _profile.SnapDistance,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
            ApplyDragFrame(_mousePreview);
        }
    }

    /// <summary>
    /// One drag step. The frame (border, label strip) is painted synchronously before the live thumbnail moves,
    /// so the two never separate on screen; previously the thumbnail moved at once while the frame repainted
    /// later, which smeared the label bar behind the cursor. The transparent-label overlay is a full-desktop
    /// layered window that is slow to repaint, so it's hidden for the drag and redrawn on release.
    /// </summary>
    private void ApplyDragFrame(PreviewState preview)
    {
        UpdateWindowRegion();
        Invalidate();
        Update();
        UpdateThumbnail(preview);
        RefreshLabelOverlay();
    }

    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        if (_mousePreview == null) return;

        var preview = _mousePreview;
        var mode = _mouseMode;
        Capture = false;
        _mousePreview = null;
        _mouseMode = MouseMode.None;
        if (mode is MouseMode.Move or MouseMode.Resize) RefreshLabelOverlay();

        if (mode is MouseMode.Move or MouseMode.Resize)
        {
            // A left press that drifted past the drag metric enters Move mode and never leaves
            // it, even if the cursor comes back. Judging the gesture on where the press actually
            // ended means an imprecise click still switches clients instead of being swallowed
            // and silently nudging the preview a few pixels.
            if (mode == MouseMode.Move
                && e.Button == Forms.MouseButtons.Left
                && PreviewPointerGesture.IsClick(_mouseDownPoint, e.Location, PreviewPointerGesture.ClickSlop))
            {
                preview.FrameRect = _mouseStartRect;
                _positionMemory.Remember(PreviewClientIdentity.From(preview.Client), preview.FrameRect);
                UpdateThumbnail(preview);
                UpdateWindowRegion();
                RefreshLabelOverlay();
                Invalidate();
                ActivateRequested?.Invoke(preview.Client);
                return;
            }

            _positionMemory.Remember(PreviewClientIdentity.From(preview.Client), preview.FrameRect);

            // A client at character select has no name, so its StableKey is the window handle in hex.
            // Persisting under that key would write an orphan into settings that can never match again
            // and is never pruned. The remembered frame above still honours the drag on screen.
            if (string.IsNullOrWhiteSpace(preview.Client.CharacterName)) return;

            PreviewLayoutChanged?.Invoke(preview.Client.StableKey, WraithViewRect.FromRectangle(preview.FrameRect));
            return;
        }

        if (mode == MouseMode.PendingClick && e.Button == Forms.MouseButtons.Left)
        {
            // Locked previews never leave PendingClick in OnMouseMove regardless of distance
            // travelled, so the gesture is decided here instead: release over the SAME preview
            // that was pressed activates it, release elsewhere is a cancelled click (pressed,
            // changed their mind, dragged off before letting go). Checked against the pressed
            // preview's own frame rather than HitPreview so an overlapping preview can't steal it.
            if (_profile.LockPreviews)
            {
                var releaseAbsolute = new Point(e.Location.X + _virtualDesktop.Left, e.Location.Y + _virtualDesktop.Top);
                if (PreviewPointerGesture.IsLockedReleaseActivation(preview.FrameRect, releaseAbsolute))
                {
                    ActivateRequested?.Invoke(preview.Client);
                }
                return;
            }

            ActivateRequested?.Invoke(preview.Client);
        }
    }

    private DwmThumbnail CreateThumbnail(nint source)
    {
        try
        {
            return new DwmThumbnail(Handle, source);
        }
        catch
        {
            DwmAvailable = false;
            return DwmThumbnail.Empty;
        }
    }

    private Rectangle ResolveFrameRect(EveClientWindow client, int index)
    {
        Rectangle? savedForCurrentKey = null;
        if (_profile.PreviewLayouts.TryGetValue(client.StableKey, out var saved) && saved.IsUsable)
        {
            savedForCurrentKey = saved.ToRectangle();
        }

        Rectangle? rememberedForClient = null;
        if (_positionMemory.TryGet(PreviewClientIdentity.From(client), out var remembered))
        {
            rememberedForClient = remembered;
        }

        Rectangle? titleFallback = null;
        if (string.IsNullOrWhiteSpace(client.CharacterName)
            && _profile.PreviewLayouts.TryGetValue(client.Title, out var titleSaved)
            && titleSaved.IsUsable)
        {
            var rect = titleSaved.ToRectangle();
            var sameTitleIndex = _clients
                .TakeWhile(item => item.Handle != client.Handle)
                .Count(item => string.Equals(item.Title, client.Title, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(item.CharacterName));
            if (sameTitleIndex > 0)
            {
                rect.Y += sameTitleIndex * (rect.Height + 10);
            }

            titleFallback = PreviewSnapping.KeepOnDesktop(rect, _virtualDesktop);
        }

        // Previews may sit partly off-screen (parked half off the top, say), but never somewhere they can't be
        // reached, such as on a monitor that has since been unplugged.
        return PreviewSnapping.KeepOnDesktop(
            WraithViewPreviewPositionMemory.Resolve(
                savedForCurrentKey,
                rememberedForClient,
                titleFallback,
                DefaultStackRect(index)),
            _virtualDesktop);
    }

    private Rectangle DefaultStackRect(int index)
    {
        var primary = ScreenGeometry.PrimaryScreenPixels();
        var width = Math.Clamp(
            _profile.PreviewWidth,
            WraithViewPreviewDimensions.Minimum,
            WraithViewPreviewDimensions.Maximum);
        var height = Math.Clamp(
            _profile.PreviewHeight,
            WraithViewPreviewDimensions.Minimum,
            WraithViewPreviewDimensions.Maximum);
        var x = primary.Right - width - 18;
        var y = primary.Top + 82 + index * (height + 10);
        if (y + height > primary.Bottom - 18)
        {
            var column = (index * (height + 10)) / Math.Max(1, primary.Height - 120);
            var row = index % Math.Max(1, (primary.Height - 120) / Math.Max(1, height + 10));
            x = primary.Right - width - 18 - column * (width + 10);
            y = primary.Top + 82 + row * (height + 10);
        }

        return new Rectangle(x, y, width, height);
    }

    private void UpdateThumbnail(PreviewState state)
    {
        var thumbRect = ThumbnailRect(state.FrameRect);
        thumbRect.Offset(-_virtualDesktop.Left, -_virtualDesktop.Top);
        state.Thumbnail.Update(thumbRect, 255, state.Visible);
    }

    private void RefreshLabelOverlay()
    {
        if (!Visible
            || _suppressLabelOverlay
            || _mouseMode is MouseMode.Move or MouseMode.Resize
            || !_profile.ShowLabels
            || !_profile.LabelBackgroundTransparent)
        {
            _labelOverlay.SetItems(Array.Empty<WraithViewLabelOverlayItem>());
            return;
        }

        var items = _previews.Values
            .Where(state => state.Visible)
            .Select(state => new WraithViewLabelOverlayItem(
                ToClientRect(state.FrameRect),
                _profile.PreviewLabelFor(state.Client),
                ColorFromString(_profile.LabelTextColor, Color.FromArgb(217, 226, 238)),
                Math.Max(8, Math.Min(32, _profile.LabelFontSize)),
                LabelPosition(),
                Math.Max(1, _profile.BorderThickness)
            ))
            .ToArray();

        _labelOverlay.SetItems(items);
    }

    private Rectangle ThumbnailRect(Rectangle frameRect)
    {
        var border = Math.Max(1, _profile.BorderThickness);
        var labelHeight = ReservedLabelHeight();
        var labelPosition = LabelPosition();
        var topLabelHeight = labelPosition == "top" ? labelHeight : 0;
        var bottomLabelHeight = labelPosition == "bottom" ? labelHeight : 0;
        return new Rectangle(
            frameRect.Left + border,
            frameRect.Top + topLabelHeight + border,
            Math.Max(1, frameRect.Width - border * 2),
            Math.Max(1, frameRect.Height - topLabelHeight - bottomLabelHeight - border * 2)
        );
    }

    private int ReservedLabelHeight()
    {
        if (!_profile.ShowLabels || _profile.LabelBackgroundTransparent) return 0;
        return Math.Max(24, Math.Min(32, _profile.LabelFontSize) + 12);
    }

    private string LabelPosition()
    {
        var position = (_profile.LabelPosition ?? "top").Trim().ToLowerInvariant();
        if (!_profile.LabelBackgroundTransparent && position == "center") return "top";
        return position is "top" or "bottom" or "center" ? position : "top";
    }

    private Rectangle ToClientRect(Rectangle rect)
    {
        var next = rect;
        next.Offset(-_virtualDesktop.Left, -_virtualDesktop.Top);
        return next;
    }

    private void DrawPreviewChrome(Graphics graphics, PreviewState state)
    {
        var frame = ToClientRect(state.FrameRect);
        var borderThickness = state.Active && _profile.ShowActiveHighlight ? Math.Max(1, _profile.BorderThickness) : 1;
        var labelHeight = ReservedLabelHeight();
        var labelPosition = LabelPosition();
        var borderColor = state.Active && _profile.ShowActiveHighlight
            ? ColorFromString(_profile.ActiveBorderColor, Color.FromArgb(167, 123, 255))
            : ColorFromString(_profile.InactiveBorderColor, Color.FromArgb(115, 123, 140));

        if (!state.Active && !_profile.ShowInactiveBorders) borderColor = Color.Transparent;

        if (_profile.EdgeOutline && !(state.Active && _profile.ShowActiveHighlight))
        {
            // Outermost pixel only; the coloured border (if any) is drawn over it.
            FillFrameEdges(graphics, frame, 1, Color.FromArgb(96, 230, 225, 242));
        }

        if (borderColor.A > 0)
        {
            FillFrameEdges(graphics, frame, borderThickness, borderColor);
        }

        if (_profile.ShowLabels && labelHeight > 0)
        {
            var labelTop = labelPosition == "bottom" ? frame.Bottom - labelHeight - 1 : frame.Top + 1;
            var labelRect = new Rectangle(frame.Left + 1, labelTop, Math.Max(1, frame.Width - 2), labelHeight);
            using var labelBrush = new SolidBrush(ColorFromString(_profile.LabelBackgroundColor, Color.FromArgb(210, 11, 17, 29)));
            graphics.FillRectangle(labelBrush, labelRect);

            var text = _profile.PreviewLabelFor(state.Client);
            using var labelFont = new Font(Font.FontFamily, Math.Max(8, Math.Min(32, _profile.LabelFontSize)), FontStyle.Regular, GraphicsUnit.Point);
            using var textBrush = new SolidBrush(ColorFromString(_profile.LabelTextColor, Color.FromArgb(217, 226, 238)));
            using var format = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Near,
            };
            var textRect = new RectangleF(labelRect.Left + 8, labelRect.Top, Math.Max(1, labelRect.Width - 36), labelRect.Height);
            graphics.DrawString(text, labelFont, textBrush, textRect, format);

            // The label strip is painted over the frame; put the border back on top of it.
            if (borderColor.A > 0) FillFrameEdges(graphics, frame, borderThickness, borderColor);
        }

        var activeAlert = state.ActiveAlert(DateTime.UtcNow);
        if (activeAlert != null)
        {
            DrawAlertBorder(graphics, frame, activeAlert);
        }

        var handle = new Rectangle(frame.Right - ResizeHitSize, frame.Bottom - ResizeHitSize, ResizeHitSize - 3, ResizeHitSize - 3);
        using var handlePen = new Pen(Color.FromArgb(180, 217, 226, 238), 1);
        graphics.DrawLine(handlePen, handle.Right - 8, handle.Bottom - 2, handle.Right - 2, handle.Bottom - 8);
        graphics.DrawLine(handlePen, handle.Right - 13, handle.Bottom - 2, handle.Right - 2, handle.Bottom - 13);
    }

    private void DrawAlertBorder(Graphics graphics, Rectangle frame, ActivePreviewAlert alert)
    {
        var baseColor = ColorFromString(alert.Color, Color.FromArgb(255, 59, 59));
        var elapsed = Math.Max(0, (DateTime.UtcNow - alert.StartedUtc).TotalMilliseconds);
        var progress = Math.Min(1, elapsed / Math.Max(1, alert.DurationMs));
        var wave = (Math.Sin(progress * alert.PulseCount * Math.PI * 2) + 1) / 2;
        var alpha = (int)Math.Max(90, Math.Min(255, 110 + wave * 145));
        FillFrameEdges(graphics, frame, Math.Max(1, alert.Thickness), Color.FromArgb(alpha, baseColor));
    }

    /// <summary>
    /// Border as four filled strips just inside the frame. It used to be a 1 px rounded path, which GDI+
    /// draws partly outside the window region (right and bottom edges) and under the square DWM thumbnail
    /// (the corners), so a thin border came out broken.
    /// </summary>
    private static void FillFrameEdges(Graphics graphics, Rectangle frame, int thickness, Color color)
    {
        var t = Math.Max(1, Math.Min(thickness, Math.Min(frame.Width, frame.Height) / 2));
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, frame.Left, frame.Top, frame.Width, t);
        graphics.FillRectangle(brush, frame.Left, frame.Bottom - t, frame.Width, t);
        graphics.FillRectangle(brush, frame.Left, frame.Top + t, t, frame.Height - 2 * t);
        graphics.FillRectangle(brush, frame.Right - t, frame.Top + t, t, frame.Height - 2 * t);
        graphics.SmoothingMode = smoothing;
    }

    private void TickAlertFlashes()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var state in _previews.Values)
        {
            removed |= state.ClearExpiredAlert(now);
        }

        var anyActive = _previews.Values.Any(state => state.ActiveAlert(now) != null);
        if (!anyActive) _alertTimer.Stop();
        if (removed || anyActive) Invalidate();
    }

    private void UpdateAlertTimer()
    {
        var anyActive = _previews.Values.Any(state => state.ActiveAlert(DateTime.UtcNow) != null);
        if (anyActive && !_alertTimer.Enabled) _alertTimer.Start();
        if (!anyActive && _alertTimer.Enabled) _alertTimer.Stop();
    }

    private static bool MatchesAlertTarget(PreviewState state, string characterName)
    {
        return string.Equals(state.Client.CharacterName, characterName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Client.StableKey, characterName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Client.Title, characterName, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateWindowRegion(bool forceEmpty = false)
    {
        var signature = forceEmpty
            ? "empty"
            : string.Join(";", _previews.Values
                .Where(state => state.Visible)
                .Select(state => $"{state.Client.Handle:X}:{state.FrameRect.X},{state.FrameRect.Y},{state.FrameRect.Width},{state.FrameRect.Height}"));
        if (string.Equals(signature, _windowRegionSignature, StringComparison.Ordinal)) return;
        _windowRegionSignature = signature;

        var region = new Region();
        region.MakeEmpty();

        foreach (var state in _previews.Values.Where(state => state.Visible && !forceEmpty))
        {
            region.Union(ToClientRect(state.FrameRect));
        }

        var oldRegion = Region;
        Region = region;
        oldRegion?.Dispose();
    }

    private PreviewState? HitPreview(Point clientPoint)
    {
        var absolute = new Point(clientPoint.X + _virtualDesktop.Left, clientPoint.Y + _virtualDesktop.Top);
        return _previews.Values
            .Where(state => state.Visible && state.FrameRect.Contains(absolute))
            .OrderByDescending(state => state.Active)
            .FirstOrDefault();
    }

    private static bool HitResizeHandle(Rectangle frame, Point clientPoint)
    {
        return clientPoint.X >= frame.Right - ResizeHitSize
            && clientPoint.X <= frame.Right
            && clientPoint.Y >= frame.Bottom - ResizeHitSize
            && clientPoint.Y <= frame.Bottom;
    }

    private Rectangle[] OtherPreviewFrames(PreviewState moving)
    {
        return _previews.Values.Where(state => state != moving && state.Visible).Select(state => state.FrameRect).ToArray();
    }

    private static Rectangle[] MonitorBounds()
    {
        return Forms.Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
    }

    internal static string HotkeySignature(WraithViewProfile profile, IReadOnlyList<EveClientWindow> clients, bool suspended)
    {
        if (suspended) return $"suspended:{profile.Id}";
        var direct = string.Join(";", profile.DirectHotkeys.Select(binding => $"{binding.Enabled}:{binding.CharacterName}:{string.Join(",", binding.Gestures)}"));
        var cycles = string.Join(";", profile.CycleGroups.Select(group => $"{group.Enabled}:{group.Id}:{string.Join(",", group.ForwardGestures)}:{string.Join(",", group.BackwardGestures)}:{string.Join(",", group.Characters)}"));
        var characterOrder = string.Join(",", profile.CharacterOrder);
        var clientKeys = string.Join(";", clients.Select(client => $"{client.StableKey}:{client.Handle}"));
        return $"{profile.Id}|{profile.HotkeysRequireEveForeground}|{direct}|{cycles}|{characterOrder}|{clientKeys}";
    }


    private static Color ColorFromString(string value, Color fallback)
    {
        if (value.Length == 9 && value[0] == '#'
            && int.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var alpha)
            && int.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var red)
            && int.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var green)
            && int.TryParse(value.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return Color.FromArgb(alpha, red, green, blue);
        }

        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class PreviewState
    {
        public PreviewState(EveClientWindow client, DwmThumbnail thumbnail)
        {
            Client = client;
            Thumbnail = thumbnail;
        }

        public EveClientWindow Client { get; set; }
        public PreviewClientIdentity Identity => PreviewClientIdentity.From(Client);
        public DwmThumbnail Thumbnail { get; }
        public Rectangle FrameRect { get; set; }
        public bool Active { get; set; }
        public bool Visible { get; set; }
        private ActivePreviewAlert? Alert { get; set; }

        public void SetAlert(WraithViewPreviewAlert alert, DateTime now)
        {
            if (Alert != null && Alert.ExpiresUtc > now && Alert.SeverityRank > alert.SeverityRank)
            {
                Alert.ExpiresUtc = now.AddMilliseconds(Math.Max(1, alert.DurationMs));
                return;
            }

            Alert = new ActivePreviewAlert(
                alert.SeverityRank,
                alert.Color,
                alert.Thickness,
                alert.DurationMs,
                alert.PulseCount,
                now,
                now.AddMilliseconds(Math.Max(1, alert.DurationMs))
            );
        }

        public ActivePreviewAlert? ActiveAlert(DateTime now) => Alert != null && Alert.ExpiresUtc > now ? Alert : null;

        public bool ClearExpiredAlert(DateTime now)
        {
            if (Alert == null || Alert.ExpiresUtc > now) return false;
            Alert = null;
            return true;
        }
    }

    private enum MouseMode
    {
        None,
        PendingClick,
        Move,
        Resize,
    }
}

internal sealed record WraithViewLabelOverlayItem(
    Rectangle Frame,
    string Text,
    Color TextColor,
    int FontSize,
    string Position,
    int BorderThickness);

internal sealed class WraithViewLabelOverlayForm : Forms.Form
{
    private static readonly Color TransparentBackColor = Color.FromArgb(1, 2, 3);
    private IReadOnlyList<WraithViewLabelOverlayItem> _items = Array.Empty<WraithViewLabelOverlayItem>();
    private bool? _appliedTopmost;

    public WraithViewLabelOverlayForm()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = TransparentBackColor;
        TransparencyKey = TransparentBackColor;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9, FontStyle.Regular);
    }

    public bool AllowTopmost { get; set; } = true;

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WraithViewNativeMethods.WsExToolWindow
                | WraithViewNativeMethods.WsExNoActivate
                | WraithViewNativeMethods.WsExTransparent
                | WraithViewNativeMethods.WsExLayered;
            if (AllowTopmost) cp.ExStyle |= WraithViewNativeMethods.WsExTopmost;
            return cp;
        }
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == WraithViewNativeMethods.WmNcHitTest)
        {
            m.Result = WraithViewNativeMethods.HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    public void SetVirtualDesktop(Rectangle virtualDesktop)
    {
        if (Bounds != virtualDesktop)
        {
            SetBounds(virtualDesktop.Left, virtualDesktop.Top, virtualDesktop.Width, virtualDesktop.Height);
        }
    }

    public void ApplyTopmostPolicy(bool force = false)
    {
        if (!force && _appliedTopmost == AllowTopmost) return;
        _appliedTopmost = AllowTopmost;
        TopMost = AllowTopmost;
        if (!IsHandleCreated) return;

        WraithViewNativeMethods.SetWindowPos(
            Handle,
            AllowTopmost ? WraithViewNativeMethods.HwndTopmost : WraithViewNativeMethods.HwndNotTopmost,
            0,
            0,
            0,
            0,
            WraithViewNativeMethods.SwpNoMove | WraithViewNativeMethods.SwpNoSize | WraithViewNativeMethods.SwpNoActivate
        );
    }

    public void SetItems(IReadOnlyList<WraithViewLabelOverlayItem> items)
    {
        var previousItems = _items;
        _items = items;
        if (_items.Count == 0)
        {
            if (Visible)
            {
                // Wipe the surface before hiding. A layered window keeps its last frame, and Windows shows
                // that frame again the moment it's re-shown, which flashed labels at their old positions
                // (e.g. above a preview just dragged down) before the next paint moved them.
                Invalidate();
                Update();
                Hide();
            }
            return;
        }

        var wasHidden = !Visible;
        if (wasHidden)
        {
            Show();
            ApplyTopmostPolicy(force: true);
            // Paint the new positions now rather than on the next message loop pass.
            Invalidate();
            Update();
            return;
        }

        ApplyTopmostPolicy();

        // Repainting is all-or-nothing here: this form is layered (WS_EX_LAYERED plus a
        // TransparencyKey), and a partial Invalidate(rect) does not reach the composited
        // surface, so stale label text survives at the old location. Bounding the
        // invalidation was measured at ~30x cheaper per paint but left visible ghosts
        // behind whenever a preview moved or a client closed. So the repaint stays
        // full-surface, and instead we skip it entirely when nothing actually changed -
        // which was the common case, roughly 90% of calls.
        if (wasHidden || !ItemsEqual(previousItems, _items))
        {
            Invalidate();
        }
    }

    private static bool ItemsEqual(
        IReadOnlyList<WraithViewLabelOverlayItem> previousItems,
        IReadOnlyList<WraithViewLabelOverlayItem> currentItems)
    {
        if (previousItems.Count != currentItems.Count) return false;

        for (var i = 0; i < currentItems.Count; i++)
        {
            if (!Equals(previousItems[i], currentItems[i])) return false;
        }

        return true;
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.Clear(TransparentBackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        foreach (var item in _items)
        {
            DrawItem(e.Graphics, item);
        }
    }

    private void DrawItem(Graphics graphics, WraithViewLabelOverlayItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return;

        var fontSize = Math.Max(8, Math.Min(32, item.FontSize));
        using var labelFont = new Font(Font.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(item.TextColor);
        using var shadowBrush = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Center,
        };

        var textHeight = Math.Max(18, (int)Math.Ceiling(labelFont.GetHeight(graphics)) + 8);
        var inset = Math.Max(6, item.BorderThickness + 6);
        var labelTop = item.Position switch
        {
            "bottom" => item.Frame.Bottom - inset - textHeight,
            "center" => item.Frame.Top + (item.Frame.Height - textHeight) / 2,
            _ => item.Frame.Top + inset,
        };
        var labelRect = new RectangleF(
            item.Frame.Left + inset,
            labelTop,
            Math.Max(1, item.Frame.Width - inset * 2),
            textHeight
        );

        var shadowRect = labelRect;
        shadowRect.Offset(1, 1);
        graphics.DrawString(item.Text, labelFont, shadowBrush, shadowRect, format);
        graphics.DrawString(item.Text, labelFont, textBrush, labelRect, format);
    }
}

internal sealed class EveWindowTracker
{
    private readonly Dictionary<uint, string> _processNames = new();

    public IReadOnlyList<EveClientWindow> GetClients(nint foreground)
    {
        var clients = new List<EveClientWindow>();
        WraithViewNativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == nint.Zero || !WraithViewNativeMethods.IsWindowVisible(hwnd)) return true;
            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            WraithViewNativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            if (!LooksLikeEve(title, ProcessName(processId))) return true;

            clients.Add(new EveClientWindow(
                hwnd,
                title,
                CharacterNameFromTitle(title),
                processId,
                WraithViewNativeMethods.IsIconic(hwnd),
                hwnd == foreground
            ));
            return true;
        }, nint.Zero);

        return clients;
    }

    private string ProcessName(uint processId)
    {
        if (_processNames.TryGetValue(processId, out var cached)) return cached;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            cached = process.ProcessName;
        }
        catch
        {
            cached = "";
        }

        _processNames[processId] = cached;
        return cached;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var length = WraithViewNativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var builder = new StringBuilder(length + 1);
        WraithViewNativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static bool LooksLikeEve(string title, string processName)
    {
        if (title.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) return false;
        if (processName.Contains("launcher", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsEveClientProcess(processName)) return false;

        return title.Length > 2;
    }

    private static bool IsEveClientProcess(string processName)
    {
        return processName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
    }

    private static string CharacterNameFromTitle(string title)
    {
        if (title.StartsWith("EVE - ", StringComparison.OrdinalIgnoreCase))
        {
            return title[6..].Trim();
        }

        if (title.EndsWith(" - EVE", StringComparison.OrdinalIgnoreCase))
        {
            return title[..^6].Trim();
        }

        return title.Equals("EVE", StringComparison.OrdinalIgnoreCase) ? "" : title.Trim();
    }
}

internal sealed record EveClientWindow(
    nint Handle,
    string Title,
    string CharacterName,
    uint ProcessId,
    bool IsMinimized,
    bool IsForeground)
{
    public string StableKey => string.IsNullOrWhiteSpace(CharacterName) ? Handle.ToString("X") : CharacterName;
}

internal sealed class DwmThumbnail : IDisposable
{
    public static DwmThumbnail Empty { get; } = new();

    private nint _handle;
    private bool _disposed;
    private bool _hasLastUpdate;
    private Rectangle _lastDestination;
    private byte _lastOpacity;
    private bool _lastVisible;

    private DwmThumbnail()
    {
    }

    public DwmThumbnail(nint destination, nint source)
    {
        var result = WraithViewNativeMethods.DwmRegisterThumbnail(destination, source, out _handle);
        if (result != 0 || _handle == nint.Zero)
        {
            throw new InvalidOperationException($"DwmRegisterThumbnail failed: 0x{result:X8}");
        }
    }

    public void Update(Rectangle destination, byte opacity, bool visible)
    {
        if (_disposed || _handle == nint.Zero) return;
        if (_hasLastUpdate && _lastDestination == destination && _lastOpacity == opacity && _lastVisible == visible)
        {
            return;
        }

        var properties = new WraithViewNativeMethods.DwmThumbnailProperties
        {
            Flags = WraithViewNativeMethods.DwmTnpRectDestination
                | WraithViewNativeMethods.DwmTnpVisible
                | WraithViewNativeMethods.DwmTnpOpacity
                | WraithViewNativeMethods.DwmTnpSourceClientAreaOnly,
            Destination = WraithViewNativeMethods.NativeRect.FromRectangle(destination),
            Opacity = opacity,
            Visible = visible,
            SourceClientAreaOnly = true,
        };

        if (WraithViewNativeMethods.DwmUpdateThumbnailProperties(_handle, ref properties) == 0)
        {
            _hasLastUpdate = true;
            _lastDestination = destination;
            _lastOpacity = opacity;
            _lastVisible = visible;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != nint.Zero)
        {
            WraithViewNativeMethods.DwmUnregisterThumbnail(_handle);
            _handle = nint.Zero;
        }
    }
}

internal sealed class WraithViewSettings
{
    public const string CurrentGuideVersion = "wraithview-guide-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool Enabled { get; set; }
    public bool HotkeysSuspended { get; set; }
    public int HotkeySyntaxVersion { get; set; } = 1;
    public bool SettingsWindowAlwaysOnTop { get; set; } = true;
    public bool GuideCompleted { get; set; }
    public string GuideVersion { get; set; } = "";
    public string SelectedProfileId { get; set; } = "default";
    public WraithAlertsSettings Alerts { get; set; } = WraithAlertsSettings.CreateDefault();
    public List<WraithViewProfile> Profiles { get; set; } = new() { WraithViewProfile.CreateDefault("Default") };

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WraithView", "wraithview-settings.json");

    public static WraithViewSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return FromJson(File.ReadAllText(SettingsPath));
            }
        }
        catch
        {
            // Corrupt settings should not stop the HUD from launching.
        }

        var defaults = new WraithViewSettings();
        defaults.Normalize();
        return defaults;
    }

    public static WraithViewSettings FromJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root == null || (root["profiles"] is not JsonArray && root["Profiles"] is not JsonArray))
        {
            throw new InvalidDataException("The selected file is not a WraithView settings backup.");
        }

        var settings = JsonSerializer.Deserialize<WraithViewSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("The selected file could not be read as a WraithView settings backup.");
        settings.Normalize();
        var syntaxVersion = root["hotkeySyntaxVersion"] ?? root["HotkeySyntaxVersion"];
        if (syntaxVersion == null || syntaxVersion.GetValue<int>() < 1)
        {
            foreach (var profile in settings.Profiles)
            {
                foreach (var binding in profile.DirectHotkeys)
                    binding.Gestures = binding.Gestures.Select(AhkGesture.UpgradeLegacyVirtualKey).ToList();
                foreach (var group in profile.CycleGroups)
                {
                    group.ForwardGestures = group.ForwardGestures.Select(AhkGesture.UpgradeLegacyVirtualKey).ToList();
                    group.BackwardGestures = group.BackwardGestures.Select(AhkGesture.UpgradeLegacyVirtualKey).ToList();
                }
            }
            settings.HotkeySyntaxVersion = 1;
            settings.Normalize();
        }
        return settings;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, ToJson());
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public WraithViewProfile ActiveProfile()
    {
        Normalize();
        return Profiles.First(profile => string.Equals(profile.Id, SelectedProfileId, StringComparison.OrdinalIgnoreCase));
    }

    public WraithViewProfile ActiveProfileFast()
    {
        return Profiles.FirstOrDefault(profile => string.Equals(profile.Id, SelectedProfileId, StringComparison.OrdinalIgnoreCase))
            ?? ActiveProfile();
    }

    private void Normalize()
    {
        GuideVersion = GuideVersion?.Trim() ?? "";
        if (!GuideCompleted) GuideVersion = "";
        Alerts ??= WraithAlertsSettings.CreateDefault();
        Alerts.Normalize();
        if (Profiles.Count == 0) Profiles.Add(WraithViewProfile.CreateDefault("Default"));
        foreach (var profile in Profiles) profile.Normalize();
        if (Profiles.All(profile => !string.Equals(profile.Id, SelectedProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedProfileId = Profiles[0].Id;
        }
    }
}

internal sealed class WraithViewProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default";
    public int PreviewWidth { get; set; } = 320;
    public int PreviewHeight { get; set; } = 204;
    public double Opacity { get; set; } = 1;
    public bool ShowLabels { get; set; } = true;
    public bool ShowActiveHighlight { get; set; } = true;
    public bool ShowInactiveBorders { get; set; } = true;
    /// <summary>A faint light line around every preview so its edge shows against dark EVE scenes.</summary>
    public bool EdgeOutline { get; set; }
    public bool LockPreviews { get; set; }
    public int BorderThickness { get; set; } = 2;
    public bool SnapEnabled { get; set; } = true;
    public int SnapDistance { get; set; } = 18;
    public bool HideActivePreview { get; set; }
    public bool HideOnLostFocus { get; set; }
    public bool MinimizeInactiveClients { get; set; }
    public bool AlwaysMaximizeClients { get; set; }
    public bool AutoRestoreClientLayouts { get; set; }
    public bool HotkeysRequireEveForeground { get; set; } = true;
    public bool RememberCycleGroupPositions { get; set; } = true;
    public string ActiveBorderColor { get; set; } = "#A77BFF";
    public string InactiveBorderColor { get; set; } = "#737B8C";
    public string LabelTextColor { get; set; } = "#D9E2EE";
    public string LabelBackgroundColor { get; set; } = "#D20B111D";
    public bool LabelBackgroundTransparent { get; set; }
    public string LabelPosition { get; set; } = "top";
    public int LabelFontSize { get; set; } = 9;
    public List<string> CharacterOrder { get; set; } = new();
    public List<string> HiddenClients { get; set; } = new();
    public List<string> NeverMinimizeClients { get; set; } = new();
    public string SelectedCycleGroupId { get; set; } = "";
    public Dictionary<string, string> PreviewLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ClientColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WraithViewRect> PreviewLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WraithViewClientPlacement> ClientPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WraithViewHotkeyBinding> DirectHotkeys { get; set; } = new();
    public List<WraithViewCycleGroup> CycleGroups { get; set; } = new();

    public static WraithViewProfile CreateDefault(string name)
    {
        return new WraithViewProfile
        {
            Id = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ? "default" : Guid.NewGuid().ToString("N"),
            Name = name,
        };
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Name)) Name = "Profile";
        PreviewWidth = Math.Clamp(
            PreviewWidth,
            WraithViewPreviewDimensions.Minimum,
            WraithViewPreviewDimensions.Maximum);
        PreviewHeight = Math.Clamp(
            PreviewHeight,
            WraithViewPreviewDimensions.Minimum,
            WraithViewPreviewDimensions.Maximum);
        Opacity = Math.Max(0.2, Math.Min(1, Opacity));
        BorderThickness = Math.Max(1, Math.Min(16, BorderThickness));
        LabelFontSize = Math.Max(8, Math.Min(32, LabelFontSize));
        LabelPosition = NormalizeLabelPosition(LabelPosition, LabelBackgroundTransparent);
        SnapDistance = Math.Max(0, Math.Min(80, SnapDistance));
        CharacterOrder = CleanList(CharacterOrder);
        HiddenClients = CleanList(HiddenClients);
        NeverMinimizeClients = CleanList(NeverMinimizeClients);
        DirectHotkeys ??= new List<WraithViewHotkeyBinding>();
        CycleGroups ??= new List<WraithViewCycleGroup>();
        foreach (var binding in DirectHotkeys) binding.Normalize();
        foreach (var group in CycleGroups) group.Normalize();
        DirectHotkeys = DirectHotkeys
            .Where(binding => binding.CharacterName.Length > 0 && binding.Gestures.Count > 0)
            .ToList();
        if (CycleGroups.Count > 0 && CycleGroups.All(group => !string.Equals(group.Id, SelectedCycleGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCycleGroupId = CycleGroups[0].Id;
        }
        if (CycleGroups.Count == 0)
        {
            SelectedCycleGroupId = "";
        }
        PreviewLabels = CleanMap(PreviewLabels);
        PreviewLayouts ??= new Dictionary<string, WraithViewRect>(StringComparer.OrdinalIgnoreCase);
        foreach (var layout in PreviewLayouts.Values)
        {
            layout.Width = Math.Clamp(
                layout.Width,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
            layout.Height = Math.Clamp(
                layout.Height,
                WraithViewPreviewDimensions.Minimum,
                WraithViewPreviewDimensions.Maximum);
        }
        ClientPlacements ??= new Dictionary<string, WraithViewClientPlacement>(StringComparer.OrdinalIgnoreCase);
        ClientColors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public object ToState()
    {
        return new
        {
            id = Id,
            name = Name,
            previewWidth = PreviewWidth,
            previewHeight = PreviewHeight,
            opacity = Opacity,
            showLabels = ShowLabels,
            showActiveHighlight = ShowActiveHighlight,
            showInactiveBorders = ShowInactiveBorders,
            edgeOutline = EdgeOutline,
            lockPreviews = LockPreviews,
            borderThickness = BorderThickness,
            snapEnabled = SnapEnabled,
            snapDistance = SnapDistance,
            hideActivePreview = HideActivePreview,
            hideOnLostFocus = HideOnLostFocus,
            minimizeInactiveClients = MinimizeInactiveClients,
            alwaysMaximizeClients = AlwaysMaximizeClients,
            autoRestoreClientLayouts = AutoRestoreClientLayouts,
            hotkeysRequireEveForeground = HotkeysRequireEveForeground,
            rememberCycleGroupPositions = RememberCycleGroupPositions,
            activeBorderColor = ActiveBorderColor,
            inactiveBorderColor = InactiveBorderColor,
            labelTextColor = LabelTextColor,
            labelBackgroundColor = LabelBackgroundColor,
            labelBackgroundTransparent = LabelBackgroundTransparent,
            labelPosition = LabelPosition,
            labelFontSize = LabelFontSize,
            characterOrderText = string.Join(Environment.NewLine, CharacterOrder),
            hiddenClientsText = string.Join(Environment.NewLine, HiddenClients),
            neverMinimizeClientsText = string.Join(Environment.NewLine, NeverMinimizeClients),
            selectedCycleGroupId = SelectedCycleGroupId,
            previewLabels = PreviewLabels,
            directHotkeys = DirectHotkeys.Select(binding => new
            {
                characterName = binding.CharacterName,
                gesture = binding.Gesture,
                gestures = binding.Gestures,
                enabled = binding.Enabled,
            }).ToArray(),
            cycleGroups = CycleGroups.Select(group => new
            {
                id = group.Id,
                name = group.Name,
                forwardGesture = group.ForwardGesture,
                backwardGesture = group.BackwardGesture,
                forwardGestures = group.ForwardGestures,
                backwardGestures = group.BackwardGestures,
                charactersText = string.Join(Environment.NewLine, group.Characters),
                enabled = group.Enabled,
            }).ToArray(),
            directHotkeysText = string.Join(Environment.NewLine, DirectHotkeys.Select(binding => $"{binding.CharacterName} = {string.Join(", ", binding.Gestures)}")),
            cycleGroupsText = string.Join(Environment.NewLine, CycleGroups.Select(group => $"{group.Name}|{string.Join(", ", group.ForwardGestures)}|{string.Join(", ", group.BackwardGestures)}|{string.Join(",", group.Characters)}")),
        };
    }

    public IEnumerable<WraithViewCycleGroup> ActiveCycleGroups()
    {
        Normalize();
        return CycleGroups.Where(group => group.Enabled);
    }

    public string PreviewLabelFor(EveClientWindow client)
    {
        if (PreviewLabels.TryGetValue(client.StableKey, out var stableLabel) && !string.IsNullOrWhiteSpace(stableLabel))
        {
            return stableLabel;
        }

        if (!string.IsNullOrWhiteSpace(client.CharacterName)
            && PreviewLabels.TryGetValue(client.CharacterName, out var characterLabel)
            && !string.IsNullOrWhiteSpace(characterLabel))
        {
            return characterLabel;
        }

        if (!string.IsNullOrWhiteSpace(client.Title)
            && PreviewLabels.TryGetValue(client.Title, out var titleLabel)
            && !string.IsNullOrWhiteSpace(titleLabel))
        {
            return titleLabel;
        }

        return string.IsNullOrWhiteSpace(client.CharacterName) ? client.Title : client.CharacterName;
    }

    private static string NormalizeLabelPosition(string? value, bool transparent)
    {
        var clean = (value ?? "top").Trim().ToLowerInvariant();
        if (!transparent && clean == "center") return "top";
        return clean is "top" or "bottom" or "center" ? clean : "top";
    }

    private static List<string> CleanList(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> CleanMap(IDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return result;

        foreach (var (key, value) in values)
        {
            var cleanKey = key.Trim();
            var cleanValue = value?.Trim() ?? "";
            if (cleanKey.Length == 0 || cleanValue.Length == 0) continue;
            result[cleanKey] = cleanValue;
        }

        return result;
    }
}

internal sealed class WraithViewRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsUsable => Width >= WraithViewPreviewDimensions.Minimum
        && Height >= WraithViewPreviewDimensions.Minimum;

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static WraithViewRect FromRectangle(Rectangle rect)
    {
        return new WraithViewRect
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        };
    }
}

internal sealed class WraithViewClientPlacement
{
    public string Title { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

internal sealed class WraithViewHotkeyBinding
{
    public string CharacterName { get; set; } = "";
    public string Gesture { get; set; } = "";
    public List<string> Gestures { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        CharacterName = CharacterName.Trim();
        Gestures ??= new List<string>();
        Gestures = CleanGestureList(Gestures.Count > 0 ? Gestures : new[] { Gesture });
        Gesture = Gestures.FirstOrDefault() ?? "";
    }

    private static List<string> CleanGestureList(IEnumerable<string>? gestures)
    {
        return (gestures ?? Array.Empty<string>())
            .SelectMany(AhkGesture.SplitList)
            .Select(gesture => gesture.Trim().Replace("Ctrl+", "Control+", StringComparison.OrdinalIgnoreCase))
            .Where(gesture => gesture.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}

internal sealed class WraithViewCycleGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Cycle";
    public List<string> Characters { get; set; } = new();
    public string ForwardGesture { get; set; } = "";
    public string BackwardGesture { get; set; } = "";
    public List<string> ForwardGestures { get; set; } = new();
    public List<string> BackwardGestures { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = "Cycle";
        Id = IdFromName(Name);
        ForwardGestures ??= new List<string>();
        BackwardGestures ??= new List<string>();
        ForwardGestures = CleanGestureList(ForwardGestures.Count > 0 ? ForwardGestures : new[] { ForwardGesture });
        BackwardGestures = CleanGestureList(BackwardGestures.Count > 0 ? BackwardGestures : new[] { BackwardGesture });
        ForwardGesture = ForwardGestures.FirstOrDefault() ?? "";
        BackwardGesture = BackwardGestures.FirstOrDefault() ?? "";
        Characters = Characters
            .Select(character => character.Trim())
            .Where(character => character.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string IdFromName(string name)
    {
        var clean = new string((name ?? "")
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        clean = clean.Trim('-');
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N") : clean;
    }

    private static List<string> CleanGestureList(IEnumerable<string>? gestures)
    {
        return (gestures ?? Array.Empty<string>())
            .SelectMany(AhkGesture.SplitList)
            .Select(gesture => gesture.Trim().Replace("Ctrl+", "Control+", StringComparison.OrdinalIgnoreCase))
            .Where(gesture => gesture.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed class WraithViewHotkeyCommand
{
    public WraithViewHotkeyCommand(
        WraithViewHotkeyKind kind,
        string characterName,
        string groupId,
        int direction,
        IReadOnlyList<string>? characterNames = null)
    {
        Kind = kind;
        CharacterName = characterName;
        GroupId = groupId;
        Direction = direction;
        CharacterNames = characterNames ?? Array.Empty<string>();
    }

    public WraithViewHotkeyKind Kind { get; }
    public string CharacterName { get; }
    public string GroupId { get; }
    public int Direction { get; }
    public IReadOnlyList<string> CharacterNames { get; }
}

internal sealed record WraithViewCycleHotkeyRegistration(
    string Gesture,
    string GroupId,
    string GroupName,
    int Direction,
    string DirectionName);

internal static class WraithViewCycleHotkeyPlanner
{
    public static IReadOnlyList<WraithViewCycleHotkeyRegistration> Plan(
        WraithViewProfile profile,
        IReadOnlyDictionary<string, string>? preclaimedGestures,
        IList<string> failures)
    {
        var claimedGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (preclaimedGestures != null)
        {
            foreach (var (gesture, owner) in preclaimedGestures)
            {
                claimedGestures[AhkGesture.Identity(gesture)] = owner;
            }
        }

        var registrations = new List<WraithViewCycleHotkeyRegistration>();
        foreach (var group in profile.ActiveCycleGroups())
        {
            if (group.Characters.Count == 0 && profile.CharacterOrder.Count == 0) continue;
            AddGroupGestures(group, group.ForwardGestures, 1, "forward", claimedGestures, registrations, failures);
            AddGroupGestures(group, group.BackwardGestures, -1, "backward", claimedGestures, registrations, failures);
        }

        return registrations;
    }

    private static void AddGroupGestures(
        WraithViewCycleGroup group,
        IEnumerable<string> gestures,
        int direction,
        string directionName,
        IDictionary<string, string> claimedGestures,
        ICollection<WraithViewCycleHotkeyRegistration> registrations,
        ICollection<string> failures)
    {
        var owner = $"cycle group \"{group.Name}\" {directionName}";
        foreach (var gesture in gestures)
        {
            var identity = AhkGesture.Identity(gesture);
            if (claimedGestures.TryGetValue(identity, out var existingOwner))
            {
                failures.Add($"{gesture}: {owner} conflicts with {existingOwner}; only the first configured binding is active");
                continue;
            }

            claimedGestures[identity] = owner;
            registrations.Add(new WraithViewCycleHotkeyRegistration(
                gesture,
                group.Id,
                group.Name,
                direction,
                directionName));
        }
    }
}

internal enum WraithViewHotkeyKind
{
    Direct,
    Cycle,
}

internal static class WraithViewNativeMethods
{
    public const int WsExTopmost = 0x00000008;
    public const int WsExTransparent = 0x00000020;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WmClose = 0x0010;
    public const int WmNcHitTest = 0x0084;
    public const int WmSysCommand = 0x0112;
    public static readonly nint ScMinimize = new(0xF020);
    public static readonly nint HtTransparent = new(-1);
    public const int SwRestore = 9;
    public const int SwMinimize = 6;
    public const int SwMaximize = 3;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const int DwmTnpRectDestination = 0x00000001;
    public const int DwmTnpRectSource = 0x00000002;
    public const int DwmTnpOpacity = 0x00000004;
    public const int DwmTnpVisible = 0x00000008;
    public const int DwmTnpSourceClientAreaOnly = 0x00000010;
    public const uint EventSystemForeground = 0x0003;
    public const uint WinEventOutOfContext = 0x0000;
    public static readonly nint HwndTop = new(0);
    public static readonly nint HwndTopmost = new(-1);
    public static readonly nint HwndNotTopmost = new(-2);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    public delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventProc eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(nint hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
    private static extern bool GetWindowPlacementNative(nint hwnd, ref WindowPlacement placement);

    public static bool GetWindowPlacement(nint hwnd, out WindowPlacement placement)
    {
        placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
        };
        return GetWindowPlacementNative(hwnd, ref placement);
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);



    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public static bool IsKeyDown(uint virtualKey)
    {
        return (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
    }

    public static bool IsAnyControlDown()
    {
        return IsKeyDown((uint)Forms.Keys.ControlKey)
            || IsKeyDown((uint)Forms.Keys.LControlKey)
            || IsKeyDown((uint)Forms.Keys.RControlKey);
    }

    public static bool IsAnyAltDown()
    {
        return IsKeyDown((uint)Forms.Keys.Menu)
            || IsKeyDown((uint)Forms.Keys.LMenu)
            || IsKeyDown((uint)Forms.Keys.RMenu);
    }

    public static bool IsAnyShiftDown()
    {
        return IsKeyDown((uint)Forms.Keys.ShiftKey)
            || IsKeyDown((uint)Forms.Keys.LShiftKey)
            || IsKeyDown((uint)Forms.Keys.RShiftKey);
    }

    public static bool IsAnyWinDown()
    {
        return IsKeyDown((uint)Forms.Keys.LWin)
            || IsKeyDown((uint)Forms.Keys.RWin);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmRegisterThumbnail(nint destinationWindow, nint sourceWindow, out nint thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static NativeRect FromRectangle(Rectangle rect)
        {
            return new NativeRect
            {
                Left = rect.Left,
                Top = rect.Top,
                Right = rect.Right,
                Bottom = rect.Bottom,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmThumbnailProperties
    {
        public int Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Visible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SourceClientAreaOnly;
    }
}
