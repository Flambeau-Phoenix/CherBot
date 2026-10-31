using System.Collections.Concurrent;

namespace CherBot.Core.Engine;

/// <summary>Named one-shot rendezvous for Send/Wait For Local Signal.</summary>
public sealed class SignalHub
{
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<Dictionary<string, object?>>>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Dictionary<string, object?>>> _queued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Delivers a signal to every current waiter. When nobody waits and <paramref name="queueIfNoWaiter"/> is set, the signal is kept for the next waiter.</summary>
    public int Send(string name, Dictionary<string, object?> args, bool queueIfNoWaiter)
    {
        List<TaskCompletionSource<Dictionary<string, object?>>>? waiting = null;
        if (_waiters.TryGetValue(name, out var list))
        {
            lock (list)
            {
                waiting = [.. list];
                list.Clear();
            }
        }
        if (waiting is { Count: > 0 })
        {
            foreach (var w in waiting) w.TrySetResult(new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase));
            return waiting.Count;
        }
        if (queueIfNoWaiter) _queued.GetOrAdd(name, _ => new()).Enqueue(args);
        return 0;
    }

    /// <summary>Waits for <paramref name="name"/>. Returns the signal's arguments, or null on timeout.</summary>
    public async Task<Dictionary<string, object?>?> WaitAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        if (_queued.TryGetValue(name, out var q) && q.TryDequeue(out var queued)) return queued;

        var tcs = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _waiters.GetOrAdd(name, _ => []);
        lock (list) list.Add(tcs);
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (list) list.Remove(tcs);
        }
    }
}
