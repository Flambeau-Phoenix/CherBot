using System.Text.Json;
using CherBot.Integrations.Modules;
using CherBot.Storage;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

/// <summary>/v1/modules: the on/off overview, module settings, chat-alert tests, spam filter tests and the moderation log.</summary>
internal static class ModuleEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.MapGet("/modules", (ModuleRegistry modules) => Json(new
        {
            modules = modules.All.Select(m => new { m.Id, m.Name, m.Description, m.Enabled, m.Summary, m.Page, hasSettings = m.Settings is not null }),
        }));
        v1.MapGet("/modules/{id}/settings", (string id, ModuleRegistry modules) =>
            modules.Get(id) is { Settings: { } s } ? Json(s) : Results.NotFound());
        v1.MapPut("/modules/{id}/settings", (string id, JsonElement body, ModuleRegistry modules) =>
        {
            if (modules.Get(id) is not { Settings: not null } m) return Results.NotFound();
            try { m.SaveSettings(body); }
            catch (JsonException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Json(m.Settings);
        });
        v1.MapPost("/modules/{id}/enabled", (string id, EnabledIn body, ModuleRegistry modules) =>
        {
            if (modules.Get(id) is not { } m) return Results.NotFound();
            m.SetEnabled(body.Enabled);
            return Json(new { m.Id, m.Enabled, m.Summary });
        });

        v1.MapGet("/modules/chat-alerts/kinds", () => Json(new
        {
            kinds = ChatAlertsModule.Kinds.Select(k => new { k.Key, k.Label, k.EventType, k.AmountArg, sample = k.Sample }),
            defaults = ChatAlertsModule.Defaults(),
        }));
        v1.MapPost("/modules/chat-alerts/test", async (AlertTestIn body, ChatAlertsModule alerts) =>
            await alerts.TestAsync(body.Alert, body.Send) is { } text ? Json(new { text }) : Results.NotFound());

        v1.MapPost("/modules/spam-filter/test", (FilterTestIn body, SpamFilterModule filter) =>
        {
            var words = body.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var emotes = words.Where(w => body.Emotes?.Contains(w) == true).ToList();
            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["message"] = body.Message, ["user"] = "tester", ["userName"] = "tester", ["userId"] = "filter-test-" + Guid.NewGuid().ToString("N"),
                ["isSubscribed"] = body.Role is "subscriber" or "vip" or "moderator", ["isVip"] = body.Role == "vip", ["isModerator"] = body.Role == "moderator",
                ["emotes"] = emotes, ["emoteCount"] = emotes.Count, ["messageStripped"] = string.Join(' ', words.Where(w => body.Emotes?.Contains(w) != true)),
            };
            var hit = filter.Evaluate(data);
            return Json(new { blocked = hit is not null, filter = hit?.Name, reason = hit?.Rule.Reason, action = hit?.Rule.Action });
        });
        v1.MapGet("/modules/spam-filter/log", (int? limit, CherBotDb db) => Json(new { log = db.GetModLog(Math.Clamp(limit ?? 100, 1, 1000)) }));
        v1.MapPost("/modules/spam-filter/permit", (PermitIn body, SpamFilterModule filter) => Json(new { seconds = filter.Permit(body.Login) }));
    }

    internal sealed record EnabledIn(bool Enabled);
    internal sealed record AlertTestIn(string Alert, bool Send = false);
    internal sealed record FilterTestIn(string Message, string? Role, List<string>? Emotes);
    internal sealed record PermitIn(string Login);
}
