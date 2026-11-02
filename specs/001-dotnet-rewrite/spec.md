# Feature Specification: .NET Rewrite — Jellyfin-First Edition

**Feature Branch**: `001-dotnet-rewrite`

**Created**: 2026-11-02

**Status**: Draft

**Input**: User description: "Plan a .NET rewrite of the application. Jellyfin-first subset; build the Discord
gateway/voice/go-live/E2EE layer in-house; .NET 10 LTS, developed side-by-side with the Python bot, which
remains the shipping application until the rewrite reaches parity for this subset."

## User Scenarios & Testing *(mandatory)*

The people involved are the same as today:

- **Operator** — deploys and configures the bot (Docker image + `.env`), owns the Discord account it runs on.
- **Friend** — a Discord user permitted to control playback (`!media …`).
- **Viewer** — anyone in the voice channel watching the screenshare.
- **Jellyfin watcher** — someone watching the same item in a Jellyfin app who joins the bot's SyncPlay group.

The rewrite is judged by one question: *can an operator swap the Python bot for the new one, for Jellyfin
viewing, without anyone in the voice channel noticing a regression?*

### User Story 1 - Watch a Jellyfin title together in Discord (Priority: P1)

A friend in a voice channel types `!media <title>` (or `!media <show> s01e02`). The bot finds it in
Jellyfin, joins the channel, and starts a screenshare with picture and sound that everyone in the channel
can watch. Series without an episode code walk the user through season → episode numbered lists.

**Why this priority**: This is the product. Without it nothing else in the rewrite has value, and it
exercises the hardest, highest-risk part (logging in as a user account, joining voice, publishing an
end-to-end-encrypted screenshare).

**Independent Test**: With only this story built, an operator can run the new bot against a real Jellyfin
server and Discord server, request a movie and an episode, and watch both with synced audio in Discord.

**Acceptance Scenarios**:

1. **Given** the bot is configured and a friend is in a voice channel, **When** they type `!media inception`
   and the search has one result, **Then** within 15 seconds the bot is in their channel and a screenshare
   of the film with audio is visible to every viewer.
2. **Given** a search returns several results, **When** the friend replies with a listed number,
   **Then** that item plays; a non-number, an out-of-range number or 10 s of silence cancels with a message.
3. **Given** `!media breaking bad` (a series), **When** the friend picks a season then an episode,
   **Then** that episode plays; `!media breaking bad s03e05` plays it directly.
4. **Given** the voice channel uses Discord's end-to-end encryption for voice/video, **When** an item plays,
   **Then** viewers see and hear it normally.
5. **Given** the item has no audio track, **When** it plays, **Then** video plays with silence (no failure).
6. **Given** the user is not in a voice channel, **When** they request media, **Then** they are told so and
   nothing starts.

---

### User Story 2 - Control playback (Priority: P2)

While something plays, friends can pause, continue, jump to a time, change the picture shape, stop, and
make the bot leave — with the same commands and wording as today.

**Why this priority**: Watching together is unusable without pause/seek; these are the next most-used
commands and they stress the streaming pipeline's restart and A/V-sync behaviour.

**Independent Test**: Play an item, then exercise every control and confirm the screenshare stays up
(viewers never have to re-join it) and audio stays in sync.

**Acceptance Scenarios**:

1. **Given** an item is playing, **When** `!media pause`, **Then** picture freezes and audio stops within 1 s;
   `!media play` continues from the same point.
2. **Given** an item is playing, **When** `!media resume 1:23:45` (also `1.23.45`, `1:23` = h:mm, `45m`,
   `90s`, bare number = minutes), **Then** playback continues from that time in the same screenshare;
   a time past the end is rejected with the item's length.
3. **Given** an item is playing, **When** `!media aspect-ratio` and the friend picks a number,
   **Then** the picture shape changes immediately (Original, 16:9, 4:3, 2.39:1, 1.85:1) and the choice is
   remembered for that Discord server.
4. **Given** an item is playing, **When** `!media stop` (or `!leave`), **Then** the screenshare ends, the
   playlist is cleared and the bot leaves the voice channel.
5. **Given** the last human leaves the voice channel, **Then** the bot stops and leaves on its own.
6. **Given** a title that starts with a command word (`!media stop making sense`), **Then** it is searched,
   not treated as the command.

---

### User Story 3 - Playlist (Priority: P3)

Friends queue up to 10 movies or specific episodes; items play in order automatically.

