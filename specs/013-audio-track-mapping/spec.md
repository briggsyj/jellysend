# Feature Specification: Map the direct stream's audio by the file's own tracks

**Feature Branch**: `013-audio-track-mapping` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: Thunderball (H.264 + AC-3) failed to start as a direct stream every time; Jellyfin's transcode (spec 012
retry) played it.

## Cause

Jellyfin's `MediaStreams` indices also count external files next to the media (subtitle or audio files), and recent
Jellyfin versions list them first. For Thunderball, two external subtitles took indices 0 and 1, so the video is
Jellyfin index 2 and the AC-3 audio index 3 (`AudioStreamIndex=3` in the transcode URL), while the file itself has
only streams 0 (video) and 1 (audio). Spec 006 passed Jellyfin's index to FFmpeg as `-map 0:3`, which matches no
stream: FFmpeg exits at once — "go-live stream failed to start".

## Requirements

- **FR-001** `MediaTrack` carries `IsExternal` and `EmbeddedOrdinal` (position among the file's own tracks of its kind).
- **FR-002** The direct stream maps audio with `-map 0:a:<EmbeddedOrdinal>` of the playing track (the chosen one, else
  Jellyfin's default, else the first).
- **FR-003** When the playing audio track is an external file, the direct stream is not used (Jellyfin's transcode).
- **FR-004** Voice ops 15 / 18 / 20 are named in the trace (MEDIA_SINK_WANTS, CLIENT_FLAGS, CLIENT_PLATFORM).

## Tasks

- [x] T001 `ParseTracks` ordinals / external flag; `JellyfinClient.PlayingAudio`; external audio → transcode
- [x] T002 `StreamRequest.AudioTrack` (ordinal) → `-map 0:a:N`
- [x] T003 Tests with the production layout (external subtitles first)
