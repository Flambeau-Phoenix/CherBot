# CherBot quick start

Use this guide for a fresh installation. CherBot has one streamer per engine/data folder. The browser and Windows desktop app share the same interface.

## 1. Start the engine

**Windows:** extract the desktop package and run `CherBot.Desktop.exe`. Choose **Local** to host the engine on this computer, or **Remote** to connect to an existing engine using its URL and API token. If WebView2 is unavailable, use **Open in browser**.

**Linux:** download the packaged installer, then run `bash cherbot-<version>-linux-x64.run --user`, replacing `<version>` with the downloaded version. A user service stores data in `~/.local/share/cherbot`. The installation guide covers system services and archive installation.

**Browser access:** open `http://127.0.0.1:8091/` on the engine computer. Get its token using `cherbot token --data-dir <data-folder>` or read `api-token` in that folder. On Windows, use `.\cherbot.exe` from the engine package directory. The desktop app in Local mode passes the token automatically.

For a remote engine, use its LAN/VPN address and token. The default address is local-only. Do not expose the owner API to the public internet. Run one engine process per data folder.

## 2. Connect Twitch and OBS

1. Register your own **Public** Twitch application. Put its client ID in the generated `cherbot.json`, under `CherBot` → `Twitch` → `ClientId`, then restart CherBot. No shared account or client ID ships with the app.
2. Open **Config → Twitch** and link the broadcaster account. Link a bot account too if you want replies from a separate identity. Complete the device-code authorization shown by Twitch.
3. In OBS, open **Tools → WebSocket Server Settings** and enable the server. Add its host, port and password on **Config → OBS**. Use the streaming PC's LAN/VPN address when the engine is on another computer.
4. Open **Live Admin** and check connection health and scenes. Stream status and uptime appear when Twitch reports a live stream.

## 3. Enable your first workflows

Fresh installations include the `!points`, `!leaderboard`, legacy first-word `!first`, and `!disc` starter commands, plus disabled game, scene, giveaway, spam-filter and song commands. Loyalty presets are ready to customize. No linked accounts, personal Discord invite, viewer history or balances ship with the app. Start with one feature, verify it, then add more.

| Page | Quick action |
| --- | --- |
| **Quick Setup** | Create a reply, alert, timer, shoutout, lurk, quote pack or scene shortcut. Review prerequisites and the preview before enabling. |
| **Quick Setup → Loyalty Points** | Customize the preset currency, chat rewards and cooldown, or turn loyalty off. `!points` and `!leaderboard` are already provided. Estimated chat presence is not exact viewing time. |
| **Quick Setup → !first & !pinned** | Enable one winner per confirmed live stream, with a default editable 100-point bonus. `!pinned` announces the winner. Existing legacy `!first` actions require explicit conversion. |
| **Quick Setup → Add Clip Command** | Enable `!clip`; check Twitch permissions and clips availability. Default cooldowns: 60 seconds globally and 120 seconds per user. |
| **Quick Setup → Add Sery_Bot** | Follow the official enrollment steps and copy `/mod sery_bot`. Mark completion yourself after setup; this checklist does not verify protection. |
| **Modules** | Enable chat alerts, games, scene commands, giveaways or CherBot's spam filter. Sery_Bot enrollment is separate. |
| **Song Requests** | Enable providers and intake. Requests default to moderator approval. Launch the YouTube player on the streaming PC; Apple Music/Spotify use external playback and manual completion. |

For a first test, create a simple `!hello` text reply on **Commands**. Check **Monitor** for events and action runs. Its chat simulator can send real replies or change OBS when integrations are connected.

## 4. Monitor, share and get help

- **Live Admin** keeps chat and stream controls together. Click a chatter or open **Users** to inspect recent activity and adjust points. Recent chatters do not identify everyone watching.
- **Loyalty & Games** has the current-balance leaderboard. **Data & History** offers optional local statistics and a separately enabled chat archive; defaults are 90 days of statistics detail and 30 days of chat. Existing event/run diagnostics have their own retention.
- **Sharing** exports feature packs and imports through **Preview → Resolve → Apply**. Imported automation starts disabled. Review scripts and text for manually embedded private information before sharing.
- **Get AI Help** downloads a public reference and builds a prompt for external AI chats. Keep tokens, passwords, private configuration and viewer data out of those chats.
- **Help** contains searchable how-to guides, the action builder reference, and troubleshooting. An action is a list of steps; triggers start it, and queues control how it runs.

Back up the complete data folder before upgrading or migrating, including its `keys/` folder. Disabling optional recording stops collection; deletion is a separate action.

For installation details, use `README-install.md` in the downloaded package or `packaging/README-install.md` in the source repository. See `docs/operating-guide.md`, `docs/configuration-reference.md`, and the repository's `CHANGELOG.md` for more detail.