**Why this priority**: Valued but not essential for a first switch-over; builds on Stories 1–2.

**Independent Test**: Queue three items, reorder and remove one, skip one, and confirm the rest play in
order without anyone typing a play command.

**Acceptance Scenarios**:

1. **Given** something is playing, **When** `!media queue <title>`, **Then** it is added at the end and its
   position is confirmed; with nothing playing it starts immediately.
2. **Given** 10 items are queued, **When** another is queued, **Then** it is refused as full.
3. **Given** a playlist, **When** `!media order <# or title> <position>`, `!media remove <# or title>`,
   `!media skip` or `!media playlist`, **Then** the playlist changes / is shown as today.
4. **Given** an item finishes on its own, **Then** the next item starts in the same voice channel.

---

### User Story 4 - Jellyfin SyncPlay group watch (Priority: P4)

With a Jellyfin user login configured, the bot opens a SyncPlay group; people watching in Jellyfin apps who
join it can pause, play and seek, and the Discord stream follows — and vice versa.

**Why this priority**: Differentiating but optional (off unless a login is configured); depends on Stories
1–2.

**Independent Test**: With a Jellyfin web client joined to the bot's group, pause/seek in either place and
confirm the other follows within 2 seconds.

**Acceptance Scenarios**:

1. **Given** a login is configured, **When** playback starts, **Then** a group named
   `slopsoil · <Discord server>` exists and Discord is told how to join it.
2. **Given** a Jellyfin member pauses/unpauses/seeks, **Then** the Discord stream does the same.
3. **Given** a friend uses `!media pause`/`play`/`resume <time>`, **Then** the Jellyfin group follows.
4. **Given** the connection to Jellyfin drops, **Then** the next item played re-creates the group.

---

### User Story 5 - Drop-in deployment (Priority: P5)

An operator runs the new bot with their existing `.env` and Docker setup, chosen by image tag, and can switch
back to the Python bot at any time.

**Why this priority**: Required for the switch-over decision, but only meaningful once Stories 1–4 work.

**Independent Test**: Start the new image with an unmodified `.env` from the Python deployment; the
Jellyfin-subset commands work; stop it and start the Python image again with no data loss.

**Acceptance Scenarios**:

1. **Given** an existing `.env`, **When** the new image starts, **Then** every setting relevant to the subset
   (Discord token, allowed users, Jellyfin URL/key/login, stream quality/resolution/fps/bitrate/pacing/A-V
   offset) is honoured with the same meaning and defaults.
2. **Given** a non-subset command (`!play`, `!channels`, `!search`, IPTV source commands), **Then** the new
   bot replies that it is not available in this edition rather than failing silently.
3. **Given** any log output, **Then** no Discord token, Jellyfin API key or access token appears in it.
4. **Given** hardware encoding is available (VA-API or NVENC), **Then** it is used, otherwise software.

### Edge Cases

- Jellyfin unreachable or credentials invalid → a clear message; nothing half-started.
- Transcode is slow to start (cold 4K) → waits up to 45 s before reporting failure.
- Video stalls mid-stream → audio holds briefly (≤ 0.3 s) then continues; no permanent freeze.
- Seek / aspect change while paused → applied on resume without stale audio.
- Discord voice reconnect or encryption-group change mid-stream → stream continues or reports and stops
  cleanly; never "plays" into a dead connection.
- Two commands race (e.g. auto-advance while a manual start is resolving) → one wins, no item lost.
- Bot is asked to play in a server/channel it can't join → refused with a message.
- Non-decimal digit replies (e.g. "²") in number prompts → treated as invalid, no crash.

## Requirements *(mandatory)*

### Functional Requirements

**Discord connectivity**

- **FR-001**: System MUST sign in to Discord as a user account with the operator's token, as the Python bot does.
- **FR-002**: System MUST join, move between and leave voice channels, and detect when the last human leaves.
- **FR-003**: System MUST publish a screenshare ("go-live") stream carrying H.264 video and Opus audio that
  ordinary Discord clients can watch, including in channels using Discord's end-to-end media encryption.
