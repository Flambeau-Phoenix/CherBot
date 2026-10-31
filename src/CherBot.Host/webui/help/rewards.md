# Channel point rewards

## Quick: Reward → effect

[Quick Setup → Reward → effect](#/quicksetup) builds an action for a reward that switches a scene, flashes a source for a few seconds, plays a sound, or reads the viewer's text aloud with Speaker.bot.

## By hand

1. New action, **+ Add trigger → Reward Redemption**, and pick the reward. The list comes from Twitch, so link the broadcaster first.
2. Add steps. The trigger gives you `%rewardName%`, `%rewardCost%`, `%rawInput%` (what the viewer typed) and `%user%`.
3. Optional: **Update Redemption Status** to fulfil or refund the redemption.

## Managing rewards from actions

Steps under *Twitch / Rewards* enable, pause, re-price, rename and recolour rewards. Twitch only lets an app edit rewards **that app created**; the picker marks the others "not editable by CherBot".

## Skipping the queue

Reward actions normally wait their turn in the queue. To run a reward's action immediately, set it to skip the queue (`PUT /v1/twitch/rewards/{rewardId}` with `skipQueue: true`, see the [configuration reference](docs/configuration-reference.md)).
