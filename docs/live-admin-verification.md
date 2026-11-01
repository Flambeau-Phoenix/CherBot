# CherBot 0.4 implementation and verification

Implemented the approved Live Admin and community roadmap in the existing .NET 8 host, SQLite database, ES-module interface and Windows desktop shell. The engine upgrades its schema to version 3; detailed tracking and the chat archive both start off. Existing balances and legacy first-word workflows are retained until explicitly changed.

## Delivered

- Live Admin chat, mentions/search/pause, uptime, stream controls, user inspection and points edits.
- Persisted broadcast identity, atomic stream-first winner/bonus, `!pinned`, legacy `!firstwords`, and confirmed `!clip` creation.
- Managed Quick Setup records with replacement protection and repeat-save updates; assisted Sery_Bot enrollment using current official instructions.
- Shareable JSON packs with dependencies, conflict and mapping previews, transactional apply, disabled imported workflows and idempotent installation receipts.
- Online `cherbot api` / `cherbot pack` commands and external AI help with a version-matched public reference.
- Provider-selectable song requests, moderation queue, external Apple Music/Spotify links, and a restricted YouTube player with controller leases and safe restart takeover.
- Optional activity/points history, separate chat archive, retained-user search, earned-points periods, retention, export and purge controls.

## Checks on 2026-11-01

- Complete .NET suite: **118 passed**. Covers fresh/previous-schema databases, concurrent first claims, reconnect/restart persistence, bot exclusions, tracking defaults and retention, points classification, pack rollback/conflicts/dependencies, CLI requests, provider URLs/queue rules, player authorization, lease expiry and restart takeover.
- Node UI suite: **9 passed**, using refreshed public capability fixtures.
- Release solution build, including the Windows desktop application: passed with no warnings or errors.
- Temporarily inverted the first-claim SQL guard: its concurrency test failed as expected. Restored the exact source and reran the complete suite successfully.
- Isolated browser: inspected Live Admin, Users, Quick Setup, Sharing, Get AI Help, Song Requests and Data & History; saved a points edit; explicitly converted legacy first; previewed/applied a disabled test pack; confirmed repeated wizard saves retain the action ID; inspected a narrow viewport and console output.
- Played the public Google for Developers YouTube embed demo through the restricted player, then closed the player.
- Read the current Sery_Bot setup at https://docs.sery.bot/how-to-add/: broadcaster moderation assignment, authorization, then `!join` in Sery_Bot's chat.

## Remaining environment checks

Real Twitch clip creation, first claims during a real broadcast, OBS browser-source playback and the Windows WebView2 runtime still need a linked streaming environment. Those integrations were exercised with fake services or a standalone browser here. Sery_Bot authorization and channel enrollment remain explicit actions for each broadcaster. Spotify and Apple Music playback is external by design.

The disposable verification host used a separate data directory and no linked production accounts.
