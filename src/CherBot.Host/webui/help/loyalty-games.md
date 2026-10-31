# Loyalty points and games

## Earning points

Set everything on [Loyalty & Games](#/loyalty):

- **Chat**: points per message, with an optional per-user cooldown, and a bonus for a viewer's first message of the stream.
- **Watch time**: every N minutes, everyone in chat earns points (only while live, if you like). With the *chatters* scope CherBot pays everyone in the viewer list; otherwise, or when you set "active in the last N minutes", it pays recent chatters.
- **Multipliers** for subscribers, VIPs and moderators (the highest one applies).
- **Bonuses** for follows, subs, resubs, each gifted sub (to the gifter), every 100 bits, and raids (to the raider).
- The **currency name** ("cherries", "coins"…) is used by `!points`, the games and giveaways.

The leaderboard lets you search, set anyone's balance, give everyone in chat an amount, pay watch time now, or reset everybody (type RESET to confirm). Messages caught by the spam filter never earn points.

## The games

Six games are created **switched off** in the *CherBot Games* group. Turn each on with its switch.

| Game | Chat | How it works |
| --- | --- | --- |
| Gamble | `!gamble 100` | Roll 1–100; at or under the win chance pays the multiplier |
| Slots | `!slots 100` | Three reels; a pair, three of a kind or three jackpot symbols pay |
| Coinflip | `!coinflip heads 100` | Call it; either order works |
| Duel | `!duel @friend 100`, `!accept`, `!decline` | The winner takes the bet; unanswered duels expire |
| Heist | `!heist 100` | Viewers join for a while; each crew member escapes (paid) or is caught (loses) |
| Roulette | `!roulette 100 red` | red/black/even/odd/low/high pay 2×, green and single numbers more |

Bets accept `100`, `1k`, `2.5m`, `all`, `half` or `25%`. Each game has a minimum and maximum bet and a per-user cooldown. **Try it** tops the *tester* account up and sends the command through the chat simulator.

Every game result also raises a `CherBot.GameResult` event you can use for overlays.
