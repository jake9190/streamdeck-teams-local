using System.Collections.Concurrent;

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
                    _ = RenderAsync(e.Context, d, _teams.Current);
                }
                break;

            case "willDisappear":
                if (e.Context is not null)
                    _visible.TryRemove(e.Context, out _);
                break;

            case "keyDown":
                if (e.Context is not null && ActionCatalog.Resolve(e.Action ?? "") is { } pressed)
                    _ = HandleKeyDownAsync(e.Context, pressed);
                break;
        }
    }

    private async Task HandleKeyDownAsync(string context, ActionDescriptor d)
    {
        var snap = _teams.Current;
        if (!snap.TeamsRunning || !snap.MeetingActive)
        {
            await _sd.ShowAlertAsync(context);
            return;
        }

        // Toggles: reflect the expected new state immediately (no waiting for a poll).
        if (d.Kind is ActionKind.ToggleMute or ActionKind.ToggleCamera or ActionKind.RaiseHand)
            _teams.ApplyOptimistic(d);

        bool ok = await Task.Run(() => _teams.Trigger(d));
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
            var image = IconRenderer.Render(d, snap);
            await _sd.SetImageAsync(context, image);
        }
        catch (Exception ex)
        {
            Log.Error($"render failed for {d.Id}", ex);
        }
    }
}
