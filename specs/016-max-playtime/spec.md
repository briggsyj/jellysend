# Feature Specification: Maximum playtime; empty-channel rule confirmed

**Feature Branch**: `016-max-playtime` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "Update the timeout so that it doesn't check if specifically the user that invoked it is still in the
channel, but if there is 2 or more users in the channel at all … Put a maximum playtime on a stream; it should stop at
the 12 hour mark."

## Findings

- The empty-channel countdown (spec 003) already counts every user in the bot's channel other than the bot itself, not
  the user who started the stream. A regression test now pins this (the starter leaves, a viewer stays → no countdown).

## Requirements

- **FR-001** `MAX_STREAM_HOURS` (default 12, 0–168, 0 = no limit).
- **FR-002** The count starts when a user starts playback (`!play <title>`, bare `!play` on the playlist, `!queue` /
  `!skip` when idle) and keeps running through auto-advance, skips, track / resolution restarts and reconnects.
- **FR-003** At the limit the stream stops like `!stop` (voice and playlist stay) with
  "⏹ stopped after 12 hours of streaming — `!play` to start again (the playlist is kept)". A new user start restarts
  the count; leaving voice cancels it.
