# Chat → OBS scenes

Three ways to let chat (usually moderators) change scenes.

## Scene shortcuts

[Quick Setup → Scene shortcuts](#/quicksetup) creates one command per scene, for example `!brb` → *BRB* and `!game` → *Gameplay*. Add rows, choose who may use each one and a cooldown, then **Create shortcuts**. Each shortcut is an ordinary command plus an action with a *Set Active Scene* step.

## `!scene <name>` and `!scenes`

Two built-in commands, created switched off:

- `!scene brb` switches to the scene whose name matches **exactly**, otherwise the only scene that **starts with** the text, otherwise the only scene that **contains** it. If several match, CherBot lists them instead of guessing.
- `!scenes` lists the scenes.

Switch them on and choose the **allowed scenes** on [Quick Setup](#/quicksetup) (no ticks = every scene). `!scene` is limited to moderators by default.

## From the Dashboard

The [Dashboard](#/dashboard) has a button per scene (the current one is highlighted) and switches for the sources in the current scene.

## In your own actions

Use **Set Active Scene**, **Set Source Visibility State**, **Set Source Filter State** and the other OBS steps. Their pickers list your live scenes, sources and filters while OBS is connected. If you run several OBS instances, choose the connection; otherwise leave *first connected*.
