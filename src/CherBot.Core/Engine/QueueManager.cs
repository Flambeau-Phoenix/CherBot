using System.Collections.Concurrent;
using System.Text.Json;
using CherBot.Core.Config;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Engine;

/// <summary>An action waiting to run (or running) on a queue.</summary>
public sealed class QueuedRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ActionDef Action { get; init; }
    public required Dictionary<string, object?> Args { get; init; }
    public string Trigger { get; init; } = "";
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Twitch reward "skips queue": ignore the queue's blocking flag.</summary>
    public bool SkipQueue { get; init; }
    internal TaskCompletionSource<RunResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<RunResult> Done => Completion.Task;
}

public sealed record QueueStatus(Guid Id, string Name, bool Blocking, bool Paused, int Pending, int Running);

/// <summary>
/// One worker per queue with two FIFOs (normal and always-run). Blocking queues run one action at a time; non-blocking
/// queues (including Default) start each action immediately. Paused queues hold normal actions but still run always-run ones.
/// Pending actions are persisted so they survive a restart.
/// </summary>
public sealed class QueueManager : IHostedService, IDisposable
{
    public static readonly Guid DefaultQueueId = Guid.Empty;

    private readonly ConfigService _config;
    private readonly ActionExecutor _executor;
    private readonly CherBotDb _db;
    private readonly EngineOptions _options;
    private readonly ILogger<QueueManager> _log;
    private readonly ConcurrentDictionary<Guid, QueueState> _queues = new();
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _flushTimer;
    private volatile bool _dirty;
    private bool _allPaused;

    public QueueManager(ConfigService config, ActionExecutor executor, CherBotDb db, EngineOptions options, ILogger<QueueManager> log)
    {
        _config = config;
        _executor = executor;
        _db = db;
        _options = options;
        _log = log;
    }

    private QueueState StateFor(Guid? queueId)
    {
        var id = queueId ?? DefaultQueueId;
        if (id != DefaultQueueId && _config.GetQueue(id) is null) id = DefaultQueueId;
        return _queues.GetOrAdd(id, qid =>
        {
            var def = qid == DefaultQueueId ? null : _config.GetQueue(qid);
            var state = new QueueState(qid, def?.Name ?? ConfigService.DefaultQueueName) { Paused = _allPaused };
            state.Worker = Task.Run(() => WorkerAsync(state));
            _log.LogInformation("ActionQueue :: Starting queue {Name} thread", state.Name);
            return state;
        });
    }

    private bool IsBlocking(QueueState q) => q.Id != DefaultQueueId && (_config.GetQueue(q.Id)?.Blocking ?? false);

    public QueuedRun Enqueue(QueuedRun run)
    {
        var q = StateFor(run.Action.QueueId);
        _log.LogInformation("ActionDispatcher :: Queueing '{Action}' ({Id}) to '{Queue}' with {Count} arguments",
            run.Action.Name, run.Id, q.Name, run.Args.Count);

        if (run.SkipQueue && (!q.Paused || run.Action.AlwaysRun))
        {
            // Skip-queue runs start at once, even while a blocking action is still running.
            var task = Task.Run(() => ExecuteAsync(q, run));
            _inFlight[run.Id] = task;
            _ = task.ContinueWith(_ => _inFlight.TryRemove(run.Id, out var _), TaskScheduler.Default);
            return run;
        }

        lock (q.Gate)
        {
            if (run.Action.AlwaysRun) q.AlwaysRun.AddLast(run);
            else q.Normal.AddLast(run);
        }
        _dirty = true;
        q.Signal.Release();
        return run;
    }

