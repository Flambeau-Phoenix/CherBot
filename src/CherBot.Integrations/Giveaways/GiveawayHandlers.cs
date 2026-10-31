using CherBot.Core.Engine;
using CherBot.Core.Generated;

namespace CherBot.Integrations.Giveaways;

/// <summary>Giveaway Enter (50030) and Giveaway Control (50031).</summary>
public sealed class GiveawayHandlers : SubActionHandlerGroup
{
    public const int Enter = 50030, Control = 50031;
    public static readonly string[] Operations = ["start", "stop", "draw", "reroll", "cancel"];

    static GiveawayHandlers()
    {
        SubActionTypes.RegisterCustom(new(Enter, "CherBotGiveawayEnter", "Giveaway: Enter", "CherBot / Giveaways", ["giveawayId:Guid"]));
        SubActionTypes.RegisterCustom(new(Control, "CherBotGiveawayControl", "Giveaway: Control", "CherBot / Giveaways",
            ["operation:string", "giveawayId:Guid", "durationSeconds:int"]));
    }

    public GiveawayHandlers()
    {
        On(Enter, async (s, c) =>
        {
            var svc = c.Service<GiveawayService>();
            var d = s.GuidOf("giveawayId") is { } id && id != Guid.Empty ? svc.Get(id) : svc.OpenOne();
            if (d is null) { c.SetArg("giveawayEntered", false); return; }
            var args = c.Args.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var result = await svc.EnterAsync(d.Id, args, c.ArgString("input0"));
            c.SetArg("giveawayEntered", result.Ok);
            c.SetArg("giveawayResult", result.Message);
        });

        // "!giveaway <start|stop|draw|reroll|cancel> [name]" when the operation field is empty.
        On(Control, async (s, c) =>
        {
            var svc = c.Service<GiveawayService>();
            var op = s.Str("operation").Trim().ToLowerInvariant();
            var nameText = "";
            if (op.Length == 0)
            {
                var raw = c.ArgString("rawInput").Trim();
                var space = raw.IndexOf(' ');
                op = (space < 0 ? raw : raw[..space]).ToLowerInvariant();
                nameText = space < 0 ? "" : raw[(space + 1)..].Trim();
            }
            if (!Operations.Contains(op))
            {
                c.SetArg("reply", $"Usage: {c.ArgString("command", "!giveaway")} <{string.Join("|", Operations)}> [name]");
                return;
            }
            var d = s.GuidOf("giveawayId") is { } id && id != Guid.Empty ? svc.Get(id)
                : nameText.Length > 0 ? svc.Find(nameText) : svc.DefaultFor(op);
            if (d is null)
            {
                c.SetArg("reply", nameText.Length > 0 ? $"No giveaway called \"{nameText}\"." : "No giveaway to " + op + ".");
                return;
            }
            var duration = s.Int("durationSeconds");
            var result = op switch
            {
                "start" => await svc.StartAsync(d.Id, duration > 0 ? duration : null),
                "stop" => await svc.StopAsync(d.Id),
                "draw" => await svc.DrawAsync(d.Id),
                "reroll" => await svc.RerollAsync(d.Id),
                _ => await svc.CancelAsync(d.Id),
            };
            c.SetArg("giveawayResult", result.Message);
            // Successful operations announce themselves through the giveaway's own messages.
            c.SetArg("reply", result.Ok ? "" : result.Message);
        });
    }
}
