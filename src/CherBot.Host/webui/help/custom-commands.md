# Custom commands

Custom commands are quick text commands, like a chatbot's `!discord` or `!socials`. Make them on [Commands](#/commands).

## Responses

- Write one or more **variants**; CherBot picks one at random each time.
- A variant with several lines sends several messages, with the **delay between lines** you choose.
- The counter under each variant shows the longest line after placeholders are filled (Twitch allows 500 characters).

## Placeholders

| Placeholder | Becomes |
| --- | --- |
| `{user}` | Who used the command |
| `{touser}` | The first word after the command without `@`, or the user when there isn't one |
| `{args}` | Everything after the command |
| `{1}` … `{9}` | The first, second… word after the command |
| `{count}` | How many times the command has been used (counts up each time) |
| `{random:1-100}` | A random number in the range |
| `{pick:a\|b\|c}` | One of the choices |
| `{uptime}` | How long you've been live |
| `{game}` / `{title}` | Current category and title |
| `{followage}` | How long the user has followed |
| `{points}` / `{currency}` | Their loyalty points and the points name |
| `{channel}` | Your channel name |

`%arguments%` and `~globals~` work too.

## Options

Send as chat, a reply or an announcement; who may use it (or only certain user groups); cooldowns; a **cost** in loyalty points (refused politely when the viewer can't afford it); and **only while live**.

## Testing

**Test** asks the engine to render the response with sample data and sends nothing. **Try in chat** sends the command through the chat simulator as a moderator called *tester*.

## Going further

Every custom command is a real command plus an action in the *Custom Commands* group. **Open in builder** lets you add sounds, OBS changes or anything else. Once you save it from the builder it is marked **builder**: the form still edits the response, cooldowns and roles, but never overwrites your action.

**Bulk import** accepts lines like `!discord Join us at discord.gg/example`.
