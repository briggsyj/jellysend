# Tasks: Match the source frame rate (spec 007)

- [x] T001 `FrameRate` (Normalize / Choose / ToFfmpeg / Format) in `Slopsoil.Streaming/Video/FrameRate.cs` + `FrameRateTests`
- [x] T002 `JellyfinClient.FrameRateOf` → `PlaybackStream.FrameRate` (direct and transcode paths) + tests
- [x] T003 `StreamRequest.SourceFps` from PlaybackInfo, else ffprobe (`MediaModule`) + tests
- [x] T004 `FfmpegPipeline` picks the output rate; diagnostics carry `SourceFps`
- [x] T005 `FfmpegCommand` passes exact rationals to `-r`
- [x] T006 `VideoPlayer` RTP increments from the exact frame clock + test
- [x] T007 Overlay / log line show `(src …)`
- [x] T008 End-to-end FFmpeg test at 24000/1001
- [x] T009 `contracts/configuration.md` STREAM_FPS row
