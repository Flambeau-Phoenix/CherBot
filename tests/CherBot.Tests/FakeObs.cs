using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CherBot.Integrations.Obs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace CherBot.Tests;

/// <summary>A small obs-websocket v5 server: password auth, canned responses, recorded requests, pushable events.</summary>
public sealed class FakeObsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private WebSocket? _socket;
    public string? Password { get; }
    public int Port { get; } = TestHost.FreeTcpPort();
    public ConcurrentQueue<(string Type, JsonElement Data)> Requests { get; } = new();
    public bool SceneItemEnabled { get; set; } = true;

    public FakeObsServer(string? password)
    {
        Password = password;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            _socket = ws;
            const string salt = "c2FsdA==", challenge = "Y2hhbGxlbmdl";
            await Send(ws, new
            {
                op = 0,
                d = new { obsWebSocketVersion = "5.5.0", rpcVersion = 1, authentication = password is null ? null : new { salt, challenge } },
            });
            while (ws.State == WebSocketState.Open)
            {
                var msg = await Receive(ws);
                if (msg is null) break;
                var root = msg.Value;
                var op = root.GetProperty("op").GetInt32();
                var d = root.GetProperty("d");
                if (op == 1)
                {
                    if (password is not null && d.GetProperty("authentication").GetString() != ObsClient.AuthString(password, salt, challenge))
                    {
                        await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed", CancellationToken.None);
                        return;
                    }
                    await Send(ws, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
                }
                else if (op == 6)
                {
                    var type = d.GetProperty("requestType").GetString()!;
                    var data = d.TryGetProperty("requestData", out var rd) ? rd.Clone() : default;
                    Requests.Enqueue((type, data));
                    object response = type switch
                    {
                        "GetVersion" => new { obsVersion = "31.0.0" },
                        "GetSceneItemId" => new { sceneItemId = 7 },
                        "GetSceneItemEnabled" => new { sceneItemEnabled = SceneItemEnabled },
                        "GetCurrentProgramScene" => new { currentProgramSceneName = "Main", sceneName = "Main" },
                        "GetSceneList" => new
                        {
                            currentProgramSceneName = "Main",
                            scenes = new[]
                            {
                                new { sceneName = "BRB", sceneIndex = 0 }, new { sceneName = "Game Over", sceneIndex = 1 },
                                new { sceneName = "Gameplay", sceneIndex = 2 }, new { sceneName = "Main", sceneIndex = 3 },
                            },
                        },
                        "GetSourceFilterList" => new { filters = new[] { new { filterName = "Blur" }, new { filterName = "Glow" } } },
                        "GetSceneItemList" or "GetGroupSceneItemList" => new { sceneItems = new[] { new { sceneItemId = 1 }, new { sceneItemId = 2 }, new { sceneItemId = 3 } } },
                        _ => new { },
                    };
                    var ok = type != "FailMe";
                    await Send(ws, new
                    {
                        op = 7,
                        d = new
                        {
                            requestType = type, requestId = d.GetProperty("requestId").GetString(),
                            requestStatus = new { result = ok, code = ok ? 100 : 600, comment = ok ? null : "nope" },
                            responseData = response,
                        },
                    });
                }
            }
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public Task EmitAsync(string eventType, object data) =>
        _socket is null ? Task.CompletedTask : Send(_socket, new { op = 5, d = new { eventType, eventIntent = 4, eventData = data } });

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static async Task Send(WebSocket ws, object payload)
    {
        await Lock.WaitAsync();
        try { await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { Lock.Release(); }
    }

    private static async Task<JsonElement?> Receive(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, CancellationToken.None); }
            catch (WebSocketException) { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Records Speaker.bot WebSocket requests and UDP commands.</summary>
public sealed class FakeSpeakerBot : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly System.Net.Sockets.UdpClient _udp;
    public int Port { get; } = TestHost.FreeTcpPort();
    public int UdpPort { get; }
    public ConcurrentQueue<JsonElement> WsRequests { get; } = new();
    public ConcurrentQueue<JsonElement> UdpCommands { get; } = new();

    public FakeSpeakerBot()
    {
        _udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        UdpPort = ((System.Net.IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await _udp.ReceiveAsync();
                    UdpCommands.Enqueue(JsonDocument.Parse(r.Buffer).RootElement.Clone());
                }
            }
            catch { /* disposed */ }
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                try { r = await ws.ReceiveAsync(buffer, CancellationToken.None); }
                catch (WebSocketException) { break; }
                if (r.MessageType == WebSocketMessageType.Close) break;
                WsRequests.Enqueue(JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone());
            }
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _udp.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
