using CherBot.Core.Platform;

namespace CherBot.Host;

/// <summary>Configuration section "CherBot" (appsettings.json, {dataDir}/cherbot.json, CHERBOT__* environment variables, command line).</summary>
public sealed class CherBotHostOptions
{
    public const string Section = "CherBot";

    /// <summary>Data directory. Resolved before configuration is read; see <see cref="DataDirectory"/>.</summary>
    public string? DataDir { get; set; }

    public ApiOptions Api { get; set; } = new();
    public SbWebSocketOptions WebSocketServer { get; set; } = new();
    public ListenerOptions HttpServer { get; set; } = new() { Enabled = false, Port = 7474 };
    public ListenerOptions UdpServer { get; set; } = new() { Enabled = false, Port = 4242 };
    public EngineOptions Engine { get; set; } = new();

    /// <summary>Instance name reported in the Streamer.bot Hello message.</summary>
    public string InstanceName { get; set; } = Environment.MachineName;
}

public class ListenerOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>127.0.0.1 (default), a specific IP such as a LAN or VPN address, or 0.0.0.0 for all interfaces.</summary>
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; }
}

public sealed class ApiOptions : ListenerOptions
{
    public ApiOptions() { Port = 8091; }

    /// <summary>Bearer token for the management API and web UI. When empty, one is generated into {dataDir}/api-token.</summary>
    public string? Token { get; set; }
}

public sealed class SbWebSocketOptions : ListenerOptions
{
    public SbWebSocketOptions() { Port = 8080; }

    public string Endpoint { get; set; } = "/";
    /// <summary>When set, clients must authenticate with Streamer.bot's salt/challenge scheme for protected requests.</summary>
    public string? Password { get; set; }
    /// <summary>Require authentication for every request, not just SendMessage.</summary>
    public bool EnforceAuth { get; set; }
}
