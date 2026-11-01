# CherBot: install and run

CherBot is a headless remake of the Streamer.bot engine. One self-contained binary runs on Linux and Windows; you manage it
through its web UI (`http://<host>:8091/`) or the Windows tray app. It speaks Streamer.bot's WebSocket, HTTP and UDP APIs
and imports Streamer.bot exports, `actions.json` and `commands.json`.

After installing, see `docs/operating-guide.md` for day-to-day operation and `docs/configuration-reference.md` for every
setting.

## Contents of a build

| File | Purpose |
| --- | --- |
| `cherbot` / `cherbot.exe` | The engine (console app, systemd service or Windows Service) |
| `cherbot.example.json` | Configuration template; copied to the data folder as `cherbot.json` on install |
| `install.sh`, `cherbot.service`, `cherbot.user.service` | Linux service install (Linux builds) |
| `install-service.ps1` | Windows Service install (Windows builds) |
| `CherBot.Desktop.exe` | Windows tray app (separate `cherbot-desktop-win-x64` build) |

## Data folder

Everything CherBot stores lives in one folder: `cherbot.db` (SQLite), `cherbot.json` (config), `api-token`, `keys/`
(encryption keys for stored tokens), `logs/`, and `references/` (extra DLLs for C# scripts).

On first run the engine creates `cherbot.json` from its bundled template if the file is missing. Existing configuration is never replaced. Packaged builds include the AI prompt and uploadable function/variable reference in `docs/`; see `docs/ai-guide.md`.

| Where it runs | Default data folder |
| --- | --- |
| Linux system service | `/var/lib/cherbot` |
| Linux user service or console | `~/.local/share/cherbot` |
| Windows (service, console or tray app Local mode) | `%ProgramData%\CherBot` |

Override with `--data-dir <path>` or the `CHERBOT_DATA` environment variable. Back up the whole folder; the `keys` folder
is needed to read the stored Twitch tokens and OBS passwords.

## Linux

The versioned Linux release is available as an offline `.run` installer and a `.tar.gz` archive. For example:

```bash
bash cherbot-0.3.0-linux-x64.run --user
# Or: sudo bash cherbot-0.3.0-linux-x64.run
```

Use the version in your downloaded filename. `--help` lists options; `--extract ./unpacked` unpacks without installing a service and requires a new destination directory. The matching `.sha256` file verifies the downloaded release files. The installer also checks its embedded payload before extraction.

With the archive:

```bash
tar -xzf cherbot-0.3.0-linux-x64.tar.gz
cd cherbot-0.3.0-linux-x64
bash ./install.sh --user
```

For older ZIP packages:

```bash
unzip cherbot-linux-x64.zip -d cherbot && cd cherbot
bash ./install.sh --user     # user service, no root; or: sudo bash ./install.sh for a system service
```

Then edit the config (`~/.local/share/cherbot/cherbot.json`, or `/var/lib/cherbot/cherbot.json` for the system service) and
restart (`systemctl --user restart cherbot` or `sudo systemctl restart cherbot`). Logs: `journalctl --user -u cherbot -f`.

To reach the web UI from other machines, bind the API to a LAN or VPN address and keep it off the public internet:

```json
"Api": { "Address": "192.0.2.10", "Port": 8091 }
```

Print the API token for the web UI or the tray app:

```bash
~/cherbot/cherbot token --data-dir ~/.local/share/cherbot
```

## Windows

- **Tray app (simplest):** run `CherBot.Desktop.exe`. On first start choose **Local** (the engine runs inside the app) or
  **Remote** (connect to an engine elsewhere by URL and API token). Profiles, "Start with Windows", queue pause/resume and
  the data and log folders are in the tray menu. The window shows the web UI; it needs the Microsoft Edge WebView2 runtime
  (preinstalled on Windows 11) and offers "Open in browser" otherwise.
- **Windows Service:** from an elevated PowerShell in the build folder run `.\install-service.ps1` (or
  `-DataDir D:\CherBot`, or `-Uninstall`). Point the tray app at it in Remote mode with `http://127.0.0.1:8091`.

Don't run the tray app's Local mode and the Windows Service against the same data folder at the same time: they use the
same ports, and stored secrets are encrypted for the Windows account that wrote them.

## Twitch

1. Register an application at <https://dev.twitch.tv/console/apps> as a **Public** client (no secret needed). Any OAuth
   redirect URL works for the device flow, for example `http://localhost:8091/twitch/callback`.
2. Put its client id in `cherbot.json` under `"Twitch": { "ClientId": "…" }` and restart.
3. Link the accounts, either in the web UI (Config → Twitch → Link) or from a terminal on a headless box:

   ```bash
   cherbot twitch-login broadcaster --data-dir <data folder>
   cherbot twitch-login bot --data-dir <data folder>
   ```

   Open the shown URL on any device, sign in as that account and enter the code. Tokens are refreshed automatically.

CherBot subscribes to EventSub over WebSocket with the broadcaster account (chat, subs and gifts, cheers, follows, raids,
channel point redemptions, stream online/offline and channel updates) and sends chat as the bot account, falling back to
the broadcaster when the bot is not linked.

## OBS (obs-websocket v5)

In OBS: Tools → WebSocket Server Settings → enable, set a password. In CherBot: Config → OBS → Add connection (host, port
4455, password), or list connections in `cherbot.json` under `"Obs": { "Connections": [ { "Name": "Main", "Host": "…",
"Port": 4455, "Password": "…" } ] }`. Imported Streamer.bot OBS sub-actions use the first connected OBS when their
connection id is unknown.

## Speaker.bot

On the PC running Speaker.bot: Settings → WebSocket Server → enable auto start, bind it to the LAN or VPN address, and
firewall port 7580 so only the CherBot host can reach it. In `cherbot.json`:
`"SpeakerBot": { "Enabled": true, "Host": "<pc address>", "Port": 7580 }`. Speaker.bot's UDP interface (port 6669) is used
as a fallback and for voice-gate profiles.

## Importing from Streamer.bot

- Web UI → Actions → Import: paste an export string or the contents of `actions.json` / `commands.json`, preview, import.
- Terminal: `cherbot import <file> [--dry-run] [--rewrite-paths] --data-dir <dir>`.

The report lists sub-actions and triggers CherBot can't run on this host. They are kept and exported again unchanged, and
skipped at run time. Windows paths in imported sound and file sub-actions can be rewritten for Linux with
`"Engine": { "PathMappings": { "C:\\Stream\\Sounds": "/srv/stream/sounds" } }`.

C# code (Execute C# Code) runs on .NET 8 with a Streamer.bot-compatible subset of the `CPH` API. Scripts run with the
same full trust as in Streamer.bot: only import code you trust. Extra DLLs a script references go in `references/`.

## Sound

`"Engine": { "SoundMode": "auto" }` plays sounds locally (NAudio on Windows; `pw-play`, `paplay`, `ffplay`, `mpv` or
`aplay` on Linux). On a box without speakers use `"overlay"` and add an OBS Browser Source pointing at
`http://<host>:8091/overlay.html#token=<api token>`. The overlay can only play files inside `Engine:MediaRoots`.

## Streamer.bot-compatible APIs

| Server | Default | Notes |
| --- | --- | --- |
| WebSocket | `ws://127.0.0.1:8080/` | Works with `@streamerbot/client`, Stream Deck plugins and overlays. Set `WebSocketServer:Password` (and `EnforceAuth`) before binding it beyond localhost |
| HTTP | off, port 7474 | `POST /DoAction`, `GET /GetActions`; no authentication, as in Streamer.bot |
| UDP | off, port 4242 | `{"request":"DoAction",...}`; no authentication |

The management API (`/v1/...`, used by the web UI) always requires the API token.

## Migrating from the Python prototype

```bash
cherbot migrate-python /path/to/python/cherbot.db --data-dir <data folder>
```

This copies points, first words, quotes and persisted globals. The `!first`, `!points`, `!leaderboard` and `!disc` commands
are created automatically on first start as ordinary actions you can edit.

## Command line

```
cherbot                          run the engine
cherbot token                    print the API token
cherbot import <file|->          import Streamer.bot data (--dry-run, --no-overwrite, --rewrite-paths)
cherbot export [--name <name>]   print an export string with all actions, commands and timers
cherbot twitch-login <broadcaster|bot>
cherbot migrate-python <cherbot.db>
```

Every command accepts `--data-dir <dir>` and any configuration key as `--CherBot:Section:Key value`.
