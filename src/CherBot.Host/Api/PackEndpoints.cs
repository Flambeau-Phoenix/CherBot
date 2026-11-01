using System.Text.Json;
using CherBot.Host.Sharing;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

internal static class PackEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or FormatException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        v1.MapGet("/packs/settings", () => Json(new { settings = FeaturePackService.SettingKeys }));
        v1.MapPost("/packs/export", (PackExport selection, FeaturePackService packs) => Json(packs.Export(selection)));
        v1.MapPost("/packs/preview", (PackRequest request, FeaturePackService packs) => Json(packs.Preview(request)));
        v1.MapPost("/packs/apply", (PackRequest request, FeaturePackService packs) => Json(packs.Apply(request)));
    }
}
