# Quick Setup wizards

[Quick Setup](#/quicksetup) creates ordinary commands, actions and timers in the *Quick Setup* group, then opens the result in the builder so you can see and change everything.

| Wizard | Creates |
| --- | --- |
| Chat command → reply | A command and an action with *Send Message* |
| Command → OBS scene | A command (moderators by default) that switches scene |
| Scene shortcuts | Several scene commands at once, listed under *Existing shortcuts* |
| Reward → effect | A Reward Redemption action: switch scene, flash a source, play a sound, or text-to-speech |
| Timer message | A timer plus an action that posts a message (optionally only after N chat lines) |
| Alert | An event action with a chat message, optional sound and optional OBS source pulse |
| Shoutout | `!so @user`: looks the user up, sends a Twitch shoutout and a message |
| Lurk | `!lurk` with a friendly reply |
| Counter | `!deaths` shows a global counter; `!death+` (moderators) adds one |
| Quotes pack | `!quote` (random or by number) and `!addquote` (moderators) |

The page also holds the switches and allowed-scenes list for the built-in `!scene` and `!scenes` commands. See [Chat → OBS scenes](obs-scenes.md).
