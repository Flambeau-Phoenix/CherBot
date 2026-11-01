# Build a CherBot workflow with an AI chat

The **Get AI Help** page downloads the same version-matched reference and builds a prompt for your feature, operating system and shell. It directs you to an external chat; it does not connect the chat to your engine. Prefer complete `cherbot api` / `cherbot pack` commands with JSON files, preview, verification and removal steps. Keep authentication in local token files with `--token-file`.

Upload **[cherbot-ai-reference.txt](cherbot-ai-reference.txt)** to ChatGPT, Arena, DeepSeek Chat, or another chat interface that accepts text attachments. Copy **[cherbot-ai-prompt.txt](cherbot-ai-prompt.txt)** into your message, replace the bracketed details, and describe what you want.

If an interface does not accept attachments, paste the reference in a chat with enough context capacity. If it rejects the size, give it the introduction and the sections for your trigger and required steps/functions. Keep the compatibility limits and variable rules with those excerpts.

The file describes the version at its top. It contains supported capabilities, not your accounts, passwords, API token, OBS scene names, or private actions. Supply scene/source names yourself and keep credentials local.

## Choose an output you can use

| Ask for | Best for | Where the result goes |
| --- | --- | --- |
| **Web UI steps** | A first command, timer, alert, straightforward OBS action | Commands, Quick Setup, or Actions |
| **C# script + trigger instructions** | Custom logic, counters, argument processing, supported CPH calls | Actions → select an action → add Execute C# Code |
| **Readable import JSON** | A complete workflow with linked commands, timers, and actions | Actions → Import → Preview, then Import |
| **Bash/PowerShell + HTTP requests** | Headless setup and inspection by a terminal agent | Your terminal, using the API and a local token |

An AI chat cannot connect to your local engine merely because you upload the reference. It can produce instructions or files; an agent with access to your terminal can apply them through the CLI/API.

## Useful requests

**Command:** “Make a moderator-only `!brb` command that changes OBS connection 0 to scene `Be Right Back` and says `Taking a short break!`. Give me importable JSON.”

**Timer:** “Every 15 minutes, after at least five chat lines, post my socials link. Give me Web UI steps.”

**C#:** “Keep a persisted death counter. Give me a C# script that increments it, sets a `deathCount` argument, and posts it in chat. Explain how to attach it to a moderator command.”

**Headless:** “Give me Bash commands to inspect queues and run one existing action by ID using the authenticated API. Read the token locally; do not ask me to paste it into chat.”

## Check the answer

- Match the reference version to the version shown in CherBot.
- Check CPH signatures against the attachment. Platform enum names and compatibility entries do not imply their integrations are implemented.
- Use valid UUIDs and exact OBS names. A command needs an action with a Command Triggered trigger pointing at its ID.
- Preview imports and review overwrites and unsupported items.
- Scripts execute with the engine account's permissions. Read generated code before running it.
- **Save & Test**, API action tests, and the chat simulator can execute real effects. `isTest` is a flag for your logic, not isolation. Custom-command **Test**/`/v1/custom-commands/preview` renders text without sending it; simulator-triggered replies can be sent for real.

If a result fails, send the relevant compile error or run trace, CherBot version, and smallest relevant action JSON. Remove credentials and private viewer details first.

## Refresh the reference (maintainers)

Build and start an engine from the same checkout. Use a disposable data folder; linked accounts are not needed to export catalogs:

```bash
dotnet build src/CherBot.Host -c Release
dotnet src/CherBot.Host/bin/Release/net8.0/cherbot.dll \
  --data-dir .devhost/ai-reference \
  --CherBot:Api:Port 18091 --CherBot:WebSocketServer:Enabled false
```

In another terminal at the repository root:

```bash
node tools/export-ai-reference.mjs \
  --url http://127.0.0.1:18091 \
  --token-file .devhost/ai-reference/api-token
```

The exporter reads only `/version`, `/v1/catalog/events`, and `/v1/catalog/subactions` from the engine. It combines them with the checked-in CPH signatures/models, variable metadata, field options/defaults, CLI source, API routes, and maintained introduction. It does not export account tokens, actions, viewer data, or private configuration. Stop the disposable host afterward.

Commit `cherbot-ai-reference.txt` alongside capability changes. Packaging copies `docs/` into engine and desktop builds, so the reference and prompt travel with a release.
