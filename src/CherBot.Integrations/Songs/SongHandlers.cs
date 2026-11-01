using CherBot.Core.Engine;
using CherBot.Core.Generated;

namespace CherBot.Integrations.Songs;

public sealed class SongHandlers : SubActionHandlerGroup
{
    public const int Request = 50008, Queue = 50009, Skip = 50010;
    static SongHandlers()
    {
        SubActionTypes.RegisterCustom(new(Request, "CherBotSongRequest", "Request a song", "CherBot / Songs", []));
        SubActionTypes.RegisterCustom(new(Queue, "CherBotSongQueue", "Song queue status", "CherBot / Songs", []));
        SubActionTypes.RegisterCustom(new(Skip, "CherBotSongSkip", "Skip current song", "CherBot / Songs", []));
    }
    public SongHandlers()
    {
        On(Request, (s, c) =>
        {
            try
            {
                var r = c.Service<SongRequestService>().Add(c.ArgString("userId"), c.ArgString("user", c.ArgString("userName")), c.ArgString("rawInput"), c.ArgString("msgId"));
                c.SetArg("reply", $"Song request {r.Status}: {r.Url}");
            }
            catch (ArgumentException ex) { c.SetArg("reply", ex.Message); }
        });
        On(Queue, (s, c) =>
        {
            var requests = c.Service<SongRequestService>().All.Where(r => r.Status is "pending" or "approved" or "playing").ToList();
            c.SetArg("reply", $"Song queue: {requests.Count} requests. Playing: {requests.FirstOrDefault(r => r.Status == "playing")?.Url ?? "nothing"}.");
        });
        On(Skip, (s, c) =>
        {
            if (c.GetArg("isModerator") is not true && c.GetArg("isBroadcaster") is not true) { c.SetArg("reply", "Only moderators can skip songs."); return; }
            var songs = c.Service<SongRequestService>(); var current = songs.All.FirstOrDefault(r => r.Status == "playing");
            if (current is not null) songs.Change(current.Id, "skip"); c.SetArg("reply", current is null ? "No song is playing." : "Song skipped.");
        });
    }
}
