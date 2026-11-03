---

description: "Task list for the .NET rewrite — Jellyfin-first edition"
---

# Tasks: .NET Rewrite — Jellyfin-First Edition

**Input**: Design documents from `/specs/001-dotnet-rewrite/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Required — FR-043 and constitution Principle IV make the .NET suite a gating suite. Test tasks
precede the implementation they cover within each phase. Behavioural reference for every task is the Python
module named in it; port behaviour, not structure.

**Organization**: Grouped by user story (spec priorities P1–P5) after a blocking Spike + Foundation.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an incomplete task)
- All paths are relative to the repository root; .NET code lives under `dotnet/`

---

## Phase 1: Setup

**Purpose**: solution skeleton, build settings, CI.

- [X] T001 Create `dotnet/Slopsoil.slnx` and projects `dotnet/src/{Slopsoil.Common,Slopsoil.Discord,Slopsoil.Services,Slopsoil.Streaming,Slopsoil.Commands,Slopsoil.Host}` (class libraries; Host is a console app) and `dotnet/tests/Slopsoil.Tests` (xUnit v3), with project references exactly as plan.md "Project references" (Services → Common only; Streaming → Discord, Common; Commands → Services, Streaming, Discord, Common; Host → all)
- [X] T002 Add `dotnet/Directory.Build.props` (net10.0, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<InvariantGlobalization>true</InvariantGlobalization>`) and `dotnet/.gitignore` (bin/, obj/, TestResults/)
- [X] T003 [P] (done via coverlet.MTP flags in `dotnet/ci/test.sh` — .NET 10 requires Microsoft.Testing.Platform, so no runsettings) Add `dotnet/coverlet.runsettings` excluding `[Slopsoil.Discord]Slopsoil.Discord.Native.*` and classes marked `[ExcludeFromCodeCoverage]`, and `dotnet/ci/test.sh` running `dotnet test --settings coverlet.runsettings` then failing if line coverage < 90 % (parse cobertura `line-rate`)
- [X] T004 [P] Add a `dotnet` job to `.github/workflows/tests.yml` (actions/setup-dotnet with `dotnet-version: 10.0.x`, run `dotnet/ci/test.sh`), alongside the unchanged Python job
- [X] T005 [P] Add `dotnet/README.md`: purpose, build/test commands from quickstart.md, status table per user story

---

## Phase 2: Spike — Discord transport gate (BLOCKING)

**Purpose**: prove research R2–R4 before investing in features. If T013 cannot be made to work, stop and
revisit R4 with the maintainer (plan.md Delivery phase 0).

- [X] T006 Write `dotnet/Dockerfile` with a `libdave` build stage (clone discord/libdave at a pinned commit, CMake `-DBUILD_SHARED_LIBS=ON`, vcpkg deps) producing `libdave.so`, and a Fedora runtime stage installing `ffmpeg-free`, `libopus`, `libsodium`, VA-API drivers (copy the package list from the root `Dockerfile`)
- [X] T007 [P] P/Invoke shims `dotnet/src/Slopsoil.Discord/Native/Sodium.cs` (`crypto_aead_xchacha20poly1305_ietf_encrypt`, `crypto_secretbox_easy`, `randombytes_buf`) and `Native/Opus.cs` (`opus_encoder_create/destroy`, `opus_encode`, 48 kHz stereo, 960-sample frames)
- [X] T008 [P] (shim is `Native/LibDave.cs` + `Dave/LibDaveSession.cs`, `Dave/PassthroughDaveSession.cs`) P/Invoke shim `dotnet/src/Slopsoil.Discord/Native/Dave.cs` for the `dave.h` C API (session create/destroy/init, key package, process proposals/commit/welcome, encryptor create/set key ratchet/encrypt for `DAVE_CODEC_OPUS` and `DAVE_CODEC_H264`, `daveFree`) plus `IDaveSession` in `dotnet/src/Slopsoil.Discord/Dave/IDaveSession.cs` (behaviour reference: `davey_compat.py`)
- [X] T009 [P] Tests with fixed vectors in `dotnet/tests/Slopsoil.Tests/Discord/RtpTests.cs` for RTP header, Opus packet framing and H.264 FU-A fragmentation at MTU 1200 (reference `_rtp_header`, `_packetize_nal` in `streaming/video_player.py`; generate vectors with a short Python script committed as `dotnet/tests/vectors/make_vectors.py`)
- [X] T010 [P] Tests in `dotnet/tests/Slopsoil.Tests/Discord/SpsRewriteTests.cs` for SPS VUI rewrite (`bitstream_restriction_flag=1`, `max_num_reorder_frames=0`) against Python-generated vectors (reference `rewrite_sps_vui`)
- [X] T011 (RtpPacketizer is `RtpHeader.cs`) Implement `dotnet/src/Slopsoil.Discord/Rtp/{RtpPacketizer.cs,H264Packetizer.cs,SpsRewriter.cs,TransportCrypto.cs}` (modes `aead_xchacha20_poly1305_rtpsize`, `xsalsa20_poly1305_lite`; nonce handling as `_encrypt` / `_encrypt_audio`) until T009–T010 pass
- [X] T012 Implement `dotnet/src/Slopsoil.Discord/Gateway/GatewayClient.cs` (user-account IDENTIFY like discord.py-self, heartbeat, RESUME, dispatch of READY/GUILD_CREATE/VOICE_STATE_UPDATE/VOICE_SERVER_UPDATE/MESSAGE_CREATE and stream ops 18–20) and `Voice/VoiceConnection.cs` (voice ws v8, UDP IP discovery, select protocol with video capabilities per `video_compat.py`, DAVE opcodes per `davey_compat.py`) and `GoLive/GoLiveConnection.cs` (per `streaming/golive.py`, idempotent disconnect clearing the stream key)
- [ ] T013 Spike harness `dotnet/src/Slopsoil.Host/Spike.cs`: when `SLOPSOIL_SPIKE=testpattern`, on `!join` start go-live with FFmpeg `testsrc2` + `sine` through T011/T012; validate manually per quickstart.md scenario 1 in a DAVE-enabled channel and record the result in `dotnet/README.md`

