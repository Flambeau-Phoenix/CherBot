# Live Admin and users

[Live Admin](#/dashboard) keeps OBS controls, queues, giveaways, pinned actions, chat sending and channel editing together with a bounded live chat feed. Pause freezes the displayed feed while new messages are buffered; search includes mentions. Select a username to inspect recent messages, points and rank.

[Users](#/users) lists recent chatters and message counts observed by this process in the current session. Chat activity and Twitch's chatter list do not identify all viewers watching video. Process-local counts start again after a restart; retained history is available separately when enabled.

## First winner and pinned spotlight

On [Quick Setup](#/quicksetup), preview **!first & !pinned**. Existing installations must explicitly replace the legacy ranking workflow. Its ranking remains under `!firstwords`.

One eligible viewer can claim `!first` while live. The winner is saved against the Twitch stream ID, so duplicate online events, reconnects, a bot restart and resetting first-word statistics do not reopen the contest. The next different stream starts a new contest. Broadcaster, bot and filtered messages cannot win. The default winner bonus is 100 points; choose zero to disable it. This bonus is separate from each chatter's existing first-message bonus.

`!pinned` reports the current stream's winner. The spotlight appears in CherBot; it does not grant Twitch VIP or pin a Twitch chat message.

## Clips and spam protection

The **!clip** setup requires a linked broadcaster with `clips:edit`. It uses a 60-second global and 120-second per-user cooldown and waits for Twitch to confirm creation before posting a URL. The Dashboard clip button and builder use the same service. Offline streams, disabled clips, missing permissions and unfinished creation return errors.

**Add Sery_Bot** links to the [official setup guide](https://docs.sery.bot/how-to-add/), provides `/mod sery_bot`, opens [authorization](https://sery.bot/auth), and links to Sery_Bot's chat for `!join`. The broadcaster must complete enrollment. The guide also describes optional follow-bot and offline protection. Marking setup complete is a personal checklist, not verification of protection. CherBot's own spam filter remains on Modules.
