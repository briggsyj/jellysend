# Feature Specification: Empty voice channel timeout

**Feature Branch**: `003-empty-channel-timeout` (implemented on `001-dotnet-rewrite`, .NET edition only)

**Created**: 2026-11-03

**Status**: Ready

**Input**: User description: "add a timeout to empty voice channels that stops the stream after 20min"

## Clarifications

### Session 2026-11-03

- Q: Which edition? → A: The .NET edition only (it supersedes the Python bot).
- Interpretation recorded: the timeout replaces the immediate auto-leave. When the last person leaves the bot's
  voice channel the stream keeps going for the timeout; if anyone comes back it continues as if nothing happened;
  if the channel is still empty when the timeout expires the bot stops the stream and leaves (as auto-leave did).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Stepping away doesn't end the movie (Priority: P1)

Everyone leaves the voice channel for a break (or loses connection). The stream keeps playing; when someone
rejoins within 20 minutes it is still there.

**Independent Test**: Stream, have the last viewer leave and rejoin after 5 minutes: the stream never stopped.

**Acceptance Scenarios**:

1. **Given** a stream in a channel, **When** the last person leaves and someone rejoins within 20 minutes,
   **Then** the stream is still running and the bot is still in the channel.
2. **Given** a stream, **When** the last person leaves, **Then** the bot logs that it will stop in 20 minutes
   unless someone returns.

### User Story 2 - A forgotten stream doesn't run forever (Priority: P1)

Nobody comes back. After 20 minutes the bot stops the stream (releasing the Jellyfin transcode) and leaves.

**Acceptance Scenarios**:

1. **Given** the channel has been empty for 20 minutes, **Then** the stream stops, the playlist/session is
   discarded and the bot leaves — the same end state as today's auto-leave.
2. **Given** the bot is in an empty channel without streaming (after `!stop`), **Then** it also leaves after 20
   minutes.

### Edge Cases

- Someone joins and leaves again: the countdown restarts from the last time the channel became empty.
- The bot is moved by a moderator into an empty channel: the countdown starts.
- `!leave`, `!media stop` or a kick during the countdown: the countdown is cancelled (nothing left to stop).
- Other people's activity in other channels never starts or cancels the countdown.
- `EMPTY_CHANNEL_TIMEOUT_MINUTES=0`: the old behaviour — stop and leave as soon as the channel is empty.

## Requirements *(mandatory)*

- **FR-001**: When the bot's voice channel has no one but the bot, a countdown of `EMPTY_CHANNEL_TIMEOUT_MINUTES`
  (default 20) MUST start; when it expires with the channel still empty the bot MUST stop streaming, discard the
  session and leave voice.
- **FR-002**: Anyone (other than the bot) joining the channel MUST cancel the countdown.
- **FR-003**: Leaving voice for any reason MUST cancel the countdown.
- **FR-004**: `EMPTY_CHANNEL_TIMEOUT_MINUTES` accepts 0–1440; 0 restores immediate auto-leave; invalid values fall
  back to 20 with a warning.
- **FR-005**: The countdown start, cancel and expiry MUST be logged.

## Success Criteria *(mandatory)*

- **SC-001**: A viewer who steps away for less than the timeout returns to the same stream 100 % of the time.
- **SC-002**: An abandoned stream stops within the timeout + 10 s.

## Assumptions

- The countdown is announced in the log, not in Discord (there is no reliable text channel to post to when
  everyone has left; the stop itself is visible when they return).
- Deployments that set nothing get the new 20-minute behaviour (requested by the maintainer; the .NET edition
  supersedes Python).
