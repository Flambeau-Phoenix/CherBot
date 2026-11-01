using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Platform;
using CherBot.Integrations.Community;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

internal static class LiveEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        v1.MapGet("/live", (StreamSessionService sessions, UserTrackingService users) => Json(new
        { session = sessions.Current, reconciliationError = sessions.LastError, users = users.Users(limit: 20), messages = users.Messages().TakeLast(100) }));
        v1.MapGet("/users", (string? search, int? offset, int? limit, bool? history, StreamSessionService sessions, UserTrackingService users, CherBotDb db) =>
            history == true
                ? Json(new
                {
                    users = (object)db.SearchTrackedUsers(sessions.BroadcasterId, search, Math.Clamp(limit ?? 100, 1, 200), Math.Max(0, offset ?? 0))
                    .Select(u => new { u.UserId, u.Login, u.UserName, u.Role, u.LastSeen, u.Messages, u.Commands, u.Streams, points = db.GetPoints("twitch", u.UserId), rank = db.GetRank("twitch", u.UserId) }),
                    historical = true
                })
                : Json(new { users = (object)users.Users(search, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 200)), historical = false }));
        v1.MapGet("/users/{userId}", (string userId, StreamSessionService sessions, UserTrackingService users, CherBotDb db) => Json(new
        {
            user = (object?)users.Users(userId).FirstOrDefault(u => u.UserId == userId) ?? db.SearchTrackedUsers(sessions.BroadcasterId, userId, 200, 0).FirstOrDefault(u => u.UserId == userId),
            points = db.GetPoints("twitch", userId),
            rank = db.GetRank("twitch", userId),
            messages = users.Messages(userId).TakeLast(50),
            historyEnabled = users.Settings.Enabled || db.UserHistory(sessions.BroadcasterId, userId, null, null, null, null, null, 1, 0).Count > 0,
            history = db.UserHistory(sessions.BroadcasterId, userId, null, null, null, null, null, 50, 0)
        }));
        v1.MapGet("/tracking/settings", (UserTrackingService tracking, EngineOptions options, CherBotDb db) => Json(new
        {
            settings = tracking.Settings,
            error = tracking.LastError ?? db.PointsHistoryError,
            storageBytes = new FileInfo(db.Path).Length + (File.Exists(db.Path + "-wal") ? new FileInfo(db.Path + "-wal").Length : 0),
            diagnostics = new { eventDays = options.EventLogDays, runDays = options.RunHistoryDays }
        }));
        v1.MapPut("/tracking/settings", (TrackingSettings settings, UserTrackingService tracking) => { tracking.Save(settings); return Json(tracking.Settings); });
        v1.MapGet("/tracking/history", (string? userId, string? kind, string? streamId, string? since, string? until, string? search, int? limit, int? offset,
            StreamSessionService sessions, CherBotDb db) => Json(new { history = db.UserHistory(sessions.BroadcasterId, userId, kind, streamId, since, until, search, Math.Clamp(limit ?? 100, 1, 1000), Math.Max(0, offset ?? 0)) }));
        v1.MapDelete("/tracking/history", (string? userId, bool? diagnostics, StreamSessionService sessions, UserTrackingService tracking, CherBotDb db) =>
        {
            var removed = db.PurgeUserHistory(sessions.BroadcasterId, userId, diagnostics ?? true);
            tracking.Forget(userId);
            return Json(new { removed });
        });
        v1.MapGet("/tracking/export/{userId}", (string userId, int? offset, StreamSessionService sessions, CherBotDb db) => Json(new
        {
            broadcaster = sessions.BroadcasterId,
            userId,
            exportedAt = DateTimeOffset.UtcNow,
            history = db.UserHistory(sessions.BroadcasterId, userId, null, null, null, null, null, 1000, Math.Max(0, offset ?? 0)),
            pageSize = 1000,
            offset = Math.Max(0, offset ?? 0)
        }));
        v1.MapGet("/loyalty/earned", (string? period, StreamSessionService sessions, UserTrackingService tracking, CherBotDb db) =>
        {
            if (!tracking.Settings.Enabled) return Results.BadRequest(new { error = "Enable extended history to view earned-points rankings." });
            if (period is not ("stream" or "7d" or "30d")) throw new ArgumentException("Period must be stream, 7d or 30d.");
            var since = period == "stream" ? sessions.Current?.StartedAt ?? DateTimeOffset.UtcNow.ToString("O") : DateTimeOffset.UtcNow.AddDays(period == "7d" ? -7 : -30).ToString("O");
            return Json(new
            {
                points = db.EarnedLeaderboard(sessions.BroadcasterId, period == "stream" ? sessions.StreamId : null, since, 100),
                coverageStartedAt = tracking.Settings.StartedAt,
                since
            });
        });
        v1.MapGet("/quicksetup/{workflow}/preview", (string workflow, QuickSetupService setup) => Json(setup.Preview(workflow)));
        v1.MapGet("/quicksetup/actions/{id:guid}/managed", (Guid id, ConfigService config, CherBotDb db) =>
        {
            var action = config.GetAction(id) ?? throw new ArgumentException("Action no longer exists.");
            var json = CherBotJson.Serialize(action);
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
            return Json(new { managed = db.GetKv("setup.action." + id) == json, fingerprint });
        });
        v1.MapPost("/quicksetup/actions/{id:guid}/remember", (Guid id, ConfigService config, CherBotDb db) =>
        {
            var action = config.GetAction(id) ?? throw new ArgumentException("Action no longer exists.");
            db.SetKv("setup.action." + id, CherBotJson.Serialize(action));
            return Results.NoContent();
        });
        v1.MapPost("/quicksetup/{workflow}", (string workflow, SetupIn body, QuickSetupService setup) =>
        { setup.Apply(workflow, body.Enabled, body.Bonus, body.Replace); return Json(setup.Preview(workflow)); });
        v1.MapGet("/quicksetup/status", (QuickSetupService setup, ConfigService config, TwitchAuth auth, CherBotDb db, LoyaltyService loyalty) => Json(new
        {
            linked = auth.Get(TwitchRole.Broadcaster) is not null,
            clipsScope = auth.Get(TwitchRole.Broadcaster)?.Scopes.Contains("clips:edit") == true,
            first = setup.First,
            clipEnabled = config.Commands.Any(c => c.Command.Split('\n').Contains("!clip") && c.Enabled),
            firstConfigured = config.Actions.Any(a => a.AllSubActions().Any(s => s.Type == LiveCommunityHandlers.FirstClaim)),
            clipConfigured = config.Commands.Any(c => c.Command.Split('\n').Contains("!clip")),
            loyalty = loyalty.Settings,
            seryCompleted = db.GetKv("setup.sery.completed") == "true"
        }));
        v1.MapPut("/quicksetup/sery", (SeryIn body, CherBotDb db) => { db.SetKv("setup.sery.completed", body.Completed ? "true" : "false"); return Json(new { completed = body.Completed }); });
    }
    internal sealed record SetupIn(bool Enabled = true, int Bonus = 100, bool Replace = false);
    internal sealed record SeryIn(bool Completed);
}
