# Feature Specification: Pick several episodes at once; 8 s buffer

**Feature Branch**: `014-multi-episode-pick` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "when the user is selecting tv show episodes presented providing a number, allow them to provide comma
separated numbers to select multiple episodes, in order, to be queued e.g `2,5,8`" · "Increase to 8 second buffer"

## Clarifications (2026-11-05)

- Ranges are allowed too: `3-6`, `1,3-5,9` (duplicates dropped, order kept).
- When the picks do not all fit the playlist (10), queue as many as fit and say how many were not queued.
- `!play <show>`: the first pick plays now, the rest go to the front of the playlist (play next, in order).
  `!queue <show>`: all picks are appended in order (when idle, the first plays now). (Default chosen; the user was
  unsure about the question.)
- `STREAM_BUFFER_SECONDS` default 5 → 8. An explicit value in `.env` still wins.

## Requirements

- **FR-001** The episode picker accepts a single number or a list/range when reached from `!play` / `!queue`.
- **FR-002** `NumberPrompt.ParseList` / `AskManyAsync`; invalid lists cancel with a hint.
- **FR-003** `Playlist.InsertNext` (front, in order, capped); replies list the queued episodes and the overflow.
- **FR-004** `DefaultStreamBufferSeconds = 8`.
