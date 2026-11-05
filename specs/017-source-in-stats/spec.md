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
- **FR-003** Stats per step (clarified: "Stats on each step would be a plus … so it can be clear where resources or timing
  are problematic"). The overlay and the 10 s log line show:
  - `JELLYFIN`: `original file — not transcoded`, or `Jellyfin transcode (<why>): <stats>` where the stats are Jellyfin's
    own `TranscodingInfo` from `GET /Sessions` (target codec, size, bitrate, transcode fps, % done, hw / software,
    audio), polled every 10 s; `stats not reported` when Jellyfin publishes none for this client.
  - `FFMPEG`: the bot's `<source codec> → <encoder> <box> <bitrate>` with FFmpeg's live `-progress` fps, speed and
    dup / drop counts (progress lines are parsed, not logged as warnings).
  - `STREAM`: position and `encoded once: bot` / `encoded twice: Jellyfin + bot`. Discord always receives the bot's
    own encode, so "direct" means the bot reads the original file.
- **FR-004** The start log line names the reason too.
