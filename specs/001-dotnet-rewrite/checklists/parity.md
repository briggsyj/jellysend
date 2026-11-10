# Parity checklist: Python → .NET edition (T040)

Sign-off for cut-over (T043). Run each row against **both** images with the same `.env` (Jellyfin with API key
and username/password; you are a Friend). Tick a column when the reply/behaviour matches the **Expected**
column exactly. Source: [contracts/commands.md](../contracts/commands.md) and the reply strings in
`cogs/jellyfin.py`, `cogs/voice.py` and `cogs/general.py`.

Legend: `<x>` = a value from your library; `…` = remainder of a multi-line reply.

## General and roles

| # | Input | Expected | Py | .NET |
|---|---|---|---|---|
| G1 | `!ping` (any guild member) | `pong!` | [ ] | [ ] |
| G2 | `!help` | General + Voice sections; Jellyfin section when configured | [ ] | [ ] |
| G3 | `!hello` as admin / as friend | `Hello, world!` / no reply | [ ] | [ ] |
| G4 | `!media x` from a non-friend guild member | no reply (silent role failure) | [ ] | [ ] |
| G5 | `!play x`, `!channels`, `!search x`, `!add-source a b`, `!sources`, `!delete-source`, `!hello` | Python: TV/IPTV behaviour · .NET: removed (no reply) | n/a | [ ] |
| G6 | `!join` `!leave` `!stop` `!help` `!ping` | .NET: shortcuts — same reply as `!media join/leave/stop/help/ping` | n/a | [ ] |

## Voice

| # | Input | Expected | Py | .NET |
|---|---|---|---|---|
| V1 | `!join` while not in voice | `you're not in a voice channel` | [ ] | [ ] |
| V2 | `!join` in voice | `joined!` and the account joins your channel | [ ] | [ ] |
| V3 | `!leave` while streaming | Python `left!` · .NET `⏹ stopped and left the voice channel`; go-live ends, account leaves | [ ] | [ ] |
| V4 | `!leave` when not connected | `not in a voice channel` | [ ] | [ ] |
| V5 | `!stop` while streaming / idle | Python `stopped` · .NET `⏹ stopped — still in voice (…)` / `nothing is playing`; stays in voice | [ ] | [ ] |
| V6 | Last human leaves the channel | Python: stops and leaves at once · .NET (spec 003): keeps streaming, stops and leaves after `EMPTY_CHANNEL_TIMEOUT_MINUTES` (20) unless someone returns; `0` = at once | [ ] | [ ] |

## Play (US1)

| # | Input | Expected | Py | .NET |
|---|---|---|---|---|
| P1 | `!media` / `!media help` | help text (+ SyncPlay line with a login) | [ ] | [ ] |
| P2 | `!media <movie>` (one match), in voice | `checking stream…` then picture + audio ≤ 15 s | [ ] | [ ] |
| P3 | `!media <query>` (several matches) | numbered list `Name (Year)`; reply `2` plays it | [ ] | [ ] |
| P4 | picker: no reply 30 s (.NET; Python 10 s) / reply `!ping` / reply `99` | `timed out — cancelled` / `cancelled` (and `pong!`) / `invalid selection — pick a number between 1 and N` | [ ] | [ ] |
| P5 | `!media <show>` | season list → episode list (`Series — SxxEyy — Name`) → plays | [ ] | [ ] |
| P6 | `!media <show> s01e02` | plays that episode directly | [ ] | [ ] |
| P7 | `!media zzzzqqq` | ``nothing found in Jellyfin for `zzzzqqq` `` | [ ] | [ ] |
| P8 | `!media <movie>` while not in voice | `you're not in a voice channel` | [ ] | [ ] |
| P9 | `!media search queue` | title search for "queue" (not the sub-command) | [ ] | [ ] |
| P10 | `!media <movie> 1:12:30` (spec 002, .NET only) | `⏩ starting at 1:12:30`; first frame at that point | n/a | [ ] |
| P11 | `!media <movie> 9:00:00` past the runtime | `9:00:00 is past the end (H:MM:SS)`; nothing starts | n/a | [ ] |
| P12 | `!media space voyage 2049` / `!media 3:33` | plays from the beginning (numbers stay part of the title) | [ ] | [ ] |

## Control (US2)

