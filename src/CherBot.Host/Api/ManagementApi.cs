using System.Text.Json;
using CherBot.Compat.Import;
using CherBot.Compat.Protocol;
using CherBot.Core;
using CherBot.Core.Commands;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using CherBot.Core.State;
using CherBot.Core.Timers;
using CherBot.Host.Security;
using CherBot.Storage;
using Microsoft.AspNetCore.Mvc;

namespace CherBot.Host.Api;

/// <summary>The /v1 management API used by the web UI, the Windows GUI and scripts. Every route requires the API token.</summary>
internal static class ManagementApi
{
    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<ApiToken>();
            return token.IsAuthorized(ctx.HttpContext) ? await next(ctx) : Results.Unauthorized();
        });

        MapActions(v1);
        MapQueues(v1);
        MapCommands(v1);
        MapTimers(v1);
        MapState(v1);
        MapEvents(v1);
        MapMisc(v1);
    }

    private static IResult Json(object? value) => Results.Json(value, CherBotJson.Options);

    // ── actions ────────────────────────────────────────────────────────────

    private static void MapActions(RouteGroupBuilder v1)
    {
        v1.MapGet("/actions", (ConfigService cfg) => Json(new { actions = cfg.Actions }));
        v1.MapGet("/actions/{id:guid}", (Guid id, ConfigService cfg) => cfg.GetAction(id) is { } a ? Json(a) : Results.NotFound());
        v1.MapPost("/actions", (ActionDef a, ConfigService cfg) =>
        {
            if (string.IsNullOrWhiteSpace(a.Name)) return Results.BadRequest(new { error = "name is required" });
            return Json(cfg.SaveAction(a));
        });
        v1.MapPut("/actions/{id:guid}", (Guid id, ActionDef a, ConfigService cfg) =>
        {
            a.Id = id;
            return Json(cfg.SaveAction(a));
        });
        v1.MapDelete("/actions/{id:guid}", (Guid id, ConfigService cfg) => cfg.DeleteAction(id) ? Results.NoContent() : Results.NotFound());
        v1.MapPost("/actions/{id:guid}/enabled", (Guid id, EnabledIn body, ConfigService cfg) =>
        {
            if (cfg.GetAction(id) is not { } a) return Results.NotFound();
            a.Enabled = body.Enabled;
            return Json(cfg.SaveAction(a));
        });
        v1.MapPost("/actions/{id:guid}/run", async (Guid id, [FromBody] RunIn? body, ConfigService cfg, ActionDispatcher dispatcher, CancellationToken ct) =>
        {
            if (cfg.GetAction(id) is not { } a) return Results.NotFound();
            var args = StreamerBotApi.ArgsToDictionary(body?.Args);
            if (body?.Wait == true)
            {
                var result = await dispatcher.RunNowAsync(a, args, new DispatchSource("api"), ct);
                return Json(result);
            }
            var run = dispatcher.Dispatch(a, args, new DispatchSource("api"));
            return run is null ? Results.Conflict(new { error = "action is disabled" }) : Json(new { status = "queued", runId = run.Id });
        });
        v1.MapPost("/doaction", (JsonElement body, StreamerBotApi api) =>
        {
            var res = api.DoAction(body.TryGetProperty("action", out var act) ? act : null, body.TryGetProperty("args", out var args) ? args : null, "api");
            return res.Ok ? Json(new { status = "queued" }) : Results.NotFound(new { error = res.Error });
        });
        v1.MapGet("/runs", (CherBotDb db, int? limit, string? actionId) =>
            Json(new
            {
                runs = db.GetRuns(Math.Clamp(limit ?? 50, 1, 500), actionId).Select(r => new
                {
                    id = r.Id, started = r.Started, actionId = r.ActionId, actionName = r.ActionName, trigger = r.Trigger,
                    status = r.Status, ms = r.Ms, detail = JsonDocument.Parse(r.Json).RootElement,
                }),
            }));
    }

    // ── queues ─────────────────────────────────────────────────────────────

    private static Guid? QueueKey(string key, ConfigService cfg) =>
        key is "all" ? null : key is "default" ? QueueManager.DefaultQueueId : Guid.TryParse(key, out var g) ? g : cfg.FindQueue(null, key)?.Id ?? Guid.Empty;

    private static void MapQueues(RouteGroupBuilder v1)
    {
        v1.MapGet("/queues", (QueueManager qm, ConfigService cfg) => Json(new { queues = qm.Status(), definitions = cfg.Queues }));
        v1.MapPost("/queues", (QueueDef q, ConfigService cfg) =>
            string.IsNullOrWhiteSpace(q.Name) ? Results.BadRequest(new { error = "name is required" }) : Json(cfg.SaveQueue(q)));
        v1.MapPut("/queues/{id:guid}", (Guid id, QueueDef q, ConfigService cfg) => { q.Id = id; return Json(cfg.SaveQueue(q)); });
        v1.MapDelete("/queues/{id:guid}", (Guid id, ConfigService cfg) => cfg.DeleteQueue(id) ? Results.NoContent() : Results.NotFound());
        v1.MapPost("/queues/{key}/pause", (string key, QueueManager qm, ConfigService cfg) =>
        {
            qm.Pause(QueueKey(key, cfg));
            return Json(new { status = "paused", queue = key });
        });
        v1.MapPost("/queues/{key}/resume", (string key, bool? clear, QueueManager qm, ConfigService cfg) =>
            Json(new { status = "resumed", queue = key, dropped = qm.Resume(QueueKey(key, cfg), clear ?? false) }));
        v1.MapPost("/queues/{key}/clear", (string key, QueueManager qm, ConfigService cfg) =>
            Json(new { status = "cleared", queue = key, cleared = qm.Clear(QueueKey(key, cfg)) }));
    }

    // ── commands and timers ────────────────────────────────────────────────

    private static void MapCommands(RouteGroupBuilder v1)
    {
        v1.MapGet("/commands", (ConfigService cfg) => Json(new { commands = cfg.Commands }));
        v1.MapGet("/commands/{id:guid}", (Guid id, ConfigService cfg) => cfg.GetCommand(id) is { } c ? Json(c) : Results.NotFound());
        v1.MapPost("/commands", (CommandDef c, ConfigService cfg) =>
            string.IsNullOrWhiteSpace(c.Command) ? Results.BadRequest(new { error = "command text is required" }) : Json(cfg.SaveCommand(c)));
        v1.MapPut("/commands/{id:guid}", (Guid id, CommandDef c, ConfigService cfg) => { c.Id = id; return Json(cfg.SaveCommand(c)); });
        v1.MapDelete("/commands/{id:guid}", (Guid id, ConfigService cfg) => cfg.DeleteCommand(id) ? Results.NoContent() : Results.NotFound());
        v1.MapPost("/commands/{id:guid}/reset-counters", (Guid id, bool? users, CommandService svc) =>
        {
            svc.ResetCounters(id, users ?? false);
            return Results.NoContent();
        });
    }

    private static void MapTimers(RouteGroupBuilder v1)
    {
        v1.MapGet("/timers", (ConfigService cfg, TimerService svc) =>
            Json(new { timers = cfg.Timers.Select(t => new { timer = t, nextFire = svc.NextFireTime(t.Id) }) }));
        v1.MapPost("/timers", (TimerDef t, ConfigService cfg) => Json(cfg.SaveTimer(t)));
        v1.MapPut("/timers/{id:guid}", (Guid id, TimerDef t, ConfigService cfg) => { t.Id = id; return Json(cfg.SaveTimer(t)); });
        v1.MapDelete("/timers/{id:guid}", (Guid id, ConfigService cfg) => cfg.DeleteTimer(id) ? Results.NoContent() : Results.NotFound());
        v1.MapPost("/timers/{id:guid}/fire", (Guid id, ConfigService cfg, TimerService svc) =>
        {
            if (cfg.GetTimer(id) is not { } t) return Results.NotFound();
            svc.Fire(t);
            return Results.NoContent();
        });
    }

    // ── globals, users, quotes, groups ─────────────────────────────────────

    private static void MapState(RouteGroupBuilder v1)
    {
        v1.MapGet("/globals", (VariableService vars, bool? persisted) => Json(new { globals = vars.ListGlobals(persisted ?? true) }));
        v1.MapPut("/globals/{name}", (string name, GlobalIn body, VariableService vars) =>
        {
            vars.SetGlobal(name, body.Value is { } v ? CherBotJson.ToClr(v) : null, body.Persisted);
            return Results.NoContent();
        });
        v1.MapDelete("/globals/{name}", (string name, bool? persisted, VariableService vars) =>
            vars.UnsetGlobal(name, persisted ?? true) ? Results.NoContent() : Results.NotFound());
        v1.MapGet("/users/{platform}/globals", (string platform, string? name, string? userId, bool? persisted, VariableService vars) =>
            Json(new { variables = vars.ListUser(platform, name, userId, persisted ?? true) }));

        v1.MapGet("/quotes", (CherBotDb db) => Json(new { quotes = db.GetQuotes() }));
        v1.MapPost("/quotes", (QuoteIn q, CherBotDb db) =>
            Json(db.GetQuote(db.AddQuote(q.UserId ?? "", q.User ?? "", q.Platform ?? "twitch", q.Game ?? "", q.Quote))));
        v1.MapDelete("/quotes/{id:long}", (long id, CherBotDb db) => db.DeleteQuote(id) ? Results.NoContent() : Results.NotFound());

        v1.MapGet("/groups", (CherBotDb db, string? group) => Json(new { members = db.GetGroupUsers(group) }));
        v1.MapPost("/groups/{group}", (string group, GroupUserIn u, CherBotDb db) =>
        {
            db.AddGroupUser(group, u.Platform ?? "twitch", u.UserId, u.Login ?? "", u.UserName ?? "");
            return Results.NoContent();
        });
        v1.MapDelete("/groups/{group}/{platform}/{userId}", (string group, string platform, string userId, CherBotDb db) =>
            db.RemoveGroupUser(group, platform, userId) ? Results.NoContent() : Results.NotFound());
    }

    // ── events ─────────────────────────────────────────────────────────────

    private static void MapEvents(RouteGroupBuilder v1)
    {
        // Server-sent events with resume: ?since=<seq> or the Last-Event-ID header.
        v1.MapGet("/events", async (HttpContext ctx, EventBus bus, CherBotDb db, long? since) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var cursor = since ?? (long.TryParse(ctx.Request.Headers["Last-Event-ID"], out var last) ? last : bus.LastSeq);
            var ct = ctx.RequestAborted;

            var (reader, sub) = bus.Listen("sse-" + Guid.NewGuid().ToString("N"));
            using (sub)
            {
                foreach (var row in db.GetEventsSince(cursor, 500))
                {
                    await WriteSse(ctx, row.Seq, row.Source, row.Type, row.Timestamp, JsonDocument.Parse(row.Json).RootElement);
                    cursor = row.Seq;
                }
                await ctx.Response.Body.FlushAsync(ct);

                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var hbTask = Task.Run(async () =>
                {
                    while (await heartbeat.WaitForNextTickAsync(ct))
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }, ct);

                try
                {
                    await foreach (var ev in reader.ReadAllAsync(ct))
                    {
                        if (ev.Persist && ev.Seq <= cursor) continue;
                        await WriteSse(ctx, ev.Persist ? ev.Seq : null, ev.Source, ev.Type, ev.Timestamp.ToString("O"), ev.Data);
                        if (ev.Persist) cursor = ev.Seq;
                    }
                }
                catch (OperationCanceledException) { }
            }
        });

        v1.MapGet("/events/recent", (CherBotDb db, long? since, int? limit) =>
            Json(new
            {
                events = db.GetEventsSince(since ?? Math.Max(0, db.LastEventSeq() - Math.Clamp(limit ?? 100, 1, 1000)), Math.Clamp(limit ?? 100, 1, 1000))
                    .Select(r => new { seq = r.Seq, ts = r.Timestamp, source = r.Source, type = r.Type, data = JsonDocument.Parse(r.Json).RootElement }),
            }));

        // Raise any event by hand (testing triggers without the platform).
        v1.MapPost("/events/publish", (PublishIn body, EventBus bus) =>
        {
            var data = body.Data is { ValueKind: JsonValueKind.Object } d ? StreamerBotApi.ArgsToDictionary(d) : new Dictionary<string, object?>();
            var ev = new BotEvent(body.Source, body.Type, data);
            bus.Publish(ev);
            return Json(new { seq = ev.Seq, typeId = ev.TypeId });
        });

        // Simulate a chat message (runs commands and chat-message triggers).
        v1.MapPost("/chat/simulate", (ChatIn body, EventBus bus, IServiceProvider sp) =>
        {
            var user = body.User ?? "tester";
            var words = body.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var emotes = words.Where(w => body.Emotes?.Contains(w) == true).ToList();
            var ev = BotEvent.FromTypeName("TwitchChatMessage", new Dictionary<string, object?>
            {
                ["message"] = body.Message, ["user"] = user, ["userName"] = user.ToLowerInvariant(), ["userId"] = body.UserId ?? "sim-" + user.ToLowerInvariant(),
                ["platform"] = "twitch", ["userType"] = "twitch", ["msgId"] = Guid.NewGuid().ToString(),
                ["isModerator"] = body.Role is "moderator" or "broadcaster", ["isVip"] = body.Role == "vip",
                ["isSubscribed"] = body.Role is "subscriber" or "vip" or "moderator" or "broadcaster", ["isBroadcaster"] = body.Role == "broadcaster",
                ["role"] = body.Role switch { "broadcaster" => 4, "moderator" => 3, "vip" => 2, _ => 1 },
                ["emotes"] = emotes, ["emoteCount"] = emotes.Count,
                ["messageStripped"] = string.Join(' ', words.Where(w => body.Emotes?.Contains(w) != true)),
                ["isInternal"] = body.Internal, ["isTest"] = true,
            });
            if (sp.GetService<CherBot.Integrations.Twitch.TwitchService>() is { } twitch) twitch.Publish(ev);
            else bus.Publish(ev);
            return Json(new { seq = ev.Seq, filtered = ev.Data.GetValueOrDefault("filtered") is true, filterReason = ev.Data.GetValueOrDefault("filterReason") });
        });
    }

    private static async Task WriteSse(HttpContext ctx, long? seq, string source, string type, string ts, object data)
    {
        // Unnamed SSE messages: source and type travel in the payload, so one onmessage handler sees everything.
        var payload = JsonSerializer.Serialize(new { seq, source, type, ts, data }, CherBotJson.Options);
        var head = seq is { } s ? $"id: {s}\n" : "";
        await ctx.Response.WriteAsync($"{head}data: {payload}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    // ── misc: catalog, import/export, integrations, media ──────────────────

    private static void MapMisc(RouteGroupBuilder v1)
    {
        v1.MapGet("/catalog/events", (IEnumerable<IEventCatalog> catalogs) =>
        {
            var emitted = new HashSet<int>(catalogs.SelectMany(c => c.EmittedEventTypes));
            return Json(new
            {
                events = EventTypes.All.Select(e => new { e.Id, e.Name, e.Source, e.Type, e.Display, e.Category, e.Fields, emitted = emitted.Contains(e.Id) }),
            });
        });
        v1.MapGet("/events/sample", (int typeId, CherBotDb db) =>
        {
            if (EventTypes.Get(typeId) is not { } info) return Results.NotFound(new { error = "unknown event type" });
            var row = db.GetLatestEvent(info.Source, info.Type);
            return row is null
                ? Results.NotFound(new { error = "no event of this type has been seen yet" })
                : Json(new { seq = row.Seq, ts = row.Timestamp, source = row.Source, type = row.Type, data = JsonDocument.Parse(row.Json).RootElement });
        });
        v1.MapGet("/catalog/subactions", (SubActionRegistry reg) =>
        {
            var supported = new HashSet<int>(reg.SupportedTypes) { 99900, 99901, 99902, 99903, 99904, 120, 124, 127, 128, 129, 130 };
            return Json(new { subActions = SubActionTypes.All.Concat(SubActionTypes.Custom).Select(s => new { s.Id, s.Name, s.Display, s.Category, s.Fields, supported = supported.Contains(s.Id) }) });
        });
        v1.MapGet("/integrations", (IEnumerable<IIntegrationStatus> list) => Json(new { integrations = list.Select(i => i.GetStatus()) }));

        v1.MapPost("/import", async (HttpContext ctx, StreamerBotImporter importer, bool? dryRun, bool? overwrite, bool? rewritePaths) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync();
            try
            {
                var report = importer.Import(text, new ImportOptions
                {
                    DryRun = dryRun ?? false, OverwriteByName = overwrite ?? true, RewritePaths = rewritePaths ?? false,
                });
                return Json(report);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        v1.MapPost("/export", (ExportIn body, StreamerBotImporter importer) =>
            Json(new { export = importer.Export(body.Actions ?? [], body.Commands, body.Timers, body.Name ?? "CherBot export", body.Author ?? "") }));

        v1.MapGet("/media", (string path, EngineOptions engine) =>
        {
            var full = Path.GetFullPath(path);
            var roots = engine.MediaRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            if (!roots.Any(r => full.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!File.Exists(full)) return Results.NotFound();
            var type = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".flac" => "audio/flac", ".m4a" => "audio/mp4",
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webm" => "video/webm", ".mp4" => "video/mp4",
                _ => "application/octet-stream",
            };
            return Results.File(full, type, enableRangeProcessing: true);
        });
    }

    internal sealed record EnabledIn(bool Enabled);
    internal sealed record RunIn(JsonElement? Args, bool Wait = false);
    internal sealed record GlobalIn(JsonElement? Value, bool Persisted = true);
    internal sealed record QuoteIn(string Quote, string? User, string? UserId, string? Platform, string? Game);
    internal sealed record GroupUserIn(string UserId, string? Login, string? UserName, string? Platform);
    internal sealed record PublishIn(string Source, string Type, JsonElement? Data);
    internal sealed record ChatIn(string Message, string? User, string? UserId, string? Role, bool Internal = false, List<string>? Emotes = null);
    internal sealed record ExportIn(List<Guid>? Actions, List<Guid>? Commands, List<Guid>? Timers, string? Name, string? Author);
}
