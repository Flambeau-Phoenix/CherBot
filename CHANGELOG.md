# What's new

## 0.4.0 — Unreleased

### Added

- **Live Admin:** bounded live chat with search, mentions and pause; stream status, uptime, connection health, scenes, queues and the current first winner.
- **Users:** recent chatters, observed roles, message counts, points, ranks and a detail view with points controls. Chat activity is an observation, not a list of everyone watching.
- **Quick Setup:** guided loyalty settings, stream-specific `!first` and `!pinned`, `!clip`, and assisted Sery_Bot enrollment. Managed workflows update existing records and protect builder edits.
- **Stream-first contest:** a persisted Twitch stream identity and an atomic winner/bonus award that survives reconnects and restarts. Existing first-word rankings remain available through explicit conversion.
- **Sharing:** portable JSON feature packs, dependency inclusion, import preview, explicit conflict replacement and installation-specific mappings. Packs exclude accounts, viewer records and balances; imported actions, commands and timers start disabled.
- **Online CLI and Get AI Help:** configure a running engine with `cherbot api` and `cherbot pack`; download public, version-matched references and build prompts for external AI chats.
- **Song Requests:** moderated YouTube, Apple Music and Spotify links; queue controls and a YouTube player with a restricted, revocable token and one active controller. Apple Music and Spotify playback stays external.
- **Optional local history:** extended statistics and a separate chat archive switch, user timelines, earned-points rankings, retention, export and deletion controls. SQLite has no database subscription cost.

### Changed

- Retained CherBot's starter commands and presets. Public builds contain no linked accounts, personal Discord invite, viewer records or balances. Existing installations keep their configuration.
- Refined the shared browser/Windows UI with local Tabler SVG icons, clearer dark surfaces, keyboard focus and responsive navigation. Transparent CherBot branding is used in the sidebar, favicon, desktop windows and tray.
- Added a single packaged and in-app quick-start guide, updated help and refreshed the AI capability reference.
- New clip actions confirm Twitch's asynchronous result before announcing the public URL; the Dashboard, command and action builder share the same service.
- New points changes use one transaction path with reason codes for optional history.

### Upgrade notes

- Back up the **entire data folder**, including `keys/`, before upgrading. SQLite upgrades to schema version 3 on startup; downgrading requires restoring the matching backup.
- Existing balances, custom actions and first-word statistics are preserved. Convert `!first` in Quick Setup only when ready; resetting first-word statistics does not reopen a claimed stream contest.
- Extended history and the chat archive are **off by default**, with no historical backfill. Recording and deletion are separate controls. Existing diagnostic retention remains independent.
- A shared pack can contain private text manually pasted into scripts or fields. Review its contents before publishing it.
- Real Twitch broadcasts/clips, OBS browser-source playback and native WebView2 rendering still require validation in a linked streaming setup; automated integration checks use fake services.
