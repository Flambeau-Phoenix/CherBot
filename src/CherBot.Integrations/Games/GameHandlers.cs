using CherBot.Core.Engine;
using CherBot.Core.Generated;

namespace CherBot.Integrations.Games;

/// <summary>Game sub-actions. Each reads the command's input0/input1 and writes the chat reply into the <c>reply</c> argument.</summary>
public sealed class GameHandlers : SubActionHandlerGroup
{
    public const int Gamble = 50010, Slots = 50011, Coinflip = 50012, Duel = 50013, Heist = 50014, Roulette = 50015;

    static GameHandlers()
    {
        SubActionTypes.RegisterCustom(new(Gamble, "CherBotGamble", "Game: Gamble", "CherBot / Games", []));
        SubActionTypes.RegisterCustom(new(Slots, "CherBotSlots", "Game: Slots", "CherBot / Games", []));
        SubActionTypes.RegisterCustom(new(Coinflip, "CherBotCoinflip", "Game: Coinflip", "CherBot / Games", []));
        SubActionTypes.RegisterCustom(new(Duel, "CherBotDuel", "Game: Duel", "CherBot / Games", ["mode:string"]));
        SubActionTypes.RegisterCustom(new(Heist, "CherBotHeist", "Game: Heist", "CherBot / Games", []));
        SubActionTypes.RegisterCustom(new(Roulette, "CherBotRoulette", "Game: Roulette", "CherBot / Games", []));
    }

    public GameHandlers()
    {
        On(Gamble, (s, c) => Reply(c, g => g.Gamble(Player(c), c.ArgString("input0"))));
        On(Slots, (s, c) => Reply(c, g => g.Slots(Player(c), c.ArgString("input0"))));
        On(Coinflip, (s, c) => Reply(c, g => g.Coinflip(Player(c), c.ArgString("input0"), c.ArgString("input1"))));
        On(Roulette, (s, c) => Reply(c, g => g.Roulette(Player(c), c.ArgString("input0"), c.ArgString("input1"))));
        On(Heist, (s, c) => Reply(c, g => g.HeistJoin(Player(c), c.ArgString("input0"))));
        On(Duel, (s, c) => Reply(c, g => s.Str("mode", "challenge").ToLowerInvariant() switch
        {
            "accept" => g.DuelAccept(Player(c)),
            "decline" => g.DuelDecline(Player(c)),
            _ => g.DuelChallenge(Player(c), c.ArgString("input0"), c.ArgString("input1")),
        }));
    }

    internal static GamePlayer Player(RunContext c) =>
        new(c.ArgString("userId"), c.ArgString("userName", c.ArgString("user")), c.ArgString("user", c.ArgString("userName", "someone")));

    private static void Reply(RunContext c, Func<GameService, string> play)
    {
        var player = Player(c);
        c.SetArg("reply", player.UserId.Length == 0 ? "Games need a chat user." : play(c.Service<GameService>()));
    }
}
