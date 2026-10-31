# CherBot configuration reference

Settings live under the `CherBot` section. Engine settings are read once at startup, so **restart CherBot after changing
them**. Actions, commands, timers, queues, globals, OBS connections added in the web UI, and Twitch links are stored in the
database and take effect immediately; they are not part of this file.

## Where settings come from

Later sources override earlier ones:

1. `appsettings.json` next to the executable (shipped defaults; don't edit, it is replaced on upgrade).
2. **`<data folder>/cherbot.json`** — your settings. Start from `cherbot.example.json`.
3. Environment variables prefixed `CHERBOT_`, with `__` between levels:
   `CHERBOT_CherBot__Api__Port=9000`.
4. Command-line arguments: `cherbot --CherBot:Api:Port 9000`.

The data folder itself is chosen before any of these are read, in this order:

1. `--data-dir <path>`
2. the `CHERBOT_DATA` environment variable
3. systemd's `STATE_DIRECTORY`
4. the default: `%ProgramData%\CherBot` on Windows, `/var/lib/cherbot` when running as root on Linux, otherwise
   `$XDG_DATA_HOME/cherbot` (usually `~/.local/share/cherbot`)

For working on the web UI, `CHERBOT_WEBUI_DIR=<path to src/CherBot.Host/webui>` serves the UI files from disk instead of
the copy built into the executable (files missing on disk still come from the built-in copy). `node tests/ui/fake-obs.mjs`
starts a stand-in OBS WebSocket server on port 14455 for trying the OBS controls.

## Example

```json
{
  "CherBot": {
    "InstanceName": "StreamServer",
    "Api": { "Address": "192.0.2.10", "Port": 8091 },
    "WebSocketServer": { "Enabled": true, "Address": "192.0.2.10", "Port": 8080, "Password": "change-me", "EnforceAuth": true },
    "Engine": {
      "SoundMode": "overlay",
      "MediaRoots": [ "/srv/stream/sounds" ],
      "PathMappings": { "C:\\Stream\\Sounds": "/srv/stream/sounds" }
    },
    "Twitch": { "ClientId": "abc123yourclientid" },
    "Obs": { "Connections": [ { "Name": "Streaming PC", "Host": "192.0.2.20", "Port": 4455, "Password": "obs-password" } ] },
    "SpeakerBot": { "Enabled": true, "Host": "192.0.2.20", "Port": 7580 }
  }
}
```

## General

| Key | Default | Description |
| --- | --- | --- |
| `CherBot:InstanceName` | machine name | Name reported to Streamer.bot clients (`Hello` / `GetInfo`) |

## Management API and web UI — `CherBot:Api`

| Key | Default | Description |
| --- | --- | --- |
| `Address` | `127.0.0.1` | `127.0.0.1`, `localhost`, a specific IP (such as a LAN or VPN address), or `0.0.0.0` for all interfaces |
| `Port` | `8091` | Port for the web UI, `/v1` API, `/health` and the sound overlay |
| `Token` | *(generated)* | Fixed API token. When empty, one is generated into `<data folder>/api-token` |

## Streamer.bot-compatible servers

### `CherBot:WebSocketServer`

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Run the WebSocket server |
| `Address` | `127.0.0.1` | Bind address (as for `Api:Address`) |
| `Port` | `8080` | Must differ from the API port |
| `Endpoint` | `/` | Path clients connect to |
| `Password` | *(empty)* | Enables Streamer.bot's salt/challenge authentication |
| `EnforceAuth` | `false` | With a password: require authentication for every request (otherwise only `SendMessage`) |

### `CherBot:HttpServer`

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `false` | Run the HTTP server (`POST /DoAction`, `GET /GetActions`, …). No authentication |
| `Address` | `127.0.0.1` | Bind address |
| `Port` | `7474` | Must differ from the other ports |

### `CherBot:UdpServer`

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `false` | Listen for `{"request":"DoAction",...}` datagrams. No authentication |
| `Address` | `127.0.0.1` | Bind address |
| `Port` | `4242` | UDP port |

## Engine — `CherBot:Engine`

| Key | Default | Description |
| --- | --- | --- |
| `SoundMode` | `auto` | `auto`, `local`, `overlay` or `none` (see the operating guide, section 14) |
| `MediaRoots` | `[]` | Folders the sound overlay may stream files from |
| `PathMappings` | `{}` | Prefix rewrites for file paths in imported actions, longest prefix first. On Linux, backslashes become `/` and file-name case is matched loosely |
| `RunHistoryDays` | `14` | How long run-log entries are kept |
| `EventLogDays` | `3` | How long the event log (used for SSE resume and Live events) is kept |
| `PendingFlushSeconds` | `30` | How often queued-but-not-run actions are saved for restart recovery (minimum 5) |

## Twitch — `CherBot:Twitch`

| Key | Default | Description |
| --- | --- | --- |
| `ClientId` | *(empty)* | Client id of **your** Twitch application. Required for anything Twitch |
| `ClientSecret` | *(empty)* | Only for the optional authorization-code login with a confidential app; the device-code login doesn't need it |
| `RedirectUri` | `http://<api host>/twitch/callback` | Redirect for the authorization-code login; must match the app registration |
| `AutoConnect` | `true` | Connect EventSub when the broadcaster is linked |
| `BroadcasterScopes` | see below | Scopes requested when linking the broadcaster. Changing them requires relinking |
| `BotScopes` | see below | Scopes requested when linking the bot account |
| `EventSub` | see below | EventSub subscription types to create |

Default broadcaster scopes: `user:read:chat`, `user:write:chat`, `channel:bot`, `channel:read:redemptions`,
`channel:manage:redemptions`, `moderator:read:followers`, `moderator:manage:shoutouts`, `moderator:manage:announcements`,
`moderator:manage:banned_users`, `moderator:manage:chat_settings`, `moderator:read:chatters`,
`moderator:manage:chat_messages`, `moderator:manage:warnings`, `bits:read`,
`channel:read:subscriptions`, `channel:manage:broadcast`, `channel:edit:commercial`, `channel:manage:raids`,
`channel:manage:vips`, `channel:manage:moderators`, `clips:edit`, `channel:read:ads`, `user:read:email`.
Installs linked before the spam filter existed should relink the broadcaster to grant the two newer moderator scopes
(Config shows a reminder).

Default bot scopes: `user:read:chat`, `user:write:chat`, `user:bot`, `moderator:manage:announcements`,
`moderator:manage:chat_messages`, `moderator:manage:banned_users`, `moderator:manage:warnings`.

Default EventSub types: `channel.chat.message`, `channel.chat.notification` (subs, resubs, gifts, announcements),
`channel.chat.message_delete`, `channel.follow`, `channel.cheer`, `channel.raid`,
`channel.channel_points_custom_reward_redemption.add`, `stream.online`, `stream.offline`, `channel.update`,
`channel.shoutout.create`, `channel.shoutout.receive`, `channel.ad_break.begin`.

Arrays replace the default list entirely. For example, setting `EventSub` to `["channel.chat.message"]` subscribes to chat only.

## Community features — `CherBot:Community`

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Initial value of the loyalty switch, and seed the built-in commands |
| `FirstWordPoints` | `10` | Initial points for a chatter's first message of the stream |
| `ChatPoints` | `1` | Initial points for every other chat message |

These three only seed the loyalty settings on first start. After that, points (and every other loyalty, game, giveaway and
module setting) are edited in the web UI and stored in the database (`kv` keys `loyalty.settings`, `games.settings`,
`modules.<id>`; giveaways and custom commands are documents).
| `SeedDefaults` | `true` | Create `!first`, `!points`, `!leaderboard` and `!disc` on first start (once; deleting them later is permanent) |
| `DefaultDiscordUrl` | `https://discord.gg/cherbot` | Link used by `!disc` until the `discord_url` global is set |

## OBS — `CherBot:Obs`

`Connections` is a list; each entry:

| Key | Default | Description |
| --- | --- | --- |
| `Name` | `OBS` | Display name; also determines the connection's stable id |
| `Host` | `127.0.0.1` | Machine running OBS |
| `Port` | `4455` | obs-websocket port |
| `Password` | *(empty)* | obs-websocket password |
| `AutoConnect` | `true` | Keep the connection open |
| `RetrySeconds` | `10` | Delay between reconnect attempts (minimum 2) |

Connections added in the web UI are stored in the database, with the password encrypted, and appear alongside these.

## Speaker.bot — `CherBot:SpeakerBot`

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `false` | Connect to Speaker.bot |
| `Host` | `127.0.0.1` | Machine running Speaker.bot |
| `Port` | `7580` | Speaker.bot WebSocket server port |
| `Endpoint` | `/` | WebSocket path |
| `UdpPort` | `6669` | Speaker.bot's UDP port (fixed in Speaker.bot; change only for testing) |
| `RetrySeconds` | `10` | Delay between reconnect attempts |

## Logging — `Serilog`

Console and daily files (`<data folder>/logs/cherbot-YYYYMMDD.log`, 14 kept) are always on. Raise detail with:

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Debug",
    "Override": { "CherBot.Integrations.Twitch": "Debug", "Microsoft": "Warning" }
  }
}
```

`Debug` logs each queued and started action, trigger matches and connection attempts. Return to `Information` afterwards.

## Windows tray app settings

`CherBot.Desktop.exe` keeps its own small settings file, `%AppData%\CherBot\desktop.json`. Point it elsewhere with the
`CHERBOT_DESKTOP_DIR` environment variable. Edit it through the tray menu (**Profiles → Manage…**); it holds:

| Setting | Default | Description |
| --- | --- | --- |
| `Mode` | asked on first start | `local` (engine inside the app) or `remote` |
| `LocalApiPort` / `LocalWebSocketPort` | `8091` / `8080` | Ports for the engine in Local mode (always bound to 127.0.0.1) |
| `LocalDataDir` | *(engine default)* | Data folder for Local mode |
| `Profiles`, `ActiveProfile` | — | Remote engines: name, URL and API token (encrypted for your Windows account) |

In Local mode the engine still reads `cherbot.json` from its data folder for everything else.

## Reference: action JSON

Actions, sub-actions and triggers use Streamer.bot's `actions.json` shape, so exports from either tool fit. The `type`
numbers are Streamer.bot's ids; `GET /v1/catalog/subactions` and `GET /v1/catalog/events` list every id, its fields, and
whether this host supports it.

### Example: a timer that posts a message

Create the timer first (`POST /v1/timers` with `{"name":"Promo","interval":900,"lines":5}`), then the action with its id:

```json
{
  "name": "Promo message",
  "group": "Timers",
  "triggers": [ { "type": 701, "timerId": "<timer id>" } ],
  "subActions": [
    { "type": 10, "index": 0, "text": "Follow for more streams! Discord: ~discord_url~", "useBot": true, "fallback": true }
  ]
}
```

### Example: a channel-point reward that switches OBS scene and back

```json
{
  "name": "Reward: BRB cam",
  "queue": null,
  "triggers": [ { "type": 112, "rewardId": "<reward id from the Live events data>" } ],
  "subActions": [
    { "type": 34, "index": 0, "sceneName": "Cam Only" },
    { "type": 1002, "index": 1, "value": "15000" },
    { "type": 34, "index": 2, "sceneName": "Gameplay" },
    { "type": 11, "index": 3, "status": 0 }
  ]
}
```

### Example: a command with an If/Else

```json
{
  "name": "!hug",
  "triggers": [ { "type": 401, "commandId": "<command id>" } ],
  "subActions": [
    { "type": 120, "index": 0, "input": "%input0%", "operation": 9, "value": "", "autoType": true,
      "subActions": [
        { "type": 99901, "index": 0, "subActions": [ { "type": 10, "index": 0, "text": "%user% hugs everyone!" } ] },
        { "type": 99902, "index": 1, "subActions": [ { "type": 10, "index": 0, "text": "%user% hugs %input0%!" } ] }
      ] }
  ]
}
```

### Common sub-action types

| Type | Name | Main fields |
| --- | --- | --- |
| 1 | Play Sound | `soundFile`, `volume` (0–1), `finishBeforeContinuing` |
| 3 | Write To File | `file`, `text`, `append` |
| 4 | Run Action | `actionId`, `runImmedately` (Streamer.bot's spelling) |
| 10 | Twitch: Send Message | `text`, `useBot`, `fallback` |
| 11 | Twitch: Update Redemption Status | `status` (0 fulfil, 1 cancel) |
| 23 | Twitch: Announcement | `text`, `color` (0 default, 1 blue, 2 green, 3 orange, 4 purple), `useBot` |
| 30 | OBS: Source Visibility | `sceneName`, `sourceName`, `state` (0 show, 1 hide, 2 toggle) |
| 34 | OBS: Set Scene | `sceneName` |
| 35 | OBS: Filter State | `sourceName`, `filterName`, `state` |
| 39 | OBS: Set Text | `sourceName`, `text` |
| 120 | If/Else | `input`, `operation`, `value`, `autoType`; children 99901 (true) / 99902 (false) |
| 121 / 122 | Global Get / Set | `variableName`, `persisted`; Set: `value`, `source` (0 value, 1 increment, 2 decrement), `destination` (0 global, 1 user) |
| 123 | Set Argument | `variableName`, `value`, `autoType` |
| 124 | Break | — |
| 127 | Switch | `input`; children 99903 (`values`) / 99904 (default) |
| 128 | While | `input`, `operation`, `value` |
| 602 | Speaker.bot: Speak | `alias`, `message`, `badWordFilter` |
| 1002 | Delay | `value` (ms), `random`, `maxValue` |
| 1003 | Random Number | `min`, `max` → `randomNumber` |
| 1007 | Fetch URL | `url`, `variableName`, `parseAsJson`, `headers` |
| 1020 | Log | `message`, `logLevel` |
| 1031 / 1032 | Send / Wait For Local Signal | `signalName`; Wait: `timeout` (ms) |
| 1034 | Custom Event Trigger | `eventName`, `useArgs` |
| 99999 | Execute C# Code | `code` (plain text) or `byteCode` (base64 of the source, as Streamer.bot stores it) |

State fields use 0 = on/enable, 1 = off/disable, 2 = toggle unless noted.

### Supported on this build

| Area | Type ids |
| --- | --- |
| Core | 1–9, 120–130, 1000–1007, 1009–1011, 1013–1017, 1020–1022, 1026–1028, 1031–1040, 99900–99904, 99998, 99999 |
| Desktop only (Windows tray app, Local mode) | 1012 Keyboard Press, 1023 Get Input, 1024 Toast |
| Twitch | 10–20, 23, 28, 29, 50–52, 200–202, 204–215, 251–254, 500–508 |
| OBS | 30–49, 321–329 |
| Speaker.bot | 601–607 |
| CherBot | 50001 Points command, 50002 Leaderboard, 50003 First Words command, 50004 Discord hub, 50005 Award Points (`amount`, `target` 0 user / 1 target user) |
| CherBot games | 50010 Gamble, 50011 Slots, 50012 Coinflip, 50013 Duel (`mode` challenge/accept/decline), 50014 Heist, 50015 Roulette; each reads `input0`/`input1` and sets `reply` |
| CherBot OBS | 50020 Scene From Chat (`allowedScenes`, `connectionId`; reads `rawInput`), 50021 List Scenes |
| CherBot giveaways | 50030 Giveaway: Enter (`giveawayId`; `input0` = tickets), 50031 Giveaway: Control (`operation` start/stop/draw/reroll/cancel or empty to read it from chat, `giveawayId`, `durationSeconds`) |
| CherBot commands, moderation | 50040 Custom Command Response (`customCommandId`), 50041 Spam Filter: Permit Links (`userLogin`, default `input0`) |

Anything else imports and round-trips but is skipped when the action runs.

## Reference: management API

All `/v1` routes need `Authorization: Bearer <token>`. `?token=<token>` also works, which `EventSource` and media URLs need.

| Area | Routes |
| --- | --- |
| Actions | `GET/POST /v1/actions`, `GET/PUT/DELETE /v1/actions/{id}`, `POST /v1/actions/{id}/enabled` `{enabled}`, `POST /v1/actions/{id}/run` `{args, wait}`, `POST /v1/doaction` `{action:{id\|name}, args}`, `GET /v1/runs?limit&actionId` |
| Queues | `GET/POST /v1/queues`, `PUT/DELETE /v1/queues/{id}`, `POST /v1/queues/{all\|default\|name\|id}/pause\|resume?clear=true\|clear` |
| Commands, timers | `GET/POST /v1/commands`, `GET/PUT/DELETE /v1/commands/{id}`, `POST /v1/commands/{id}/reset-counters?users=true`; `GET/POST /v1/timers`, `PUT/DELETE /v1/timers/{id}`, `POST /v1/timers/{id}/fire` |
| State | `GET /v1/globals?persisted`, `PUT /v1/globals/{name}` `{value, persisted}`, `DELETE /v1/globals/{name}?persisted`, `GET /v1/users/{platform}/globals?name&userId`, `GET/POST /v1/quotes`, `DELETE /v1/quotes/{id}`, `GET /v1/groups?group`, `POST /v1/groups/{group}` `{userId, login, userName, platform}`, `DELETE /v1/groups/{group}/{platform}/{userId}` |
| Events | `GET /v1/events?since=<seq>` (SSE; resumes from `Last-Event-ID`), `GET /v1/events/recent?since&limit` (without `since`: the latest `limit` events), `GET /v1/events/sample?typeId=` (latest real event of a type), `POST /v1/events/publish` `{source, type, data}`, `POST /v1/chat/simulate` `{message, user, userId, role, emotes}` (returns `filtered`, `filterReason`) |
| Builder helpers | `POST /v1/actions/{id}/duplicate`, `POST /v1/actions/{id}/test` `{args, sampleArgs}`, `PUT/DELETE /v1/users/{platform}/{userId}/globals/{name}` `{value, persisted, userLogin, userName}`, `GET/PUT /v1/ui/settings` (pins, dashboard layout) |
| Loyalty, games | `GET/PUT /v1/loyalty/settings`, `GET /v1/points?search&platform&limit`, `POST /v1/points/give-active` `{amount}`, `POST /v1/points/watch-now`, `DELETE /v1/points/{platform}` (reset everyone), `GET/PUT /v1/games/settings`, `GET /v1/games/state`, `GET /v1/games/commands` |
| Giveaways | `GET/POST /v1/giveaways`, `GET/PUT/DELETE /v1/giveaways/{id}`, `POST /v1/giveaways/{id}/start` `{durationSeconds}`, `POST /v1/giveaways/{id}/stop\|draw\|reroll\|cancel\|reset` (draw takes `{count}`), `GET /v1/giveaways/{id}/entries`, `DELETE /v1/giveaways/{id}/entries/{platform}/{userId}`, `GET /v1/giveaways/{id}/winners`, `POST /v1/giveaways/{id}/winners/{winnerId}/claim`, `GET /v1/giveaways/messages/defaults` |
| Modules | `GET /v1/modules`, `GET/PUT /v1/modules/{id}/settings`, `POST /v1/modules/{id}/enabled` `{enabled}`, `GET /v1/modules/chat-alerts/kinds`, `POST /v1/modules/chat-alerts/test` `{alert, send}`, `POST /v1/modules/spam-filter/test` `{message, role, emotes}`, `GET /v1/modules/spam-filter/log?limit`, `POST /v1/modules/spam-filter/permit` `{login}` |
| Custom commands | `GET/POST /v1/custom-commands`, `GET/PUT/DELETE /v1/custom-commands/{id}`, `POST /v1/custom-commands/{id}/test` `{user, input, args}`, `POST /v1/custom-commands/preview` `{definition, user, input}`, `POST /v1/custom-commands/import` `{text}` |
| Import/export | `POST /v1/import?dryRun&overwrite&rewritePaths` (body: the text), `POST /v1/export` `{actions, commands, timers, name}` |
| Twitch | `GET /v1/twitch/status`, `POST /v1/twitch/reconnect`, `POST /v1/twitch/auth/{broadcaster\|bot}/device`, `GET\|DELETE /v1/twitch/auth/{role}`, `GET /v1/twitch/auth/{role}/authorize-url`, `POST /v1/twitch/chat` `{message, useBot}`, `POST /v1/twitch/eventsub/loopback` `{type, event}`, `GET /v1/twitch/rewards`, `PUT /v1/twitch/rewards/{id}` `{skipQueue, group}`, `GET /v1/twitch/rewards/list` (live from Helix), `GET/PATCH /v1/twitch/channel` `{title, gameId}`, `GET /v1/twitch/categories?query=`, `POST /v1/twitch/marker` `{description}`, `POST /v1/twitch/clip`, `POST /v1/twitch/commercial` `{length}`, `POST /v1/twitch/shoutout` `{login}`, `GET /v1/viewers` |
| Points | `GET /v1/leaderboard?limit`, `PUT /v1/points/{platform}/{userId}` `{points, userName, add}`, `GET/DELETE /v1/first-words` |
| OBS, Speaker.bot | `GET/POST /v1/obs/connections`, `DELETE /v1/obs/connections/{id}`, `POST /v1/obs/reconnect`, `POST /v1/obs/request` `{requestType, requestData, connectionId}`, `GET /v1/obs/scenes?connectionId`, `GET /v1/obs/scene-items?scene=`, `GET /v1/obs/filters?source=`, `GET /v1/obs/inputs`, `POST /v1/obs/scene` `{sceneName}`, `POST /v1/obs/scene-item` `{sceneName, sceneItemId, enabled}`, `GET /v1/speakerbot/status`, `POST /v1/speakerbot/speak` `{voice, message}` |
| Other | `GET /v1/integrations`, `GET /v1/catalog/events`, `GET /v1/catalog/subactions`, `GET /v1/media?path=`, `POST /v1/migrate/python` `{path}` |

Without a token: `GET /health`, `GET /version`, the web UI files, and the Twitch OAuth callback.
