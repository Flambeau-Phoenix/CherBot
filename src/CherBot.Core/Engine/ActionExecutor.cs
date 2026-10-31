using System.Collections.Concurrent;
using System.Diagnostics;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Storage;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Engine;

public sealed record RunResult(
    Guid RunId, Guid ActionId, string ActionName, string Trigger, string Status, long Ms,
    DateTimeOffset Started, IReadOnlyList<SubActionTrace> Trace, IReadOnlyDictionary<string, object?> Args)
{
    public bool Success => Status is "ok" or "break";
}

/// <summary>Runs an action's sub-actions: weighted random-one, concurrent-all or sequential, recursing through containers.</summary>
public sealed class ActionExecutor
{
    public const int GroupType = 99900, IfTrueType = 99901, IfFalseType = 99902, SwitchCaseType = 99903, SwitchDefaultType = 99904;
    public const int IfType = 120, BreakType = 124, SwitchType = 127, WhileType = 128, LoopBreakType = 129, LoopContinueType = 130;
    private const int MaxDepth = 16;
    private const int MaxLoopIterations = 100_000;

    private readonly SubActionRegistry _registry;
    private readonly EventBus _bus;
    private readonly CherBotDb _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<ActionExecutor> _log;
    private readonly ConcurrentDictionary<int, byte> _warnedUnsupported = new();

    public ActionExecutor(SubActionRegistry registry, EventBus bus, CherBotDb db, IServiceProvider services, ILogger<ActionExecutor> log)
    {
        _registry = registry;
        _bus = bus;
        _db = db;
        _services = services;
        _log = log;
    }

    public async Task<RunResult> RunAsync(ActionDef action, IDictionary<string, object?> args, string trigger, CancellationToken ct, int depth = 0, Guid? runId = null)
    {
        var id = runId ?? (args.TryGetValue("runningActionId", out var r) && Guid.TryParse(CherBotJson.Format(r), out var g) ? g : Guid.NewGuid());
        var ctx = new RunContext(id, action, args, _services, _log, ct, depth);
        ctx.SetArg("runningActionId", id.ToString());
        ctx.SetArg("actionId", action.Id.ToString());
        ctx.SetArg("actionName", action.Name);

        _bus.Publish(BotEvent.FromTypeName("RawAction", new Dictionary<string, object?>
        {
            ["actionId"] = action.Id, ["actionName"] = action.Name, ["runningActionId"] = id, ["trigger"] = trigger,
        }));

        var sw = Stopwatch.StartNew();
        string status;
        if (depth > MaxDepth)
        {
            _log.LogWarning("Action {Action} skipped: inline Run Action nesting exceeded {Max}", action.Name, MaxDepth);
            status = "error";
        }
        else
        {
            try
            {
                var signal = await ExecuteListAsync(action.SubActions, ctx, action.RandomAction, action.Concurrent);
                status = signal == FlowSignal.Break ? "break" : "ok";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                status = "cancelled";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Action {Action} failed", action.Name);
                status = "error";
            }
        }
        sw.Stop();

        var result = new RunResult(id, action.Id, action.Name, trigger, status, sw.ElapsedMilliseconds, ctx.Started, ctx.Trace,
            new Dictionary<string, object?>(ctx.Args, StringComparer.OrdinalIgnoreCase));
        _log.LogInformation("Action ({Id}) :: Completed '{Name}' in {Ms}ms with a result of '{Status}'", id, action.Name, sw.ElapsedMilliseconds, status);

        if (!action.ExcludeFromHistory)
        {
            try
            {
                _db.AddRun(new RunRow(id.ToString(), ctx.Started.ToString("O"), action.Id.ToString(), action.Name, trigger, status, sw.ElapsedMilliseconds,
                    CherBotJson.Serialize(new { trace = result.Trace, args = SnapshotArgs(result.Args) })));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not record run {Run}", id);
            }
        }

        _bus.Publish(BotEvent.FromTypeName("RawActionCompleted", new Dictionary<string, object?>
        {
            ["actionId"] = action.Id, ["actionName"] = action.Name, ["runningActionId"] = id,
            ["trigger"] = trigger, ["status"] = status, ["success"] = result.Success, ["duration"] = sw.ElapsedMilliseconds,
        }));
        return result;
    }

    private static Dictionary<string, string> SnapshotArgs(IReadOnlyDictionary<string, object?> args) =>
        args.Take(200).ToDictionary(kv => kv.Key, kv =>
        {
            var s = CherBotJson.Format(kv.Value);
            return s.Length > 500 ? s[..500] + "…" : s;
        });

