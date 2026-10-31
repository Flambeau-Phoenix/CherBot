using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace CherBot.Tests;

/// <summary>Stands in for id.twitch.tv and api.twitch.tv/helix. Records every request.</summary>
public sealed class FakeTwitchHandler : HttpMessageHandler
{
    public sealed record Call(string Method, string Url, string? Body, string? Auth);

    public ConcurrentQueue<Call> Calls { get; } = new();
    public int PendingPolls { get; set; } = 1;
    public HashSet<string> RejectTokens { get; } = [];
    public string Login { get; set; } = "streamer";
    public string UserId { get; set; } = "1001";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var auth = request.Headers.Authorization?.Parameter;
        var url = request.RequestUri!.ToString();
        Calls.Enqueue(new Call(request.Method.Method, url, body, auth));
        var path = request.RequestUri.AbsolutePath;

        if (auth is not null && RejectTokens.Contains(auth)) return Json(HttpStatusCode.Unauthorized, new { message = "Invalid OAuth token" });

        switch (request.RequestUri.Host, path)
        {
            case ("id.twitch.tv", "/oauth2/device"):
                return Json(HttpStatusCode.OK, new { device_code = "dev-code", user_code = "ABCD-EFGH", verification_uri = "https://www.twitch.tv/activate", expires_in = 600, interval = 1 });
            case ("id.twitch.tv", "/oauth2/token"):
                if (body!.Contains("device_code") && PendingPolls-- > 0)
                    return Json(HttpStatusCode.BadRequest, new { status = 400, message = "authorization_pending" });
                return Json(HttpStatusCode.OK, new
                {
                    access_token = body.Contains("refresh_token") ? "refreshed-token" : "device-token",
                    refresh_token = "refresh-1", expires_in = 14400, token_type = "bearer",
                });
            case ("id.twitch.tv", "/oauth2/validate"):
                return Json(HttpStatusCode.OK, new { login = Login, user_id = UserId, scopes = new[] { "user:read:chat", "user:write:chat" }, expires_in = 14400 });
            case ("api.twitch.tv", "/helix/users"):
                return Json(HttpStatusCode.OK, new { data = new[] { new { id = UserId, login = Login, display_name = "Streamer", broadcaster_type = "affiliate", description = "", profile_image_url = "", created_at = "2020-01-01T00:00:00Z" } } });
            case ("api.twitch.tv", "/helix/chat/messages"):
                return Json(HttpStatusCode.OK, new { data = new[] { new { message_id = Guid.NewGuid().ToString(), is_sent = true } } });
            case ("api.twitch.tv", "/helix/eventsub/subscriptions") when request.Method == HttpMethod.Get:
                return Json(HttpStatusCode.OK, new { data = new[] { new { id = "stale-1" } } });
            case ("api.twitch.tv", "/helix/eventsub/subscriptions") when request.Method == HttpMethod.Post:
                return Json(HttpStatusCode.Accepted, new { data = new[] { new { id = Guid.NewGuid().ToString(), status = "enabled" } } });
            case ("api.twitch.tv", "/helix/eventsub/subscriptions") when request.Method == HttpMethod.Delete:
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return Json(HttpStatusCode.OK, new { data = Array.Empty<object>() });
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

/// <summary>A tiny EventSub WebSocket server: sends session_welcome, then whatever the test queues.</summary>
public sealed class FakeEventSubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public BlockingCollection<string> Outbox { get; } = new();
    public int Port { get; } = TestHost.FreeTcpPort();
    public string Url => $"ws://127.0.0.1:{Port}/ws";
    public int Connections;

    public FakeEventSubServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            Interlocked.Increment(ref Connections);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await Send(ws, Message("session_welcome", new { session = new { id = "session-1", status = "connected", keepalive_timeout_seconds = 10, reconnect_url = (string?)null } }));
            while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
            {
                if (Outbox.TryTake(out var next, 200)) await Send(ws, next);
            }
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public static string Message(string type, object payload, string? subscriptionType = null) => JsonSerializer.Serialize(new
    {
        metadata = new { message_id = Guid.NewGuid().ToString(), message_type = type, message_timestamp = DateTimeOffset.UtcNow, subscription_type = subscriptionType },
        payload,
    });

    public void Notify(string subscriptionType, object ev) =>
        Outbox.Add(Message("notification", new { subscription = new { id = "sub", type = subscriptionType, version = "1" }, @event = ev }, subscriptionType));

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
