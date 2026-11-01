using CherBot.Integrations.Songs;
using static CherBot.Host.Api.ApiGroup;

namespace CherBot.Host.Api;

internal static class SongEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.V1();
        v1.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
        });
        v1.MapGet("/songs", (SongRequestService songs) => Json(new { settings = songs.Settings, requests = songs.All, playerConnected = songs.PlayerConnected }));
        v1.MapPut("/songs/settings", (SongSettings settings, SongRequestService songs) => { songs.Save(settings); return Json(songs.Settings); });
        v1.MapPost("/songs/request", (AddIn body, SongRequestService songs) => Json(songs.Add(body.UserId, body.UserName, body.Url, Guid.NewGuid().ToString("N"))));
        v1.MapPost("/songs/reorder", (ReorderIn body, SongRequestService songs) => { songs.Reorder(body.Ids); return Results.NoContent(); });
        v1.MapPost("/songs/advance", (SongRequestService songs) => Json(songs.Advance()));
        v1.MapPost("/songs/{id:guid}/{operation}", (Guid id, string operation, SongRequestService songs) => { songs.Change(id, operation); return Results.NoContent(); });
        v1.MapDelete("/songs/{id:guid}", (Guid id, SongRequestService songs) => { songs.Remove(id); return Results.NoContent(); });
        v1.MapDelete("/songs", (SongRequestService songs) => { songs.Clear(); return Results.NoContent(); });
        v1.MapPost("/songs/player-token", (SongRequestService songs) => Json(new { token = songs.RotatePlayerToken() }));

        var player = app.MapGroup("/song-player").AddEndpointFilter(async (context, next) =>
        {
            var token = context.HttpContext.Request.Headers["X-Song-Token"].ToString();
            if (!context.HttpContext.RequestServices.GetRequiredService<SongRequestService>().AuthorizePlayer(token)) return Results.Unauthorized();
            try { return await next(context); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        player.MapPost("/lease", (LeaseIn body, SongRequestService songs) => Json(songs.Acquire(body.ClientId)));
        player.MapGet("/state", (string leaseId, SongRequestService songs) =>
        {
            var r = songs.PlayerState(leaseId);
            return Json(new { current = r is null ? null : new { id = r.Id, provider = r.Provider, trackId = r.TrackId, url = r.Url } });
        });
        player.MapPost("/result", (ResultIn body, SongRequestService songs) => { songs.PlayerResult(body.LeaseId, body.RequestId, body.Failed, body.Error); return Results.NoContent(); });
    }
    internal sealed record AddIn(string UserId, string UserName, string Url);
    internal sealed record ReorderIn(List<Guid> Ids);
    internal sealed record LeaseIn(string ClientId);
    internal sealed record ResultIn(string LeaseId, Guid RequestId, bool Failed, string? Error);
}
