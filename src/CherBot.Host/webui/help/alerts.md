# Alerts and sounds

## Chat alerts (no building needed)

The **Chat Alerts** module on [Modules](#/modules/chat-alerts) thanks followers, subs, resubs, gifters, cheerers and raiders in chat, and can also greet first-time chatters, announce reward redemptions, stream start and end, shoutouts you receive and ad breaks.

For each alert you choose:

- one or more **messages** (one is picked at random), with placeholders such as `{user}`, `{months}`, `{bits}`, `{viewers}`;
- a **minimum** (bits, raiders, gifts or months) below which it stays quiet;
- **send as** chat, a reply, or a coloured announcement;
- cooldowns for the alert and per user.

A 50-sub gift bomb produces one gift-bomb message, not 50 gift-sub messages. **Preview** renders an alert with sample data; **Save & send test** posts it to chat.

## Alerts with sounds or OBS effects

[Quick Setup → Alert](#/quicksetup) builds an action for one event with a chat message, an optional sound and an optional OBS source shown for five seconds (for example an animation). Edit it in the builder to add anything else.

## Sounds

**Play Sound** plays a file on the engine machine (volume 0–1). **Play Sound From Folder** picks a random file. File paths are on the computer running CherBot, not your browser. On a machine without speakers (a headless server), add CherBot's overlay as an OBS Browser Source and sounds play there instead (see the [operating guide](docs/operating-guide.md#14-sounds)).
