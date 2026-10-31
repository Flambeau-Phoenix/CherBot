# Variables and templates

## Arguments: `%name%`

While an action runs it carries **arguments**. The trigger provides most of them (`%user%`, `%rawInput%`, `%bits%`, `%rewardName%`…), steps add more (*Get User Info for Target* sets `%targetUser%`, *Get Random Number* sets `%randomNumber%`), and you can set your own with **Set Argument**.

Put `%name%` in any text field and CherBot replaces it. Unknown names are left as typed, which makes typos easy to spot.

Formats work too: `%count:N0%` adds thousands separators, `%actionQueuedAt:HH:mm%` formats a time.

## Globals: `~name~`

**Globals** keep a value between runs, for example a death counter. Persisted globals survive restarts; temporary ones live until CherBot stops.

- Read one anywhere with `~deaths~`, or with a **Global (Get)** step into an argument.
- Change one with **Global (Set)**: set a value, or increment / decrement it.
- **User globals** belong to one viewer (for example their personal wins). Use *Global (Set)* with "Write to: the user who triggered it".

Manage all of them on [Variables](#/variables).

## Placeholders: `{name}`

Custom commands, chat alerts, giveaway messages and module messages also understand friendly placeholders such as `{user}`, `{touser}`, `{count}`, `{random:1-100}` or `{pick:a|b|c}`. Each editor's `{x}` button lists the ones available there. `%args%` and `~globals~` work in those messages as well.

## Finding the right variable

- The `{x}` button in the builder groups variables by where they come from: the trigger (with values from the latest real event when there has been one), steps earlier in the action, always-available ones, globals and format examples.
- [Variables → Variable reference](#/variables) and [All triggers](ref-events.md) list everything.
