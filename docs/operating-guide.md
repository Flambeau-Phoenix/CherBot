# Operating CherBot

This guide covers running CherBot day to day: where things live, how to start and stop it, how actions, triggers, queues
and commands behave, how to connect Twitch, OBS and Speaker.bot, and what to check when something goes wrong.
Installation is in [`packaging/README-install.md`](../packaging/README-install.md); every setting is listed in the
[configuration reference](configuration-reference.md).

## 1. How CherBot is put together

CherBot is one engine process. It has no built-in window; you manage it through:

- the **web UI** at `http://<host>:8091/` (needs the API token),
- the **Windows tray app** (`CherBot.Desktop.exe`), which shows the same web UI, or
- the **management API** (`/v1/...`) and the **command line**.

Inside the engine, everything follows Streamer.bot's model:

```text
event (chat message, follow, timer, OBS scene change, ...)
  → triggers on actions that match the event
    → the action is queued on its queue
      → its sub-actions run (in order, randomly, or all at once)
        → effects: chat messages, OBS changes, sounds, variables, other actions ...
```

The engine keeps running whether or not any UI is open. Closing the browser or the tray window doesn't stop the bot.

## 2. Starting, stopping and status

| Where it runs | Start / stop | Logs |
| --- | --- | --- |
| Linux user service | `systemctl --user start\|stop\|restart cherbot` | `journalctl --user -u cherbot -f` and `~/.local/share/cherbot/logs/` |
| Linux system service | `sudo systemctl start\|stop\|restart cherbot` | `journalctl -u cherbot -f` and `/var/lib/cherbot/logs/` |
| Windows Service | `Start-Service CherBot` / `Stop-Service CherBot` / `Restart-Service CherBot` | `%ProgramData%\CherBot\logs\` |
| Windows tray app (Local mode) | Start the app; tray menu → **Exit** stops the engine | Tray menu → **Open logs** |
| Console | `cherbot` (Ctrl+C to stop) | Console and `<data folder>/logs/` |

Log files roll daily (`cherbot-YYYYMMDD.log`) and the last 14 are kept.

Stopping cleanly matters: on shutdown CherBot saves actions still waiting in queues and runs them after the next start.
Killing the process skips that save; the cache is also written every 30 seconds, so at most that window is lost.

**Health check.** `GET http://<host>:8091/health` needs no token and returns the version, data folder, sound output and
the state of every integration. The web UI's header badges and the tray icon colour come from it:

| Tray icon dot | Meaning |
| --- | --- |
| Green | Engine reachable, every configured integration connected |
| Amber | Engine running, but an integration is down (hover for which) |
| Red | Engine unreachable |
| Grey | Starting |

## 3. Signing in to the web UI

The web UI and API are protected by one **API token**, generated on first start into `<data folder>/api-token`.

- Print it: `cherbot token --data-dir <data folder>`.
- The tray app passes it automatically. In a browser, paste it on the sign-in screen, or open
  `http://<host>:8091/#token=<token>` once; it is remembered in that browser.
- **Config → Forget token on this browser** signs out.
- To change it, delete `api-token` and restart (a new one is generated), or set `CherBot:Api:Token` in the config.

## 4. The web UI

The left sidebar has one page per job. Every page also has a matching guide on **Help**.

