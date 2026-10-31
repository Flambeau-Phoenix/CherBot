using CherBot.Integrations.Loyalty;
using CherBot.Storage;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

/// <summary>/v1/loyalty and /v1/points: settings and point administration.</summary>
internal static class LoyaltyEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.MapGet("/loyalty/settings", (LoyaltyService loyalty) => Json(new { settings = loyalty.Settings, live = loyalty.IsLive }));
        v1.MapPut("/loyalty/settings", (LoyaltySettings settings, LoyaltyService loyalty) =>
        {
            loyalty.Save(settings);
            return Json(loyalty.Settings);
        });

        v1.MapGet("/points", (CherBotDb db, string? search, string? platform, int? limit) =>
            Json(new { points = db.SearchPoints(platform ?? "twitch", search, Math.Clamp(limit ?? 100, 1, 1000)) }));
        v1.MapPost("/points/give-active", async (GiveIn body, LoyaltyService loyalty, CancellationToken ct) =>
            Json(new { paid = await loyalty.GiveAllAsync(body.Amount, ct), amount = body.Amount }));
        v1.MapPost("/points/watch-now", async (LoyaltyService loyalty, CancellationToken ct) =>
            Json(new { paid = await loyalty.AwardWatchTimeAsync(ct) }));
        v1.MapDelete("/points/{platform}", (string platform, CherBotDb db) => Json(new { removed = db.ResetPoints(platform) }));
    }

    internal sealed record GiveIn(long Amount);
}
