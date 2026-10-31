using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Tests;

/// <summary>A started engine (core services only) on a throwaway database.</summary>
public sealed class TestEngine : IAsyncDisposable
{
    public string Dir { get; }
    public ServiceProvider Services { get; }
    private readonly List<IHostedService> _started = [];
    private readonly bool _ownsDir;

    public TestEngine(Action<IServiceCollection>? configure = null, string? dir = null, bool start = true)
    {
        _ownsDir = dir is null;
        Dir = dir ?? Path.Combine(Path.GetTempPath(), "cherbot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<ISoundPlayer, FakeSoundPlayer>();
        services.AddCherBotCore(Path.Combine(Dir, "cherbot.db"), new EngineOptions { PendingFlushSeconds = 3600 });
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();
        if (start) Start();
    }

    public void Start()
    {
        foreach (var hs in Services.GetServices<IHostedService>().Where(h => h is QueueManager or TriggerMatcher or CherBot.Core.Commands.CommandService))
        {
            hs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _started.Add(hs);
        }
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public ConfigService Config => Get<ConfigService>();
    public EventBus Bus => Get<EventBus>();

    public ActionDef AddAction(string name, params SubActionDef[] subActions) => AddAction(name, null, subActions);

    public ActionDef AddAction(string name, Action<ActionDef>? setup, params SubActionDef[] subActions)
    {
        var a = new ActionDef { Name = name, SubActions = [.. subActions] };
        for (var i = 0; i < a.SubActions.Count; i++) a.SubActions[i].Index = i;
        setup?.Invoke(a);
        return Config.SaveAction(a);
    }

    public Task<RunResult> Run(ActionDef action, Dictionary<string, object?>? args = null) =>
        Get<ActionDispatcher>().RunNowAsync(action, args, new DispatchSource("test"), CancellationToken.None);

    /// <summary>Collects events matching the filter.</summary>
    public List<BotEvent> Capture(Func<BotEvent, bool> filter)
    {
        var list = new List<BotEvent>();
        Bus.Subscribe("test-capture", e => { lock (list) list.Add(e); return Task.CompletedTask; }, filter);
        return list;
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hs in _started) await hs.StopAsync(CancellationToken.None);
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_ownsDir)
            try { Directory.Delete(Dir, true); } catch { /* temp */ }
    }

    // ── builders ─────────────────────────────────────────────────────────────

    public static SubActionDef Sa(int type, object? fields = null, params SubActionDef[] children)
    {
        var sa = new SubActionDef { Type = type };
        if (fields is not null)
        {
            var el = JsonSerializer.SerializeToElement(fields, CherBotJson.Options);
            foreach (var p in el.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "weight": sa.Weight = p.Value.GetDouble(); break;
                    case "enabled": sa.Enabled = p.Value.GetBoolean(); break;
                    default: sa.Set(p.Name, p.Value); break;
                }
            }
        }
        if (children.Length > 0)
        {
            sa.SubActions = [.. children];
            for (var i = 0; i < children.Length; i++) { children[i].Index = i; children[i].ParentId = sa.Id; }
        }
        return sa;
    }

    public static SubActionDef SetArg(string name, string value, bool autoType = false) => Sa(123, new { variableName = name, value, autoType });
    public static SubActionDef Delay(int ms) => Sa(1002, new { value = ms.ToString() });
    public static SubActionDef If(string input, int op, string value, SubActionDef[] whenTrue, SubActionDef[]? whenFalse = null) =>
        Sa(120, new { input, operation = op, value, autoType = true }, Sa(99901, null, whenTrue), Sa(99902, null, whenFalse ?? []));

    public static TriggerDef Trigger(string eventTypeName, object? fields = null)
    {
        var t = new TriggerDef { Type = CherBot.Core.Generated.EventTypes.Id(eventTypeName) };
        if (fields is not null)
            foreach (var p in JsonSerializer.SerializeToElement(fields, CherBotJson.Options).EnumerateObject()) t.Set(p.Name, p.Value);
        return t;
    }
}

public sealed class FakeSoundPlayer : ISoundPlayer
{
    public List<string> Played { get; } = [];
    public string Name => "fake";
    public Task PlayAsync(string file, float volume, bool wait, CancellationToken ct) { lock (Played) Played.Add(file); return Task.CompletedTask; }
    public void StopAll() { }
}
