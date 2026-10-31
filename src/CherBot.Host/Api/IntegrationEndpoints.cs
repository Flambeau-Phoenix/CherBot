using System.Text.Json;
using CherBot.Core;
using CherBot.Host.Security;
using CherBot.Integrations.Obs;
using CherBot.Integrations.SpeakerBot;

namespace CherBot.Host.Api;

/// <summary>/v1/obs and /v1/speakerbot: connection management and test requests.</summary>
internal static class IntegrationEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<ApiToken>().IsAuthorized(ctx.HttpContext) ? await next(ctx) : Results.Unauthorized());

        static IResult Json(object? v) => Results.Json(v, CherBotJson.Options);

        v1.MapGet("/obs/connections", (ObsService obs) =>
            Json(new { connections = obs.Definitions().Select(d => new { d.Id, d.Name, d.Host, d.Port, d.AutoConnect, d.RetrySeconds, hasPassword = !string.IsNullOrEmpty(d.Password) }), status = obs.GetStatus() }));
        v1.MapPost("/obs/connections", (ObsConnectionIn body, ObsService obs) =>
        {
            var def = new ObsConnectionDef
            {
                Id = body.Id ?? Guid.NewGuid(), Name = body.Name ?? "OBS", Host = body.Host ?? "127.0.0.1", Port = body.Port ?? 4455,
                AutoConnect = body.AutoConnect ?? true, RetrySeconds = body.RetrySeconds ?? 10,
            };
            obs.SaveConnection(def, body.Password);
            return Json(new { def.Id });
        });
        v1.MapDelete("/obs/connections/{id:guid}", (Guid id, ObsService obs) => obs.DeleteConnection(id) ? Results.NoContent() : Results.NotFound());
        v1.MapPost("/obs/request", async (ObsRequestIn body, ObsService obs, CancellationToken ct) =>
        {
            var client = obs.Client(body.ConnectionId);
            if (client is null) return Results.Conflict(new { error = "no OBS connection is available" });
            try
            {
                var result = await client.RequestAsync(body.RequestType, body.RequestData, ct);
                return Json(new { responseData = result });
            }
            catch (ObsRequestException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.Code });
            }
        });

        v1.MapGet("/speakerbot/status", (SpeakerBotService sb) => Json(sb.GetStatus()));
        v1.MapPost("/speakerbot/speak", async (SpeakIn body, SpeakerBotService sb, CancellationToken ct) =>
        {
            var fields = new Dictionary<string, object?> { ["voice"] = body.Voice, ["message"] = body.Message, ["badWordFilter"] = body.BadWordFilter };
            if (sb.IsConnected) await sb.RequestAsync("Speak", fields, ct);
            else await sb.UdpAsync("speak", fields, ct);
            return Results.Accepted();
        });
    }

    internal sealed record ObsConnectionIn(Guid? Id, string? Name, string? Host, int? Port, string? Password, bool? AutoConnect, int? RetrySeconds);
    internal sealed record ObsRequestIn(string RequestType, JsonElement? RequestData, Guid? ConnectionId);
    internal sealed record SpeakIn(string Voice, string Message, bool BadWordFilter = true);
}
