using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Platform;
using CherBot.Integrations.Community;
using CherBot.Integrations.CustomCommands;
using CherBot.Integrations.Games;
using CherBot.Integrations.Giveaways;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Moderation;
using CherBot.Integrations.Modules;
using CherBot.Integrations.Obs;
using CherBot.Integrations.SpeakerBot;
using CherBot.Integrations.Twitch;
using CherBot.Integrations.Songs;
using Microsoft.Extensions.DependencyInjection;

namespace CherBot.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddTwitch(this IServiceCollection services, TwitchOptions twitch, CommunityOptions community)
    {
        services.AddSingleton(twitch);
        services.AddSingleton(community);
        services.AddHttpClient("twitch", c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<TwitchAuth>();
        services.AddSingleton<HelixClient>();
        services.AddSingleton<StreamSessionService>();
        services.AddHostedService(sp => sp.GetRequiredService<StreamSessionService>());
        services.AddSingleton<UserTrackingService>();
        services.AddHostedService(sp => sp.GetRequiredService<UserTrackingService>());
        services.AddSingleton<ClipService>();
        services.AddSingleton<QuickSetupService>();
        services.AddSingleton<ISubActionHandler, LiveCommunityHandlers>();
        services.AddSingleton<SongRequestService>();
        services.AddHostedService(sp => sp.GetRequiredService<SongRequestService>());
        services.AddSingleton<ISubActionHandler, SongHandlers>();
        services.AddSingleton<TwitchEventMapper>();
        services.AddSingleton<TwitchService>();
        services.AddHostedService(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<IIntegrationStatus>(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<IChatSender>(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<IBroadcasterInfo>(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<IActiveViewers>(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<IEventCatalog>(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<ISubActionHandler, TwitchHandlers>();

        services.AddSingleton<ISubActionHandler, CommunityHandlers>();
        services.AddSingleton<CommunityService>();
        services.AddHostedService(sp => sp.GetRequiredService<CommunityService>());
        services.AddSingleton<PythonMigration>();

        services.AddSingleton<LoyaltyService>();
        services.AddHostedService(sp => sp.GetRequiredService<LoyaltyService>());

        services.AddSingleton<IGameRandom, SharedGameRandom>();
        services.AddSingleton<GameService>();
        services.AddHostedService(sp => sp.GetRequiredService<GameService>());
        services.AddSingleton<ISubActionHandler, GameHandlers>();

        services.AddSingleton<GiveawayService>();
        services.AddHostedService(sp => sp.GetRequiredService<GiveawayService>());
        services.AddSingleton<ISubActionHandler, GiveawayHandlers>();

        services.AddSingleton<ChatModeration>();
        services.AddSingleton<ChatAlertsModule>();
        services.AddSingleton<EmotePyramidModule>();
        services.AddSingleton<SpamFilterModule>();
        services.AddSingleton<IBotModule>(sp => sp.GetRequiredService<ChatAlertsModule>());
        services.AddSingleton<IBotModule>(sp => sp.GetRequiredService<EmotePyramidModule>());
        services.AddSingleton<IBotModule>(sp => sp.GetRequiredService<SpamFilterModule>());
        services.AddHostedService(sp => sp.GetRequiredService<ChatAlertsModule>());
        services.AddHostedService(sp => sp.GetRequiredService<EmotePyramidModule>());
        services.AddHostedService(sp => sp.GetRequiredService<SpamFilterModule>());
        services.AddSingleton<ISubActionHandler, ModuleHandlers>();
        services.AddSingleton<IBotModule, LoyaltyModuleCard>();
        services.AddSingleton<IBotModule>(sp => new CommandGroupModuleCard(sp.GetRequiredService<ConfigService>(), "games", "Chat games",
            "Gamble, slots, coinflip, duels, heists and roulette, paid in loyalty points.", GameService.Group, "loyalty"));
        services.AddSingleton<IBotModule, GiveawaysModuleCard>();
        services.AddSingleton<IBotModule>(sp => new CommandGroupModuleCard(sp.GetRequiredService<ConfigService>(), "scene-commands", "Scene commands",
            "!scene and !scenes let moderators switch OBS scenes from chat.", SceneCommandSeeder.Group, "quicksetup"));
        services.AddSingleton<ModuleRegistry>();

        services.AddSingleton<CustomCommandService>();
        services.AddHostedService(sp => sp.GetRequiredService<CustomCommandService>());
        services.AddSingleton<ISubActionHandler, CustomCommandHandlers>();
        return services;
    }

    public static IServiceCollection AddObs(this IServiceCollection services, ObsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ObsService>();
        services.AddHostedService(sp => sp.GetRequiredService<ObsService>());
        services.AddSingleton<IIntegrationStatus>(sp => sp.GetRequiredService<ObsService>());
        services.AddSingleton<IEventCatalog>(sp => sp.GetRequiredService<ObsService>());
        services.AddSingleton<ISubActionHandler, ObsHandlers>();
        services.AddSingleton<ISubActionHandler, SceneCommandHandlers>();
        services.AddHostedService<SceneCommandSeeder>();
        return services;
    }

    public static IServiceCollection AddSpeakerBot(this IServiceCollection services, SpeakerBotOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<SpeakerBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<SpeakerBotService>());
        services.AddSingleton<IIntegrationStatus>(sp => sp.GetRequiredService<SpeakerBotService>());
        services.AddSingleton<IEventCatalog>(sp => sp.GetRequiredService<SpeakerBotService>());
        services.AddSingleton<ISubActionHandler, SpeakerBotHandlers>();
        return services;
    }
}
