# Feature Specification: Runtime resolution and the denied reply

**Feature Branch**: `010-runtime-resolution` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "a command for letting me set the transcode resolution at runtime, ideally a shorthand for the vertical
resolution such as 360p, 480p, 540p, 720p, 1080p" · "include a reply if the user the command came from isn't on
the approved list. Make it say: `_quack_ no.`"

## Decisions

- `!resolution` with alias `!res` (and `!media resolution`); Friend role like the other playback commands.
- Choices: `default` (the configured `STREAM_RESOLUTION` / `STREAM_QUALITY`), `360p`, `480p`, `540p`, `720p`, `1080p`.
  Also accepted: `480`, `1280x720`, `1920:1080`, `sd`, `hd`, `full hd`, or the picker number. Bare or unknown → picker.
- The output box is 16:9 for the height (640:360, 854:480, 960:540, 1280:720, 1920:1080); the source is fitted inside it.
- The bitrate scales with the pixel count when the box is smaller than the configured one, with a 300k floor, and
  never goes above the configured bitrate (bandwidth-limited deployments).
- Per guild and remembered (like the aspect ratio). While something plays, the stream restarts at its position.
- "Approved list" = the role a command needs. Any known command whose role check fails replies `_quack_ no.`;
  unknown `!words` stay silent.

## Requirements

- **FR-001** `StreamRequest.OutputHeight`; `FfmpegPipeline` rebuilds the encoder configs with `OutputSize.For`.
- **FR-002** `!resolution` / `!res` set, clear and pick the per-guild height; restart the playing item at its position.
- **FR-003** Diagnostics (overlay, log line) show the effective resolution and bitrate.
- **FR-004** `CommandRouter` replies `CommandRouter.DeniedReply` (`_quack_ no.`) when the global or command role check fails.

## Tasks

- [x] T001 `OutputSize` (16:9 box, bitrate scaling, kbps parsing) + `OutputSizeTests`
- [x] T002 `EncoderConfig.For` rebuild + `StreamRequest.OutputHeight` in `FfmpegPipeline` + end-to-end FFmpeg test
- [x] T003 `!resolution` / `!res` command, picker and names + `MediaResolutionTests`
- [x] T004 `_quack_ no.` on failed role checks + router tests
- [x] T005 README, `contracts/commands.md`, help text
