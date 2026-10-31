using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Obs;

/// <summary>An OBS connection. Defined in configuration (CherBot:Obs:Connections) or added at runtime through the API.</summary>
public sealed class ObsConnectionDef
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "OBS";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    /// <summary>Only used from configuration; runtime connections keep their password in the secret store.</summary>
    public string? Password { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int RetrySeconds { get; set; } = 10;
}

public sealed class ObsOptions
{
    public const string Section = "CherBot:Obs";
    public List<ObsConnectionDef> Connections { get; set; } = [];
}

/// <summary>Keeps every configured OBS connection open, republishes OBS events, and hands clients to the OBS sub-actions.</summary>
public sealed class ObsService : BackgroundService, IIntegrationStatus, IEventCatalog
{
    private const string DocKind = "obs-connection";
    private readonly ObsOptions _options;
    private readonly ConfigService _config;
    private readonly ISecretStore _secrets;
    private readonly EventBus _bus;
    private readonly ILogger<ObsService> _log;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private CancellationToken _stopping;

    public ObsService(ObsOptions options, ConfigService config, ISecretStore secrets, EventBus bus, ILogger<ObsService> log)
    {
        _options = options;
        _config = config;
        _secrets = secrets;
        _bus = bus;
        _log = log;
        _config.Changed += (kind, _) =>
        {
            if (kind == DocKind) Sync();
        };
    }

