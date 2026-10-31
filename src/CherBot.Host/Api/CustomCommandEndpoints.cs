using System.Text.Json;
using CherBot.Compat.Protocol;
using CherBot.Core.Config;
using CherBot.Integrations.CustomCommands;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

/// <summary>/v1/custom-commands: Nightbot-style commands, response previews and bulk import.</summary>
internal static class CustomCommandEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.MapGet("/custom-commands", (CustomCommandService svc, ConfigService config) =>
            Json(new { commands = svc.All.Select(d => View(d, config)) }));
        v1.MapPost("/custom-commands", (CustomCommandDef def, CustomCommandService svc, ConfigService config) =>
        {
            def.Id = Guid.NewGuid();
            def.CommandId = def.ActionId = null;
            return svc.Validate(def) is { } error ? Results.BadRequest(new { error }) : Json(View(svc.Save(def), config));
        });
        v1.MapGet("/custom-commands/{id:guid}", (Guid id, CustomCommandService svc, ConfigService config) =>
            svc.Get(id) is { } d ? Json(View(d, config)) : Results.NotFound());
        v1.MapPut("/custom-commands/{id:guid}", (Guid id, CustomCommandDef def, CustomCommandService svc, ConfigService config) =>
        {
            if (svc.Get(id) is not { } existing) return Results.NotFound();
            def.Id = id;
            def.CommandId = existing.CommandId;
            def.ActionId = existing.ActionId;
            def.Advanced = existing.Advanced;
            return svc.Validate(def) is { } error ? Results.BadRequest(new { error }) : Json(View(svc.Save(def), config));
        });
        v1.MapDelete("/custom-commands/{id:guid}", (Guid id, CustomCommandService svc) => svc.Delete(id) ? Results.NoContent() : Results.NotFound());

        // Renders a response with sample data without sending it. Works for unsaved definitions too (body.definition).
        v1.MapPost("/custom-commands/{id:guid}/test", async (Guid id, TestIn? body, CustomCommandService svc, CancellationToken ct) =>
        {
            var def = body?.Definition ?? svc.Get(id);
            if (def is null) return Results.NotFound();
            if (def.Id == Guid.Empty) def.Id = id;
            return Json(new { lines = await svc.RespondAsync(def, SampleArgs(body), ct, send: false) });
        });
        v1.MapPost("/custom-commands/preview", async (TestIn body, CustomCommandService svc, CancellationToken ct) =>
            body.Definition is null ? Results.BadRequest(new { error = "definition is required" })
                : Json(new { lines = await svc.RespondAsync(body.Definition, SampleArgs(body), ct, send: false) }));
        v1.MapPost("/custom-commands/import", (ImportIn body, CustomCommandService svc) =>
        {
            var (added, skipped) = svc.Import(body.Text ?? "");
            return Json(new { added, skipped });
        });
    }

    private static object View(CustomCommandDef d, ConfigService config) => new
    {
        command = d,
        commandMissing = d.CommandId is not { } c || config.GetCommand(c) is null,
        actionMissing = d.ActionId is not { } a || config.GetAction(a) is null,
    };

    private static Dictionary<string, object?> SampleArgs(TestIn? body)
    {
        var user = string.IsNullOrWhiteSpace(body?.User) ? "Viewer" : body.User!;
        var input = body?.Input ?? "@Friend";
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = user, ["userName"] = user.ToLowerInvariant(), ["userId"] = "sample-" + user.ToLowerInvariant(), ["rawInput"] = input,
            ["msgId"] = "", ["isTest"] = true,
        };
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++) args[$"input{i}"] = words[i];
        foreach (var (k, v) in StreamerBotApi.ArgsToDictionary(body?.Args)) args[k] = v;
        return args;
    }

    internal sealed record TestIn(CustomCommandDef? Definition, string? User, string? Input, JsonElement? Args);
    internal sealed record ImportIn(string? Text);
}