**Checkpoint**: test pattern visible and audible to another Discord client → proceed.

---

## Phase 3: Foundational (blocking all stories)

- [X] T014 [P] Tests then implementation of env config in `dotnet/src/Slopsoil.Common/Config/SlopsoilOptions.cs` + `dotnet/tests/Slopsoil.Tests/Common/OptionsTests.cs` covering every row of contracts/configuration.md (names, defaults, clamps: `STREAM_FPS` 1–60, `STREAM_PACKET_PACE` 0.0–0.95, `STREAM_AV_SYNC_MS` ±5000, presets 720p/1080p/4k)
- [X] T015 [P] Tests then implementation of redaction in `dotnet/src/Slopsoil.Common/Logging/Redactor.cs` + `RedactingLoggerProvider.cs` (same keys as `redact.py`: api_key, apikey, token, access_token, password, x-emby-token; userinfo masking; applied to messages and exceptions) with tests in `dotnet/tests/Slopsoil.Tests/Common/RedactorTests.cs`
- [X] T016 [P] Tests then implementation of roles in `dotnet/src/Slopsoil.Common/Permissions/RoleResolver.cs` (Admin ⊃ Friend ⊃ Viewer ⊃ None; bot's own account passes; reference `permissions.py`)
- [X] T017 Command router in `dotnet/src/Slopsoil.Commands/Routing/CommandRouter.cs`: `!` prefix, groups with sub-commands, `*rest` arguments, role attribute checks (silent on failure), "not available in this edition" replies for `!play`, `!channels`, `!search`, `!add-source`, `!sources`, `!delete-source` (FR-044), tests in `dotnet/tests/Slopsoil.Tests/Commands/RouterTests.cs`
- [X] T018 [P] Number-picker helper `dotnet/src/Slopsoil.Commands/Interaction/NumberPrompt.cs` (10 s timeout → "timed out — cancelled", `!`-prefixed reply → "cancelled", decimal digits only, out-of-range message; reference `_wait_for_number`) with tests
- [X] T019 Host wiring in `dotnet/src/Slopsoil.Host/Program.cs` (generic host, options, redacting logging, gateway client, command modules) and `dotnet/src/Slopsoil.Commands/General/GeneralModule.cs` (`!help`, `!ping` per `cogs/general.py`)

**Checkpoint**: bot logs in, answers `!ping`/`!help`, refuses non-subset commands.

---

## Phase 4: User Story 1 — Watch a Jellyfin title together (P1) 🎯 MVP

**Goal**: `!media <title>` / `<show> sXXeYY` / series picker → screenshare with audio.

**Independent Test**: quickstart.md scenario 2.

- [ ] T020 [P] [US1] Tests for Jellyfin client in `dotnet/tests/Slopsoil.Tests/Services/JellyfinClientTests.cs` (search fields, find_episode via series→seasons→episodes, PlaybackInfo body with `SubtitleStreamIndex=-1` and H.264/AAC HLS profile, URL rewrite forcing `SubtitleStreamIndex=-1` and appending `api_key`, MediaStreams → `HasAudio`/`VideoCodec` incl. "no video stream → null", stop_encoding DELETE; reference `services/jellyfin.py` and its tests)
- [ ] T021 [US1] Implement `dotnet/src/Slopsoil.Services/Jellyfin/{JellyfinClient.cs,Models.cs,AuthHeader.cs}` (API key + lazy username/password sign-in under a lock) until T020 passes
- [ ] T022 [P] [US1] HLS variant resolver + ffprobe wrapper in `dotnet/src/Slopsoil.Services/Media/{HlsVariantResolver.cs,ProbeService.cs}` with tests (reference `services/hls.py`, `services/probe.py`; logs redacted)
- [ ] T023 [P] [US1] Tests for FFmpeg command building in `dotnet/tests/Slopsoil.Tests/Streaming/FfmpegCommandTests.cs`: encoder detection order NVENC → VA-API → libx264 → libopenh264 with `-bf 0` on hardware, fit-to-box scale expression (`dar`, `2*round`, `setsar=1`), exact `-r` (`29.97`) and `-g round(fps)`, `-ss` before `-i`, silence `anullsrc` when no audio, reconnect flags only for URLs
- [ ] T024 [US1] Implement `dotnet/src/Slopsoil.Streaming/Video/{EncoderDetector.cs,FfmpegCommand.cs}` until T023 passes
- [ ] T025 [P] [US1] Tests for the video player in `dotnet/tests/Slopsoil.Tests/Streaming/VideoPlayerTests.cs` with a fake process stream of canned Annex-B: frame grouping by AUD/slice, pacing clock, no flush after stop, SW-encoder fallback on zero frames, `Position = offset + frames/fps`
- [ ] T026 [US1] Implement `dotnet/src/Slopsoil.Streaming/Video/VideoPlayer.cs` (dedicated thread, FIFO creation via `mkfifo`, stdout closed before SIGTERM on kill) until T025 passes
- [ ] T027 [P] [US1] Tests for the audio sender clock follower in `dotnet/tests/Slopsoil.Tests/Streaming/AudioSenderTests.cs`: slack `max(40 ms, 0.5/fps+20 ms)`, settling-window alignment (see FR-054), drop when behind, `STREAM_AV_SYNC_MS` target kept, no correction before the segment's first frame (port the cases from `tests/test_golive.py`)
- [ ] T028 [US1] Implement `dotnet/src/Slopsoil.Streaming/Audio/AudioSender.cs` until T027 passes
- [ ] T029 [US1] Implement `dotnet/src/Slopsoil.Streaming/Engine/StreamEngine.cs` (`StartLiveStreamAsync(guild, channel, title, url, audio, startOffset, aspect, onEnd)`; waits for old stream cleanup before STREAM_CREATE; owner-checked cleanup; 45 s FIFO timeout; onEnd only on natural end) with tests in `dotnet/tests/Slopsoil.Tests/Streaming/StreamEngineTests.cs` (reference `streaming/engine.py`, `tests/test_engine.py`)
- [ ] T030 [US1] Implement `dotnet/src/Slopsoil.Commands/Media/MediaModule.cs` play flow: `!media <query>`, `!media search`, bare `!media` → help, result picker, season → episode pickers, labels (`Name (Year)`, `Series — SxxEyy — Name`, S00/E00 kept), "you're not in a voice channel", "checking stream…", error replies identical to `cogs/jellyfin.py`; tests in `dotnet/tests/Slopsoil.Tests/Commands/MediaPlayTests.cs`
- [ ] T031 [US1] Implement `dotnet/src/Slopsoil.Commands/Voice/VoiceModule.cs` `!join` and voice-state tracking (resolve author's voice channel from guild voice states; reference `cogs/utils.py`)

**Checkpoint**: MVP — quickstart scenario 2 passes against real Discord + Jellyfin.

---

## Phase 5: User Story 2 — Playback control (P2)

**Independent Test**: quickstart.md scenario 3.

- [ ] T032 [P] [US2] Tests for `TimestampParser` in `dotnet/tests/Slopsoil.Tests/Commands/TimestampTests.cs` (port every case from `tests/test_jellyfin_commands.py`: `1:23:45`, `1.23.45`, `1:23` = h:mm, bare = minutes, `45m`, `90s`, `1h30m`, reject components ≥ 60 after the first)
- [ ] T033 [P] [US2] Video player pause gate / in-place seek / restart handshake (`RestartSeq`, `SegmentOffset`, `SegmentFrames`, `SeekPending` incl. restarting window, ≤ 0.5 s drained-wait, unregister on sender exit) + tests in `VideoPlayerTests.cs`; audio sender drain-on-restart, pause-loop restart handling and pending-seek silence + tests in `AudioSenderTests.cs`
- [ ] T034 [US2] `MediaModule` `pause`, `play`, `resume <time>`, `stop`, `aspect-ratio`/`aspect`/`ar` (options exactly: Original (default), 16:9, 4:3, 2.39:1 (21:9), 1.85:1; applies live only when the stream is a Jellyfin one; remembered per guild across leaving voice), and title fall-through for help/pause/play/stop/skip/playlist/resume/aspect, with tests in `dotnet/tests/Slopsoil.Tests/Commands/MediaControlTests.cs`
- [ ] T035 [US2] `VoiceModule` `!leave`/`!stop` sharing one stop helper (cancel stream → stop audio → disconnect go-live → leave) and auto-leave when the last human leaves, with tests (reference `cogs/voice.py`, `tests/test_voice.py`)

---

## Phase 5b: User Story 6 — Stable stream performance and on-stream diagnostics (P2)

**Goal**: smooth 2-hour streams (SC-008–SC-010) and `!media debug on|off` burning live diagnostics into the
stream.

**Independent Test**: spec User Story 6 — 2-hour item with debug on; overlay refreshes ≥ 1/s; stability
targets hold; `off` removes it without a screenshare drop.

- [ ] T044 [P] [US6] Tests then implementation of `dotnet/src/Slopsoil.Streaming/Diagnostics/StreamDiagnostics.cs`: rolling 10 s window and immutable snapshot with exactly the fields in data-model.md "StreamDiagnostics" (FpsSent/FpsTarget, LateFrames, MaxLatenessMs, ReadStallMs, PacketsPerSec, SendDrops, SendErrors, DriftMs, SilenceSteps, SkipSteps, Encoder, Resolution, Bitrate, SourceCodec, Position, Runtime); fed by VideoPlayer, AudioSender and the UDP transport; tests in `dotnet/tests/Slopsoil.Tests/Streaming/StreamDiagnosticsTests.cs`
- [ ] T045 [US6] Periodic log line every 10 s while streaming (debug on or off, redacted) from the snapshot, matching the Python "video stats" fields (FR-053), in `dotnet/src/Slopsoil.Streaming/Diagnostics/DiagnosticsLogger.cs` with tests
- [ ] T046 [US6] Overlay: when debug is on, `FfmpegCommand` appends a `drawtext=textfile=<tmp>/slopsoil_<ssrc>_debug.txt:reload=1` stage (after scaling, before hwupload for VA-API; monospace, top-left, semi-opaque box) and `VideoPlayer` rewrites that file atomically once per second from the snapshot with the four groups in spec US6 scenario 1; no extra filter when off (FR-051, US6 scenario 5); tests in `FfmpegCommandTests.cs` and `VideoPlayerTests.cs`
- [ ] T047 [US6] `MediaModule` `!media debug [on|off]` (Friend): on/off toggles per-guild `DebugOverlay` and applies via the in-place restart path (FR-052); bare `debug` replies with state + snapshot text; other words → title search; setting survives playlist advance and is cleared on leaving voice; reply wording added to contracts/commands.md parity rows; tests in `dotnet/tests/Slopsoil.Tests/Commands/MediaDebugTests.cs`
- [ ] T048 [US6] Port the follower rules exactly as FR-054 (no stall hold; settling window: first 1 s of each segment, immediate 20 ms steps ≤ 15; steady state: drift > 120 ms for ≥ 1 s → one 20 ms step per 250 ms, ≤ 15 per episode, until ≤ 30 ms) with the anti-rubber-banding test cases from `tests/test_golive.py` (transient lag untouched, settling cap, persistence, rate limit, episode cap, exit band, restart reset) in `AudioSenderTests.cs`
- [ ] T049 [US6] Soak harness `dotnet/tests/soak/README.md` + `SLOPSOIL_SOAK=1` mode in the host that streams a local 2-hour test file with debug on and writes the per-10 s snapshots to CSV; document how to check SC-008/SC-009 from the CSV; run once on reference hardware before the parity sign-off

---

## Phase 6: User Story 3 — Playlist (P3)

**Independent Test**: quickstart.md scenario 4.

- [ ] T036 [US3] `GuildSession` per data-model.md in `dotnet/src/Slopsoil.Commands/Media/GuildSession.cs` ("max 10 (`PlaylistMax`); order matters", generation counter, start lock) and `MediaModule` `queue`, `order`, `remove`, `playlist`, `skip`, auto-advance on natural end with generation guard and start-lock serialisation, tests in `dotnet/tests/Slopsoil.Tests/Commands/PlaylistTests.cs` (port playlist cases from `tests/test_jellyfin_commands.py`)

---

## Phase 7: User Story 4 — SyncPlay (P4)

**Independent Test**: quickstart.md scenario 5.

- [ ] T037 [P] [US4] Tests then implementation of `dotnet/src/Slopsoil.Services/SyncPlay/SyncPlayGroup.cs` (device id `slopsoil-<guildId>`, `/socket?api_key=&deviceId=`, New/SetIgnoreWait/SetNewQueue/Pause/Unpause/Seek/Leave bodies, ForceKeepAlive → KeepAlive every Data/2 s, SyncPlayCommand and PlayQueue parsing, dead socket → `Closed`) with tests in `dotnet/tests/Slopsoil.Tests/Services/SyncPlayTests.cs` (reference `services/syncplay.py`, `tests/test_syncplay.py`)
- [ ] T038 [US4] Wire SyncPlay into `MediaModule`: group per guild (serialised creation), queue sent after first frame using player position, own-command 5 s drift window, item echo window 10 s, Pause/Unpause/Seek/Stop handlers with 3 s drift tolerance, PlayQueue switch, teardown on leave; tests in `dotnet/tests/Slopsoil.Tests/Commands/SyncPlayCommandTests.cs`

---

## Phase 8: User Story 5 — Drop-in deployment (P5)

**Independent Test**: quickstart.md scenario 6.

- [X] T039 [US5] Extend `.github/workflows/docker-publish.yml` with a .NET image job building `dotnet/Dockerfile`: PR build-check (no push, cache read-only) and on merge to develop push `ghcr.io/<owner>/slopsoil:dotnet` and `:dotnet-sha-<short sha>` (`latest` untouched)
- [ ] T040 [US5] Parity checklist `specs/001-dotnet-rewrite/checklists/parity.md` generated from contracts/commands.md rows (input → expected reply) for manual sign-off before cut-over
- [ ] T041 [US5] README section "Running the .NET edition" (tag, env compatibility, what's not included) in `README.md`

---

## Phase 9: Polish & cut-over

- [ ] T042 Log-scan test in `dotnet/tests/Slopsoil.Tests/Common/LogScanTests.cs` driving a full fake play/pause/seek/stop through the router and asserting no credential appears in captured logs (SC-006)
- [ ] T043 Cut-over PR (after parity.md is signed off): tag last Python build as `v1.x`, switch `latest` to the .NET image, title `feat!: .NET edition becomes latest` with `BREAKING CHANGE:` footer naming the removed TV/IPTV/yt-dlp features and the Python tag to pin (constitution Principle I)

---

## Dependencies & Execution Order

- Phase 1 → Phase 2 (spike gate) → Phase 3 → US1 → US2 → US3 → US4 → US5 → Polish.
- US2–US4 depend on US1's engine and media module; US3 and US4 are independent of each other once US2 lands.
- US6 depends on US1 (pipeline) and US2 (in-place restart); T048 can be done together with T033.
- Within a phase, `[P]` test tasks can be written in parallel; each implementation task follows its tests.

## Parallel Examples

- Spike: T007, T008, T009, T010 in parallel; then T011 → T012 → T013.
- Foundational: T014, T015, T016, T018 in parallel; then T017 → T019.
- US1: T020, T022, T023, T025, T027 in parallel; then T021, T024, T026, T028 → T029 → T030/T031.

## Implementation Strategy

1. **MVP** = Phases 1–4 (through US1): an operator can watch a Jellyfin title in Discord from the .NET bot.
2. Add US2 (controls) and US6 (stability + debug overlay) before any real-world use; US3/US4 next; US5
   publishes the `dotnet` tag. The US6 soak run (T049) is part of the parity sign-off.
3. Cut-over (T043) only after the parity checklist passes — Python stays on `latest` until then.
