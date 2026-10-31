# Getting started

CherBot runs your stream automation: chat commands, alerts, OBS scene changes, loyalty points, games and giveaways. Everything is built from **actions**.

## The five-minute tour

1. **Connect Twitch** on [Config](#/config): link the broadcaster account (and a bot account if you have one). Approve the code Twitch shows you.
2. **Connect OBS** on [Config](#/config): in OBS open *Tools → WebSocket Server Settings*, enable it, then add the host, port and password here.
3. **Open the [Dashboard](#/dashboard)**: you should see your scenes, the live status, and the tools row.
4. **Make something** with [Quick Setup](#/quicksetup) (for example a `!brb` scene command), or write a text command on [Commands](#/commands).
5. **Switch on modules** you want on [Modules](#/modules): chat alerts, loyalty points, games, the spam filter.

## How CherBot thinks

- An **action** is a list of **steps** (sub-actions) such as *Send Message*, *Set Active Scene* or *Play Sound*.
- **Triggers** start an action: a chat command, a follow, a reward redemption, a timer, an OBS scene change…
- While an action runs it has **arguments** like `%user%` or `%rawInput%`. **Globals** like `~deaths~` keep values between runs. See [Variables and templates](variables.md).
- Actions run on **queues**, one after another by default. See [Queues](queues.md).

## Where things live

| Page | What it is for |
| --- | --- |
| Dashboard | Live controls: scenes, sources, pinned actions, chat, title and category, ads, clips |
| Actions | Every action, and the visual builder |
| Commands | Quick text commands and every chat command |
| Modules | Chat alerts, emote pyramids, spam filter, and the on/off switch for everything else |
| Quick Setup | Wizards that build common things for you |
| Loyalty & Games | Points settings, the leaderboard and the six chat games |
| Giveaways | Raffles and giveaways |
| Variables | Globals, user globals and the variable reference |
| Monitor | Queues, the chat simulator, live events and recent runs |

> Tip: the **chat simulator** on [Monitor](#/monitor) lets you try commands without going live. Replies are still sent to your real chat when Twitch is linked.
