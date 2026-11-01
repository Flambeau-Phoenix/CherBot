# Song Requests and optional history

## Song Requests

[Song Requests](#/songs) lets you choose YouTube, Apple Music and Spotify. Enable the workflow, select providers and save. Viewers use `!sr <track URL>` or `!songrequest <track URL>`. Free-text searches, playlists and albums are not supported.

Defaults: owner approval, 30 seconds per-user cooldown, three outstanding requests per viewer and 100 queued requests. The owner panel approves, rejects, reorders, removes and clears requests. `!songqueue` and `!songskip` are moderator commands. Requests cost no points.

Create a restricted player link and open it on the streaming PC. Press Start player. For OBS, use the URL as a browser source and Interact if autoplay needs a click. Keep YouTube's standard player visible. Only one controller can hold the playback lease. Connection loss stops playback before another controller can take over; a replacement link or an engine restart during playback may need 20 seconds for the previous player to stop.

YouTube uses its official player. Spotify and Apple Music requests open in their own apps and remain current until the owner completes or skips them. The player link cannot administer CherBot. Rotating it revokes older links.

## Data & History

[Data & History](#/history) starts with extended recording off. Existing points, engine configuration and short-term diagnostics still persist.

Enable extended statistics for per-user activity, stream observations, command use, interactions and points transactions. Enable the separate chat archive switch to retain searchable chat text. Filtered messages do not enter the chat archive. History starts at opt-in; old balances are not reconstructed into imaginary earnings or viewing time.

Detailed retention defaults to 90 days; chat text defaults to 30 days. Cleanup runs periodically. Disabling recording stops collection, and deletion is separate. Existing event/run diagnostics have their own retention, displayed on this page.

Search by user ID, stream, date, type or text. Export a user's retained history or delete it, including applicable structured diagnostic copies. Balances remain unchanged. Operational data and external backups are separate from extended history.

Loyalty's leaderboard always supports current balances. With extended history enabled, choose earned points this stream, seven days or thirty days. Loyalty accrual and stream-first bonuses count; transfers, admin adjustments and games do not. Coverage begins when tracking was enabled.
