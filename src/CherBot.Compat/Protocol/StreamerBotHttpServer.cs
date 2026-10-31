using System.Text.Json;
using CherBot.Core;
using Microsoft.AspNetCore.Http;

namespace CherBot.Compat.Protocol;

/// <summary>
/// Streamer.bot-compatible HTTP server (default port 7474): POST /DoAction, GET /GetActions, GET /GetCredits,
/// GET /TestCredits, GET /ClearCredits and GET /ClearFirstWordsCache. Like Streamer.bot it has no authentication,
/// so keep it bound to localhost or a trusted link.
/// </summary>
public sealed class StreamerBotHttpServer(StreamerBotApi api)
{
    private static readonly JsonSerializerOptions Plain = new() { PropertyNamingPolicy = null };

    public async Task HandleAsync(HttpContext ctx)
    {
        var path = (ctx.Request.Path.Value ?? "/").TrimEnd('/');
        var method = ctx.Request.Method;
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        if (HttpMethods.IsOptions(method))
        {
            ctx.Response.Headers.AccessControlAllowHeaders = "Content-Type";
            ctx.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        switch (path.ToLowerInvariant())
        {
            case "/doaction" when HttpMethods.IsPost(method):
            {
                JsonElement body;
                try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
                catch (JsonException)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var res = api.DoAction(body.TryGetProperty("action", out var a) ? a : null, body.TryGetProperty("args", out var args) ? args : null, "http");
                ctx.Response.StatusCode = res.Ok ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
                if (!res.Ok) await ctx.Response.WriteAsJsonAsync(new { error = res.Error });
                return;
            }
            case "/executecodetrigger" when HttpMethods.IsPost(method):
            {
                var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                api.ExecuteCodeTrigger(body.TryGetProperty("triggerName", out var t) ? t.GetString() ?? "" : "", body.TryGetProperty("args", out var args) ? args : null);
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            case "/getactions":
                await ctx.Response.WriteAsJsonAsync(api.GetActions(), CherBotJson.Options);
                return;
            case "/getcredits" or "/testcredits":
                await ctx.Response.WriteAsJsonAsync(StreamerBotApi.EmptyCredits(), Plain);
                return;
            case "/clearcredits":
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            case "/clearfirstwordscache":
                api.ClearFirstWords();
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }
}
