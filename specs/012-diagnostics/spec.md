# Feature Specification: Start diagnostics and transcode retry

**Feature Branch**: `012-diagnostics` (implemented on `claude/busy-meitner-7979sz`, with spec 011)
**Status**: Implemented
**Input**: "enhance container logging so it provides all the info you could need to debug this and future issues" ·
Thunderball (H.264 + AC-3) failed to start every time after specs 007–010 although it worked 5 hours earlier.

## Investigation (2026-11-05)

- In the production container, the bot's exact FFmpeg command for Thunderball (VA-API, `-ss 3600`, `-r 24000/1001`,
  `-map 0:1`) runs cleanly; reads, decodes and the VA-API encoder all work.
- A local end-to-end run of `FfmpegPipeline` over HTTP with the same request (live, 23.976 fps source, AC-3 track 1,
  start 60 s) streams video and audio normally.
- Since the working image (`f4aaf01`) the start path only gained the source frame rate (spec 007) and the output
  height (spec 010, unset); Discord, DAVE and the engine are unchanged. The base image (`fedora:44`) is unpinned.
- The bot's own log lines were never seen: the log viewer stops at libdave's hex dumps (fixed by spec 011).

## Requirements

- **FR-001** Voice sessions are labelled `[voice <guild>]` / `[stream <rtc_server_id>]` in logs; every voice-gateway op in
  and out (except heartbeats) is traced with a safe summary (ids, modes, versions — never keys or tokens); close codes
  are logged with their reason text and a description.
- **FR-002** The bot's own VOICE_STATE_UPDATE, VOICE_SERVER_UPDATE and STREAM_CREATE / SERVER_UPDATE / UPDATE / DELETE
  (with `reason`) are logged without tokens.
- **FR-003** Each start logs mode, codecs, audio track, source fps, start, tracks and height; the pipeline logs when the
  audio FIFO opened, when the first frame was sent, and — on failure — whether FFmpeg exited or never opened the FIFO.
- **FR-004** Each stream end logs reason, error, position, whether a frame was sent and how the screenshare link closed.
- **FR-005** A direct stream that fails before its first frame is retried once through Jellyfin's transcode at the same
  position; the item then always uses the transcode until the bot restarts. Transcoded streams and failures after the
  first frame are not retried.

## Tasks

- [x] T001 Voice op names, close-code descriptions, safe payload summary; labelled session logs + trace sink
- [x] T002 `GatewayVoiceTrace` for self voice / stream events
- [x] T003 Start / FIFO / first-frame / end diagnostics (`PipelineEnd.FirstFrameSent`, `IGoLiveLink.CloseCode`)
- [x] T004 Transcode retry (`allowDirect`, remembered per item) + `MediaRetryTests`
- [x] T005 README "Logs"
