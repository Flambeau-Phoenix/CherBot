using System.Collections.Concurrent;
using System.Threading.Channels;
using CherBot.Storage;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Events;

/// <summary>
/// In-process publish/subscribe for engine events. Publishing never blocks the caller: each subscriber
/// gets its own bounded channel. Persistable events are written to the event log so clients can resume by sequence.
/// </summary>
public sealed class EventBus : IDisposable
{
    private readonly CherBotDb _db;
    private readonly ILogger<EventBus> _log;
    private readonly ConcurrentDictionary<Guid, Subscription> _subs = new();
    private readonly object _seqLock = new();
    private long _seq;

    public EventBus(CherBotDb db, ILogger<EventBus> log)
    {
        _db = db;
        _log = log;
        _seq = db.LastEventSeq();
    }

    public long LastSeq => Interlocked.Read(ref _seq);

    public void Publish(BotEvent ev)
    {
        lock (_seqLock)
        {
            if (ev.Persist)
            {
                try
                {
                    ev.Seq = _db.AddEvent(ev.Timestamp.ToString("O"), ev.Source, ev.Type, CherBotJson.Serialize(ev.Data));
                    _seq = ev.Seq;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Event log write failed for {Event}", ev);
                    ev.Seq = ++_seq;
                }
            }
            else
            {
                ev.Seq = _seq;
            }
        }

        foreach (var sub in _subs.Values)
        {
            if (sub.Filter is not null && !sub.Filter(ev)) continue;
            if (!sub.Channel.Writer.TryWrite(ev))
                _log.LogWarning("Subscriber {Name} is not keeping up; dropped {Event}", sub.Name, ev);
        }
    }

    /// <summary>Subscribes a handler that runs on its own reader loop. Dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe(string name, Func<BotEvent, Task> handler, Func<BotEvent, bool>? filter = null, int capacity = 10_000)
    {
        var sub = new Subscription(name, filter, Channel.CreateBounded<BotEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        }));
        _subs[sub.Id] = sub;
        sub.Loop = Task.Run(async () =>
        {
            await foreach (var ev in sub.Channel.Reader.ReadAllAsync())
            {
                try { await handler(ev); }
                catch (Exception ex) { _log.LogError(ex, "Subscriber {Name} failed on {Event}", name, ev); }
            }
        });
        return new Unsubscriber(() =>
        {
            if (_subs.TryRemove(sub.Id, out var s)) s.Channel.Writer.TryComplete();
        });
    }

    /// <summary>Returns a channel reader of live events (for WebSocket and SSE clients).</summary>
    public (ChannelReader<BotEvent> Reader, IDisposable Subscription) Listen(string name, Func<BotEvent, bool>? filter = null, int capacity = 2_000)
    {
        var sub = new Subscription(name, filter, Channel.CreateBounded<BotEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        }));
        _subs[sub.Id] = sub;
        return (sub.Channel.Reader, new Unsubscriber(() =>
        {
            if (_subs.TryRemove(sub.Id, out var s)) s.Channel.Writer.TryComplete();
        }));
    }

    public void Dispose()
    {
        foreach (var s in _subs.Values) s.Channel.Writer.TryComplete();
        _subs.Clear();
    }

    private sealed class Subscription(string name, Func<BotEvent, bool>? filter, Channel<BotEvent> channel)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; } = name;
        public Func<BotEvent, bool>? Filter { get; } = filter;
        public Channel<BotEvent> Channel { get; } = channel;
        public Task? Loop { get; set; }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) dispose();
        }
    }
}
