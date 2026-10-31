# The action builder

Open any action on [Actions](#/actions) to edit it visually.

## Header

- **Name, group and queue.** Groups only organise the list; the queue decides what runs in order (see [Queues](queues.md)).
- **Enabled** turns the whole action on or off.
- **Always run** lets it run even while its queue is paused.
- **Sequential / Random / Concurrent**: run every step in order, run one top-level step chosen by weight, or let several runs of this action overlap.

## Triggers

**+ Add trigger** lists the events CherBot raises, grouped by source. Tick *Show all* to see Streamer.bot events CherBot never raises (kept so imported actions stay intact). Each trigger card has its own filters, for example a command, a reward, a minimum number of bits, or a scene name.

## Steps

Each step is a card with:

- an **on/off switch**, **move up/down**, **duplicate** and **delete**;
- the **drag handle** (⠿) to reorder steps within the same list;
- **collapse**, which shows a one-line summary instead of the form.

Fields are typed: switches, numbers, drop-downs, and **pickers** that list your actions, commands, timers, OBS scenes, sources, filters, rewards and active viewers. Text fields have an `{x}` button for variables.

### Logic steps

- **If/Else** has a *Then* lane and an *Else* lane. Choose a comparison (equals, contains, regex, less than…).
- **Switch** has one lane per case (each case lists the values it matches) plus a *Default* lane.
- **While** repeats its steps while the condition holds (with a safety limit).
- **Group** just groups steps; switch on **Random** to run one of them, chosen by each step's weight.

## Saving, testing, running

- **Save** checks the action first: required pickers set, referenced actions and commands still exist, regexes compile. You can save anyway.
- **Save & Test** runs the saved action with the most recent real event of its first trigger (or example data when there hasn't been one) and shows the trace and the final arguments. Test runs carry `%isTest%`.
- **Run** runs it now with no arguments.
- **Duplicate** copies the action (switched off). **Export** gives a Streamer.bot-compatible string.
- **Advanced JSON** edits the raw action. Switch back to *Builder* to continue visually.

Pin actions with the ☆ in the list to get one-click buttons on the [Dashboard](#/dashboard).