    private async Task WorkerAsync(QueueState q)
    {
        var ct = _stopping.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await q.Signal.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            while (!ct.IsCancellationRequested)
            {
                QueuedRun? next = null;
                var blocking = IsBlocking(q);
                lock (q.Gate)
                {
                    if (q.AlwaysRun.First is { } ar) { next = ar.Value; q.AlwaysRun.RemoveFirst(); }
                    else if (!q.Paused && q.Normal.First is { } n) { next = n.Value; q.Normal.RemoveFirst(); }
                }
                if (next is null) break;
                _dirty = true;

                if (!(_config.GetAction(next.Action.Id) ?? next.Action).Enabled)
                {
                    _log.LogInformation("ActionQueue :: DISABLED - {Mode} '{Action}'", blocking ? "Blocking" : "Non-Blocking", next.Action.Name);
                    next.Completion.TrySetResult(new RunResult(next.Id, next.Action.Id, next.Action.Name, next.Trigger, "disabled", 0,
                        DateTimeOffset.UtcNow, [], next.Args));
                    continue;
                }

                if (blocking && !next.SkipQueue)
                {
                    _log.LogDebug("ActionQueue :: Performing blocking '{Action}'", next.Action.Name);
                    await ExecuteAsync(q, next);
                }
                else
                {
                    _log.LogDebug("ActionQueue :: Performing non-blocking '{Action}'", next.Action.Name);
                    var run = next;
                    var task = Task.Run(() => ExecuteAsync(q, run));
                    _inFlight[run.Id] = task;
                    _ = task.ContinueWith(_ => _inFlight.TryRemove(run.Id, out var _), TaskScheduler.Default);
                }
            }
        }
    }

    private async Task ExecuteAsync(QueueState q, QueuedRun run)
    {
        Interlocked.Increment(ref q.Running);
        try
        {
            // Latest definition wins, in case the action was edited while queued.
            var action = _config.GetAction(run.Action.Id) ?? run.Action;
            var result = await _executor.RunAsync(action, run.Args, run.Trigger, _stopping.Token, runId: run.Id);
            run.Completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Queue {Queue} failed to run {Action}", q.Name, run.Action.Name);
            run.Completion.TrySetException(ex);
        }
        finally
        {
            Interlocked.Decrement(ref q.Running);
        }
    }

    // ── control ────────────────────────────────────────────────────────────

    /// <summary>Pauses one queue, or every queue when <paramref name="queueId"/> is null.</summary>
    public void Pause(Guid? queueId)
    {
        if (queueId is null)
        {
            _allPaused = true;
            foreach (var q in KnownStates()) q.Paused = true;
        }
        else StateFor(queueId).Paused = true;
    }

    /// <summary>Resumes one queue (or all). When <paramref name="clear"/> is set, held actions are dropped instead of run.</summary>
    public int Resume(Guid? queueId, bool clear = false)
    {
        var targets = queueId is null ? KnownStates().ToList() : [StateFor(queueId)];
        if (queueId is null) _allPaused = false;
        var dropped = 0;
        foreach (var q in targets)
        {
            if (clear) dropped += Clear(q.Id);
            int held;
            lock (q.Gate) held = q.Normal.Count;
            _log.LogInformation("ActionQueue :: Resuming queue {Name} with {Count} actions", q.Name, held);
            q.Paused = false;
            q.Signal.Release();
        }
        return dropped;
    }

    public int Clear(Guid? queueId)
    {
        var targets = queueId is null ? KnownStates().ToList() : [StateFor(queueId)];
        var count = 0;
        foreach (var q in targets)
        {
            lock (q.Gate)
            {
                count += q.Normal.Count;
                foreach (var r in q.Normal) r.Completion.TrySetCanceled();
                q.Normal.Clear();
            }
        }
        _dirty = true;
        return count;
    }

    public void SetBlocking(Guid queueId, bool blocking)
    {
        if (_config.GetQueue(queueId) is not { } def) return;
        def.Blocking = blocking;
        _config.SaveQueue(def);
        StateFor(queueId).Signal.Release();
    }

    public bool IsPaused(Guid? queueId) => StateFor(queueId).Paused;

    private IEnumerable<QueueState> KnownStates()
    {
        StateFor(DefaultQueueId);
        foreach (var q in _config.Queues) StateFor(q.Id);
        return _queues.Values;
    }

    public IReadOnlyList<QueueStatus> Status()
    {
        var list = new List<QueueStatus>();
        foreach (var q in KnownStates().OrderBy(q => q.Id == DefaultQueueId ? 0 : 1).ThenBy(q => q.Name))
        {
            int pending;
            lock (q.Gate) pending = q.Normal.Count + q.AlwaysRun.Count;
            list.Add(new QueueStatus(q.Id, q.Name, IsBlocking(q), q.Paused, pending, Volatile.Read(ref q.Running)));
        }
        return list;
    }

    // ── pending cache ──────────────────────────────────────────────────────

    private sealed record PendingEntry(Guid ActionId, string Trigger, DateTimeOffset QueuedAt, bool SkipQueue, Dictionary<string, JsonElement> Args);

    internal void FlushPending()
    {
        var rows = new List<PendingRow>();
        foreach (var q in _queues.Values)
        {
            List<QueuedRun> snapshot;
            lock (q.Gate) snapshot = [.. q.AlwaysRun, .. q.Normal];
            foreach (var r in snapshot.Where(r => !r.Action.ExcludeFromPending))
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var (k, v) in r.Args)
                {
                    try { args[k] = JsonSerializer.SerializeToElement(v, CherBotJson.Options); }
                    catch { args[k] = JsonSerializer.SerializeToElement(CherBotJson.Format(v)); }
                }
                rows.Add(new PendingRow(r.Id.ToString(), r.QueuedAt.ToString("O"),
                    CherBotJson.Serialize(new PendingEntry(r.Action.Id, r.Trigger, r.QueuedAt, r.SkipQueue, args))));
            }
        }
        _db.ReplacePending(rows);
        if (rows.Count > 0) _log.LogInformation("ActionQueue :: Writing {Count} pending actions to cache", rows.Count);
        _dirty = false;
    }

    private void LoadPending()
    {
        var rows = _db.GetPending();
        if (rows.Count == 0) return;
        _log.LogInformation("ActionQueue :: Pending Cache found!");
        var loaded = 0;
        foreach (var row in rows)
        {
            var entry = CherBotJson.Deserialize<PendingEntry>(row.Json);
            if (entry is null || _config.GetAction(entry.ActionId) is not { } action) continue;
            var args = entry.Args.ToDictionary(kv => kv.Key, kv => CherBotJson.ToClr(kv.Value), StringComparer.OrdinalIgnoreCase);
            Enqueue(new QueuedRun
            {
                Id = Guid.TryParse(row.Id, out var id) ? id : Guid.NewGuid(),
                Action = action, Args = args, Trigger = entry.Trigger, QueuedAt = entry.QueuedAt, SkipQueue = entry.SkipQueue,
            });
            loaded++;
        }
        _db.ReplacePending([]);
        _log.LogInformation("ActionQueue :: Loaded {Count} pending actions", loaded);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadPending();
        var period = TimeSpan.FromSeconds(Math.Max(5, _options.PendingFlushSeconds));
        _flushTimer = new Timer(_ =>
        {
            if (!_dirty) return;
            try { FlushPending(); } catch (Exception ex) { _log.LogWarning(ex, "Pending cache flush failed"); }
        }, null, period, period);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _flushTimer?.Dispose();
        try { FlushPending(); } catch (Exception ex) { _log.LogWarning(ex, "Pending cache flush on shutdown failed"); }
        _stopping.Cancel();
        try { await Task.WhenAll(_inFlight.Values).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch { /* shutting down */ }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _stopping.Dispose();
    }

    private sealed class QueueState(Guid id, string name)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public object Gate { get; } = new();
        public LinkedList<QueuedRun> Normal { get; } = new();
        public LinkedList<QueuedRun> AlwaysRun { get; } = new();
        public SemaphoreSlim Signal { get; } = new(0);
        public volatile bool Paused;
        public int Running;
        public Task? Worker { get; set; }
    }
}
