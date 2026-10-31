using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Engine;

/// <summary>
/// Listens to every event and queues each enabled action that has an enabled trigger of the same EventType id whose
/// filters accept the event (identity filters such as commandId/rewardId/timerId, and min/max amount ranges).
/// </summary>
public sealed class TriggerMatcher(EventBus bus, ConfigService config, ActionDispatcher dispatcher, ILogger<TriggerMatcher> log) : IHostedService
{
    /// <summary>Trigger fields that must equal the event value when set. A missing event value means no match.</summary>
    private static readonly string[] RequiredIdentityKeys = ["commandId", "timerId", "rewardId", "eventName", "fileWatcherId", "fileTailId", "codeTriggerName"];

    /// <summary>Trigger fields compared only when the event carries the same key.</summary>
    private static readonly string[] OptionalIdentityKeys = ["connectionId", "obsId", "sceneName", "websocketId", "serverId", "clientId", "profileId", "name", "variableName", "vendorName"];

    private static readonly string[] AmountKeys = ["amount", "bits", "viewers", "viewerCount", "gifts", "count", "cumulativeMonths", "months"];

    private IDisposable? _sub;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sub = bus.Subscribe("trigger-matcher", OnEventAsync, e => e.TypeId is not null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sub?.Dispose();
        return Task.CompletedTask;
    }

    private Task OnEventAsync(BotEvent ev)
    {
        foreach (var (action, trigger) in Match(ev))
        {
            var skipQueue = ev.Data.TryGetValue("skipQueue", out var sq) && sq is true;
            var label = EventTypes.Get(trigger.Type)?.Display ?? ev.Type;
            dispatcher.Dispatch(action, null, new DispatchSource(label, trigger, ev, skipQueue));
        }
        return Task.CompletedTask;
    }

    public IEnumerable<(ActionDef Action, TriggerDef Trigger)> Match(BotEvent ev)
    {
        if (ev.TypeId is not { } typeId) yield break;
        foreach (var action in config.Actions)
        {
            if (!action.Enabled) continue;
            foreach (var trigger in action.Triggers)
            {
                if (!trigger.Enabled || trigger.Type != typeId) continue;
                if (!Accepts(trigger, ev)) continue;
                log.LogDebug("Trigger {Trigger} on {Action} matched {Event}", trigger.Id, action.Name, ev);
                yield return (action, trigger);
                break; // one dispatch per action per event, even when several triggers match
            }
        }
    }

    public static bool Accepts(TriggerDef trigger, BotEvent ev)
    {
        foreach (var key in RequiredIdentityKeys)
        {
            if (!HasValue(trigger, key)) continue;
            if (!ev.Data.TryGetValue(key, out var actual) || !SameValue(trigger.Str(key), actual)) return false;
        }
        foreach (var key in OptionalIdentityKeys)
        {
            if (!HasValue(trigger, key)) continue;
            if (ev.Data.TryGetValue(key, out var actual) && !SameValue(trigger.Str(key), actual)) return false;
        }

        // First Words trigger: optional username (or user id when isUserId) filter.
        if (HasValue(trigger, "username"))
        {
            var wanted = trigger.Str("username");
            var key = trigger.Bool("isUserId") ? "userId" : "userName";
            if (!ev.Data.TryGetValue(key, out var who) || !SameValue(wanted, who)) return false;
        }

        var min = trigger.Dbl("min");
        var max = trigger.Dbl("max");
        if (min > 0 || max > 0)
        {
            var amount = AmountKeys.Select(k => ev.Data.TryGetValue(k, out var v) && CherBotJson.TryNumber(v, out var n) ? (double?)n : null)
                .FirstOrDefault(v => v is not null);
            if (amount is not { } a) return false;
            if (min > 0 && a < min) return false;
            if (max > 0 && a > max) return false;
        }
        return true;
    }

    private static bool HasValue(TriggerDef t, string key)
    {
        if (!t.Has(key)) return false;
        var s = t.Str(key);
        return s.Length > 0 && s != Guid.Empty.ToString();
    }

    private static bool SameValue(string expected, object? actual) =>
        string.Equals(expected, CherBotJson.Format(actual), StringComparison.OrdinalIgnoreCase);
}
