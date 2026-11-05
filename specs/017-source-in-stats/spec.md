# Feature Specification: Show the picture's source in the stats

**Feature Branch**: `017-source-in-stats` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "Can the video stats also be updated to include transcode information; whether direct playing to discord or
whether jellyfin is also transcoding."

## Requirements

- **FR-001** `PlaybackStream.TranscodeReason`: why Jellyfin transcodes instead of handing over the original file — direct
  stream turned off, the original file failed to start, burning in subtitles, not a plain file (disc image or remote
  source), external audio track, bot can't decode `<format>`, video codec not reported; null for a direct stream.
- **FR-002** `StreamRequest.JellyfinTranscodeReason` → diagnostics `Source`.
- **FR-003** The overlay gains a `SOURCE` line and the 10 s log line a `source` field:
  `original file → encoded once (bot)` or `Jellyfin transcode (<why>) → encoded twice (Jellyfin + bot)`. Discord always
  receives the bot's own encode, so "direct" means the bot reads the original file.
- **FR-004** The start log line names the reason too.
