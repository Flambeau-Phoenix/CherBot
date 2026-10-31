using System.Collections.Concurrent;
using CherBot.Core.Commands;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Timers;

/// <summary>Raises the Timed Actions trigger (Misc/TimedAction, 701) for each enabled timer on its interval.</summary>
public sealed class TimerService : BackgroundService
{
    private readonly ConfigService _config;
    private readonly EventBus _bus;
    private readonly ILogger<TimerService> _log;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _nextFire = new();
    private readonly ConcurrentDictionary<Guid, int> _linesSince = new();
    private IDisposable? _chatSub;

    public TimerService(ConfigService config, EventBus bus, ILogger<TimerService> log)
    {
        _config = config;
        _bus = bus;
        _log = log;
        _config.Changed += (kind, id) =>
        {
            if (kind == "timer") _nextFire.TryRemove(id, out _);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _chatSub = _bus.Subscribe("timers-chat-lines", _ =>
        {
            foreach (var id in _config.Timers.Select(t => t.Id)) _linesSince.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }, ev => CommandService.SourceOf(ev) == CommandSource.Message);

        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await tick.WaitForNextTickAsync(stoppingToken)) Tick(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _chatSub.Dispose();
        }
    }

    internal void Tick(DateTimeOffset now)
    {
        foreach (var timer in _config.Timers)
        {
            if (!timer.Enabled)
            {
                _nextFire.TryRemove(timer.Id, out _);
                continue;
            }
            var next = _nextFire.GetOrAdd(timer.Id, _ => now + NextInterval(timer));
            if (now < next) continue;
            _nextFire[timer.Id] = now + NextInterval(timer);

            if (timer.Lines > 0 && _linesSince.GetValueOrDefault(timer.Id) < timer.Lines)
            {
                _log.LogDebug("Timer {Timer} skipped: fewer than {Lines} chat lines since last fire", timer.Name, timer.Lines);
                continue;
            }
            Fire(timer);
        }
    }

    public void Fire(TimerDef timer)
    {
        _linesSince[timer.Id] = 0;
        timer.Counter++;
        if (!timer.Repeat) timer.Enabled = false;
        _config.SaveTimer(timer);

        _bus.Publish(BotEvent.FromTypeName("TimedAction", new Dictionary<string, object?>
        {
            ["timerId"] = timer.Id.ToString(),
            ["timerName"] = timer.Name,
            ["timerInterval"] = timer.Interval,
            ["timerCounter"] = timer.Counter,
        }));
    }

    private static TimeSpan NextInterval(TimerDef t)
    {
        var seconds = Math.Max(1, t.Interval);
        if (t.RandomInterval && t.UpperInterval > seconds) seconds = Random.Shared.Next(seconds, t.UpperInterval + 1);
        return TimeSpan.FromSeconds(seconds);
    }

    public DateTimeOffset? NextFireTime(Guid timerId) => _nextFire.TryGetValue(timerId, out var t) ? t : null;
}
