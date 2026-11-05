# Feature Specification: Decoder-aware direct stream, quiet libdave

**Feature Branch**: `011-decoder-fallback` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "stream failed to start" — every play attempt of an HEVC file failed after spec 006.

## Findings (2026-11-05, in the production container)

- `ffmpeg -decoders` (Fedora `ffmpeg-free`) has no `hevc` and no `eac3` decoder, and decodes H.264 only through
  `libopenh264` (8-bit, progressive). Spec 006 hands the original file to this FFmpeg, so HEVC (and E-AC-3 audio)
  originals fail at once: "go-live stream failed to start".
- An H.264 + AC-3 file decodes and encodes fine with the bot's exact FFmpeg command, so its failure is
  elsewhere; the bot's own log lines were hidden because the log viewer stops at libdave's multi-kilobyte hex lines.

## Requirements

- **FR-001** At startup the host lists `ffmpeg -decoders` (`DecoderSet`). A direct stream whose video codec, or the audio
  codec of the track that would play, has no decoder — or H.264 above 8-bit / interlaced when only OpenH264 decodes
  H.264 — falls back to Jellyfin's transcode, with an info log naming the formats.
- **FR-002** If the decoder list is unavailable, behaviour is unchanged (direct stream).
- **FR-003** libdave's log output goes through the logger (`Slopsoil.Dave`): verbose/info at Debug (hidden by default),
  warnings and errors at their levels.

## Tasks

- [x] T001 `DecoderSet` (parse, H.264/OpenH264 rule, detect) + `DecoderSetTests`
- [x] T002 `JellyfinClient.CanDecode` / `FormatOf` / `DirectStreamFormat` + tests
- [x] T003 Host wiring; `DaveLogging` sink over `daveSetLogSinkCallback`
- [x] T004 README, configuration contract
- [x] T005 Make `Read_stalls_are_measured` robust to playout sleeps landing inside a read (failed once under load)
