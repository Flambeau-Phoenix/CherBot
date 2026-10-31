# Build your first command

There are three ways to make a chat command. Pick the one that matches what you want.

## 1. A text reply (easiest)

Open [Commands](#/commands) and press **New**.

1. Type the command, for example `!discord`.
2. Write the response: `Join us at discord.gg/example, {user}!`
3. Press **Test** to see exactly what CherBot would send, then **Create**.

Placeholders such as `{user}`, `{touser}`, `{count}` and `{random:1-6}` are listed under the `{x}` button. See [Custom commands](custom-commands.md).

## 2. A wizard

[Quick Setup](#/quicksetup) builds common commands for you: scene switches, shoutouts, lurk, counters and quotes. Everything it creates opens in the builder afterwards.

## 3. The builder (anything)

1. On [Actions](#/actions) press **New action** and name it.
2. **+ Add trigger → Command Triggered**, then pick the command from the list (or choose **+ New command…**).
3. **+ Add step** and search for what you want, for example *Send Message to Channel*.
4. Fill in the fields. The `{x}` button next to a text field lists every variable you can use there, such as `%user%` or `%input0%`.
5. **Save & Test** runs the action with sample data and shows you the result step by step.

## Command options worth knowing

- **Cooldowns**: a global cooldown (anyone) and a per-user cooldown, in seconds.
- **Who can use it**: everyone, subscribers, VIPs, moderators, or only you.
- **Aliases**: put several command words on separate lines; any of them works.
- **Where in the message**: at the start (default), the whole message, or anywhere.

Edit these on [Commands → All commands](#/commands?view=all).
