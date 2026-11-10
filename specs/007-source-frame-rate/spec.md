# Feature Specification: Match the source frame rate

**Feature Branch**: `007-source-frame-rate` (implemented on `001-dotnet-rewrite`)
**Status**: Implemented
**Input**: "Could the env var be considered a limit and the software automatically matches the film framerate if lower?"

## Clarifications

- Sources faster than the ceiling are **evenly divided** (60 → 30, 59.94 → 29.97, 50 → 25), not plainly capped.
- `STREAM_FPS` default stays **30** (bandwidth limited).

## Requirements

- **FR-001** `STREAM_FPS` is a ceiling. The output frame rate is the source frame rate when it is at or below the ceiling.
- **FR-002** Source rate comes from Jellyfin PlaybackInfo (video `AverageFrameRate`, else `RealFrameRate`), else from ffprobe when the bot probes.
- **FR-003** Reported rates within 0.01 of an NTSC rate (n·1000/1001, n ∈ 24, 30, 48, 60, 120) or a whole number are snapped to the exact value; FFmpeg receives NTSC rates as exact rationals (`-r 24000/1001`).
- **FR-004** Above the ceiling, the output is source ÷ the smallest whole number that fits under the ceiling.
- **FR-005** Unknown or implausible source rates (< 1 or > 240 fps) fall back to `STREAM_FPS`.
- **FR-006** RTP video timestamps follow the exact frame clock at fractional rates (alternating 3754/3753 ticks at 23.976 fps), so they never drift.
- **FR-007** The debug overlay and log line show the source rate: `VIDEO 23.9/23.98 fps (src 23.98)`.

## Success criteria

- **SC-001** A 23.976 fps film streams at 24000/1001 fps with no duplicated frames (end-to-end FFmpeg test).
- **SC-002** 24 000 frames at 24000/1001 fps advance the RTP clock by exactly 90 090 000 ticks.
- **SC-003** A/V sync (spec 004) is unchanged: audio follows the same one-clock anchor at any frame rate.
