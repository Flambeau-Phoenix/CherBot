using CherBot.Core.Config;
using CherBot.Integrations.Games;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

/// <summary>/v1/games: settings, live state, and the seeded game commands.</summary>
internal static class GamesEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.MapGet("/games/settings", (GameService games) => Json(games.Settings));
        v1.MapPut("/games/settings", (GameSettings settings, GameService games) =>
        {
            games.Save(settings);
            return Json(games.Settings);
        });
        v1.MapGet("/games/state", (GameService games) => Json(new { heist = games.HeistState(), duels = games.PendingDuels() }));
        v1.MapGet("/games/commands", (ConfigService config) =>
            Json(new { commands = config.Commands.Where(c => c.Group == GameService.Group) }));
    }
}
