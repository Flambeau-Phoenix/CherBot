using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Engine;

/// <summary>Describes why an action is being started.</summary>
public sealed record DispatchSource(string Label, TriggerDef? Trigger = null, BotEvent? Event = null, bool SkipQueue = false);

/// <summary>Builds the starting arguments for an action and hands it to its queue (or runs it inline).</summary>
public sealed class ActionDispatcher(ConfigService config, QueueManager queues, ActionExecutor executor, ILogger<ActionDispatcher> log)
{
    public Dictionary<string, object?> BuildArgs(ActionDef action, IDictionary<string, object?>? args, DispatchSource source, Guid runId)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source.Event is { } ev)
            foreach (var (k, v) in ev.Data) result[k] = v;

        result["actionId"] = action.Id.ToString();
        result["actionName"] = action.Name;
        result["runningActionId"] = runId.ToString();
        result["actionQueuedAt"] = DateTimeOffset.UtcNow;
        result["__source"] = source.Event is { TypeId: { } tid } ? EventTypes.Get(tid)?.Name ?? source.Label : source.Label;
        result["eventSource"] = source.Event?.Source.ToLowerInvariant() ?? "core";
        if (source.Trigger is { } t)
        {
            var info = EventTypes.Get(t.Type);
            result["triggerId"] = t.Id.ToString();
            result["triggerName"] = info?.Display ?? source.Label;
            result["triggerCategory"] = info?.Category ?? "";
        }
        else
        {
            result["triggerName"] = source.Label;
        }

        if (args is not null)
            foreach (var (k, v) in args) result[k] = v;
        return result;
    }

    /// <summary>Queues an action. Returns null when the action is disabled.</summary>
    public QueuedRun? Dispatch(ActionDef action, IDictionary<string, object?>? args, DispatchSource source)
    {
        if (!action.Enabled)
        {
            log.LogDebug("Action {Action} is disabled; not queued ({Source})", action.Name, source.Label);
            return null;
        }
        var runId = Guid.NewGuid();
        return queues.Enqueue(new QueuedRun
        {
            Id = runId,
            Action = action,
            Args = BuildArgs(action, args, source, runId),
            Trigger = source.Label,
            SkipQueue = source.SkipQueue,
        });
    }

    public QueuedRun? Dispatch(Guid? actionId, string? actionName, IDictionary<string, object?>? args, DispatchSource source) =>
        config.FindAction(actionId, actionName) is { } action ? Dispatch(action, args, source) : null;

    /// <summary>Runs an action inline and waits for it, bypassing its queue (Run Action with "run immediately").</summary>
    public Task<RunResult> RunNowAsync(ActionDef action, IDictionary<string, object?>? args, DispatchSource source, CancellationToken ct, int depth = 0)
    {
        var runId = Guid.NewGuid();
        return executor.RunAsync(action, BuildArgs(action, args, source, runId), source.Label, ct, depth, runId);
    }
}
