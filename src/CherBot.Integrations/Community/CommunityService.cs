using CherBot.Core.Config;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Community;

/// <summary>Seeds the !first, !points, !leaderboard and !disc commands once, as ordinary editable actions. Points are awarded by the loyalty service.</summary>
public sealed class CommunityService(CherBotDb db, ConfigService config, CommunityOptions options, ILogger<CommunityService> log) : IHostedService
{
    private const string SeedKey = "seed.community.v1";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Enabled && options.SeedDefaults && db.GetKv(SeedKey) is null) Seed();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal void Seed()
    {
        const string group = "CherBot Community";
        Seeding.CommandAction(config, group, "!first", "!first\n!1st\n!firstwords", [Seeding.Step(CommunityHandlers.FirstWords), Seeding.Reply()]);
        Seeding.CommandAction(config, group, "!points", "!points", [Seeding.Step(CommunityHandlers.Points, new { platform = "twitch" }), Seeding.Reply()]);
        Seeding.CommandAction(config, group, "!leaderboard", "!leaderboard\n!lb", [Seeding.Step(CommunityHandlers.Leaderboard, new { count = 5, platform = "twitch" }), Seeding.Reply()]);
        Seeding.CommandAction(config, group, "!disc", "!disc\n!discord\n!hub", [Seeding.Step(CommunityHandlers.DiscordHub, new { defaultUrl = options.DefaultDiscordUrl }), Seeding.Reply()]);
        db.SetKv(SeedKey, "true");
        log.LogInformation("Seeded community commands: !first, !points, !leaderboard, !disc");
    }
}
