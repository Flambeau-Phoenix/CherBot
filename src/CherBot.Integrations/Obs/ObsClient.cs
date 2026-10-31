using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CherBot.Core;

namespace CherBot.Integrations.Obs;

public sealed class ObsRequestException(string requestType, int code, string? comment)
    : Exception($"OBS {requestType} failed ({code}){(string.IsNullOrEmpty(comment) ? "" : ": " + comment)}")
{
    public int Code { get; } = code;
}

/// <summary>
/// obs-websocket v5 client: Hello / Identify (salted SHA-256 auth), requests matched by id, and events.
/// One instance per connection; not reusable after <see cref="DisposeAsync"/>.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    /// <summary>All non-high-volume event categories (General … UI).</summary>
    public const int EventSubscriptionAll = 0x7FF;

    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    public event Action<string, JsonElement>? EventReceived;
    public event Action<string?>? Closed;

    public string? ObsVersion { get; private set; }
    public string? WebSocketVersion { get; private set; }
    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, string? password, CancellationToken ct)
    {
        await _ws.ConnectAsync(uri, ct);
        var hello = await ReceiveOneAsync(ct);
        if (hello.GetProperty("op").GetInt32() != 0) throw new InvalidOperationException("OBS did not send Hello");
        var d = hello.GetProperty("d");
        WebSocketVersion = d.TryGetProperty("obsWebSocketVersion", out var v) ? v.GetString() : null;

        var identify = new Dictionary<string, object?> { ["rpcVersion"] = 1, ["eventSubscriptions"] = EventSubscriptionAll };
        if (d.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("OBS requires a password");
            identify["authentication"] = AuthString(password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
        }
        await SendAsync(new { op = 1, d = identify }, ct);

        var identified = await ReceiveOneAsync(ct);
        if (identified.GetProperty("op").GetInt32() != 2) throw new InvalidOperationException("OBS identification failed");
        _receiveLoop = Task.Run(ReceiveLoopAsync);

        try
        {
            var version = await RequestAsync("GetVersion", null, ct);
            ObsVersion = version.TryGetProperty("obsVersion", out var ov) ? ov.GetString() : null;
        }
        catch (ObsRequestException) { /* informational only */ }
    }

    public static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Sends a request and returns its responseData (an empty object when OBS returns none).</summary>
    public async Task<JsonElement> RequestAsync(string requestType, object? requestData, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var d = new Dictionary<string, object?> { ["requestType"] = requestType, ["requestId"] = id };
            if (requestData is not null) d["requestData"] = requestData;
            await SendAsync(new { op = 6, d }, ct);
            var response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), ct);
            var status = response.GetProperty("requestStatus");
            if (!status.GetProperty("result").GetBoolean())
                throw new ObsRequestException(requestType, status.GetProperty("code").GetInt32(),
                    status.TryGetProperty("comment", out var c) ? c.GetString() : null);
            return response.TryGetProperty("responseData", out var data) ? data.Clone() : JsonDocument.Parse("{}").RootElement;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        string? reason = null;
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveOneAsync(_cts.Token);
                var op = msg.GetProperty("op").GetInt32();
                var d = msg.GetProperty("d");
                switch (op)
                {
                    case 5:
                        EventReceived?.Invoke(d.GetProperty("eventType").GetString()!,
                            d.TryGetProperty("eventData", out var data) ? data.Clone() : JsonDocument.Parse("{}").RootElement);
                        break;
                    case 7:
                        if (_pending.TryGetValue(d.GetProperty("requestId").GetString()!, out var tcs)) tcs.TrySetResult(d.Clone());
                        break;
                }
            }
            reason = _ws.CloseStatusDescription;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            reason = ex.Message;
        }
        foreach (var p in _pending.Values) p.TrySetException(new WebSocketException("OBS connection closed"));
        Closed?.Invoke(reason);
    }

    private async Task<JsonElement> ReceiveOneAsync(CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException($"OBS closed the connection: {(int?)_ws.CloseStatus} {_ws.CloseStatusDescription}");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, CherBotJson.Options);
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* closing anyway */ }
        if (_receiveLoop is not null) await Task.WhenAny(_receiveLoop, Task.Delay(2000));
        _ws.Dispose();
        _cts.Dispose();
    }
}
