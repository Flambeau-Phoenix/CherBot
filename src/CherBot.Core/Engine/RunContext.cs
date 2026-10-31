using System.Collections.Concurrent;
using CherBot.Core.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Engine;

/// <summary>How control continues after a sub-action.</summary>
public enum FlowSignal
{
    Continue,
    /// <summary>Stop the whole action (Break sub-action, or C# code returning false).</summary>
    Break,
    LoopBreak,
    LoopContinue,
}

public sealed record SubActionTrace(Guid Id, int Type, string Name, string Status, long Ms, string? Error = null);

/// <summary>State for one running action: its arguments, services and a trace of executed sub-actions.</summary>
public sealed class RunContext
{
    private readonly ConcurrentQueue<SubActionTrace> _trace = new();

    public RunContext(Guid runId, ActionDef action, IDictionary<string, object?> args, IServiceProvider services, ILogger log, CancellationToken ct, int depth = 0)
    {
        RunId = runId;
        Action = action;
        Args = new ConcurrentDictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        Services = services;
        Log = log;
        Cancellation = ct;
        Depth = depth;
    }

    public Guid RunId { get; }
    public ActionDef Action { get; }
    public ConcurrentDictionary<string, object?> Args { get; }
    public IServiceProvider Services { get; }
    public ILogger Log { get; }
    public CancellationToken Cancellation { get; }
    /// <summary>Nesting depth of inline Run Action calls, used to stop runaway recursion.</summary>
    public int Depth { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();
    public T? OptionalService<T>() => Services.GetService<T>();

    public string Parse(string? text) => Service<TemplateEngine>().Parse(text, Args);

    public void SetArg(string name, object? value)
    {
        if (!string.IsNullOrWhiteSpace(name)) Args[name] = value;
    }

    public object? GetArg(string name) => Args.TryGetValue(name, out var v) ? v : null;

    public string ArgString(string name, string fallback = "") => Args.TryGetValue(name, out var v) && v is not null ? CherBotJson.Format(v) : fallback;

    internal void AddTrace(SubActionTrace t)
    {
        if (_trace.Count < 500) _trace.Enqueue(t);
    }

    public IReadOnlyList<SubActionTrace> Trace => _trace.ToList();
}