- **FR-004**: System MUST keep audio and video within 40 ms of each other in steady state and recover from
  stalls and restarts without lasting offset (behaviour equivalent to the Python bot's clock follower).

**Commands (unchanged names, arguments, wording and permission tiers)**

- **FR-010**: `!media <title>`, `!media <show> sXXeYY`, `!media search <title>` with numbered pickers.
- **FR-011**: `!media help` (and bare `!media`) listing the subset's commands.
- **FR-012**: `!media pause`, `play`, `resume <time>`, `stop`, `skip`, `aspect-ratio` (+ `aspect`, `ar`).
- **FR-013**: `!media queue`, `order`, `remove`, `playlist` with a 10-item cap and auto-advance.
- **FR-014**: `!join`, `!leave`, `!stop`, `!help`, `!ping` (general/voice commands needed to operate the subset).
- **FR-015**: Titles beginning with a sub-command word MUST be searched (same fall-through rules as today).
- **FR-016**: Role checks (admin / friend / viewer / none) MUST match the Python bot's rules and sources.

**Jellyfin**

- **FR-020**: System MUST search movies, series and episodes, list seasons/episodes, and start a playback
  session that Jellyfin transcodes to H.264/AAC with subtitles suppressed.
- **FR-021**: System MUST stop the server-side transcode when an item ends, is replaced or is stopped.
- **FR-022**: System MUST support API-key and username/password sign-in; SyncPlay requires the latter.
- **FR-023**: SyncPlay behaviour per User Story 4, including echo suppression of the bot's own requests.

**Streaming**

- **FR-030**: Pause, in-place seek and in-place aspect change MUST NOT end the screenshare.
- **FR-031**: Output size MUST fit inside the configured resolution keeping the source's aspect ratio unless
  a forced ratio is chosen.
- **FR-032**: Hardware encoder selection and fallback MUST match today's order (NVENC → VA-API → software),
  with B-frames disabled.

**Operations**

- **FR-040**: Configuration via the same environment variable names, meanings and defaults (subset).
- **FR-041**: Credentials MUST be redacted from all log output.
- **FR-042**: Published as a separate container image tag; the Python image remains the default.
- **FR-043**: An automated test suite MUST run in CI on every PR alongside the Python suite, with a
  coverage gate.
- **FR-044**: Non-subset commands MUST answer that they are not available in this edition.

### Key Entities

- **Guild session** — per Discord server: current item, playback position, paused state, playlist (≤ 10),
  forced aspect ratio, SyncPlay group link.
- **Media item** — a Jellyfin movie or episode: id, display label, runtime.
- **Playback stream** — a started Jellyfin session: stream address, session id, audio presence, video codec.
- **Stream session** — the live screenshare: voice connection, go-live connection, encryption state,
  video/audio senders sharing one media clock.
- **SyncPlay group** — Jellyfin group bound to a guild session.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From `!media <title>` (single match) to picture visible to viewers in ≤ 15 s for a warm
  Jellyfin server, matching or beating the Python bot on the same hardware.
- **SC-002**: A/V offset stays within ±40 ms over a 2-hour film, including 10 pause/seek operations.
- **SC-003**: Pause takes effect within 1 s; seek and aspect changes complete within 3 s with the screenshare
  never dropping.
- **SC-004**: 100% of the subset's commands produce the same user-visible replies as the Python bot for the
  same inputs (verified by a parity checklist).
- **SC-005**: An operator can switch between the Python and .NET images using only an image-tag change.
- **SC-006**: Zero credentials found when scanning a full day of logs.
- **SC-007**: The .NET suite runs in under 2 minutes in CI with ≥ 90% line coverage of non-protocol code.

## Assumptions

- **Scope**: TV (TVheadend), IPTV/M3U/EPG sources and yt-dlp `!play` are out of scope for this edition and
  remain Python-only; they may follow in a later feature.
- **Coexistence**: Only one bot (Python or .NET) runs on a given Discord account at a time; both live in this
  repository, and the Python bot remains the shipping default until the parity checklist passes.
- **Discord layer**: Built in-house (no third-party library supports user-account voice + go-live + E2EE).
  The protocol knowledge in `STREAMING.md` and the Python implementation is the reference.
- **Media encryption**: Discord's E2EE uses the official libdave library, called from .NET as a native
  dependency, as the Python bot does today.
- **FFmpeg** remains the decoder/encoder; Jellyfin remains the transcoder.
- **Persistence**: The subset has no persisted data beyond the in-memory guild sessions (no database, so the
  constitution's migration principle does not apply).
- **Self-bot terms**: Using a user account violates Discord's Terms of Service, exactly as the Python bot
  does today; the rewrite does not change that risk and keeps the README disclaimer.
