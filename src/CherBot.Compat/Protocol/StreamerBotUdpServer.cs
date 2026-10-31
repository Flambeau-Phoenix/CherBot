using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Compat.Protocol;

public sealed class UdpServerSettings
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4242;
}

/// <summary>Streamer.bot-compatible UDP server: {"request":"DoAction","action":{id|name},"args":{}}. No authentication, as in Streamer.bot.</summary>
public sealed class StreamerBotUdpServer(StreamerBotApi api, UdpServerSettings settings, ILogger<StreamerBotUdpServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        var address = settings.Address is "0.0.0.0" or "*" or "" ? IPAddress.Any : settings.Address == "localhost" ? IPAddress.Loopback : IPAddress.Parse(settings.Address);
        using var udp = new UdpClient(new IPEndPoint(address, settings.Port));
        log.LogInformation("UDP server listening on {Address}:{Port}", address, settings.Port);
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                log.LogDebug(ex, "UDP receive error");
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(packet.Buffer);
                var root = doc.RootElement;
                var request = root.TryGetProperty("request", out var r) ? r.GetString() : null;
                if (!string.Equals(request, "DoAction", StringComparison.OrdinalIgnoreCase))
                {
                    log.LogDebug("Ignoring UDP request {Request} from {Remote}", request, packet.RemoteEndPoint);
                    continue;
                }
                var res = api.DoAction(root.TryGetProperty("action", out var a) ? a : null, root.TryGetProperty("args", out var args) ? args : null, "udp");
                if (!res.Ok) log.LogWarning("UDP DoAction from {Remote} failed: {Error}", packet.RemoteEndPoint, res.Error);
            }
            catch (JsonException)
            {
                log.LogDebug("Invalid UDP payload from {Remote}", packet.RemoteEndPoint);
            }
        }
    }
}