| # | Input | Expected | Py | .NET |
|---|---|---|---|---|
| C1 | `!media pause` / again | `⏸ paused at H:MM:SS` / `already paused — `!media play` to carry on` | [ ] | [ ] |
| C2 | `!media play` / again | `▶ resumed at H:MM:SS` / `already playing` | [ ] | [ ] |
| C3 | `!media pause` / `play` with nothing on (empty playlist) | `nothing is playing` / `nothing is paused — use `!media <title>` to play something` | [ ] | [ ] |
| C4 | `!media resume 1:00:00` (within runtime) | `⏩ jumping to 1:00:00`; screenshare never drops; A/V aligned | [ ] | [ ] |
| C5 | `!media resume 99:00:00` | rejected: past the runtime | [ ] | [ ] |
| C6 | `!media resume tomorrow` | title search for "resume tomorrow" | [ ] | [ ] |
| C7 | `!media aspect-ratio` → `3` | picker (5 options, `← current`), `📐 aspect ratio set to **4:3** — applying now` | [ ] | [ ] |
| C8 | `!media ar 9` | `pick a number between 1 and 5 — see `!media aspect-ratio`` | [ ] | [ ] |
| C9 | aspect remembered after `!leave` + new play | new item uses the chosen ratio | [ ] | [ ] |
| C10 | `!media stop` | Python: stops and leaves · .NET: stops, stays in voice, keeps the playlist (= `!stop`); use `!media leave` to leave | [ ] | [ ] |

## Playlist (US3)

| # | Input | Expected | Py | .NET |
|---|---|---|---|---|
| L1 | `!media queue <a>` while idle | starts playing `<a>` | [ ] | [ ] |
| L2 | `!media queue <b>`, `<c>` while playing | `➕ queued **<b>** at #1`, `… at #2` | [ ] | [ ] |
| L3 | 11th queue | `the playlist is full (10 items)` | [ ] | [ ] |
| L4 | `!media playlist` | `**Now playing:** … — pos / runtime` (+ ` (paused)`), `**Up next** (n/10):` numbered | [ ] | [ ] |
| L5 | `!media order 2 1` / `order zz 1` / `order 1` | `moved **…** to #1` / ``nothing in the playlist matches `zz` `` / usage | [ ] | [ ] |
| L6 | `!media remove 1` | `removed **…** from the playlist` | [ ] | [ ] |
| L7 | `!media skip` with / without next / idle | `⏭ up next: **…**` / `playlist is empty — stopped` / `nothing to skip` | [ ] | [ ] |
| L8 | item ends naturally | next item starts with `⏭ up next: **…**` | [ ] | [ ] |
| L9 | `!media playlist` idle and empty | `nothing playing and the playlist is empty` | [ ] | [ ] |

## SyncPlay (US4) — Jellyfin username/password configured

| # | Input | Expected | Py | .NET |
|---|---|---|---|---|
| S1 | play anything | group created; help shows the group name | [ ] | [ ] |
| S2 | join from Jellyfin web, pause there | `⏸ paused from Jellyfin at H:MM:SS`; stream pauses ≤ 2 s | [ ] | [ ] |
| S3 | unpause / seek there | `▶ resumed from Jellyfin` / `⏩ Jellyfin jumped to H:MM:SS` | [ ] | [ ] |
| S4 | `!media pause` / `resume 0:10` in Discord | Jellyfin web follows ≤ 2 s; no echo loop | [ ] | [ ] |
| S5 | switch item in Jellyfin web | `🔀 Jellyfin group switched to **…**` | [ ] | [ ] |
| S6 | stop in Jellyfin web | `⏹ stopped from Jellyfin` | [ ] | [ ] |

## Deployment, logs, stability (US5, US6)

| # | Check | Expected | Py | .NET |
|---|---|---|---|---|
| D1 | swap `SLOPSOIL_IMAGE` latest ↔ dotnet, same `.env` and volume | both start and work | [ ] | [ ] |
| D2 | `docker compose logs \| grep -Ei "api_key=[^*]\|token=[^*]\|password="` after P2–S6 | no matches | [ ] | [ ] |
| D3 | `!media debug on` / `off` / `debug` | overlay appears / disappears without dropping the screenshare / state + diagnostics reply | n/a | [ ] |
| D4 | 2-hour item with debug on (T049 soak) | SC-008–SC-010 hold | n/a | [ ] |

Signed off by: maintainer, 2026-11-04 — accepted from production use of the `:dotnet` image without a row-by-row run (spec 009). The rows are kept as a manual regression list.
