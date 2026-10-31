# Troubleshooting

## A command does nothing

1. Is the **command** on? ([Commands → All commands](#/commands?view=all))
2. Is the **action** on, and does it have a *Command Triggered* trigger for that command? (The All commands table shows "no action" otherwise.)
3. Cooldowns and roles: try it as the broadcaster or from the chat simulator with role *Moderator*.
4. Was the message caught by the **spam filter**? The Monitor feed shows `filtered: true` and the moderation log says why.
5. Look at **Monitor → Recent runs**: open the run to see each step's result and error.

## Nothing reaches chat

- Twitch must be linked on [Config](#/config). With a bot account, the bot must be allowed to chat (it falls back to the broadcaster when allowed).
- Empty messages are skipped on purpose (an alert or giveaway message left blank stays silent).

## OBS steps fail

- The badge at the bottom of the sidebar shows the OBS state. Check the WebSocket server settings in OBS and the password on Config, then **Reconnect OBS** on the Dashboard.
- Scene and source names must match exactly; the pickers list the live names.

## A step says "not on this host"

Some Streamer.bot steps (for example YouTube or Kick) aren't implemented in CherBot. They are kept so imported actions stay intact, and skipped when the action runs.

## Points, games or giveaways seem stuck

- Points are paid only while loyalty is on; watch time can require being live.
- A game command replies "you can play again in Ns" during its cooldown.
- A giveaway's entry command is only on while the giveaway is open.

## Still stuck

The engine log (see the [operating guide](docs/operating-guide.md#20-troubleshooting)) records every warning. The Monitor's live events show exactly what CherBot receives.
