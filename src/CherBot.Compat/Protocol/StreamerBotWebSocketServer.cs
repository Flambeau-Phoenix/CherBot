using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CherBot.Compat.Protocol;

/// <summary>
/// Streamer.bot-compatible WebSocket server: Hello (with optional salt/challenge auth), request/response by id,
/// and event push for subscribed source/type pairs.
/// </summary>
public sealed class StreamerBotWebSocketServer(StreamerBotApi api, EventBus bus, ILogger<StreamerBotWebSocketServer> log) : CherBot.Core.Platform.IWebSocketBroadcaster
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public int SessionCount => _sessions.Count;

    public async Task BroadcastAsync(string text)
    {
        foreach (var s in _sessions.Values)
        {
            try { await s.SendRawAsync(text); }
            catch (Exception ex) { log.LogDebug(ex, "Broadcast to {Session} failed", s.Id); }
        }
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        var endpoint = "/" + api.Settings.Endpoint.Trim('/');
        var path = "/" + (ctx.Request.Path.Value ?? "").Trim('/');
        if (!string.Equals(path, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await ctx.Response.WriteAsync("Streamer.bot-compatible WebSocket endpoint");
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new Session(socket, string.IsNullOrEmpty(api.Settings.Password));
        _sessions[session.Id] = session;
        log.LogInformation("WebSocket client {Session} connected from {Remote}", session.Id, ctx.Connection.RemoteIpAddress);

        var (reader, sub) = bus.Listen($"sbws-{session.Id}", session.IsSubscribed);
        var pump = Task.Run(async () =>
        {
            await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
                await session.SendAsync(StreamerBotApi.EventEnvelope(ev));
        });

        try
        {
            var hello = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.UtcNow,
                ["session"] = session.Id,
                ["request"] = "Hello",
                ["info"] = api.Info("websocketServer"),
            };
            if (!string.IsNullOrEmpty(api.Settings.Password))
                hello["authentication"] = new { salt = session.Salt, challenge = session.Challenge };
            await session.SendAsync(hello);

            await ReceiveLoopAsync(session, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            log.LogDebug(ex, "WebSocket session {Session} ended", session.Id);
        }
        finally
        {
            sub.Dispose();
            _sessions.TryRemove(session.Id, out _);
            log.LogInformation("WebSocket client {Session} disconnected", session.Id);
            try { await pump; } catch { /* closing */ }
        }
    }

    private async Task ReceiveLoopAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (session.Socket.State == WebSocketState.Open)
        {
            var result = await session.Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > 4 * 1024 * 1024)
            {
                await session.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                return;
            }
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException)
            {
                await session.SendAsync(new { status = "error", error = "Invalid JSON" });
                continue;
            }
            using (doc)
            {
                var response = await HandleRequestAsync(session, doc.RootElement, ct);
                if (response is not null) await session.SendAsync(response);
            }
        }
    }

    internal async Task<Dictionary<string, object?>?> HandleRequestAsync(Session session, JsonElement req, CancellationToken ct)
    {
        var id = req.TryGetProperty("id", out var idEl) ? (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText()) : null;
        var request = req.TryGetProperty("request", out var r) ? r.GetString() ?? "" : "";

        Dictionary<string, object?> Ok(object? body = null)
        {
            var d = new Dictionary<string, object?> { ["id"] = id, ["status"] = "ok" };
            if (body is not null)
                foreach (var p in JsonSerializer.SerializeToElement(body, CherBotJson.Options).EnumerateObject())
                    d[p.Name] = p.Value;
            return d;
        }
        Dictionary<string, object?> Error(string error) => new() { ["id"] = id, ["status"] = "error", ["error"] = error };
        Dictionary<string, object?> From(SbResult res) => res.Ok ? Ok(res.Body) : Error(res.Error ?? "error");

        string Str(string name) => req.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Bool(string name, bool fallback) => req.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;
        JsonElement? El(string name) => req.TryGetProperty(name, out var v) ? v : null;
        string? Variable() => req.TryGetProperty("variable", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (request == "Authenticate")
        {
            if (string.IsNullOrEmpty(api.Settings.Password)) return Ok();
            if (session.Verify(api.Settings.Password, Str("authentication"))) return Ok();
            return Error("Authentication failed");
        }

        var needsAuth = !session.Authenticated && !string.IsNullOrEmpty(api.Settings.Password) &&
                        (api.Settings.EnforceAuth || request == "SendMessage");
        if (needsAuth) return Error("Authentication required");

        try
        {
            switch (request)
            {
                case "GetInfo": return Ok(new { info = api.Info("websocketServer") });
                case "GetEvents": return Ok(new { events = api.Events() });
                case "Subscribe":
                    session.Subscribe(El("events"));
                    return Ok(new { events = session.Subscriptions() });
                case "UnSubscribe":
                    session.Unsubscribe(El("events"));
                    return Ok(new { events = session.Subscriptions() });
                case "GetActions": return Ok(api.GetActions());
                case "DoAction": return From(api.DoAction(El("action"), El("args"), "websocket"));
                case "GetCommands": return Ok(api.GetCommands());
                case "GetGlobals": return Ok(api.GetGlobals(Bool("persisted", true)));
                case "GetGlobal": return From(api.GetGlobal(Variable() ?? "", Bool("persisted", true)));
                case "GetBroadcaster": return Ok(api.GetBroadcaster());
                case "GetActiveViewers": return Ok(api.GetActiveViewers());
                case "GetCodeTriggers": return Ok(api.GetCodeTriggers());
                case "ExecuteCodeTrigger":
                    api.ExecuteCodeTrigger(Str("triggerName"), El("args"));
                    return Ok();
                case "SendMessage":
                    return From(await api.SendMessageAsync(Str("platform") is { Length: > 0 } p ? p : "twitch", Str("message"),
                        Bool("bot", false), Bool("internal", true), Str("replyId") is { Length: > 0 } reply ? reply : null, ct));
                case "GetCredits":
                case "TestCredits": return Ok(StreamerBotApi.EmptyCredits());
                case "ClearCredits": return Ok();
                case "GetMonitoredYouTubeBroadcasts": return Ok(new { broadcasts = Array.Empty<object>(), count = 0 });
                case "TwitchGetEmotes":
                    return Ok(new { emotes = new { userEmotes = Array.Empty<object>(), bttvEmotes = Array.Empty<object>(), ffzEmotes = Array.Empty<object>(), sevenTvEmotes = Array.Empty<object>() } });
                case "YouTubeGetEmotes":
                    return Ok(new { emotes = new { userEmotes = Array.Empty<object>(), bttvEmotes = Array.Empty<object>(), sevenTvEmotes = Array.Empty<object>() } });
                case "GetUserPronouns": return Error("Pronouns are not supported");
                default:
                    foreach (var platform in new[] { "Twitch", "YouTube", "Kick", "Trovo" })
                    {
                        if (request == $"{platform}GetUserGlobals")
                            return Ok(api.GetUserGlobals(platform.ToLowerInvariant(), Variable(), Bool("persisted", true)));
                        if (request == $"{platform}GetUserGlobal")
                            return Ok(api.GetUserGlobal(platform.ToLowerInvariant(), Str("userId"), Variable(), Bool("persisted", true)));
                    }
                    return Error($"Unknown request '{request}'");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WebSocket request {Request} failed", request);
            return Error(ex.Message);
        }
    }

    internal sealed class Session
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, HashSet<string>> _subs = new(StringComparer.OrdinalIgnoreCase);

        public Session(WebSocket socket, bool authenticated)
        {
            Socket = socket;
            Authenticated = authenticated;
        }

        public string Id { get; } = Guid.NewGuid().ToString();
        public WebSocket Socket { get; }
        public bool Authenticated { get; private set; }
        public string Salt { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string Challenge { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        public bool Verify(string password, string authentication)
        {
            var secret = Sha256Base64(password + Salt);
            var expected = Sha256Base64(secret + Challenge);
            Authenticated = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(authentication ?? ""));
            return Authenticated;
        }

        public static string Sha256Base64(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

        public void Subscribe(JsonElement? events)
        {
            if (events is not { ValueKind: JsonValueKind.Object } o) return;
            foreach (var p in o.EnumerateObject())
            {
                var set = _subs.GetOrAdd(p.Name, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set)
                {
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var t in p.Value.EnumerateArray()) set.Add(t.GetString() ?? "");
                    else if (p.Value.ValueKind == JsonValueKind.String) set.Add(p.Value.GetString() ?? "");
                }
            }
        }

        public void Unsubscribe(JsonElement? events)
        {
            if (events is { ValueKind: JsonValueKind.String } s && s.GetString() == "*")
            {
                _subs.Clear();
                return;
            }
            if (events is not { ValueKind: JsonValueKind.Object } o) return;
            foreach (var p in o.EnumerateObject())
            {
                if (!_subs.TryGetValue(p.Name, out var set)) continue;
                lock (set)
                    foreach (var t in p.Value.EnumerateArray()) set.Remove(t.GetString() ?? "");
            }
        }

        public Dictionary<string, List<string>> Subscriptions() =>
            _subs.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => { lock (kv.Value) return kv.Value.ToList(); });

        public bool IsSubscribed(BotEvent ev)
        {
            if (!_subs.TryGetValue(ev.Source, out var set)) return false;
            lock (set) return set.Contains(ev.Type) || set.Contains("*");
        }

        public Task SendAsync(object payload) => SendBytesAsync(JsonSerializer.SerializeToUtf8Bytes(payload, CherBotJson.Options));

        public Task SendRawAsync(string text) => SendBytesAsync(Encoding.UTF8.GetBytes(text));

        private async Task SendBytesAsync(byte[] bytes)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
