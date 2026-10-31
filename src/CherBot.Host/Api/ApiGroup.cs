using CherBot.Core;
using CherBot.Host.Security;

namespace CherBot.Host.Api;

internal static class ApiGroup
{
    /// <summary>A /v1 route group that requires the API token.</summary>
    public static RouteGroupBuilder V1(this WebApplication app) =>
        app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<ApiToken>().IsAuthorized(ctx.HttpContext) ? await next(ctx) : Results.Unauthorized());

    public static IResult Json(object? value) => Results.Json(value, CherBotJson.Options);
}
