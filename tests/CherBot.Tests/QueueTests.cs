using System.Collections.Concurrent;
using CherBot.Core.Engine;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using CherBot.Core;
using Microsoft.Extensions.Logging.Abstractions;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class QueueTests
{
    private static (ActionDef Action, ConcurrentQueue<string> Log) Recorder(TestEngine e, string name, Guid? queue, int delayMs, bool alwaysRun = false)
    {
        var log = new ConcurrentQueue<string>();
        var a = e.AddAction(name, x => { x.QueueId = queue; x.AlwaysRun = alwaysRun; },
            Sa(1020, new { message = $"{name} start", logLevel = 2 }),
            Delay(delayMs));
        return (a, log);
    }

    private static QueuedRun Enqueue(TestEngine e, ActionDef a, bool skipQueue = false) =>
        e.Get<ActionDispatcher>().Dispatch(a, null, new DispatchSource("test", SkipQueue: skipQueue))!;

    [Fact]
    public async Task Blocking_queue_runs_one_action_at_a_time()
    {
        await using var e = new TestEngine();
        var q = e.Config.SaveQueue(new QueueDef { Name = "alerts", Blocking = true });
        var (a, _) = Recorder(e, "alert", q.Id, 300);
        var r1 = Enqueue(e, a);
        var r2 = Enqueue(e, a);
        var done1 = await r1.Done;
        var done2 = await r2.Done;
        Assert.True(done2.Started >= done1.Started.AddMilliseconds(done1.Ms - 20), "second run should start after the first finished");
    }

    [Fact]
    public async Task Default_queue_is_non_blocking()
    {
        await using var e = new TestEngine();
        var (a, _) = Recorder(e, "free", null, 400);
        var r1 = Enqueue(e, a);
        var r2 = Enqueue(e, a);
        var d1 = await r1.Done;
        var d2 = await r2.Done;
        Assert.True(d2.Started < d1.Started.AddMilliseconds(d1.Ms), "the second run should start before the first finishes");
    }

    [Fact]
    public async Task Skip_queue_bypasses_blocking()
    {
        await using var e = new TestEngine();
        var q = e.Config.SaveQueue(new QueueDef { Name = "alerts", Blocking = true });
        var (slow, _) = Recorder(e, "slow", q.Id, 600);
        var (fast, _) = Recorder(e, "fast", q.Id, 10);
        var r1 = Enqueue(e, slow);
        await Task.Delay(50);
        var r2 = Enqueue(e, fast, skipQueue: true);
        var d2 = await r2.Done;
        var d1 = await r1.Done;
        Assert.True(d2.Started < d1.Started.AddMilliseconds(d1.Ms), "skip-queue run should not wait for the blocking run");
    }

    [Fact]
    public async Task Paused_queue_holds_normal_actions_but_runs_always_run()
    {
        await using var e = new TestEngine();
        var q = e.Config.SaveQueue(new QueueDef { Name = "held", Blocking = true });
        var queues = e.Get<QueueManager>();
        queues.Pause(q.Id);
        var (normal, _) = Recorder(e, "normal", q.Id, 1);
        var (always, _) = Recorder(e, "always", q.Id, 1, alwaysRun: true);

        var held = Enqueue(e, normal);
        var vip = Enqueue(e, always);
        await vip.Done;
        await Task.Delay(200);
        Assert.False(held.Done.IsCompleted);
        Assert.Equal(1, queues.Status().Single(s => s.Id == q.Id).Pending);

        queues.Resume(q.Id);
        Assert.Equal("ok", (await held.Done.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task Resume_with_clear_drops_held_actions()
    {
        await using var e = new TestEngine();
        var q = e.Config.SaveQueue(new QueueDef { Name = "held", Blocking = false });
        var queues = e.Get<QueueManager>();
        queues.Pause(q.Id);
        var (normal, _) = Recorder(e, "normal", q.Id, 1);
        var held = Enqueue(e, normal);
        var dropped = queues.Resume(q.Id, clear: true);
        Assert.Equal(1, dropped);
        await Assert.ThrowsAnyAsync<TaskCanceledException>(() => held.Done);
    }

    [Fact]
    public async Task Pending_actions_survive_a_restart_unless_excluded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cherbot-tests", Guid.NewGuid().ToString("N"));
        Guid keepId, skipId;
        await using (var e = new TestEngine(dir: dir))
        {
            var q = e.Config.SaveQueue(new QueueDef { Name = "held", Blocking = true });
            var queues = e.Get<QueueManager>();
            queues.Pause(q.Id);
            var keep = e.AddAction("keep", x => x.QueueId = q.Id, SetArg("ran", "%seed%"));
            var skip = e.AddAction("skip", x => { x.QueueId = q.Id; x.ExcludeFromPending = true; }, SetArg("ran", "x"));
            keepId = keep.Id;
            skipId = skip.Id;
            e.Get<ActionDispatcher>().Dispatch(keep, new Dictionary<string, object?> { ["seed"] = "persisted-arg" }, new DispatchSource("test"));
            e.Get<ActionDispatcher>().Dispatch(skip, null, new DispatchSource("test"));
            queues.FlushPending();
        }

        await using var e2 = new TestEngine(dir: dir, start: false);
        var completed = e2.Capture(ev => ev.Type == "ActionCompleted");
        e2.Start();
        await WaitUntil(() => { lock (completed) return completed.Count >= 1; });
        await Task.Delay(200);
        lock (completed)
        {
            Assert.Single(completed);
            Assert.Equal(keepId.ToString(), completed[0].Data["actionId"]!.ToString());
        }
        var run = e2.Get<CherBot.Storage.CherBotDb>().GetRuns(10, keepId.ToString()).Single();
        Assert.Contains("persisted-arg", run.Json);
        Assert.Empty(e2.Get<CherBot.Storage.CherBotDb>().GetRuns(10, skipId.ToString()));
    }
}
