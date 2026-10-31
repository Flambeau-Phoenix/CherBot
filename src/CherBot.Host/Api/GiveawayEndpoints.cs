using CherBot.Integrations.Giveaways;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

/// <summary>/v1/giveaways: definitions, lifecycle controls, entries and winners.</summary>
internal static class GiveawayEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.MapGet("/giveaways", (GiveawayService svc) => Json(new { giveaways = svc.All.Select(svc.View) }));
        v1.MapPost("/giveaways", (GiveawayDef def, GiveawayService svc) =>
        {
            def.Id = Guid.NewGuid();
            return Json(svc.View(svc.Save(def)));
        });
        v1.MapGet("/giveaways/{id:guid}", (Guid id, GiveawayService svc) => svc.Get(id) is { } d ? Json(svc.View(d)) : Results.NotFound());
        v1.MapPut("/giveaways/{id:guid}", (Guid id, GiveawayDef def, GiveawayService svc) =>
        {
            if (svc.Get(id) is null) return Results.NotFound();
            def.Id = id;
            return Json(svc.View(svc.Save(def)));
        });
        v1.MapDelete("/giveaways/{id:guid}", (Guid id, GiveawayService svc) => svc.Delete(id) ? Results.NoContent() : Results.NotFound());

        v1.MapPost("/giveaways/{id:guid}/start", async (Guid id, StartIn? body, GiveawayService svc) => Op(svc, id, await svc.StartAsync(id, body?.DurationSeconds)));
        v1.MapPost("/giveaways/{id:guid}/stop", async (Guid id, GiveawayService svc) => Op(svc, id, await svc.StopAsync(id)));
        v1.MapPost("/giveaways/{id:guid}/draw", async (Guid id, DrawIn? body, GiveawayService svc) => Op(svc, id, await svc.DrawAsync(id, body?.Count)));
        v1.MapPost("/giveaways/{id:guid}/reroll", async (Guid id, GiveawayService svc) => Op(svc, id, await svc.RerollAsync(id)));
        v1.MapPost("/giveaways/{id:guid}/cancel", async (Guid id, GiveawayService svc) => Op(svc, id, await svc.CancelAsync(id)));
        v1.MapPost("/giveaways/{id:guid}/reset", async (Guid id, GiveawayService svc) => Op(svc, id, await svc.ResetAsync(id)));

        v1.MapGet("/giveaways/{id:guid}/entries", (Guid id, GiveawayService svc) =>
            svc.Get(id) is null ? Results.NotFound() : Json(new { entries = svc.Entries(id) }));
        v1.MapDelete("/giveaways/{id:guid}/entries/{platform}/{userId}", (Guid id, string platform, string userId, GiveawayService svc) =>
            svc.RemoveEntry(id, platform, userId) ? Results.NoContent() : Results.NotFound());
        v1.MapGet("/giveaways/{id:guid}/winners", (Guid id, GiveawayService svc) =>
            svc.Get(id) is null ? Results.NotFound() : Json(new { winners = svc.Winners(id) }));
        v1.MapPost("/giveaways/{id:guid}/winners/{winnerId:long}/claim", async (Guid id, long winnerId, GiveawayService svc) =>
            Op(svc, id, await svc.ClaimAsync(id, winnerId)));
        v1.MapGet("/giveaways/messages/defaults", () => Json(new GiveawayMessages()));
    }

    private static IResult Op(GiveawayService svc, Guid id, GiveawayResult result) =>
        svc.Get(id) is not { } d
            ? Results.NotFound(new { error = result.Message })
            : Results.Json(new { ok = result.Ok, message = result.Message, state = svc.View(d) }, CherBot.Core.CherBotJson.Options,
                statusCode: result.Ok ? 200 : 409);

    internal sealed record StartIn(int? DurationSeconds);
    internal sealed record DrawIn(int? Count);
}
