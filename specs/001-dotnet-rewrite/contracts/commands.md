# Contract: Discord Commands (Jellyfin-first edition)

Prefix `!`. Command names are case-insensitive. Every command works two ways: as the shortcut `!<name>` and as
`!media <name>`, with the same aliases and role (spec 008, 2026-11-04). `!media` alone shows the help; `!media`
with an unknown word shows a hint (`!media <title>` is now `!play <title>`) and then the help. A command that needs
arguments shows its own help when run bare. Commands that take no arguments ignore extra words.

| Command | Role | Arguments | Behaviour |
|---|---|---|---|
| `!play <query>` | Friend | title + optional episode / season / start time, any order (below) | search → numbered picker (30 s) → series: season → episode → play |
| `!play` | Friend | — | resume a paused stream, else start the playlist |
| `!play <time>` | Friend | a time only | while something plays or is paused: jump there; otherwise a title search |
| `!pause` | Friend | — | pause in place |
| `!time` | Friend | — | `⏱ position / runtime (paused)` |
| `!time <time>` | Friend | `H:MM[:SS]`, `.` separators, `45m`, `90s`, `1h30m`, bare = minutes | in-place seek; past runtime rejected |
| `!skip` | Friend | — | next playlist item or stop if empty |
| `!stop` | Friend | — | stop the stream, stay in voice, keep the playlist |
| `!leave` | Friend | — | stop, clear the session, leave voice |
| `!join` | Friend | — | join the author's voice channel |
| `!queue <query>` | Friend | as `!play`, but a time stays part of the title (spec 002 FR-013) | append (≤10); plays immediately if idle |
| `!playlist` | Friend | — | now playing (pos/runtime/paused) + numbered list |
| `!order <# or title> <new #>` | Friend | required | move item |
| `!remove <# or title>` | Friend | required | remove item |
| `!subtitles` / `!subs` / `!sub` | Friend | optional `#`, language (`english`, `eng`, `en`), label word, or `off` | none or no match: picker (accepts numbers and names); burned in by Jellyfin |
| `!audio` | Friend | optional `#` or language | as `!subs` for the audio track |
| `!aspect-ratio` / `!aspect` / `!ar` | Friend | optional `#`, ratio (`16:9`, `2.39`, `21:9`) or name (`widescreen`, `scope`, `original`) | none or no match: picker: Original, 16:9, 4:3, 2.39:1, 1.85:1 |
| `!resolution` / `!res` | Friend | optional `#`, `360p`/`480p`/`540p`/`720p`/`1080p` (also `480`, `1280x720`, `hd`), `default` | per guild output height (16:9 box; bitrate scaled down by pixel count, never above the configured one); restarts the playing stream at its position; none or no match: picker (spec 010) |
| `!debug [on\|off]` | Friend | `on`, `off`, or none | on/off: diagnostics overlay (per guild); none: state + current diagnostics; other words: its help |
| `!help [command]` | Viewer | optional command name | all commands, or one command's help |
| `!ping` | Viewer | — | `pong!` |

**`!play` / `!queue` arguments** (spec 008), recognised by format, case-insensitive, anywhere in the text:
episode `s3e5` (leading zeros allowed), `3x5`, `season 3 episode 5`, `episode 5 season 3`; season only `s3` or
`season 3` (opens that season's episode picker; shows only series when a film has the same name); a start time with
a `:`/`.` separator or an `h`/`m`/`s` unit (not for `!queue`). `episode 5` alone and years are title words. Quoted
text (`"…"`, `'…'`, `“…”`) is always title. When the recognised formats find nothing, the whole text is searched as
a title (start from the beginning), unless the title was quoted.

Removed: `!media <title>`, `!media search`, `!media resume` (hint replies).

Roles: Admin (in `ALLOWED_USER_IDS`) ⊃ Friend (Discord friend) ⊃ Viewer (shares a guild) ⊃ None; the bot's
own account always passes; a failed check on a known command replies `_quack_ no.` (spec 010); unknown commands are silent.
