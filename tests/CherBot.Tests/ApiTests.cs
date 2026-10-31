using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CherBot.Core.Config;
using CherBot.Core.Model;
using CherBot.Core.State;
using CherBot.Storage;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class ApiTests
{
    private static ActionDef EchoAction(TestHost h)
    {
        var a = new ActionDef
        {
            Name = "Compat Echo",
            SubActions =
            [
                SetArg("echo", "%from%"),
                Sa(1034, new { eventName = "compat-ping", useArgs = true }),
            ],
        };
        for (var i = 0; i < a.SubActions.Count; i++) a.SubActions[i].Index = i;
        return h.Get<ConfigService>().SaveAction(a);
    }

    [Fact]
    public async Task Health_is_public_and_v1_requires_the_token()
    {
        await using var h = new TestHost();
        using var anon = new HttpClient { BaseAddress = h.Api.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/v1/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Api.GetAsync("/v1/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/v1/actions?token={h.Token}")).StatusCode);
    }

    [Fact]
    public async Task Actions_can_be_created_run_and_listed_over_rest()
    {
        await using var h = new TestHost();
        var create = await h.Api.PostAsJsonAsync("/v1/actions", new
        {
            name = "rest action",
            subActions = new object[] { new { type = 123, index = 0, variableName = "greeting", value = "hi %who%" } },
        });
        create.EnsureSuccessStatusCode();
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var run = await h.Api.PostAsJsonAsync($"/v1/actions/{id}/run", new { args = new { who = "chat" }, wait = true });
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal("hi chat", result.GetProperty("args").GetProperty("greeting").GetString());

        var runs = await h.Api.GetFromJsonAsync<JsonElement>("/v1/runs");
        Assert.True(runs.GetProperty("runs").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Sse_replays_events_from_a_cursor()
    {
        await using var h = new TestHost();
        var before = h.Get<CherBot.Core.Events.EventBus>().LastSeq;
        h.Get<VariableService>().SetGlobal("sseCheck", "yes");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await h.Api.GetStreamAsync($"/v1/events?since={before}", cts.Token);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            if (line.StartsWith("data:") && line.Contains("sseCheck")) break;
        Assert.NotNull(line);
        Assert.Contains("GlobalVariableCreated", line);
    }

    [Fact]
    public async Task WebSocket_hello_requests_and_events_follow_the_streamerbot_shape()
    {
        await using var h = new TestHost();
        var action = EchoAction(h);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{h.WsPort}/"), CancellationToken.None);

        var hello = await Receive(ws);
        Assert.Equal("Hello", hello.GetProperty("request").GetString());
        Assert.Equal("test-instance", hello.GetProperty("info").GetProperty("name").GetString());
        Assert.False(hello.TryGetProperty("authentication", out _));

        await Send(ws, new { request = "Subscribe", id = "s1", events = new { Custom = new[] { "Event" } } });
        var sub = await ReceiveId(ws, "s1");
        Assert.Equal("ok", sub.GetProperty("status").GetString());

        await Send(ws, new { request = "DoAction", id = "d1", action = new { name = "compat echo" }, args = new { from = "raw-ws" } });
        Assert.Equal("ok", (await ReceiveId(ws, "d1")).GetProperty("status").GetString());

        JsonElement ev;
        do ev = await Receive(ws);
        while (!ev.TryGetProperty("event", out _));
        Assert.Equal("Custom", ev.GetProperty("event").GetProperty("source").GetString());
        Assert.Equal("Event", ev.GetProperty("event").GetProperty("type").GetString());
        Assert.Equal("raw-ws", ev.GetProperty("data").GetProperty("args").GetProperty("echo").GetString());

        await Send(ws, new { request = "Nope", id = "x" });
        Assert.Equal("error", (await ReceiveId(ws, "x")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task WebSocket_auth_uses_salt_and_challenge_and_protects_send_message()
    {
        await using var h = new TestHost(wsPassword: "s3cret");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{h.WsPort}/"), CancellationToken.None);
        var hello = await Receive(ws);
        var auth = hello.GetProperty("authentication");
        var salt = auth.GetProperty("salt").GetString()!;
        var challenge = auth.GetProperty("challenge").GetString()!;

        await Send(ws, new { request = "SendMessage", id = "m0", platform = "twitch", message = "x" });
        Assert.Equal("Authentication required", (await ReceiveId(ws, "m0")).GetProperty("error").GetString());

        await Send(ws, new { request = "Authenticate", id = "bad", authentication = "wrong" });
        Assert.Equal("error", (await ReceiveId(ws, "bad")).GetProperty("status").GetString());

        static string H(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        await Send(ws, new { request = "Authenticate", id = "a1", authentication = H(H("s3cret" + salt) + challenge) });
        Assert.Equal("ok", (await ReceiveId(ws, "a1")).GetProperty("status").GetString());

        await Send(ws, new { request = "SendMessage", id = "m1", platform = "twitch", message = "x" });
        var sent = await ReceiveId(ws, "m1");
        Assert.NotEqual("Authentication required", sent.TryGetProperty("error", out var e) ? e.GetString() : null);
    }

    [Fact]
    public async Task Http_and_udp_servers_run_actions()
    {
        await using var h = new TestHost();
        var action = EchoAction(h);
        var db = h.Get<CherBotDb>();

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync($"http://127.0.0.1:{h.HttpPort}/DoAction", new { action = new { id = action.Id }, args = new { from = "http" } });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var list = await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{h.HttpPort}/GetActions");
        Assert.Contains(list.GetProperty("actions").EnumerateArray(), a => a.GetProperty("id").GetString() == action.Id.ToString());

        using var udp = new UdpClient();
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request = "DoAction", action = new { name = "Compat Echo" }, args = new { from = "udp" } }));
        await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, h.UdpPort));

        await WaitUntil(() =>
        {
            var runs = db.GetRuns(10, action.Id.ToString());
            return runs.Any(r => r.Json.Contains("\"http\"")) && runs.Any(r => r.Json.Contains("\"udp\""));
        });
    }

    [Fact]
    public async Task Official_streamerbot_client_works_against_cherbot()
    {
        var clientDir = FindCompatClient();
        if (clientDir is null || FindNode() is not { } node)
        {
            // The CI workflow installs these; skip quietly on machines without Node.
            return;
        }

        await using var h = new TestHost();
        var action = EchoAction(h);
        h.Get<VariableService>().SetGlobal("compatGlobal", "hello");
        h.Get<ConfigService>().SaveCommand(new CommandDef { Name = "hello", Command = "!hello" });

        var psi = new ProcessStartInfo(node, $"compat-test.mjs {h.WsPort} {action.Id}")
        {
            WorkingDirectory = clientDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(60_000), "compat client timed out");
        var output = await stdout + await stderr;
        Assert.True(proc.ExitCode == 0, "official client checks failed:\n" + output);
        Assert.Contains("\"name\":\"DoAction customEventResponse\",\"ok\":true", output);
    }

    internal static string? FindCompatClient()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "tests", "compat-client");
            if (File.Exists(Path.Combine(candidate, "node_modules", "@streamerbot", "client", "package.json"))) return candidate;
        }
        return null;
    }

    internal static string? FindNode() =>
        CherBot.Core.Platform.CommandLineSoundPlayer.FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");

    private static Task Send(ClientWebSocket ws, object payload) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Receive(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[256 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveId(ClientWebSocket ws, string id)
    {
        while (true)
        {
            var msg = await Receive(ws);
            if (msg.TryGetProperty("id", out var i) && i.GetString() == id) return msg;
        }
    }
}
