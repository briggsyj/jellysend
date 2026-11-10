# Tasks: Buffered playout with one A/V clock

- [X] T001 FFmpeg command: one media timeline for both outputs (FR-001) + `STREAM_BUFFER_SECONDS` option (FR-002)
- [X] T002 VideoPlayer read-ahead: reader thread → bounded frame buffer → pre-roll → paced playout; publishes the segment's playout origin `t0` (pause-adjusted) through `IMediaClock` (FR-002–FR-004, FR-007)
- [X] T003 AudioSender read-ahead + clock-locked scheduling on `t0`, underrun handling, no follower corrections in this mode (FR-004–FR-007)
- [X] T004 Diagnostics: buffer fill (video s / audio s) in snapshot, overlay and log line; drift measured against the clock (FR-006)
- [X] T005 End-to-end sync tests with real FFmpeg (SC-001, SC-003) and docs (configuration contract, README)
