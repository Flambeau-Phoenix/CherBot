# Modules: alerts, pyramids, spam filter

[Modules](#/modules) is the on/off overview of ready-made features. Loyalty, games, giveaways and scene commands have their own pages; the switches here turn them on or off too.

## Chat alerts

See [Alerts and sounds](alerts.md).

## Emote pyramids

When someone builds a pyramid from consecutive messages (`Kappa`, `Kappa Kappa`, `Kappa Kappa Kappa`, `Kappa Kappa`, `Kappa`), CherBot can celebrate it and pay points: a base amount plus an amount per width level.

- **Minimum and maximum width.**
- **Emotes only**, or any repeated word.
- By default one person must build it; another person's message **breaks** it, with an optional message naming who broke it. Switch on *anyone can continue* to allow team pyramids.
- **Block mode** is for channels that don't want pyramids: delete the rows, or time out the builder, from a width you choose.

Pyramids raise `pyramid.completed` and `pyramid.broken` Custom Events. **Run a scripted pyramid** sends one through the chat simulator.

## Spam filter

The spam filter checks every chat message **before** commands, points, games and other modules see it. A blocked message is still visible in the Monitor feed, marked `filtered`, and raises a `chat.filtered` Custom Event.

Filters (each with its own switch, punishment, reason and exemption):

| Filter | Catches |
| --- | --- |
| Links | Links outside your allowed domains. Moderators can `!permit @user` for a short window |
| Caps | Messages that are mostly capitals (above a length) |
| Symbols | Messages that are mostly symbols |
| Repeated characters | `aaaaaaaaaaaa` |
| Message length | Very long messages |
| Emote spam | More emotes than you allow |
| Banned words | Words, phrases, `wild*cards` or `/regexes/` |
| Duplicate messages | The same person repeating themselves within N seconds |
| Zalgo | Text stacked with combining marks |

**Punishments**: delete the message, delete and warn in chat, time out, or only block commands and points. With **escalation** on, repeat offenders climb a ladder (for example warn → 10 s → 10 min) and strikes reset after a quiet period.

**Exemptions**: subscribers and up by default (per filter you can change this), listed users and user groups, and always you and the bot.

**Test a message** shows which filter would fire without punishing anyone. The **moderation log** lists every action taken.

Deleting messages needs the `moderator:manage:chat_messages` permission: relink the broadcaster on [Config](#/config) if CherBot asks. The bot account is used when it is a moderator in your channel.
