# Data Model: .NET Rewrite — Jellyfin-First Edition

All state is in memory; nothing is persisted (no database — constitution Principle III not applicable).

## GuildSession (per Discord server)

| Field | Type | Rules |
|---|---|---|
| GuildId | ulong | key |
| Send | reply channel delegate | last channel a command came from |
| NowPlaying | NowPlaying? | null when idle |
| Queue | list of MediaItem | max 10 (`PlaylistMax`); order matters |
| Generation | int | bumped on every start/stop; stale end-of-stream callbacks compare and ignore |
| ForcedAspect | double? | null = original; survives leaving voice (kept in a separate per-guild map) |
| DebugOverlay | bool | per guild; survives playlist advance; cleared when the bot leaves voice |
| SyncPlay | SyncPlayGroup? | only when a Jellyfin login is configured |
| SyncSent | map itemId → time | echo window 10 s |
| SyncOwnCommandAt | time | own-command drift window 5 s |
| StartLock | async lock | serialises starts / auto-advance / SyncPlay switches |

**Transitions**: Idle → Starting (lock held) → Playing ⇄ Paused → (natural end → auto-advance → Starting) |
(stop/leave → Idle, queue cleared) | (bot leaves voice → session discarded except ForcedAspect).

## MediaItem

| Field | Rules |
|---|---|
| Id | Jellyfin item id |
| Type | Movie / Episode / Series (Series is never played; picker resolves to Episode) |
| Name, ProductionYear, SeriesName, ParentIndexNumber, IndexNumber | label: `Name (Year)` or `Series — SxxEyy — Name` (S00/E00 kept) |
| RunTimeTicks | runtime; seeks at/after it are rejected |

## PlaybackStream

| Field | Rules |
|---|---|
| Url | HLS URL incl. credentials — never logged unredacted |
| PlaySessionId | used to stop the transcode (`DELETE /Videos/ActiveEncodings`) |
| HasAudio, VideoCodec | from PlaybackInfo MediaStreams; null → ffprobe fallback; no video stream → ffprobe |

## StreamSession (one live screenshare)

| Component | Notes |
|---|---|
| VoiceConnection | main voice channel connection (DAVE session, UDP, SSRC) |
| GoLiveConnection | separate stream connection (ops 18–20), own SSRCs, idempotent disconnect |
| VideoPlayer | FFmpeg process + pacing thread; `Position`, `Paused`, `SeekPending`, `RestartSeq`, `SegmentOffset`, `SegmentFrames`, `Fps`, `Aspect` |
| AudioSender | reads PCM FIFO; follows `VideoPlayer` clock (slack max(40 ms, 0.5/fps+20 ms), settling ≤15 steps per segment, persistent drift ≤15 steps per episode, drain on restart) |
| OnEnd | invoked only on natural end (not stop/replace) |

## StreamDiagnostics

Rolling 10 s window maintained by VideoPlayer + AudioSender + transport; snapshot is immutable.

| Field | Source |
|---|---|
| FpsSent / FpsTarget, LateFrames, MaxLatenessMs, ReadStallMs | VideoPlayer pacing loop |
| PacketsPerSec, SendDrops, SendErrors | transport (UDP send) |
| DriftMs, SilenceSteps, SkipSteps | AudioSender follower |
| Encoder, Resolution, Bitrate, SourceCodec, Position, Runtime | FFmpeg command / PlaybackStream / VideoPlayer |

Overlay: FFmpeg `drawtext` reading a small text file (`textfile=…:reload=1`) that the player rewrites once
per second from the snapshot; only added to the filter chain when DebugOverlay is on.

## SyncPlayGroup

| Field | Rules |
|---|---|
| DeviceId | `slopsoil-<guildId>` (one Jellyfin session per guild) |
| GroupName | `slopsoil · <guild name>` |
| Closed | set on leave or when the websocket drops (next play re-creates) |
| Handlers | OnCommand(Pause/Unpause/Seek/Stop, position), OnPlayQueue(itemId, start, isPlaying) |

## Configuration (subset of today's env vars)

See [contracts/configuration.md](contracts/configuration.md).
