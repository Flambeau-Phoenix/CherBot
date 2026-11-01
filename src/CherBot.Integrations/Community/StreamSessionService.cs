using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Events;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Community;

/// <summary>Authoritative persisted broadcast identity. Reconciliation never resets an existing broadcast's winner.</summary>
public sealed class StreamSessionService(CherBotDb db, TwitchAuth auth, HelixClient helix, TwitchService twitch, ILogger<StreamSessionService> log) : BackgroundService
{
    public string BroadcasterId => auth.Get(TwitchRole.Broadcaster)?.UserId ?? "";
    public StreamSession? Current => BroadcasterId.Length == 0 ? null : db.GetStreamSession(BroadcasterId);
    private volatile bool _confirmed;
    private long _revision;
    public bool Live => _confirmed && Current?.Live == true;
    public string StreamId => Current is { Live: true } s ? s.StreamId : "";
    private readonly SemaphoreSlim _reconcile = new(1);
    private string? _lastError;
    public string? LastError => _lastError;

    public void OnEvent(BotEvent ev)
    {
        var id = CherBotJson.Format(ev.Data.GetValueOrDefault("broadcastUserId"));
        if (id.Length == 0) id = BroadcasterId;
        if (ev.Type == "StreamOnline")
        {
            Interlocked.Increment(ref _revision);
            var stream = CherBotJson.Format(ev.Data.GetValueOrDefault("streamId"));
            var started = CherBotJson.Format(ev.Data.GetValueOrDefault("startedAt"));
            if (id.Length > 0 && stream.Length > 0) { db.StartStream(id, stream, started.Length > 0 ? started : ev.Timestamp.ToString("O")); _confirmed = true; _lastError = null; }
        }
        else if (ev.Type == "StreamOffline" && id.Length > 0) { Interlocked.Increment(ref _revision); db.EndStream(id); _confirmed = true; _lastError = null; }
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (BroadcasterId.Length == 0) { _confirmed = false; return; }
        await _reconcile.WaitAsync(ct);
        try
        {
            var broadcaster = BroadcasterId;
            var revision = Interlocked.Read(ref _revision);
            var response = await helix.GetAsync("streams", new Dictionary<string, string?> { ["user_id"] = broadcaster }, ct: ct);
            if (broadcaster != BroadcasterId || revision != Interlocked.Read(ref _revision)) return;
            if (HelixClient.First(response) is { } stream)
                db.StartStream(broadcaster, stream.GetProperty("id").GetString()!, stream.GetProperty("started_at").GetString()!);
            else db.EndStream(broadcaster);
            _confirmed = true;
            _lastError = null;
        }
        catch (Exception ex) when (ex is HelixException or HttpRequestException or JsonException or KeyNotFoundException)
        {
            _lastError = ex.Message;
            log.LogWarning(ex, "Stream reconciliation failed; retaining persisted broadcast identity");
        }
        finally { _reconcile.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        twitch.BeforePublish += OnEvent;
        // A reconnect notification triggers reconciliation immediately rather than waiting for the periodic poll.
        var reconnect = new Action<BotEvent>(ev => { if (ev.Type == "BroadcasterChatConnected") _ = ReconcileAsync(stoppingToken); });
        twitch.BeforePublish += reconnect;
        try
        {
            await ReconcileAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(stoppingToken)) await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { twitch.BeforePublish -= OnEvent; twitch.BeforePublish -= reconnect; }
    }
}
