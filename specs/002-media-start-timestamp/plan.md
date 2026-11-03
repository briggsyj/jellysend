# Implementation Plan: Start playback at a timestamp

**Branch**: `001-dotnet-rewrite` (feature lands in the .NET edition) | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

## Summary

`!media <title> <time>` starts the chosen item at `<time>`. The trailing time is split off in the command layer,
the item is resolved with the existing search/pickers, and the start offset is passed to the existing stream start
(which already supports an FFmpeg `-ss` start for resume/seek). No new services or settings.

## Technical Context

.NET 10, `dotnet/src/Slopsoil.Commands/Media` (MediaModule, MediaText). Tests in `dotnet/tests/Slopsoil.Tests/Commands`.
Reuses `MediaText.ParseTimestamp` (the `!media resume` rules) and `StreamRequest.StartOffset`.

## Constitution Check

| Principle | Status |
|---|---|
| I. Public API compatibility | Pass — additive; titles without a trailing time behave as before (FR-003/FR-004 keep number-ending titles working). |
| II. Service boundaries | Pass — Commands layer only. |
| IV. Test suites | Pass — unit tests for the splitter and module paths; `dotnet/ci/test.sh` gate unchanged. |

## Design

- `MediaText.SplitTrailingTime(query)` → `(title, seconds?)`: last whitespace-separated word, only when a non-empty
  title precedes it, the word contains `:`/`.` or an `h`/`m`/`s` unit, and `ParseTimestamp` accepts it (FR-002/FR-003).
- `MediaModule.MediaQueryAsync`: when a time was split off, resolve the title with a fall-back to the full text
  (FR-004, then no start time); check the runtime (FR-006); reply `⏩ starting at H:MM:SS` (FR-007); start with the offset.
- `!media search` shares the path (FR-008); `!media queue` does not split (FR-013).
- SyncPlay already queues the item from the first shown frame's position, which is the start offset (FR-010).
- Help text gains one line (FR-011).
