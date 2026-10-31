using System.Text.Json;
using CherBot.Core;
using CherBot.Host.Security;
using CherBot.Integrations.Community;
using CherBot.Integrations.Twitch;
using CherBot.Storage;

namespace CherBot.Host.Api;

internal static class TwitchEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<ApiToken>().IsAuthorized(ctx.HttpContext) ? await next(ctx) : Results.Unauthorized());

        static IResult Json(object? v) => Results.Json(v, CherBotJson.Options);
        static bool TryRole(string role, out TwitchRole r) => Enum.TryParse(role, true, out r);

        v1.MapGet("/twitch/status", (TwitchService tw) => Json(tw.GetStatus()));
        v1.MapPost("/twitch/reconnect", (TwitchService tw) => { tw.Reconnect(); return Results.Accepted(); });

        v1.MapPost("/twitch/auth/{role}/device", async (string role, TwitchAuth auth, CancellationToken ct) =>
        {
            if (!TryRole(role, out var r)) return Results.BadRequest(new { error = "role must be broadcaster or bot" });
            try { return Json(await auth.StartDeviceFlowAsync(r, ct)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        v1.MapGet("/twitch/auth/{role}", (string role, TwitchAuth auth) =>
        {
            if (!TryRole(role, out var r)) return Results.BadRequest();
            var t = auth.Get(r);
            return Json(new
            {
                linked = t is not null, login = t?.Login, userId = t?.UserId, expiresAt = t?.ExpiresAt, scopes = t?.Scopes, flow = auth.Flow(r),
            });
        });
        v1.MapDelete("/twitch/auth/{role}", (string role, TwitchAuth auth) =>
        {
            if (!TryRole(role, out var r)) return Results.BadRequest();
            auth.Logout(r);
            return Results.NoContent();
        });
        v1.MapGet("/twitch/auth/{role}/authorize-url", (string role, HttpContext ctx, TwitchAuth auth, TwitchOptions opts) =>
        {
            if (!TryRole(role, out var r)) return Results.BadRequest();
            var redirect = opts.RedirectUri ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}/twitch/callback";
            try { return Json(new { url = auth.BuildAuthorizeUrl(r, redirect), redirectUri = redirect }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // OAuth redirect target: protected by the one-time state value instead of the API token.
        app.MapGet("/twitch/callback", async (HttpContext ctx, string? code, string? state, string? error, TwitchAuth auth, TwitchOptions opts, CancellationToken ct) =>
        {
            if (error is not null || code is null || state is null)
                return Results.Content($"<h1>Twitch login failed</h1><p>{System.Net.WebUtility.HtmlEncode(error ?? "missing code")}</p>", "text/html");
            var redirect = opts.RedirectUri ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}/twitch/callback";
            try
            {
                var token = await auth.ExchangeCodeAsync(state, code, redirect, ct);
                return Results.Content($"<h1>Linked {System.Net.WebUtility.HtmlEncode(token.DisplayName)}</h1><p>You can close this tab.</p>", "text/html");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Content($"<h1>Twitch login failed</h1><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>", "text/html");
            }
        });

        v1.MapPost("/twitch/chat", async (ChatSendIn body, TwitchService tw, CancellationToken ct) =>
            Json(await tw.SendChatAsync(body.Message, body.UseBot, true, body.ReplyTo, ct)));

        // Feed a raw EventSub notification event through the normal pipeline (testing without Twitch).
        v1.MapPost("/twitch/eventsub/loopback", (LoopbackIn body, TwitchService tw) =>
        {
            var events = tw.HandleNotification(body.Type, body.Event);
            return Json(new { published = events.Select(e => new { e.Source, e.Type, e.TypeId, e.Seq }) });
        });

        v1.MapGet("/twitch/rewards", (TwitchService tw) => Json(new { rewards = tw.RewardSettings().Values }));
        v1.MapPut("/twitch/rewards/{rewardId}", (string rewardId, RewardIn body, TwitchService tw) =>
        {
            tw.SaveRewardSetting(new TwitchService.RewardSetting(rewardId, body.SkipQueue, body.Group));
            return Results.NoContent();
        });

        v1.MapGet("/leaderboard", (CherBotDb db, int? limit, string? platform) =>
            Json(new { leaderboard = db.GetLeaderboard(platform ?? "twitch", Math.Clamp(limit ?? 10, 1, 100)) }));
        v1.MapPut("/points/{platform}/{userId}", (string platform, string userId, PointsIn body, CherBotDb db) =>
            Json(new { points = body.Add ? db.AddPoints(platform, userId, body.UserName ?? "", body.Points) : db.SetPoints(platform, userId, body.UserName ?? "", body.Points) }));
        v1.MapGet("/first-words", (CherBotDb db, int? limit) => Json(new { firstWords = db.GetFirstWords(Math.Clamp(limit ?? 50, 1, 1000)) }));
        v1.MapDelete("/first-words", (CherBotDb db) => Json(new { cleared = db.ResetFirstWords() }));

        v1.MapPost("/migrate/python", (MigrateIn body, PythonMigration migration) =>
        {
            try { return Json(migration.Run(body.Path)); }
            catch (FileNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    internal sealed record ChatSendIn(string Message, bool UseBot = true, string? ReplyTo = null);
    internal sealed record LoopbackIn(string Type, JsonElement Event);
    internal sealed record RewardIn(bool SkipQueue, string? Group);
    internal sealed record PointsIn(long Points, string? UserName, bool Add = false);
    internal sealed record MigrateIn(string Path);
}
