# Feature Specification: Start playback at a timestamp

**Feature Branch**: `002-media-start-timestamp`

**Created**: 2026-11-03

**Status**: Draft

**Input**: User description: "Extending the play media command `!media <name> <timestamp>` to allow the user to jump to a hours-minutes-optionalseconds timestamp immediately"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Start a movie part-way through (Priority: P1)

A friend of the bot wants to finish a film the group stopped watching last night. Instead of starting it with
`!media <title>` and then sending `!media resume 1:12:30` once it is on screen, they type
`!media inception 1:12:30` and the screenshare starts directly at 1 h 12 m 30 s.

**Why this priority**: It is the whole feature — one command instead of two, no wasted start-up of the
opening minutes, and viewers never see the beginning flash by.

**Independent Test**: In a voice channel, send `!media <a movie> 0:10:00`; the first picture viewers see is
from 10 minutes in, with sound in sync.

**Acceptance Scenarios**:

1. **Given** the user is in a voice channel and the search matches one movie, **When** they send
   `!media inception 1:12:30`, **Then** the bot replies that it is starting at `1:12:30` and the stream's
   first frame is at that position.
2. **Given** the same, **When** they send `!media inception 1.12` (dot separators, seconds omitted),
   **Then** playback starts at 1 h 12 m 0 s.
3. **Given** the search matches several items, **When** the user sends `!media alien 0:45` and picks one
   from the numbered list, **Then** the picked item starts at 0 h 45 m.
4. **Given** nothing is playing, **When** the user sends `!media inception` (no time), **Then** behaviour is
   exactly as today: playback starts from the beginning.

---

### User Story 2 - Start an episode part-way through (Priority: P2)

The user names a specific episode and a time: `!media the office s02e03 0:11`, or names a show, walks the
season and episode pickers, and the chosen episode starts at the given time.

**Why this priority**: Episodes are the other half of the library; same value, slightly more paths.

**Independent Test**: `!media <show> s01e02 0:05` starts that episode at 5 minutes; `!media <show> 0:05`
followed by season and episode picks starts the picked episode at 5 minutes.

**Acceptance Scenarios**:

1. **Given** the episode exists, **When** the user sends `!media the office s02e03 0:11`, **Then** S02E03
   starts at 0 h 11 m.
2. **Given** a show with several seasons, **When** the user sends `!media the office 0:11` and picks a
   season and an episode, **Then** the picked episode starts at 0 h 11 m.

---

### User Story 3 - Titles that end in numbers keep working (Priority: P1)

