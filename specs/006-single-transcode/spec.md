# Feature Specification: Transcode once (direct stream of the original)

**Feature Branch**: `006-single-transcode` (implemented on `001-dotnet-rewrite`, .NET edition only)

**Created**: 2026-11-04 · **Status**: Implemented

**Input**: "does this result in both Jellyfin and this tool transcoding the video; is there a way to only transcode
once?" → option A: Jellyfin sends the original, the bot encodes once.

## Problem

Jellyfin transcoded every item to H.264 HLS and the bot re-encoded that to Discord's H.264: two generations of loss,
double encode load, extra latency.

## Requirements

- **FR-001**: With no subtitles selected, the bot MUST play Jellyfin's static stream of the original file
  (`/Videos/{id}/stream?static=true&MediaSourceId=…`) and transcode it once with its own encoder.
- **FR-002**: With a subtitle track selected, Jellyfin's transcode with burn-in (spec 005) MUST be used.
- **FR-003**: Sources that are not plain local files (Blu-ray/DVD folders, ISOs, remote/strm) MUST fall back to
  Jellyfin's transcode.
- **FR-004**: In direct mode the bot MUST map the audio track itself: the chosen track (spec 005), else Jellyfin's
  default track (which need not be the file's first audio stream). Jellyfin's MediaStream index is used as the
  FFmpeg input stream index (embedded streams).
- **FR-005**: Any codec the bot's FFmpeg can decode is accepted in direct mode (the transcode-path codec list does
  not apply).
- **FR-006**: `JELLYFIN_DIRECT_STREAM` (default `true`) turns it off (always Jellyfin transcode), e.g. when the bot
  reaches Jellyfin over a slow link.

## Assumptions

- The bot reaches Jellyfin over a LAN: the original bitrate (up to ~80 Mbps for 4K remuxes) must flow to the bot.
- The bot's machine can decode the originals (4K HEVC/AV1 may need its GPU's decoder or enough CPU).
