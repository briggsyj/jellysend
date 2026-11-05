# Feature Specification: Subtitle and audio track selection

**Feature Branch**: `005-subtitle-audio-tracks` (implemented on `001-dotnet-rewrite`, .NET edition only)

**Created**: 2026-11-04 · **Status**: Implemented

**Input**: "add a command to select a subtitle track or to turn on and off subtitles. same with audio track"

## Clarifications

### Session 2026-11-04

- Remember: by language, per server — later items auto-pick the same subtitle/audio language when they have
  it (default/off otherwise); cleared on `!media leave`.
- Rendering: Jellyfin burns subtitles in while transcoding (all formats incl. PGS/VobSub/ASS).
- Names: `!media subtitles` (aliases `subs`, `sub`) and `!media audio`.

## Requirements

- **FR-001**: `!media subtitles` lists `Off` plus the item's subtitle tracks (language/title, forced marked, current
  marked) as a 30 s numbered picker; `!media subs <n>` picks directly; `!media subs off` turns them off.
- **FR-002**: `!media audio` lists the item's audio tracks the same way (no `Off`); `!media audio <n>` picks directly.
- **FR-003**: A change restarts the current item at its current position with the new tracks (screenshare stays up).
- **FR-004**: Selected subtitles are burned in by Jellyfin (PlaybackInfo `SubtitleStreamIndex` + SubtitleProfiles with
  method Encode); the chosen audio uses `AudioStreamIndex`; the stream URL is rewritten to match.
- **FR-005**: The choice is remembered per guild by language and applied to later items (non-forced subtitle track
  preferred); no match → default audio / subtitles off. Cleared on leave.
- **FR-006**: Nothing playing → `nothing is playing`; no tracks → `this item has no subtitles` / `this item has no
  audio tracks to choose from`; words that aren't a number/`off` are a title search (`!media audio sample reel`).

## Success Criteria

- **SC-001**: Switching subtitles/audio resumes within the buffer time at the same position, in sync.
- **SC-002**: A remembered language is applied to the next playlist item without user action.
