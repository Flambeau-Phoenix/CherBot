using System.Text.Json;
using CherBot.Compat.Protocol;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.State;
using CherBot.Integrations.Obs;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

/// <summary>Endpoints behind the UI's pickers and quick controls: OBS lists, Twitch channel tools, action helpers, user globals, UI settings.</summary>
internal static class HelperEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        MapObs(v1);
        MapTwitch(v1);
        MapQol(v1);
    }

    private static IResult NoObs => Results.Conflict(new { error = "No OBS connection is available" });

    private static async Task<IResult> Obs(ObsService obs, Guid? connectionId, Func<ObsClient, Task<IResult>> work)
    {
        if (obs.Client(connectionId) is not { } client) return NoObs;
        try { return await work(client); }
        catch (ObsRequestException ex) { return Results.BadRequest(new { error = ex.Message, code = ex.Code }); }
    }

    private static void MapObs(RouteGroupBuilder v1)
    {
        v1.MapGet("/obs/scenes", (ObsService obs, Guid? connectionId, CancellationToken ct) => Obs(obs, connectionId, async c =>
        {
            var res = await c.RequestAsync("GetSceneList", null, ct);
            return Json(new
            {
                connectionId = obs.ConnectedId(connectionId),
                current = res.TryGetProperty("currentProgramSceneName", out var cur) ? cur.GetString() : null,
                scenes = await SceneCommandHandlers.ListAsync(c, ct),
            });
        }));
        v1.MapGet("/obs/scene-items", (ObsService obs, string scene, Guid? connectionId, CancellationToken ct) => Obs(obs, connectionId, async c =>
        {
            var res = await c.RequestAsync("GetSceneItemList", new { sceneName = scene }, ct);
            return Json(new
            {
                items = res.GetProperty("sceneItems").EnumerateArray().Select(i => new
                {
                    id = i.GetProperty("sceneItemId").GetInt32(),
                    name = i.GetProperty("sourceName").GetString(),
                    enabled = i.TryGetProperty("sceneItemEnabled", out var e) && e.GetBoolean(),
                    isGroup = i.TryGetProperty("isGroup", out var g) && g.ValueKind == JsonValueKind.True,
                    kind = i.TryGetProperty("inputKind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
                }).Reverse(),
            });
        }));
        v1.MapGet("/obs/filters", (ObsService obs, string source, Guid? connectionId, CancellationToken ct) => Obs(obs, connectionId, async c =>
        {
            var res = await c.RequestAsync("GetSourceFilterList", new { sourceName = source }, ct);
            return Json(new
            {
                filters = res.GetProperty("filters").EnumerateArray().Select(f => new
                {
                    name = f.GetProperty("filterName").GetString(),
                    enabled = f.TryGetProperty("filterEnabled", out var e) && e.GetBoolean(),
                }),
            });
        }));
        v1.MapGet("/obs/inputs", (ObsService obs, Guid? connectionId, CancellationToken ct) => Obs(obs, connectionId, async c =>
        {
            var res = await c.RequestAsync("GetInputList", null, ct);
            return Json(new
            {
                inputs = res.GetProperty("inputs").EnumerateArray().Select(i => new
                {
                    name = i.GetProperty("inputName").GetString(),
                    kind = i.TryGetProperty("inputKind", out var k) ? k.GetString() : null,
                }),
            });
        }));
        v1.MapPost("/obs/reconnect", (ObsService obs) =>
        {
            obs.Reconnect();
            return Results.NoContent();
        });
        v1.MapPost("/obs/scene", (SceneIn body, ObsService obs, CancellationToken ct) => Obs(obs, body.ConnectionId, async c =>
        {
            await c.RequestAsync("SetCurrentProgramScene", new { sceneName = body.SceneName }, ct);
            return Results.NoContent();
        }));
        v1.MapPost("/obs/scene-item", (SceneItemIn body, ObsService obs, CancellationToken ct) => Obs(obs, body.ConnectionId, async c =>
        {
            await c.RequestAsync("SetSceneItemEnabled", new { sceneName = body.SceneName, sceneItemId = body.SceneItemId, sceneItemEnabled = body.Enabled }, ct);
            return Results.NoContent();
        }));
    }

    private static async Task<IResult> Helix(TwitchAuth auth, Func<string, Task<IResult>> work)
    {
        if (auth.Get(TwitchRole.Broadcaster) is not { } b) return Results.Conflict(new { error = "Twitch broadcaster is not linked" });
        try { return await work(b.UserId); }
        catch (HelixException ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
    }

    private static Dictionary<string, string?> Q(params (string, string?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static void MapTwitch(RouteGroupBuilder v1)
    {
        v1.MapGet("/twitch/rewards/list", (TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            var res = await helix.GetAsync("channel_points/custom_rewards", Q(("broadcaster_id", id)), ct: ct);
            var manageable = new HashSet<string>();
            try
            {
                var own = await helix.GetAsync("channel_points/custom_rewards", Q(("broadcaster_id", id), ("only_manageable_rewards", "true")), ct: ct);
                foreach (var r in own.GetProperty("data").EnumerateArray()) manageable.Add(r.GetProperty("id").GetString()!);
            }
            catch (HelixException) { /* optional */ }
            return Json(new
            {
                rewards = res.GetProperty("data").EnumerateArray().Select(r => new
                {
                    id = r.GetProperty("id").GetString(),
                    title = r.GetProperty("title").GetString(),
                    cost = r.GetProperty("cost").GetInt32(),
                    prompt = r.GetProperty("prompt").GetString(),
                    enabled = r.GetProperty("is_enabled").GetBoolean(),
                    paused = r.GetProperty("is_paused").GetBoolean(),
                    color = r.GetProperty("background_color").GetString(),
                    manageable = manageable.Contains(r.GetProperty("id").GetString()!),
                }).OrderBy(r => r.cost),
            });
        }));
        v1.MapGet("/twitch/channel", (TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            var ch = HelixClient.First(await helix.GetAsync("channels", Q(("broadcaster_id", id)), ct: ct));
            var stream = HelixClient.First(await helix.GetAsync("streams", Q(("user_id", id)), ct: ct));
            return Json(new
            {
                title = ch?.GetProperty("title").GetString(),
                gameId = ch?.GetProperty("game_id").GetString(),
                gameName = ch?.GetProperty("game_name").GetString(),
                tags = ch is { } c && c.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()).ToList() : [],
                live = stream is not null,
                viewers = stream?.GetProperty("viewer_count").GetInt32(),
                startedAt = stream?.GetProperty("started_at").GetString(),
            });
        }));
        v1.MapPatch("/twitch/channel", (ChannelIn body, TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            var patch = new Dictionary<string, object?>();
            if (body.Title is not null) patch["title"] = body.Title;
            if (body.GameId is not null) patch["game_id"] = body.GameId;
            if (patch.Count > 0) await helix.SendAsync(new HttpMethod("PATCH"), "channels", Q(("broadcaster_id", id)), patch, ct: ct);
            return Results.NoContent();
        }));
        v1.MapGet("/twitch/categories", (string query, TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async _ =>
        {
            var res = await helix.GetAsync("search/categories", Q(("query", query), ("first", "10")), ct: ct);
            return Json(new
            {
                categories = res.GetProperty("data").EnumerateArray().Select(c => new
                {
                    id = c.GetProperty("id").GetString(), name = c.GetProperty("name").GetString(), boxArt = c.GetProperty("box_art_url").GetString(),
                }),
            });
        }));
        v1.MapPost("/twitch/marker", (MarkerIn body, TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            var m = HelixClient.First(await helix.SendAsync(HttpMethod.Post, "streams/markers", body: new { user_id = id, description = body.Description ?? "" }, ct: ct));
            return Json(new { position = m?.GetProperty("position_seconds").GetInt32() });
        }));
        v1.MapPost("/twitch/clip", (TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            var clip = HelixClient.First(await helix.SendAsync(HttpMethod.Post, "clips", Q(("broadcaster_id", id)), ct: ct));
            var clipId = clip?.GetProperty("id").GetString();
            return Json(new { id = clipId, url = clipId is null ? null : $"https://clips.twitch.tv/{clipId}", editUrl = clip?.GetProperty("edit_url").GetString() });
        }));
        v1.MapPost("/twitch/commercial", (CommercialIn body, TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            await helix.SendAsync(HttpMethod.Post, "channels/commercial", body: new { broadcaster_id = id, length = Math.Clamp(body.Length, 30, 180) }, ct: ct);
            return Results.NoContent();
        }));
        v1.MapPost("/twitch/shoutout", (ShoutoutIn body, TwitchAuth auth, HelixClient helix, CancellationToken ct) => Helix(auth, async id =>
        {
            if (await helix.GetUserByLoginAsync(body.Login, ct) is not { } user) return Results.NotFound(new { error = $"Twitch user '{body.Login}' not found" });
            await helix.SendAsync(HttpMethod.Post, "chat/shoutouts",
                Q(("from_broadcaster_id", id), ("to_broadcaster_id", user.GetProperty("id").GetString()), ("moderator_id", id)), ct: ct);
            return Results.NoContent();
        }));
        v1.MapGet("/viewers", (TwitchService tw) => Json(new { viewers = tw.GetActiveViewers() }));
    }

    private static void MapQol(RouteGroupBuilder v1)
    {
        v1.MapPost("/actions/{id:guid}/duplicate", (Guid id, ConfigService cfg) =>
            cfg.GetAction(id) is { } a ? Json(cfg.SaveAction(Clone(a))) : Results.NotFound());

        v1.MapPost("/actions/{id:guid}/test", async (Guid id, TestIn? body, ConfigService cfg, CherBotDb db, ActionDispatcher dispatcher, CancellationToken ct) =>
        {
            if (cfg.GetAction(id) is not { } a) return Results.NotFound();
            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            string sample = "none";
            var trigger = a.Triggers.FirstOrDefault(t => t.Enabled) ?? a.Triggers.FirstOrDefault();
            BotEvent? ev = null;
            if (trigger is not null && EventTypes.Get(trigger.Type) is { } info && db.GetLatestEvent(info.Source, info.Type) is { } row)
            {
                using var doc = JsonDocument.Parse(row.Json);
                foreach (var p in doc.RootElement.EnumerateObject()) args[p.Name] = CherBotJson.ToClr(p.Value);
                sample = $"latest {info.Source}.{info.Type} event (#{row.Seq})";
                ev = new BotEvent(info.Source, info.Type, args, info.Id);
            }
            else if (body?.SampleArgs is { ValueKind: JsonValueKind.Object } s && s.EnumerateObject().Any())
            {
                foreach (var (k, v) in StreamerBotApi.ArgsToDictionary(s)) args[k] = v;
                sample = "example data";
            }
            foreach (var (k, v) in StreamerBotApi.ArgsToDictionary(body?.Args)) args[k] = v;
            args["isTest"] = true;
            var result = await dispatcher.RunNowAsync(a, args, new DispatchSource("test", trigger, ev), ct);
            return Json(new { sample, result });
        });

        v1.MapPut("/users/{platform}/{userId}/globals/{name}", (string platform, string userId, string name, UserGlobalIn body, VariableService vars) =>
        {
            vars.SetUser(platform, userId, name, body.Value is { } v ? CherBotJson.ToClr(v) : null, body.Persisted, body.UserLogin ?? "", body.UserName ?? "");
            return Results.NoContent();
        });
        v1.MapDelete("/users/{platform}/{userId}/globals/{name}", (string platform, string userId, string name, bool? persisted, VariableService vars) =>
            vars.UnsetUser(platform, userId, name, persisted ?? true) ? Results.NoContent() : Results.NotFound());

        v1.MapGet("/ui/settings", (CherBotDb db) => Results.Content(db.GetKv("ui.settings") ?? "{}", "application/json"));
        v1.MapPut("/ui/settings", (JsonElement body, CherBotDb db) =>
        {
            db.SetKv("ui.settings", body.GetRawText());
            return Results.NoContent();
        });
    }

    /// <summary>Deep copy with fresh ids; the copy starts disabled so its triggers don't double-fire.</summary>
    internal static ActionDef Clone(ActionDef source)
    {
        var copy = CherBotJson.Deserialize<ActionDef>(CherBotJson.Serialize(source))!;
        copy.Id = Guid.NewGuid();
        copy.Name = source.Name + " (copy)";
        copy.Enabled = false;
        foreach (var t in copy.Triggers) t.Id = Guid.NewGuid();
        void Renew(List<SubActionDef>? list, Guid? parent)
        {
            if (list is null) return;
            foreach (var s in list)
            {
                s.Id = Guid.NewGuid();
                s.ParentId = parent;
                Renew(s.SubActions, s.Id);
            }
        }
        Renew(copy.SubActions, null);
        return copy;
    }

    internal sealed record SceneIn(string SceneName, Guid? ConnectionId);
    internal sealed record SceneItemIn(string SceneName, int SceneItemId, bool Enabled, Guid? ConnectionId);
    internal sealed record ChannelIn(string? Title, string? GameId);
    internal sealed record MarkerIn(string? Description);
    internal sealed record CommercialIn(int Length = 30);
    internal sealed record ShoutoutIn(string Login);
    internal sealed record TestIn(JsonElement? Args, JsonElement? SampleArgs);
    internal sealed record UserGlobalIn(JsonElement? Value, bool Persisted = true, string? UserLogin = null, string? UserName = null);
}
