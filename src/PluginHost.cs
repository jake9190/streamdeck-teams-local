using System.Collections.Concurrent;
using System.Text.Json;

namespace MsTeamsLocal;

/// <summary>
/// Coordinates the Stream Deck connection, the Teams automation engine, and icon
/// rendering. Keeps a map of visible keys and repaints them whenever Teams state changes.
/// </summary>
public sealed class PluginHost
{
    private readonly StreamDeckConnection _sd;
    private readonly TeamsAutomation _teams;

    // context -> descriptor for every currently-visible key.
    private readonly ConcurrentDictionary<string, ActionDescriptor> _visible = new();

    // context -> configured icon type for visible Audio Device keys ("speaker"/"headset"/null).
    private readonly ConcurrentDictionary<string, string?> _audioIconType = new();

    public PluginHost(StreamDeckConnection sd, TeamsAutomation teams)
    {
        _sd = sd;
        _teams = teams;
        _sd.EventReceived += OnEvent;
        _teams.SnapshotChanged += OnSnapshotChanged;
    }

    private void OnEvent(SdEvent e)
    {
        switch (e.Event)
        {
            case "willAppear":
                if (e.Context is not null && ActionCatalog.Resolve(e.Action ?? "") is { } d)
                {
                    _visible[e.Context] = d;
                    if (d.Kind == ActionKind.AudioDevice)
                        _audioIconType[e.Context] = ReadSetting(e.Settings, "iconType");
                    _ = RenderAsync(e.Context, d, _teams.Current);
                }
                break;

            case "didReceiveSettings":
                if (e.Context is not null && ActionCatalog.Resolve(e.Action ?? "") is { } updated)
                {
                    if (updated.Kind == ActionKind.AudioDevice)
                        _audioIconType[e.Context] = ReadSetting(e.Settings, "iconType");
                    _ = RenderAsync(e.Context, updated, _teams.Current);
                }
                break;

            case "willDisappear":
                if (e.Context is not null)
                {
                    _visible.TryRemove(e.Context, out _);
                    _audioIconType.TryRemove(e.Context, out _);
                }
                break;

            case "keyDown":
                if (e.Context is not null && ActionCatalog.Resolve(e.Action ?? "") is { } pressed)
                    _ = HandleKeyDownAsync(e.Context, pressed, e.Settings);
                break;

            case "sendToPlugin":
                if (e.Context is not null)
                    _ = HandleSendToPluginAsync(e.Action, e.Context, e.Payload);
                break;
        }
    }

    /// <summary>
    /// Responds to a Property Inspector request for the active Windows audio devices, so the
    /// Audio Device action can offer mic/speaker dropdowns.
    /// </summary>
    private async Task HandleSendToPluginAsync(string? action, string context, JsonElement? payload)
    {
        try
        {
            if (payload is not JsonElement p || p.ValueKind != JsonValueKind.Object) return;
            if (!p.TryGetProperty("request", out var r) || r.GetString() != "audioDevices") return;

            var endpoints = await Task.Run(() => AudioEndpoints.Enumerate());
            var mics = endpoints.Where(e => e.IsCapture).Select(e => new { id = e.Id, name = e.Name }).ToArray();
            var speakers = endpoints.Where(e => !e.IsCapture).Select(e => new { id = e.Id, name = e.Name }).ToArray();
            await _sd.SendToPropertyInspectorAsync(context, action ?? "", new { kind = "audioDevices", mics, speakers });
        }
        catch (Exception ex) { Log.Error("audio device request failed", ex); }
    }

    /// <summary>Per-button "restore previous window focus" setting; defaults to true when unset.</summary>
    private static bool GetRestoreFocus(JsonElement? settings)
    {
        if (settings is JsonElement s && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty("restoreFocus", out var v))
        {
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.True) return true;
        }
        return true;
    }

    /// <summary>Reads a string setting, treating empty/absent as null.</summary>
    private static string? ReadSetting(JsonElement? settings, string name)
    {
        if (settings is JsonElement s && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var str = v.GetString();
            return string.IsNullOrEmpty(str) ? null : str;
        }
        return null;
    }

    private async Task HandleKeyDownAsync(string context, ActionDescriptor d, JsonElement? settings)
    {
        var snap = _teams.Current;
        if (!ActionCatalog.IsActionable(d, snap))
        {
            await _sd.ShowAlertAsync(context);
            return;
        }

        // Audio Device: switch the meeting's mic and/or speaker to the pre-selected devices.
        if (d.Kind == ActionKind.AudioDevice)
        {
            string? micId = ReadSetting(settings, "micId");
            string? speakerId = ReadSetting(settings, "speakerId");
            bool switched = await Task.Run(() => _teams.SetAudioDevices(micId, speakerId));
            if (switched) await _sd.ShowOkAsync(context); else await _sd.ShowAlertAsync(context);
            return;
        }

        // Toggles: reflect the expected new state immediately (no waiting for a poll).
        if (d.Kind is ActionKind.ToggleMute or ActionKind.ToggleCamera or ActionKind.RaiseHand)
            _teams.ApplyOptimistic(d);

        bool ok = await Task.Run(() => _teams.Trigger(d, GetRestoreFocus(settings)));
        if (!ok)
        {
            await _sd.ShowAlertAsync(context);
            return;
        }

        if (d.Kind == ActionKind.Reaction)
            await _sd.ShowOkAsync(context);

        // Toggles change observable state, so run fast confirmation polls to reconcile
        // the key quickly. Reactions don't change state, so skip the extra polls and
        // keep UIA free for rapid repeat presses.
        if (d.Kind is ActionKind.ToggleMute or ActionKind.ToggleCamera
            or ActionKind.RaiseHand or ActionKind.ShareScreen)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(180);
                _teams.PollNow();
                await Task.Delay(400);
                _teams.PollNow();
            });
        }
    }

    private void OnSnapshotChanged(TeamsSnapshot snap)
    {
        foreach (var kv in _visible)
            _ = RenderAsync(kv.Key, kv.Value, snap);
    }

    private async Task RenderAsync(string context, ActionDescriptor d, TeamsSnapshot snap)
    {
        try
        {
            string? iconType = d.Kind == ActionKind.AudioDevice
                ? _audioIconType.GetValueOrDefault(context)
                : null;
            var image = IconRenderer.Render(d, snap, iconType);
            await _sd.SetImageAsync(context, image);
        }
        catch (Exception ex)
        {
            Log.Error($"render failed for {d.Id}", ex);
        }
    }
}
