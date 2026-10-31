using CherBot.Core.Model;

namespace CherBot.Core.Engine;

/// <summary>Executes one or more sub-action types (by Streamer.bot type id).</summary>
public interface ISubActionHandler
{
    IReadOnlyCollection<int> Types { get; }
    ValueTask<FlowSignal> ExecuteAsync(SubActionDef subAction, RunContext ctx);
}

/// <summary>Convenience base: map type ids to small delegates.</summary>
public abstract class SubActionHandlerGroup : ISubActionHandler
{
    private readonly Dictionary<int, Func<SubActionDef, RunContext, ValueTask<FlowSignal>>> _map = new();

    public IReadOnlyCollection<int> Types => _map.Keys;

    public ValueTask<FlowSignal> ExecuteAsync(SubActionDef subAction, RunContext ctx) => _map[subAction.Type](subAction, ctx);

    protected void On(int type, Func<SubActionDef, RunContext, ValueTask<FlowSignal>> handler) => _map[type] = handler;

    protected void On(int type, Func<SubActionDef, RunContext, Task> handler) =>
        _map[type] = async (s, c) => { await handler(s, c); return FlowSignal.Continue; };

    protected void On(int type, Action<SubActionDef, RunContext> handler) =>
        _map[type] = (s, c) => { handler(s, c); return ValueTask.FromResult(FlowSignal.Continue); };
}

public sealed class SubActionRegistry
{
    private readonly Dictionary<int, ISubActionHandler> _handlers = new();

    public SubActionRegistry(IEnumerable<ISubActionHandler> handlers)
    {
        foreach (var h in handlers)
            foreach (var t in h.Types)
                _handlers[t] = h;
    }

    public ISubActionHandler? Get(int type) => _handlers.GetValueOrDefault(type);

    public IReadOnlyCollection<int> SupportedTypes => _handlers.Keys;
}

/// <summary>Marker exception: the sub-action cannot run on this host (desktop-only feature on a headless box).</summary>
public sealed class UnsupportedOnHostException(string message) : Exception(message);
