# Feature Specification: Buffered playout with one A/V clock

**Feature Branch**: `004-buffered-av-sync` (implemented on `001-dotnet-rewrite`, .NET edition only)

**Created**: 2026-11-04 · **Status**: Ready

**Input**: "I'm getting video and audio desync … It's OK if the video stream on Discord is behind what the device
is transcoding … an approach like Jellyfin where the device transcodes ahead of what is streamed to Discord."

## Clarifications

### Session 2026-11-04

- Edition: .NET only (all new development).
- Buffer: up to 5 s accepted; quality matters more than start latency. Default 5 s, configurable.
- Symptom: desync on the `:dotnet` image. Debug overlay from the user's run (1:00:09 into a 2:10:23 h264 item,
  `h264_vaapi` 960x540 2000k): `VIDEO 30.1/30 fps late 0 (max 0 ms) read-stall 7 ms`, `A/V drift +90 ms silence 0
  skip 0`, `NETWORK drops 0 errors 0`. Video pacing is healthy; audio runs a **constant ~90 ms ahead**, inside the
  follower's 120 ms dead band, so it is never corrected. The fix must remove the steady offset (FR-001, FR-004),
  not only absorb stalls.

## Problem

Audio and video are paced by two independent wall clocks and an audio "follower" nudges audio towards the video
position. FFmpeg writes video to stdout and audio to a ~0.34 s FIFO and is read at the playout rate, so any
transcode/network hiccup stalls one side first; the follower then corrects it (audible) or not (desync). FFmpeg
also starts each raw output at its own first sample, so a source whose audio starts later than its video is
offset by that amount from the first frame.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Lips match the words for the whole film (Priority: P1)

**Acceptance Scenarios**:

1. **Given** any item, **When** it plays, **Then** audio and video stay within ±40 ms of each other from the first
   frame to the end, including after pause/resume, seek, aspect-ratio and debug-overlay changes.
2. **Given** the source's audio track starts later (or earlier) than its video, **Then** the stream is still in
   sync (the offset is corrected at the source, not by the follower).
3. **Given** the transcode stalls for less than the buffer, **Then** viewers see and hear no stall and no
   correction.

### User Story 2 - Hiccups are absorbed, not heard (Priority: P1)

**Acceptance Scenarios**:

1. **Given** a transcode stall longer than the buffer, **Then** both audio and video pause together and resume
   together, still in sync (no rubber-banding, no catch-up burst of audio).

### Edge Cases

- Start/seek latency grows by the time needed to fill the pre-roll (bounded by the buffer length; shorter when
  the server transcodes faster than real time).
- End of item: the buffer drains to the last frame before the stream ends naturally.
- Source without audio: generated silence is buffered like real audio.
- `STREAM_BUFFER_SECONDS=0`: no read-ahead (lowest latency); the single clock still applies.

## Requirements *(mandatory)*

- **FR-001**: FFmpeg's video and audio output MUST share one media timeline starting at the segment's start
  (audio padded/trimmed to the video's origin).
- **FR-002**: Video frames and audio frames MUST be read ahead of playout into bounded in-memory buffers of up to
  `STREAM_BUFFER_SECONDS` (default 5, 0–30).
- **FR-003**: Playout of a segment MUST start once the pre-roll (the buffer, or less at end of input) is filled.
- **FR-004**: One playout clock MUST schedule both streams: video frame *n* at `t0 + n/fps`, audio frame *k* at
  `t0 + k·20 ms + STREAM_AV_SYNC_MS`; pause shifts `t0` for both.
- **FR-005**: An audio underrun MUST keep the schedule (silence now, the late frame dropped when it arrives) so
  audio never falls behind the clock; a video underrun waits and resumes on the same schedule.
- **FR-006**: The follower's drift corrections MUST NOT run in this mode (the clock makes them unnecessary); drift
  and buffer fill MUST still be reported in diagnostics (overlay + 10 s log).
- **FR-007**: Seek / aspect / debug restarts MUST flush both buffers and re-anchor both streams on the new
  segment's `t0`.

## Success Criteria *(mandatory)*

- **SC-001**: An automated end-to-end test with a flash+beep sync marker (including a source whose audio starts
  0.5 s late) measures the beep's audio packet within ±1 audio frame (20 ms) of the flash's video frame on the
  wire, at start, after a seek and after a pause.
- **SC-002**: A 2-hour soak (T049 harness) shows no audio corrections and |drift| ≤ 40 ms.
- **SC-003**: An injected 2 s read stall with a 5 s buffer produces no late frames and no audio silence.
