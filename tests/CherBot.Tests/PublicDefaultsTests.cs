using CherBot.Core.Config;
using CherBot.Integrations.Community;
using CherBot.Integrations.Modules;
using CherBot.Integrations.Obs;
using CherBot.Integrations.Songs;
using CherBot.Integrations.Twitch;
using CherBot.Storage;

namespace CherBot.Tests;

public sealed class PublicDefaultsTests
{
    [Fact]
    public async Task Fresh_install_keeps_starter_workflows_without_personal_data()
    {
        await using var host = new TestHost();
        var config = host.Get<ConfigService>();
        foreach (var name in new[] { "!first", "!points", "!leaderboard", "!disc" })
            Assert.Contains(config.Commands, c => c.Name == name && c.Enabled);
        foreach (var name in new[] { "!gamble", "!scene", "!giveaway", "!permit", "!sr" })
            Assert.Contains(config.Commands, c => c.Name == name && !c.Enabled);
        Assert.Empty(host.Get<CherBotDb>().GetLeaderboard("twitch", 10));
        Assert.Null(host.Get<TwitchAuth>().Get(TwitchRole.Broadcaster));
        Assert.Null(host.Get<TwitchAuth>().Get(TwitchRole.Bot));
        Assert.Empty(host.Get<ObsService>().Definitions());
        Assert.All(host.Get<ModuleRegistry>().All.Where(m => m.Id != "loyalty"), module => Assert.False(module.Enabled, module.Id));
        Assert.False(host.Get<SongRequestService>().Settings.Enabled);
        Assert.False(host.Get<UserTrackingService>().Settings.Enabled);
        Assert.False(host.Get<UserTrackingService>().Settings.ChatArchive);
        Assert.False(host.Get<QuickSetupService>().First.Enabled);
        Assert.Equal("", host.Get<CommunityOptions>().DefaultDiscordUrl);
        Assert.Empty(host.Get<UserTrackingService>().Users());
        Assert.Empty(host.Get<UserTrackingService>().Messages());
    }

    [Fact]
    public async Task Quick_start_is_available_from_the_embedded_help_route()
    {
        await using var host = new TestHost();
        var guide = await host.Api.GetStringAsync("/help/getting-started.md");
        Assert.Contains("# CherBot quick start", guide);
        Assert.Contains("Live Admin", guide);
        Assert.Contains("starter commands", guide);
    }
}