    /// <summary>Runs a list of sub-actions in the requested mode and returns the first non-continue signal.</summary>
    public async Task<FlowSignal> ExecuteListAsync(IReadOnlyList<SubActionDef>? list, RunContext ctx, bool random = false, bool concurrent = false)
    {
        if (list is null || list.Count == 0) return FlowSignal.Continue;
        var enabled = list.Where(s => s.Enabled).OrderBy(s => s.Index).ToList();
        if (enabled.Count == 0) return FlowSignal.Continue;

        if (random)
            return await ExecuteOneAsync(PickWeighted(enabled), ctx);

        if (concurrent)
        {
            var results = await Task.WhenAll(enabled.Select(s => ExecuteOneAsync(s, ctx)));
            return results.Contains(FlowSignal.Break) ? FlowSignal.Break : FlowSignal.Continue;
        }

        foreach (var sa in enabled)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var signal = await ExecuteOneAsync(sa, ctx);
            if (signal != FlowSignal.Continue) return signal;
        }
        return FlowSignal.Continue;
    }

    internal static SubActionDef PickWeighted(IReadOnlyList<SubActionDef> items)
    {
        var total = items.Sum(s => Math.Max(0, s.Weight));
        if (total <= 0) return items[Random.Shared.Next(items.Count)];
        var roll = Random.Shared.NextDouble() * total;
        foreach (var s in items)
        {
            roll -= Math.Max(0, s.Weight);
            if (roll < 0) return s;
        }
        return items[^1];
    }

    public async Task<FlowSignal> ExecuteOneAsync(SubActionDef sa, RunContext ctx)
    {
        if (!sa.Enabled) return FlowSignal.Continue;
        var sw = Stopwatch.StartNew();
        var name = SubActionTypes.NameOf(sa.Type);
        try
        {
            var signal = sa.Type switch
            {
                GroupType or IfTrueType or IfFalseType or SwitchCaseType or SwitchDefaultType =>
                    await ExecuteListAsync(sa.SubActions, ctx, sa.Bool("random")),
                IfType => await RunIfAsync(sa, ctx),
                SwitchType => await RunSwitchAsync(sa, ctx),
                WhileType => await RunWhileAsync(sa, ctx),
                BreakType => FlowSignal.Break,
                LoopBreakType => FlowSignal.LoopBreak,
                LoopContinueType => FlowSignal.LoopContinue,
                _ => await RunHandlerAsync(sa, ctx),
            };
            ctx.AddTrace(new SubActionTrace(sa.Id, sa.Type, name, signal == FlowSignal.Continue ? "ok" : signal.ToString(), sw.ElapsedMilliseconds));
            return signal;
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (UnsupportedOnHostException ex)
        {
            ctx.AddTrace(new SubActionTrace(sa.Id, sa.Type, name, "unsupported_on_host", sw.ElapsedMilliseconds, ex.Message));
            if (_warnedUnsupported.TryAdd(sa.Type, 0))
                _log.LogWarning("Sub-action {Name} ({Type}) is not available on this host: {Reason}", name, sa.Type, ex.Message);
            return FlowSignal.Continue;
        }
        catch (Exception ex)
        {
            ctx.AddTrace(new SubActionTrace(sa.Id, sa.Type, name, "error", sw.ElapsedMilliseconds, ex.Message));
            _log.LogWarning(ex, "Sub-action {Name} ({Type}) in action {Action} failed", name, sa.Type, ctx.Action.Name);
            return FlowSignal.Continue;
        }
    }

    private async Task<FlowSignal> RunHandlerAsync(SubActionDef sa, RunContext ctx)
    {
        var handler = _registry.Get(sa.Type);
        if (handler is null)
            throw new UnsupportedOnHostException($"{SubActionTypes.NameOf(sa.Type)} is not implemented in CherBot");
        return await handler.ExecuteAsync(sa, ctx);
    }

    private async Task<FlowSignal> RunIfAsync(SubActionDef sa, RunContext ctx)
    {
        var matched = Conditions.Evaluate(ctx, sa.Str("input"), sa.Int("operation"), sa.Str("value"), sa.Bool("autoType", true));
        var children = sa.SubActions ?? [];
        var groupType = matched ? IfTrueType : IfFalseType;
        var group = children.FirstOrDefault(c => c.Type == groupType);
        if (group is not null) return await ExecuteListAsync(group.SubActions, ctx, group.Bool("random"));

        // Tolerate hand-written actions without the IfTrue/IfFalse wrapper: plain children form the true branch.
        if (matched && children.All(c => c.Type is not IfTrueType and not IfFalseType))
            return await ExecuteListAsync(children, ctx);
        return FlowSignal.Continue;
    }

    private async Task<FlowSignal> RunSwitchAsync(SubActionDef sa, RunContext ctx)
    {
        var input = ctx.Parse(sa.Str("input"));
        var autoType = sa.Bool("autoType");
        var children = (sa.SubActions ?? []).Where(c => c.Enabled).OrderBy(c => c.Index).ToList();
        foreach (var c in children.Where(c => c.Type == SwitchCaseType))
        {
            var cmp = c.Bool("caseSensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var raw in c.StrList("values"))
            {
                var v = ctx.Parse(raw);
                var equal = autoType && CherBotJson.TryNumber(input, out var a) && CherBotJson.TryNumber(v, out var b)
                    ? a.Equals(b)
                    : string.Equals(input, v, cmp);
                if (equal) return await ExecuteListAsync(c.SubActions, ctx, c.Bool("random"));
            }
        }
        var fallback = children.FirstOrDefault(c => c.Type == SwitchDefaultType);
        return fallback is null ? FlowSignal.Continue : await ExecuteListAsync(fallback.SubActions, ctx, fallback.Bool("random"));
    }

    private async Task<FlowSignal> RunWhileAsync(SubActionDef sa, RunContext ctx)
    {
        var iterations = 0;
        while (Conditions.Evaluate(ctx, sa.Str("input"), sa.Int("operation"), sa.Str("value"), sa.Bool("autoType", true), sa.Bool("ignoreCase")))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (++iterations > MaxLoopIterations)
            {
                _log.LogWarning("While loop in {Action} stopped after {Max} iterations", ctx.Action.Name, MaxLoopIterations);
                break;
            }
            var signal = await ExecuteListAsync(sa.SubActions, ctx);
            if (signal == FlowSignal.LoopBreak) break;
            if (signal == FlowSignal.Break) return FlowSignal.Break;
            if (iterations % 1000 == 0) await Task.Yield();
        }
        return FlowSignal.Continue;
    }
}
