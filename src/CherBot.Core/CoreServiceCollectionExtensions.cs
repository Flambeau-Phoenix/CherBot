using CherBot.Core.Commands;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Handlers;
using CherBot.Core.Platform;
using CherBot.Core.State;
using CherBot.Core.Timers;
using CherBot.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CherBot.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the engine: storage, bus, config, state, queues, executor, triggers, commands, timers and core sub-actions.</summary>
    public static IServiceCollection AddCherBotCore(this IServiceCollection services, string databasePath, EngineOptions? options = null)
    {
        services.AddSingleton(options ?? new EngineOptions());
        services.AddSingleton(new CherBotDb(databasePath));
        services.AddSingleton<EventBus>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<VariableService>();
        services.AddSingleton<TemplateEngine>();
        services.AddSingleton<SignalHub>();
        services.AddSingleton<SubActionRegistry>();
        services.AddSingleton<ActionExecutor>();
        services.AddSingleton<QueueManager>();
        services.AddSingleton<ActionDispatcher>();
        services.AddSingleton<TriggerMatcher>();
        services.AddSingleton<CommandService>();
        services.AddSingleton<TimerService>();
        services.AddSingleton<CodeTriggerRegistry>();
        services.AddSingleton<Moderation.ChatModerationPipeline>();

        services.AddSingleton<ISubActionHandler, CoreHandlers>();
        services.AddSingleton<IEventCatalog, CoreEventCatalog>();

        services.TryAddSingleton<IPathMapper, PathMapper>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IDesktopServices, NullDesktopServices>();
        services.AddHttpClient("fetch", c => c.Timeout = TimeSpan.FromSeconds(30));

        // Order matters: queues reload pending work before triggers start dispatching new work.
        services.AddHostedService(sp => sp.GetRequiredService<QueueManager>());
        services.AddHostedService(sp => sp.GetRequiredService<TriggerMatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<CommandService>());
        services.AddHostedService(sp => sp.GetRequiredService<TimerService>());
        services.AddHostedService<MaintenanceService>();
        return services;
    }
}
