# Contract: Discord Commands (Jellyfin-first edition)

Prefix `!`. Replies and behaviour MUST match the Python bot (`cogs/jellyfin.py`, `cogs/voice.py`,
`cogs/general.py`) for the same input — the parity checklist is generated from this table.

| Command | Role | Arguments | Behaviour |
|---|---|---|---|
| `!media <query>` / `!media search <query>` | Friend | title, optional `sXXeYY` | search → numbered picker (10 s) → series: season → episode → play |
| `!media` / `!media help [extra]` | Friend | — | HELP_TEXT (+ SyncPlay line when a login is configured); extra words → title search |
| `!media pause` / `play` | Friend | — (extra words → search) | pause/continue in place; `play` with nothing on starts the playlist |
| `!media resume <time>` | Friend | `H:MM[:SS]`, `.` separators, `45m`, `90s`, `1h30m`, bare = minutes | in-place seek; past runtime rejected; no digits → title search |
| `!media stop` | Friend | — | stop, clear playlist, leave voice |
| `!media skip` | Friend | — | next playlist item or stop if empty |
| `!media queue <query>` | Friend | as `!media` | append (≤10); plays immediately if idle |
| `!media order <# or title> <pos>` | Friend | | move item |
| `!media remove <# or title>` | Friend | | remove item |
| `!media playlist` | Friend | | now playing (pos/runtime/paused) + numbered list |
| `!media aspect-ratio` / `aspect` / `ar` | Friend | optional 1–5 | picker: Original, 16:9, 4:3, 2.39:1, 1.85:1; applies live to Jellyfin streams only |
| `!join` / `!leave` / `!stop` | Friend | | voice controls; `!leave`/`!stop` also end the go-live |
| `!help`, `!ping` | Viewer | | as Python `cogs/general.py` |
| `!play`, `!channels`, `!search`, `!add-source`, `!sources`, `!delete-source` | — | | reply: not available in the .NET edition |

Roles: Admin (in `ALLOWED_USER_IDS`) ⊃ Friend (Discord friend) ⊃ Viewer (shares a guild) ⊃ None; the bot's
own account always passes; failed checks are silent.