Users who play titles that end in, or consist of, numbers (*1917*, *Apollo 13*, *Blade Runner 2049*,
*Ocean's 11*, *2:22*) get exactly what they get today.

**Why this priority**: Existing commands must not break (constitution Principle I); a wrong guess here
would play the wrong title or the right title at a nonsense position.

**Independent Test**: The parity rows for these titles give the same result before and after the change.

**Acceptance Scenarios**:

1. **Given** a library with *Blade Runner 2049*, **When** the user sends `!media blade runner 2049`,
   **Then** it searches for "blade runner 2049" and starts from the beginning (a bare number is never a
   start time).
2. **Given** a library with the film *2:22*, **When** the user sends `!media 2:22`, **Then** it searches
   for "2:22" (a time with no title in front of it is the title).
3. **Given** a library where a title genuinely ends in a time-like word and searching without it finds
   nothing, **When** the user sends that full title, **Then** the bot searches for the full text and
   starts from the beginning.

---

### Edge Cases

- **Time at or past the end**: `!media inception 9:00:00` for a 2 h 28 m film → the bot replies
  `9:00:00 is past the end (2:28:00)` (the same message as `!media resume`) and does not start it.
- **Invalid time-looking text**: `!media inception 1:75` — not a valid time (minutes ≥ 60) so the whole
  text is treated as the title, as today.
- **Picker cancelled or timed out**: no playback, no time message, same replies as today.
- **Time `0:00`**: allowed; plays from the beginning.
- **Runtime unknown** (the server does not report one): the time is not validated against the end; if it is
  past the end the stream ends at once, as with `!media resume` today.
- **Something already playing**: the new item replaces it (as today) and starts at the given time.
- **Shared Jellyfin group watching (SyncPlay)**: the group is told the item starts at the given time, so
  people watching in Jellyfin apps start at the same point.
- **Aspect ratio / debug overlay settings** in effect for the server apply as usual.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `!media <title> <time>` MUST start the chosen item at `<time>` directly — the first frame
  viewers see is at that position; there is no visible playback from the beginning first.
- **FR-002**: `<time>` MUST accept the same forms as `!media resume`, hours first and seconds optional:
  `H:MM:SS`, `H.MM.SS`, `H:MM`, `H.MM`, and unit forms `1h30m`, `45m`, `90s`, `1h2m3s`; components after
  the first must be below 60.
- **FR-003**: Only the **last** word of the query MAY be taken as the time, and only when (a) there is a
  non-empty title before it, and (b) it contains a `:` or `.` separator or an `h`/`m`/`s` unit. A bare
  number (`2049`, `13`) is never a start time in this position (unlike `!media resume 45`, where it means
  minutes).
- **FR-004**: If the title without the time finds nothing, the bot MUST search the full text (time word
  included) before reporting "nothing found", and in that case play from the beginning.
- **FR-005**: The time MUST work with every way of choosing an item: one match, the numbered result picker,
  a direct episode code (`sXXeYY`), and the season → episode pickers.
- **FR-006**: When the item's runtime is known and the time is at or past it, the bot MUST reply
  `<time> is past the end (<runtime>)` and MUST NOT start the item.
- **FR-007**: When a start time is used, the bot MUST say so in its reply (e.g. `⏩ starting at 1:12:30`),
  with the time normalised to `H:MM:SS`.
- **FR-008**: `!media search <title> <time>` MUST behave like `!media <title> <time>`.
- **FR-009**: Commands without a trailing time MUST behave exactly as before, including replies.
- **FR-010**: When a shared Jellyfin group (SyncPlay) is active, the group MUST be given the start time as
  the item's starting position.
- **FR-011**: The `!media help` text MUST document the new form (e.g. `!media <movie> 1:23:45` — start at a
  time).
- **FR-012**: The feature MUST be available in [NEEDS CLARIFICATION: which edition(s) — the current Python
  bot, the .NET edition being built, or both?].
- **FR-013**: `!media queue <title> <time>` [NEEDS CLARIFICATION: should a queued item remember a start
  time and begin there when the playlist reaches it, or is the time only for immediate playback?].

### Key Entities

- **Play request**: the title query, an optional episode code, and an optional start position (seconds).
- **Start position**: a non-negative time within the item; shown to users as `H:MM:SS`.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Starting at a time takes one command; the first picture appears no later than starting the
  same item from the beginning plus 2 seconds.
- **SC-002**: The first frame shown is within 2 seconds of the requested time, and audio is in sync with it
  from the first second.
- **SC-003**: Every existing `!media` parity row (titles without a trailing time, including titles ending in
  numbers) gives the same result as before — 100 %.
- **SC-004**: All time forms accepted by `!media resume` (except bare minutes) are accepted here — verified
  against the same list of examples.

## Assumptions

- Users with Friend access (who can use `!media` today) can use this; no new permission.
- "Immediately" means the stream starts at the time — not that it starts at 0 and then seeks.
- Reusing `!media resume`'s time rules (and its past-the-end message) keeps one mental model for users.
- The trade-off in FR-003 (bare numbers are titles) is accepted: `!media inception 72` will search for
  "inception 72"; users write `1:12` or `72m` instead.
- Applies to Jellyfin library items only; there is no live/TV source in the Jellyfin-first edition.