| Page | What it does |
| --- | --- |
| **Dashboard** | Live status (Twitch, live/viewers, OBS scene, queues), OBS scene buttons and source switches, pinned actions, quick chat (as the bot or the broadcaster), title and category editor, and one-click tools: stream marker, clip, ads (30/60/90 s), shoutout, pause/resume all queues, reset first words, reconnect Twitch and OBS. Also the active giveaway and a mini event feed. |
| **Actions** | The action list (search, enable switches, ☆ to pin to the Dashboard) and the visual builder ([section 21](#21-the-action-builder)). Import and Export all are here. |
| **Commands** | Custom text commands ([section 22](#22-custom-commands)) and, under *All commands*, every command with its cooldowns, roles and the actions it starts. |
| **Modules** | On/off switches for every ready-made feature, and the editors for chat alerts, emote pyramids and the spam filter ([section 26](#26-modules-chat-alerts-emote-pyramids-spam-filter)). |
| **Quick Setup** | Wizards that build common commands and actions, scene shortcuts, and the `!scene` / `!scenes` switches ([section 23](#23-quick-setup)). |
| **Loyalty & Games** | Points settings, the leaderboard and the six chat games ([section 24](#24-loyalty-points-and-games)). |
| **Giveaways** | Raffles and giveaways ([section 25](#25-raffles-and-giveaways)). |
| **Variables** | Globals (typed create, edit, delete), user globals, and a searchable reference of every argument. |
| **Monitor** | Queues (pause, resume, clear), the chat simulator, live events and recent runs. |
| **Config** | Twitch accounts and EventSub status, OBS connections, timers, queues and integration status. |
| **Help** | How-to guides, generated references for every step and trigger, this guide and the configuration reference, with search. |

**Chat simulator** (Monitor). Sends a fake Twitch chat message as any user and role, optionally marking words as emotes.
It goes through the same path as real chat: the spam filter, loyalty points, commands, modules and chat triggers. Replies
are sent to real Twitch chat if Twitch is linked. The simulator reports when the spam filter blocked the message.

**Live events** (Monitor) shows every event as it happens (filterable, pausable); expand one to see its data. The
builder's `{x}` helper also shows the arguments of the latest real event for each trigger.

## 5. Actions

An action has a name, an optional group, an enabled flag, a queue, triggers, and sub-actions. Options:

| Option | Effect |
| --- | --- |
| `randomAction` | Run **one** enabled sub-action, picked by `weight` (equal chance when all weights are 0) |
| `concurrent` | Start **all** sub-actions at once and wait for all of them |
| *(neither)* | Run sub-actions in `index` order, one after another |
| `alwaysRun` | Runs even when its queue is paused |
| `excludeFromHistory` | Don't record runs in the run log |
| `excludeFromPending` | Don't save it in the restart cache if it is still queued at shutdown |

### Ways an action starts

- A **trigger** matching an event (see section 6).
- A **Run Action** sub-action or `CPH.RunAction(...)` from a script.
- **Run now / Queue** in the web UI, `POST /v1/actions/{id}/run`, or `POST /v1/doaction`.
- Streamer.bot clients: WebSocket `DoAction`, HTTP `POST /DoAction`, UDP `DoAction`.

Disabled actions are never started by triggers. If an action is disabled while it is waiting in a queue, it is skipped
when its turn comes.

### Arguments and variables

Each run has its own **arguments**: everything from the triggering event (for example `user`, `userName`, `userId`,
`message`, `bits`, `rewardName`) plus `actionId`, `actionName`, `runningActionId`, `triggerName` and `eventSource`.
Sub-actions add more (`randomNumber`, `quote`, `input0` ...). Look at a run's arguments in **Recent runs**.

Text fields understand templates:

| Template | Replaced with |
| --- | --- |
| `%name%` | The argument `name` (case-insensitive). Left as-is when there is no such argument |
| `%name:format%` | The argument formatted, for example `%amount:N0%` or `%time:HH:mm%` |
| `~name~` | The persisted global variable `name` |

**Globals** are named values shared by all actions; **user globals** belong to one user on one platform. Each is either
*persisted* (kept in the database across restarts) or *temporary* (gone on restart). Changing a global raises the Global
Variable Created / Updated / Deleted events, so actions can trigger on them. Manage them in Config → Globals or with the
Global (Get) / Global (Set) sub-actions.

### Logic

- **If/Else** compares `input` with `value`. Operators: 0 Equals, 1 Not Equals, 2 Contains, 3 Regex Match, 4 Less Than,
  5 Greater Than, 6 Does Not Exist, 7 Equals (ignore case), 8 Not Equals (ignore case), 9 Is Null or Empty. With
  *auto type*, numbers and true/false compare as values (`5` equals `5.0`; `15` is greater than `2`). Regex Match adds
  `match.count`, `match[0]`, `match[1]` ... and named groups as arguments.
- **Switch** runs the first case whose values match the input, otherwise the default case.
- **While** repeats its children while its condition holds; **Loop Break** and **Loop Continue** control it. Loops stop
  after 100,000 iterations as a safety net.
- **Break** stops the whole action. A C# script returning `false` also stops the action.
- **Group** bundles sub-actions; with *random* it runs one of them.
- **Send / Wait For Local Signal** let one action wait (up to 30 s) for another to signal it, optionally passing arguments.

When a sub-action fails (for example OBS isn't connected), the error is logged and shown in the run trace, and the action
**continues** with the next sub-action.

## 6. Triggers

A trigger fires its action when an event of its type happens and its filters match:

| Filter | Example |
| --- | --- |
| Identity | Command Triggered → `commandId`; Reward Redemption → `rewardId`; Timed Actions → `timerId`; Custom Event → `eventName`; OBS Scene Changed → `sceneName` |
| Range | Cheer, Raid, Gift Bomb, Resub: `min` / `max` (0 = no limit) checked against bits, viewers, gifts or months |
| User | First Words: `username` (or the user id with `isUserId`) |

An action with several matching triggers still runs once per event.

### Events CherBot raises

| Source | Events |
| --- | --- |
| Twitch | Chat Message, First Words, Follow, Cheer, Sub, Resub, Gift Sub, Gift Bomb, Raid, Reward Redemption, Stream Online/Offline, Stream Update, Announcement, Shoutout Created, Ad Run, Chat Message Deleted, Present Viewers (every 5 min), broadcaster connected/disconnected/authenticated |
| Commands | Command Triggered, Command Cooldown |
| Core | Timed Actions, Test, global variable created/updated/deleted, user global updated, Quote Added/Show, Custom Event, Custom Code Event, Action / Action Completed |
| OBS | Connected, Disconnected, Scene Changed, Streaming/Recording Started/Stopped, Event (any OBS event), Vendor Event |
| Speaker.bot | Connected, Disconnected |

Triggers for other event types (Elgato, YouTube, Kick, Streamlabs, ...) import fine but never fire. The import report
lists them.

To test a trigger without the real platform, raise the event yourself:

```bash
curl -X POST http://127.0.0.1:8091/v1/events/publish -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"source":"Twitch","type":"Cheer","data":{"user":"Tester","userName":"tester","bits":500,"message":"gg"}}'
```

## 7. Queues

Every action runs on a queue; actions without one use **Default**.

- **Blocking** queues run one action at a time, in order. Use them for alerts so they never overlap.
- **Non-blocking** queues (including Default) start every action immediately.
- **Pausing** a queue holds new actions until it is resumed; actions marked *always run* still go through. Resume can drop
  the held actions instead (*clear*).
- A Twitch reward set to **skip queue** (`PUT /v1/twitch/rewards/{rewardId}` with `{"skipQueue": true}`) starts at once,
  even on a blocking queue.

Pause and resume from the Monitor tab, the tray menu (all queues), the Set Action Queue Pause State sub-action, or
`POST /v1/queues/{all|default|<name>|<id>}/pause|resume|clear`. Pause state is not saved: after a restart all queues run.

## 8. Commands

A command matches chat text and raises **Command Triggered**; attach that trigger (with the command's id) to an action.

| Field | Meaning |
| --- | --- |
| `command` | One or more command texts, one per line (`!so` and `!shoutout`) |
| `mode` | 0 = text, 1 = regular expression |
| `location` | Text mode: 0 = message starts with it, 1 = message is exactly it, 2 = anywhere in the message |
| `caseSensitive` | Match case exactly |
| `globalCooldown` / `userCooldown` | Seconds before anyone / the same user can use it again; during a cooldown **Command Cooldown** fires instead |
| `grantType` + `permittedUsers` / `permittedGroups` | 0 = only listed users/groups (empty list = everyone); 1 = everyone except listed (empty list = nobody) |
| `minRole` | CherBot extra: `subscriber`, `vip`, `moderator` or `broadcaster` and above |
| `ignoreBotAccount` / `ignoreInternal` | Skip messages from the bot account / internal messages |
| `sources` | 1 = Twitch chat (default), 2 = whispers, 8 = resub messages |

The broadcaster bypasses cooldowns and permissions. The built-in groups `Moderators`, `VIPs` and `Subscribers` follow the
chatter's Twitch badges; other groups are lists you fill (`POST /v1/groups/{group}`, or `CPH.AddUserToGroup`).

Arguments passed to the action: `command`, `rawInput` (text after the command), `input0`, `input1`, ... (words),
`counter`, `userCounter`, plus all chat arguments (`user`, `userName`, `userId`, `msgId`, `isModerator`, ...).
Regex commands also get `match[n]` and named groups.

## 9. Built-in community commands

On first start CherBot creates four commands, each with an action in the group **CherBot Community**. They're ordinary
records: edit, disable or delete them like any other.

| Command | Subcommands |
| --- | --- |
| `!first` (`!1st`, `!firstwords`) | *(none)*: your first-chatter rank this stream · `top` / `list`: first five chatters · `reset`: clear first words (moderators) |
| `!points` | *(none)*: your points and rank · `top` / `lb`: top five · `give <user> <n>`: transfer points · `add <user> <n>` / `set <user> <n>`: moderators |
| `!leaderboard` (`!lb`) | Top five |
| `!disc` (`!discord`, `!hub`) | `link` (default), `rules`, `schedule`, `lfg [game]`, `vip` (VIPs and moderators), `set <url>` and `announce <text>` (moderators) |

How points work: see [section 24](#24-loyalty-points-and-games). In short, chat messages, the first message of the
stream, watch time, follows, subs, gifts, bits and raids earn points, all configurable on **Loyalty & Games**.
`Community:ChatPoints` and `Community:FirstWordPoints` only seed those settings on first start.

- First words reset when the stream goes online, with `!first reset`, or with the Reset First Words sub-action.
- The Discord link is the persisted global `discord_url` (set with `!disc set <url>` or on the Variables page).

Each of these actions is a CherBot sub-action that writes the reply into the `reply` argument, followed by a Send Message
sub-action with the text `%reply%`. Change that second step to change where or how the reply is sent.

## 10. Timers

A timer raises **Timed Actions** every `interval` seconds; attach a Timed Actions trigger (with the timer's id) to an action.

- `randomInterval` + `upperInterval` picks a random gap between the two values each time.
- `lines` skips a fire unless at least that many chat messages arrived since the last one (good for promos).
- `repeat: false` fires once, then disables the timer.

Use **Fire** in Config → Timers to test, and the Set Timer State sub-action or `CPH.EnableTimer` to switch them from actions.

## 11. Twitch

CherBot uses two accounts:

- **Broadcaster** — required. Receives events (EventSub) and does channel actions: title, game, rewards, raids, moderation.
- **Bot** — optional. Sends chat messages and announcements. When it isn't linked, or sending as the bot fails, messages
  go out as the broadcaster (unless the sub-action turns *fallback* off).

To link the accounts:

- In the web UI: Config → Twitch → **Link**. Open the shown address, sign in as the right account, enter the code.
- From a terminal: `cherbot twitch-login broadcaster` (or `bot`) with `--data-dir`.
- **Unlink** removes a token.
- Tokens refresh automatically and are stored encrypted in the database.

**EventSub.** Config → Twitch shows the session state and each subscription. A subscription marked *failed* usually means
the token lacks a scope, for example after a scope was added to `BroadcasterScopes`. **Relink** the broadcaster to grant
it. **Reconnect** forces a new session; CherBot also reconnects by itself, with backoff, after network drops.

**Chat sends** are split at 500 characters. Twitch's chat API has no `/me`, so `/me` messages (`CPH.SendAction`) go out as
normal messages.

To test without a live stream, feed a raw EventSub event through the real pipeline:

```bash
curl -X POST http://127.0.0.1:8091/v1/twitch/eventsub/loopback -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"type":"channel.follow","event":{"user_id":"1","user_login":"tester","user_name":"Tester","followed_at":"2026-10-31T00:00:00Z"}}'
```

## 12. OBS

Add connections in Config → OBS (stored encrypted) or in the config file. Each connection reconnects every
`RetrySeconds` (default 10) while OBS is closed.

- OBS sub-actions pick their connection by id. Imported Streamer.bot actions carry ids from the old install, so when the id
  is unknown CherBot uses the first connected OBS (alphabetical by name).
- C# scripts address connections by number: `connection: 0` is the first connection.
- To try a request by hand: `POST /v1/obs/request` with `{"requestType":"GetSceneList"}` returns OBS's response.

## 13. Speaker.bot

With `SpeakerBot:Enabled`, CherBot keeps a WebSocket open to Speaker.bot and sends Speak, Stop, Enable/Disable,
Pause/Resume and Clear over it. When the WebSocket is down it falls back to Speaker.bot's UDP port. Voice-gate profiles
always use UDP. Test with `POST /v1/speakerbot/speak` and `{"voice":"<alias>","message":"hello"}`.

## 14. Sounds

| `Engine:SoundMode` | Behaviour |
| --- | --- |
| `auto` (default) | Play on this machine when possible (Windows: WAV, MP3 and AIFF built in, other formats through `ffplay` or `mpv` if installed; Linux: the first of `pw-play`, `paplay`, `ffplay`, `mpv`, `aplay` found), otherwise use the overlay |
| `local` | This machine only |
| `overlay` | Browser overlay only (see below) |
| `none` | Never play |

**Overlay** — for a headless box, or to hear alerts on the streaming PC:

1. Add an OBS Browser Source at `http://<host>:8091/overlay.html#token=<api token>`.
2. List the folders it may stream from in `Engine:MediaRoots`; other files are refused.

*Finish before continuing* waits for the sound's length.

## 15. C# scripts

**Execute C# Code** compiles the script once (on first use, or at startup when *precompile* is set) and reuses it. Scripts:

- declare `public class CPHInline` with `public bool Execute()`, written exactly as for Streamer.bot;
- use `CPH` (arguments, globals, actions, queues, timers, chat, OBS, quotes, groups, custom triggers) and `args`;
- can use Newtonsoft.Json and the .NET 8 libraries; extra DLLs go in `<data folder>/references/` and are referenced by
  file name.

A script that uses a `CPH` method outside CherBot's subset fails to compile. The error appears in the run trace and the
log, and the action continues with its next step. **Execute C# Method** calls another public method of an existing script.

Scripts run inside the engine with full access to the machine, as in Streamer.bot. Only import scripts you trust.

## 16. Importing and exporting

- **Import** (web UI or `cherbot import <file>`) takes a Streamer.bot export string, `actions.json` or `commands.json`.
  Use **Preview** (`--dry-run`) first. Items with the same id are updated; items with the same name are replaced unless
  you untick *Replace items with the same name* (`--no-overwrite`).
- The report lists sub-actions and triggers that are kept but can't run here, and every file path with where it maps on
  this machine. Set `Engine:PathMappings` for Windows paths, and add `--rewrite-paths` to store the mapped paths.
- Commands in Streamer.bot exports are often disabled by default. Enable them in Config → Commands after importing.
- **Export** (per action, **Export all**, or `cherbot export`) produces a Streamer.bot-style export string for moving
  actions between CherBot instances.

## 17. Connecting other tools (Streamer.bot API)

Tools written for Streamer.bot connect to CherBot's WebSocket server (default `ws://127.0.0.1:8080/`). That includes
Stream Deck plugins, overlays and `@streamerbot/client` scripts. To allow other machines:

1. Set `WebSocketServer:Address` to the LAN or VPN IP.
2. Set `WebSocketServer:Password`; add `EnforceAuth: true` to require it for every request, not only SendMessage.
3. Firewall the port to the machines that need it.

The HTTP (7474) and UDP (4242) servers are off by default and have no authentication; enable them only on localhost or a
trusted link.

A client that sends `DoAction` with `args.sbClientResponse` gets the action's final arguments back as a Custom event, so
`doAction(..., { customEventResponse: true })` works without changing the action.

## 18. Backup, restore and moving machines

Everything is in the data folder. To back up:

```bash
systemctl --user stop cherbot
tar czf cherbot-backup.tgz -C ~/.local/share cherbot
systemctl --user start cherbot
```

To restore or move, stop CherBot, put the folder in place and start it.

Keep `keys/` with `cherbot.db`; without it the stored Twitch tokens and OBS passwords can't be decrypted. Relink them in
that case. On Windows the keys are additionally tied to the Windows account that created them, so a folder moved to a
different account or PC also needs Twitch and OBS relinked.

Run history is kept 14 days and the event log 3 days by default (`Engine:RunHistoryDays`, `Engine:EventLogDays`).

## 19. Security checklist

- Keep the API (8091) on `127.0.0.1` or a private LAN/VPN address; never expose it to the internet. Anyone with the API token
  controls the bot.
- Protect the data folder (it is created owner-only on Linux). It holds the API token and the encryption keys.
- Set a WebSocket password before binding the Streamer.bot WebSocket server beyond localhost.
- Leave the HTTP and UDP servers off unless you need them.
- Review imported C# scripts.

## 20. Troubleshooting

| Symptom | Check |
| --- | --- |
| Engine won't start: "Port N is used by more than one listener" | Two of `Api`, `WebSocketServer`, `HttpServer` share a port in the config |
| Engine won't start: address in use | Another program (or a second CherBot, such as the Windows Service and the tray app in Local mode) holds the port |
| Web UI keeps asking for the token | The token changed; get it again with `cherbot token` |
| Twitch: "not configured" | `Twitch:ClientId` missing; set it and restart |
| Twitch: "not linked" | Link the broadcaster in Config → Twitch |
| Twitch events missing | Config → Twitch → EventSub list: failed entries need a relink for the missing scope |
| Chat replies don't appear | Recent runs → open the run: a Send Message step shows the Twitch error; check the bot has a token and isn't banned or timed out |
| OBS "disconnected" | OBS open? WebSocket server enabled? Host, port and password right? The error is shown next to the connection |
| A sub-action shows "unsupported_on_host" | That step type isn't available here (for example Keyboard Press on Linux); the rest of the action still runs |
| Command doesn't fire | Command enabled? Action enabled with a Command Triggered trigger for that command? Cooldown or permission (watch for Command Cooldown in Live events)? |
| Imported sounds silent on Linux | Check the import report's paths; add `Engine:PathMappings`; check `/health` → `sound` is not `none` |
| Tray app window is blank | Install the WebView2 runtime, or use **Open in browser** |
| A command ignores some messages | The spam filter blocked them: Live events shows `filtered: true` and `filterReason`; the moderation log (Modules → Spam filter) says what was done |
| Spam filter can't delete messages | Relink the broadcaster on Config to grant `moderator:manage:chat_messages`; a bot account must be a moderator in your channel |
| A game or giveaway command does nothing | Seeded game and `!giveaway` commands start switched off; a giveaway's entry command is only on while it is open |

For detail, temporarily raise the log level (see the configuration reference) and watch the log while reproducing.

## 21. The action builder

Selecting an action on **Actions** opens the visual builder (the raw JSON is still available on its *Advanced JSON* tab).

- **Header**: name, group, queue, enabled, *always run*, *exclude from history*, and the run mode: Sequential, Random (one
  top-level step chosen by weight) or Concurrent.
- **Triggers**: *+ Add trigger* lists the events CherBot raises, grouped by source (tick *Show all* for Streamer.bot events
  CherBot never raises). Each trigger card has typed filters: a command picker, a reward picker, minimum and maximum
  amounts, a scene name, and so on.
- **Steps**: a card per sub-action with an enable switch, move up/down, drag to reorder (within one list), duplicate, delete
  and collapse. Fields are typed from the sub-action catalog: switches, numbers, drop-downs with the values the handler
  understands, and pickers for actions, commands, timers, queues, OBS connections, live OBS scenes, sources and filters,
  Twitch rewards, active viewers, C# steps, and steps or triggers of another action. Unknown types fall back to a JSON box.
- **Containers** nest: If/Else shows *Then* and *Else* lanes; Switch shows one lane per case (with its values) plus
  *Default*; While and Group show their steps. Lanes and groups can be random, which shows each step's weight.
- **`{x}`** next to a text field lists the variables available there: the trigger's arguments (with values from the latest
  real event), arguments set by earlier steps, always-available arguments, persisted globals and format examples.
- **Save** validates first (required pickers, referenced items still exist, regexes compile); *Save anyway* is offered.
  **Save & Test** runs the action with the latest real event of its first trigger, or example data, and shows the trace.
  Test runs carry `isTest = true`. **Duplicate** copies the action (disabled); **Export** and **Delete** are here too.

## 22. Custom commands

**Commands → Custom commands** makes Nightbot-style text commands. Each one is stored as a definition and kept in sync with
a real command plus an action (group *Custom Commands*) whose single step is **Custom Command Response**.

- One or more response variants (picked at random); a variant with several lines sends several messages, with a delay.
- Placeholders: `{user}`, `{touser}`, `{args}`, `{1}`..`{9}`, `{count}` (persistent counter), `{random:1-100}`,
  `{pick:a|b|c}`, `{uptime}`, `{game}`, `{title}`, `{followage}`, `{points}`, `{currency}`, `{channel}`, plus `%args%`
  and `~globals~`.
- Send as chat, a reply or an announcement; minimum role or user groups; global and per-user cooldowns; a cost in loyalty
  points; only while live.
- **Test** renders a response with sample data on the engine without sending it. **Try in chat** uses the simulator.
- **Open in builder** edits the action. Once the action is saved from the builder, the command is marked *builder*: the form
  keeps editing the response, cooldowns and roles but never rewrites the action.
- **Bulk import** takes `!command response` lines.

## 23. Quick Setup

**Quick Setup** wizards create ordinary records (group *Quick Setup*) and open them in the builder: chat command → reply,
command → OBS scene, reward → effect (scene, source flash, sound or Speaker.bot TTS), timer message, alert (chat message,
optional sound, optional OBS source pulse), shoutout (`!so`), lurk, counter (`!deaths` / `!death+`) and a quotes pack.

The **scene shortcuts** table creates several scene commands at once (group *Scene Shortcuts*); they are listed with
switches underneath. The page also switches the seeded **`!scene <name>`** (moderators; exact name, then a unique prefix,
then a unique substring) and **`!scenes`** commands and sets the scenes they may use.

## 24. Loyalty points and games

Settings are on **Loyalty & Games** (stored in the database, effective immediately):

| Setting | Default |
| --- | --- |
| Currency name | `points` |
| Per chat message / chat cooldown | 1 / 0 s |
| First message of the stream | 10 |
| Watch time: every N minutes, points, only while live, only chatters active in the last N minutes | 10 min, 5, yes, 0 (everyone in the chatter list) |
| Multipliers for subscribers, VIPs, moderators (the highest applies) | 1.0 |
| Bonuses: follow, sub, resub, per gifted sub (to the gifter), per 100 bits, raid (to the raider) | 50, 250, 250, 100, 50, 100 |

Watch time uses Helix *chatters* when the broadcaster granted `moderator:read:chatters`, otherwise recent chatters. Messages
blocked by the spam filter earn nothing. The leaderboard searches viewers, sets balances, gives everyone in chat an amount,
pays watch time on demand and can reset everyone.

**Games** are seeded once in the group *CherBot Games*, all **switched off**: `!gamble` (`!roll`), `!slots`, `!coinflip`
(`!flip`), `!duel` / `!accept` / `!decline`, `!heist` and `!roulette`. Each game's card edits its command text, odds and
payouts, bet limits and per-user cooldown, and has a *Try it* button. Bets accept `100`, `1k`, `2.5m`, `all`, `half` and
`25%`. Results raise `CherBot.GameResult` events (game, user, bet, net, balance) for overlays.

## 25. Raffles and giveaways

**Giveaways** keeps any number of giveaway definitions. Each has:

- entry commands (a real command in group *CherBot Giveaways*, enabled only while the giveaway is open) or a keyword;
- a duration (0 = until stopped; otherwise it closes itself, also after a restart), auto-draw, winner count, a claim
  timeout (the winner must chat in time or another is drawn) and a reminder interval;
- entry rules: cost per ticket in loyalty points (refunded on cancel), maximum tickets per viewer (`!join 3`), bonus
  tickets for subs and VIPs, minimum role, followers only, and skipping previous winners;
- thirteen chat messages with placeholders (`{name}`, `{prize}`, `{command}`, `{user}`, `{tickets}`, `{entries}`,
  `{entrants}`, `{winner}`, `{winners}`, `{cost}`, `{currency}`, `{duration}`, `{remaining}`, `{claimSeconds}`);
  empty means silent.

Draws are weighted by tickets and use a cryptographic random generator. Start, Stop, Draw, Draw more, Reroll, Cancel and
Reset are on each card; the run view lists entries (remove one), winners (mark claimed) and earlier runs. The seeded
`!giveaway start|stop|draw|reroll|cancel [name]` (moderators, switched off) and the **Giveaway: Control** step control
giveaways from chat or actions. Custom Events `giveaway.started`, `.entry`, `.closed`, `.winner`, `.claimed` and
`.cancelled` carry the giveaway, user and counts.

## 26. Modules: chat alerts, emote pyramids, spam filter

**Modules** lists every feature with a switch: Chat alerts, Emote pyramids, Spam filter, and (with links to their pages)
Loyalty, Games, Giveaways and Scene commands. Module settings are stored in the database under `modules.<id>`.

**Chat alerts** posts a message for follows, subs, resubs, gift subs, gift bombs, cheers, raids, first words, reward
redemptions, stream online/offline, shoutouts received and ad breaks. Each alert has message variants with placeholders,
a minimum amount, a send mode (chat, reply, announcement with a colour), the account, and cooldowns. Gift subs that are
part of a gift bomb are folded into the gift-bomb message. Alerts ship with sensible messages; the module starts off.

**Emote pyramids** detects pyramids built from consecutive messages by one user (or anyone, if allowed): minimum and maximum
width, emotes only or any word, a success message and points reward (base plus per width), an optional "broken by" message,
or block mode (delete rows or time out from a width). Raises `pyramid.completed` and `pyramid.broken`.

**Spam filter** runs before anything else sees a chat message. Filters: links (allow-list, `!permit @user` for N seconds),
caps, symbols, repeated characters, length, emote count, banned words (plain, `wild*card` or `/regex/`), duplicates within N
seconds, and zalgo. Each filter has a punishment (delete, delete and warn in chat, timeout, or only block commands and
points), a reason and an exemption role (default: subscribers and up); permitted users and user groups, the broadcaster and
the bot are always exempt. Optional escalation follows a ladder (for example warn → 10 s → 10 min) with strikes that reset
after a quiet period. Blocked messages are still published with `filtered = true` and `filterReason`, raise a
`chat.filtered` Custom Event, and are written to the moderation log. Deleting needs `moderator:manage:chat_messages`
(included in the default broadcaster scopes; relink older installs). The seeded `!permit` switches on and off with the module.
