# Feature Specification: Episode ranges and lists in the command

**Feature Branch**: `018-episode-ranges` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "Could I currently do `!play <show> season 1 episode 1-3`" · cross-season ranges: yes · missing episodes: say
"episodes not found" and carry on.

## Requirements

- **FR-001** `!play` / `!queue` recognise (case-insensitive, leading zeros allowed): `s1e1-3`, `s01e01-e03`,
  `s1e1-s1e3`, `s1e8-s2e2` (across seasons), `1x1-3`, `1x8-2x2`, `season 1 episode(s) 1-3` / `1 to 3`,
  `episode(s) 1-3 (of) season 1`, `s1e1,3,5`, `season 1 episodes 1, 3 and 5`. A range that runs backwards is not a range.
- **FR-002** A range resolves through the show (series results only): its seasons and their episodes by number, in
  order. `!play` plays the first and queues the rest next; `!queue` appends them (the playlist cap applies).
- **FR-003** Episodes or seasons that do not exist are reported ("S01E11–E12 of **X** not found — carrying on with the
  rest") and skipped; when none exist, nothing plays.
- **FR-004** `season X episodes Y` (plural) and `episode Y of season X` also work for a single episode.