    /// <summary>Configuration connections get a stable id derived from their name, so sub-actions can reference them.</summary>
    public static Guid StableId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("obs:" + name.ToLowerInvariant())));

    public IReadOnlyList<ObsConnectionDef> Definitions()
    {
        var list = _options.Connections.Select(c =>
        {
            if (c.Id == Guid.Empty) c.Id = StableId(c.Name);
            return c;
        }).ToList();
        foreach (var d in _config.GetDocuments<ObsConnectionDef>(DocKind))
        {
            d.Password = _secrets.Get($"obs.password.{d.Id}");
            list.RemoveAll(x => x.Id == d.Id);
            list.Add(d);
        }
        return list;
    }

    public void SaveConnection(ObsConnectionDef def, string? password)
    {
        if (def.Id == Guid.Empty) def.Id = Guid.NewGuid();
        if (password is not null) _secrets.Set($"obs.password.{def.Id}", password);
        var stored = new ObsConnectionDef { Id = def.Id, Name = def.Name, Host = def.Host, Port = def.Port, AutoConnect = def.AutoConnect, RetrySeconds = def.RetrySeconds };
        _config.SaveDocument(DocKind, def.Id.ToString(), def.Name, stored);
    }

    public bool DeleteConnection(Guid id)
    {
        _secrets.Delete($"obs.password.{id}");
        var removed = _config.DeleteDocument(DocKind, id.ToString());
        Sync();
        return removed;
    }

    /// <summary>Returns the requested connection's client, or the first connected one when the id is unknown (imported actions).</summary>
    public ObsClient? Client(Guid? connectionId)
    {
        if (connectionId is { } id && _connections.TryGetValue(id, out var c) && c.Client?.IsOpen == true) return c.Client;
        return _connections.Values.Where(x => x.Client?.IsOpen == true).OrderBy(x => x.Def.Name).FirstOrDefault()?.Client;
    }

    public Guid? ConnectedId(Guid? requested) =>
        requested is { } id && _connections.TryGetValue(id, out var c) && c.Client?.IsOpen == true
            ? id
            : _connections.Values.Where(x => x.Client?.IsOpen == true).OrderBy(x => x.Def.Name).FirstOrDefault()?.Def.Id;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        Sync();
        return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    /// <summary>Drops every connection and connects again (the Dashboard's Reconnect OBS button).</summary>
    public void Reconnect()
    {
        foreach (var (id, conn) in _connections)
        {
            conn.Cts.Cancel();
            _connections.TryRemove(id, out _);
        }
        Sync();
    }

    /// <summary>Starts loops for new connections and stops removed ones.</summary>
    private void Sync()
    {
        if (_stopping == default) return;
        var defs = Definitions().ToDictionary(d => d.Id);
        foreach (var (id, conn) in _connections)
        {
            if (defs.ContainsKey(id)) continue;
            conn.Cts.Cancel();
            _connections.TryRemove(id, out _);
        }
        foreach (var def in defs.Values)
        {
            if (_connections.TryGetValue(def.Id, out var existing))
            {
                if (existing.Def.Host == def.Host && existing.Def.Port == def.Port && existing.Def.Password == def.Password) continue;
                existing.Cts.Cancel();
            }
            if (!def.AutoConnect) continue;
            var conn = new Connection(def, CancellationTokenSource.CreateLinkedTokenSource(_stopping));
            _connections[def.Id] = conn;
            _ = Task.Run(() => RunAsync(conn));
        }
    }

    private async Task RunAsync(Connection conn)
    {
        var ct = conn.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var client = new ObsClient();
            var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Closed += r => closed.TrySetResult(r);
            client.EventReceived += (type, data) => OnObsEvent(conn.Def, type, data);
            try
            {
                conn.State = "connecting";
                await client.ConnectAsync(new Uri($"ws://{conn.Def.Host}:{conn.Def.Port}"), conn.Def.Password, ct);
                conn.Client = client;
                conn.State = "connected";
                conn.Error = null;
                _log.LogInformation("OBS '{Name}' connected (OBS {Version}, obs-websocket {Ws})", conn.Def.Name, client.ObsVersion, client.WebSocketVersion);
                _bus.Publish(BotEvent.FromTypeName("ObsConnected", Base(conn.Def)));
                var reason = await closed.Task.WaitAsync(ct);
                conn.Error = reason;
                _log.LogWarning("OBS '{Name}' disconnected: {Reason}", conn.Def.Name, reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                conn.Error = ex.Message;
                _log.LogDebug(ex, "OBS '{Name}' connection failed", conn.Def.Name);
            }
            finally
            {
                var wasConnected = conn.State == "connected";
                conn.Client = null;
                conn.State = "disconnected";
                await client.DisposeAsync();
                if (wasConnected) _bus.Publish(BotEvent.FromTypeName("ObsDisconnected", Base(conn.Def)));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, conn.Def.RetrySeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static Dictionary<string, object?> Base(ObsConnectionDef def) => new()
    {
        ["obsId"] = def.Id.ToString(), ["connectionId"] = def.Id.ToString(), ["obsConnectionName"] = def.Name,
    };

    private void OnObsEvent(ObsConnectionDef def, string type, JsonElement data)
    {
        var raw = Base(def);
        raw["eventType"] = type;
        raw["eventData"] = data.GetRawText();
        foreach (var p in data.EnumerateObject()) raw[p.Name] = CherBotJson.ToClr(p.Value);
        // Streamer.bot's OBS Event trigger filters on "eventName".
        raw["eventName"] = type;

        if (type == "VendorEvent")
        {
            // Vendor Event triggers filter on vendorName + the vendor's own event type.
            raw["vendorName"] = data.TryGetProperty("vendorName", out var vn) ? vn.GetString() : null;
            raw["eventName"] = data.TryGetProperty("eventType", out var vt) ? vt.GetString() : null;
            _bus.Publish(BotEvent.FromTypeName("ObsVendorEvent", raw));
            return;
        }
        _bus.Publish(BotEvent.FromTypeName("ObsEvent", raw));

        switch (type)
        {
            case "CurrentProgramSceneChanged":
            {
                var d = Base(def);
                d["sceneName"] = data.TryGetProperty("sceneName", out var s) ? s.GetString() : null;
                _bus.Publish(BotEvent.FromTypeName("ObsSceneChanged", d));
                break;
            }
            case "StreamStateChanged" or "RecordStateChanged":
            {
                var state = data.TryGetProperty("outputState", out var os) ? os.GetString() : null;
                var stream = type == "StreamStateChanged";
                var name = state switch
                {
                    "OBS_WEBSOCKET_OUTPUT_STARTED" => stream ? "ObsStreamingStarted" : "ObsRecordingStarted",
                    "OBS_WEBSOCKET_OUTPUT_STOPPED" => stream ? "ObsStreamingStopped" : "ObsRecordingStopped",
                    _ => null,
                };
                if (name is not null) _bus.Publish(BotEvent.FromTypeName(name, Base(def)));
                break;
            }
        }
    }

    public IntegrationStatus GetStatus()
    {
        var connections = _connections.Values.Select(c => new
        {
            id = c.Def.Id, name = c.Def.Name, host = c.Def.Host, port = c.Def.Port, state = c.State, error = c.Error,
            obsVersion = c.Client?.ObsVersion,
        }).ToList();
        var defined = Definitions().Count;
        var connected = connections.Count(c => c.state == "connected");
        var state = defined == 0 ? "not configured" : connected == defined ? "connected" : connected > 0 ? "partial" : "disconnected";
        return new IntegrationStatus("obs", state, connected > 0, null, new Dictionary<string, object?> { ["connections"] = connections });
    }

    public IEnumerable<int> EmittedEventTypes => [14001, 14002, 14003, 14004, 14005, 14006, 14007, 14008, 14009];

    private sealed class Connection(ObsConnectionDef def, CancellationTokenSource cts)
    {
        public ObsConnectionDef Def { get; } = def;
        public CancellationTokenSource Cts { get; } = cts;
        public volatile ObsClient? Client;
        public volatile string State = "idle";
        public string? Error;
    }
}
