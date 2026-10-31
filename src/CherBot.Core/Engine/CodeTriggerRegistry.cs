using System.Collections.Concurrent;
using CherBot.Core.Events;

namespace CherBot.Core.Engine;

public sealed record CodeTrigger(string Name, string EventName, string[] Categories);

/// <summary>Custom triggers registered by C# scripts (CPH.RegisterCustomTrigger) and fired by ExecuteCodeTrigger.</summary>
public sealed class CodeTriggerRegistry(EventBus bus)
{
    private readonly ConcurrentDictionary<string, CodeTrigger> _triggers = new(StringComparer.OrdinalIgnoreCase);

    public bool Register(string name, string eventName, string[] categories) =>
        _triggers.TryAdd(eventName, new CodeTrigger(name, eventName, categories));

    public IReadOnlyList<CodeTrigger> All => _triggers.Values.OrderBy(t => t.Name).ToList();

    /// <summary>Raises the Custom Code Event trigger for <paramref name="eventName"/>.</summary>
    public void Fire(string eventName, IDictionary<string, object?>? args)
    {
        var data = new Dictionary<string, object?>(args ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["eventName"] = eventName,
        };
        if (_triggers.TryGetValue(eventName, out var t)) data["triggerName"] = t.Name;
        bus.Publish(BotEvent.FromTypeName("CustomCodeEvent", data));
    }
}
