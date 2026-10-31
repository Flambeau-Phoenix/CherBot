using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.SpeakerBot;

public sealed class SpeakerBotOptions
{
    public const string Section = "CherBot:SpeakerBot";

    public bool Enabled { get; set; }
    /// <summary>Speaker.bot's WebSocket server address (bind it to the LAN or VPN IP on the PC and firewall it to this host).</summary>
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7580;
    public string Endpoint { get; set; } = "/";
    /// <summary>Speaker.bot's UDP port is fixed at 6669; configurable only for tests.</summary>
    public int UdpPort { get; set; } = 6669;
    public int RetrySeconds { get; set; } = 10;
}

/// <summary>Drives Speaker.bot (Windows TTS) over its WebSocket API, with UDP for the commands that only exist there.</summary>
public sealed class SpeakerBotService(SpeakerBotOptions options, EventBus bus, ILogger<SpeakerBotService> log) : BackgroundService, IIntegrationStatus, IEventCatalog
{
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile string _state = "disabled";
    private string? _error;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                _state = "connecting";
                await ws.ConnectAsync(new Uri($"ws://{options.Host}:{options.Port}/{options.Endpoint.Trim('/')}"), stoppingToken);
                _ws = ws;
                _state = "connected";
                _error = null;
                log.LogInformation("Speaker.bot connected at {Host}:{Port}", options.Host, options.Port);
                bus.Publish(BotEvent.FromTypeName("SpeakerBotConnected", new Dictionary<string, object?>()));
                var buffer = new byte[16 * 1024];
                while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, stoppingToken);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                log.LogDebug(ex, "Speaker.bot connection failed");
            }
            finally
            {
                var was = _state == "connected";
                _ws = null;
                _state = "disconnected";
                ws.Dispose();
                if (was) bus.Publish(BotEvent.FromTypeName("SpeakerBotDisconnected", new Dictionary<string, object?>()));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, options.RetrySeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Sends a WebSocket request ({id, request, ...}).</summary>
    public async Task RequestAsync(string request, IDictionary<string, object?>? fields, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) throw new InvalidOperationException("Speaker.bot is not connected");
        var payload = new Dictionary<string, object?>(fields ?? new Dictionary<string, object?>()) { ["id"] = Guid.NewGuid().ToString(), ["request"] = request };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, CherBotJson.Options);
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Sends a UDP command ({command, ...}) to Speaker.bot's fixed UDP port.</summary>
    public async Task UdpAsync(string command, IDictionary<string, object?>? fields, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(fields ?? new Dictionary<string, object?>()) { ["command"] = command };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, CherBotJson.Options);
        using var udp = new UdpClient();
        await udp.SendAsync(bytes, options.Host, options.UdpPort, ct);
    }

    public IntegrationStatus GetStatus() =>
        new("speakerbot", options.Enabled ? _state : "disabled", IsConnected, _error, new Dictionary<string, object?>
        {
            ["host"] = options.Host, ["port"] = options.Port, ["udpPort"] = options.UdpPort,
        });

    public IEnumerable<int> EmittedEventTypes => [23001, 23002];
}

/// <summary>Speaker.bot sub-actions (Streamer.bot type ids 601–607).</summary>
public sealed class SpeakerBotHandlers : SubActionHandlerGroup
{
    public SpeakerBotHandlers()
    {
        On(602, async (s, c) =>
        {
            var sb = c.Service<SpeakerBotService>();
            var fields = new Dictionary<string, object?>
            {
                ["voice"] = c.Parse(s.Str("alias")), ["message"] = c.Parse(s.Str("message")), ["badWordFilter"] = s.Bool("badWordFilter"),
            };
            if (sb.IsConnected) await sb.RequestAsync("Speak", fields, c.Cancellation);
            else await sb.UdpAsync("speak", fields, c.Cancellation);
        });
        On(601, async (s, c) => await c.Service<SpeakerBotService>().UdpAsync("speak", new Dictionary<string, object?>
        {
            ["voice"] = c.Parse(s.Str("alias")), ["message"] = c.Parse(s.Str("message")), ["badWordFilter"] = s.Bool("badWordFilter"),
        }, c.Cancellation));
        On(603, async (s, c) => await Send(c, s.Int("state") == 0 ? "Enable" : "Disable", s.Int("state") == 0 ? "enable" : "disable"));
        On(604, async (s, c) => await Send(c, s.Int("state") == 0 ? "Pause" : "Resume", s.Int("state") == 0 ? "pause" : "resume"));
        On(605, async (_, c) => await Send(c, "Stop", "stop"));
        On(606, async (_, c) => await Send(c, "Clear", "clear"));
        On(607, async (s, c) => await c.Service<SpeakerBotService>().UdpAsync("profile", new Dictionary<string, object?>
        {
            ["profile"] = c.Parse(s.Str("profileName")),
        }, c.Cancellation));
    }

    private static async Task Send(RunContext c, string wsRequest, string udpCommand)
    {
        var sb = c.Service<SpeakerBotService>();
        if (sb.IsConnected) await sb.RequestAsync(wsRequest, null, c.Cancellation);
        else await sb.UdpAsync(udpCommand, null, c.Cancellation);
    }
}
