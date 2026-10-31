using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Generated;
using CherBot.Integrations.Community;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Obs;

/// <summary>Chat-driven OBS scene switching: Scene From Chat (50020) and List Scenes (50021).</summary>
public sealed class SceneCommandHandlers : SubActionHandlerGroup
{
    public const int SceneFromChat = 50020, ListScenes = 50021;

    static SceneCommandHandlers()
    {
        SubActionTypes.RegisterCustom(new(SceneFromChat, "CherBotSceneFromChat", "OBS: Scene From Chat", "CherBot / OBS", ["allowedScenes:List<string>", "connectionId:Guid"]));
        SubActionTypes.RegisterCustom(new(ListScenes, "CherBotListScenes", "OBS: List Scenes", "CherBot / OBS", ["allowedScenes:List<string>", "connectionId:Guid"]));
    }

    public SceneCommandHandlers()
    {
        On(SceneFromChat, async (s, c) =>
        {
            var obs = c.Service<ObsService>().Client(s.GuidOf("connectionId"));
            if (obs is null) { c.SetArg("reply", "OBS isn't connected."); return; }
            var scenes = await AvailableScenes(obs, s, c);
            var wanted = c.ArgString("rawInput").Trim();
            if (wanted.Length == 0) { c.SetArg("reply", $"Usage: {c.ArgString("command", "!scene")} <name>. Scenes: {string.Join(", ", scenes)}"); return; }
            var (match, candidates) = Match(scenes, wanted);
            if (match is null)
            {
                c.SetArg("reply", candidates.Count > 1
                    ? $"'{wanted}' matches several scenes: {string.Join(", ", candidates)}. Be more specific."
                    : $"No scene matches '{wanted}'. Scenes: {string.Join(", ", scenes)}");
                return;
            }
            await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = match }, c.Cancellation);
            c.SetArg("sceneName", match);
            c.SetArg("reply", $"Switched to {match}.");
        });
        On(ListScenes, async (s, c) =>
        {
            var obs = c.Service<ObsService>().Client(s.GuidOf("connectionId"));
            c.SetArg("reply", obs is null ? "OBS isn't connected." : $"Scenes: {string.Join(", ", await AvailableScenes(obs, s, c))}");
        });
    }

    private static async Task<List<string>> AvailableScenes(ObsClient obs, CherBot.Core.Model.SubActionDef s, RunContext c)
    {
        var all = await ListAsync(obs, c.Cancellation);
        var allowed = s.StrList("allowedScenes").Where(x => x.Length > 0).ToList();
        return allowed.Count == 0 ? all : all.Where(x => allowed.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Scene names in the order OBS shows them (top first).</summary>
    public static async Task<List<string>> ListAsync(ObsClient obs, CancellationToken ct)
    {
        var res = await obs.RequestAsync("GetSceneList", null, ct);
        return res.GetProperty("scenes").EnumerateArray()
            .OrderByDescending(x => x.TryGetProperty("sceneIndex", out var i) ? i.GetInt32() : 0)
            .Select(x => x.GetProperty("sceneName").GetString()!).ToList();
    }

    /// <summary>Exact (case-insensitive), then a unique prefix, then a unique substring.</summary>
    public static (string? Match, List<string> Candidates) Match(IReadOnlyList<string> scenes, string wanted)
    {
        var exact = scenes.FirstOrDefault(x => string.Equals(x, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, [exact]);
        var prefix = scenes.Where(x => x.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1) return (prefix[0], prefix);
        if (prefix.Count > 1) return (null, prefix);
        var contains = scenes.Where(x => x.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return contains.Count == 1 ? (contains[0], contains) : (null, contains);
    }
}

/// <summary>Seeds the !scene (moderators) and !scenes commands once, disabled.</summary>
public sealed class SceneCommandSeeder(ConfigService config, CherBotDb db, ILogger<SceneCommandSeeder> log) : IHostedService
{
    public const string Group = "CherBot Scenes";
    private const string SeedKey = "seed.scenes.v1";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (db.GetKv(SeedKey) is not null) return Task.CompletedTask;
        Seeding.CommandAction(config, Group, "!scene", "!scene", [Seeding.Step(SceneCommandHandlers.SceneFromChat, new { allowedScenes = Array.Empty<string>() }), Seeding.Reply()],
            enabled: false, configure: c => { c.Set("minRole", "moderator"); c.GlobalCooldown = 0; c.UserCooldown = 0; });
        Seeding.CommandAction(config, Group, "!scenes", "!scenes", [Seeding.Step(SceneCommandHandlers.ListScenes, new { allowedScenes = Array.Empty<string>() }), Seeding.Reply()],
            enabled: false, configure: c => { c.GlobalCooldown = 10; c.UserCooldown = 30; });
        db.SetKv(SeedKey, "true");
        log.LogInformation("Seeded scene commands (disabled): !scene, !scenes");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
