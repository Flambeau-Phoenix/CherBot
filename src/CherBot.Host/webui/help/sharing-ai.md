# Sharing and Get AI Help

[Sharing](#/sharing) exports selected actions and their supported dependencies, selected module settings and explicitly selected globals. Exported packs exclude linked accounts, engine tokens, encryption keys, viewer records, balances and histories. Review scripts, response text and selected variables for private values you entered yourself.

Import a JSON file or paste pack text. Preview changes, conflicts, unsupported capabilities and executable code. Resolve each listed scene/source/reward/path/connection mapping with the target engine's value; accepting the same value is explicit. Preview again after every change. Existing items are kept unless replacement is selected. Imported actions, commands, timers and module settings start disabled. Enable the underlying actions as well as their commands when ready. Applying an unchanged installed pack again is a no-op.

Streamer.bot imports and encoded exports are still available on Actions. Feature packs are configuration sharing, not personal database backups.

## Online CLI

Use `cherbot api GET /v1/actions --url http://127.0.0.1:8091 --token-file /path/to/api-token` to inspect a running engine. Writes accept `--body-file request.json`. On Windows invoke `cherbot.exe` from its installation directory and use Windows paths.

`cherbot pack export pack.json --selection-file selection.json` exports selected action IDs/settings/globals. `cherbot pack preview pack.json` validates without changing configuration. `cherbot pack apply pack.json` applies; use `--mappings-file mappings.json` and explicit `--replace` only when needed. All online commands accept `--url`, `--token-file`, and `--data-dir` for local token discovery. Preview exits with code 3 if the pack needs attention.

Existing `cherbot import` and `cherbot export` access the database offline. Stop the engine before offline changes.

## External AI chats

[Get AI Help](#/aihelp) provides version-matched reference files and a prompt for ChatGPT, DeepSeek Chat, Gemini or another chat. Describe your feature and shell; enter only names you want to share. Ask the AI for complete supported CherBot CLI commands, required JSON files, a preview, verification and removal steps.

Keep credentials local. For follow-up errors, share the version and smallest relevant redacted error/trace. The page neither connects an AI to your engine nor needs an AI API key.
