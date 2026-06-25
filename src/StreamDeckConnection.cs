using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MsTeamsLocal;

/// <summary>A single inbound Stream Deck event we care about.</summary>
public sealed record SdEvent(string Event, string? Action, string? Context, JsonElement? Settings = null);

/// <summary>
/// Minimal Stream Deck plugin transport: connects to the Stream Deck software over
/// a local WebSocket, registers, dispatches inbound events, and sends commands.
/// Uses only the BCL (ClientWebSocket + System.Text.Json) — no external SDK.
/// </summary>
public sealed class StreamDeckConnection : IDisposable
{
    private readonly int _port;
    private readonly string _pluginUuid;
    private readonly string _registerEvent;
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<SdEvent>? EventReceived;

    public StreamDeckConnection(int port, string pluginUuid, string registerEvent)
    {
        _port = port;
        _pluginUuid = pluginUuid;
        _registerEvent = registerEvent;
    }

    public async Task ConnectAndRegisterAsync(CancellationToken ct)
    {
        var uri = new Uri($"ws://127.0.0.1:{_port}");
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        await SendRawAsync(new { @event = _registerEvent, uuid = _pluginUuid }, ct).ConfigureAwait(false);
        Log.Info($"registered with Stream Deck on port {_port}");
    }

    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Info("Stream Deck closed the socket");
                    return;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
        }
    }

    private void Dispatch(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evtProp)) return;

            var evt = evtProp.GetString() ?? "";
            var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            var context = root.TryGetProperty("context", out var c) ? c.GetString() : null;

            // Carry the action's settings (cloned so it survives doc disposal).
            JsonElement? settings = null;
            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("settings", out var s))
            {
                settings = s.Clone();
            }

            EventReceived?.Invoke(new SdEvent(evt, action, context, settings));
        }
        catch (Exception ex)
        {
            Log.Error("failed to parse inbound message", ex);
        }
    }

    // ---- Outbound commands --------------------------------------------------

    public Task SetImageAsync(string context, string dataUri) =>
        SendRawAsync(new
        {
            @event = "setImage",
            context,
            payload = new { image = dataUri, target = 0 }
        });

    public Task ShowAlertAsync(string context) =>
        SendRawAsync(new { @event = "showAlert", context });

    public Task ShowOkAsync(string context) =>
        SendRawAsync(new { @event = "showOk", context });

    private async Task SendRawAsync(object message, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
