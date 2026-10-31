# Running a raffle or giveaway

Giveaways live on [Giveaways](#/giveaways). Each one is a saved definition you can run as often as you like.

## Create it

1. **New giveaway**. Give it a name and a prize.
2. **Entry commands**: one or more, such as `!join` and `!enter`. CherBot creates the command and switches it on **only while the giveaway is open**. Or set a **keyword**: any chat message containing it counts as an entry.
3. **Activation**: how long entries stay open. *Until stopped* (0) waits for you; otherwise it closes by itself, even across a restart. Choose whether to **draw automatically** when it closes, how many **winners**, a **claim timeout** (the winner must chat within N seconds or someone else is drawn) and a **reminder** interval.
4. **Entry rules**: a cost per ticket in loyalty points (refunded if you cancel), the most tickets per viewer (`!join 3` buys three when allowed), bonus tickets for subs and VIPs, who may enter, followers only, and whether previous winners of this giveaway are skipped.
5. **Messages**: every message has a sensible default; empty means silent. The preview fills in sample values. `{user}`, `{tickets}`, `{entrants}`, `{winner}`, `{remaining}` and friends are listed under `{x}`.

## Run it

- **Start** opens entries (you can also start one from chat with `!giveaway start <name>` once you switch that command on, or from any action with a *Giveaway: Control* step, for example when a reward is redeemed).
- The card shows a live countdown and the entrant and ticket counts.
- **Stop** closes entries (and draws, if automatic). **Draw** closes and draws; **Draw more** adds winners; **Reroll** replaces the last winner.
- **Cancel** refunds paid entries. **Reset** clears entries and the winner history.

Open a giveaway to see the entries (search, remove someone), the winners (mark a prize claimed by hand) and earlier runs. **Simulate an entry** sends the entry command through the chat simulator as a test user.

## Chat control

`!giveaway start|stop|draw|reroll|cancel [name]` is created switched off and limited to moderators. Without a name it uses the open giveaway (or, for start, the most likely one).

## Events

Each step raises a Custom Event (`giveaway.started`, `giveaway.entry`, `giveaway.closed`, `giveaway.winner`, `giveaway.claimed`, `giveaway.cancelled`) with the giveaway's name, prize, entrant counts and the user. Trigger actions or overlays on them with a **Custom Event** trigger.
