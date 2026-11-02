# Implementation Plan: .NET Rewrite — Jellyfin-First Edition

**Branch**: `001-dotnet-rewrite` | **Date**: 2026-11-02 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-dotnet-rewrite/spec.md`

## Summary

Re-implement the Jellyfin-facing half of slopsoil in .NET 10: user-account Discord gateway, voice + go-live
transport with DAVE end-to-end encryption, the FFmpeg-driven H.264/Opus pipeline with the A/V clock follower,
the Jellyfin/SyncPlay clients, and the `!media` / voice / general commands — behaviour-for-behaviour with the
Python bot. It lives in `dotnet/` beside the Python code, ships as `ghcr.io/briggsyj/slopsoil:dotnet` and
takes over `latest` only after the parity checklist passes. The Discord protocol layer is built in-house
(R2–R4 in [research.md](research.md)); everything else is a straight port of reviewed Python behaviour.

## Technical Context

**Language/Version**: C# 14 on .NET 10 LTS

**Primary Dependencies**: Microsoft.Extensions.Hosting/Logging/Options (BCL-adjacent); native `libopus`,
`libsodium`, `libdave` (shared, built in Docker) via P/Invoke; FFmpeg/ffprobe executables

**Storage**: N/A (in-memory guild sessions only)

**Testing**: xUnit v3, coverlet; fixed test vectors from the Python implementation; real-FFmpeg tests
skipped when absent

**Target Platform**: Linux x64 container (Fedora base like today, VA-API/NVENC capable)

**Project Type**: long-running service (Discord self-bot)

**Performance Goals**: 30–60 fps H.264 + 50 audio packets/s per stream with ≤1 ms pacing jitter;
start-to-picture ≤15 s (SC-001)

**Constraints**: A/V within ±40 ms (SC-002); no credential in logs; one stream per account at a time

**Scale/Scope**: one Discord account, a handful of guilds, one concurrent go-live; ~6–8k LOC C# + tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|---|---|---|
| I. Public API compatibility | **Pass with approved exception** | Commands, replies, env vars and compose deployment are preserved for the subset ([contracts/](contracts/)). Until cut-over the change is purely additive (new `dotnet` tag). At cut-over `latest` loses TV/IPTV/yt-dlp — **breaking change approved by the maintainer on 2026-11-02** (spec Clarifications). Upgrade path: operators who need those features pin the last Python tag (`sha-…` or the final Python version tag, to be cut as `v1.x` before cut-over). The cut-over PR MUST be marked `feat!:` with a `BREAKING CHANGE:` footer. |
| II. Service boundaries | **Pass (new layer justified)** | The .NET solution mirrors the existing layers as separate projects so boundaries are compile-time enforced (see Structure). One new layer, `Slopsoil.Discord`, replaces the third-party `discord.py-self` dependency — see Complexity Tracking. `Services` references no other app project; `Streaming` references only `Discord`; only `Commands` references `Services` + `Streaming`. The Python code is unchanged. |
| III. Reversible migrations | N/A | No database. |
| IV. Established test suites | **Pass** | The Python suite keeps running unchanged. The .NET suite becomes an established suite: a CI job runs it on every PR with a 90% line-coverage gate (excluding `Native/` shims and live-network adapters, which are reported). FFmpeg-dependent tests report skips. |
| Workflow: Conventional Commits | Pass | All commits/PR titles in this feature use `feat(dotnet): …`, `docs(spec): …`, `ci: …`. |
| Workflow: plans state interfaces/layers/migrations | Pass | This table + Structure section; no migration. |

Re-check after Phase 1 design: **unchanged — pass** (contracts match the Python surface; project references
enforce the layer rules; no new persistence).

## Project Structure

### Documentation (this feature)

```text
specs/001-dotnet-rewrite/
├── plan.md            # this file
├── research.md        # Phase 0
├── data-model.md      # Phase 1
├── quickstart.md      # Phase 1
├── contracts/
│   ├── commands.md
│   └── configuration.md
├── checklists/requirements.md
└── tasks.md           # /speckit-tasks
```

### Source Code (repository root)

```text
dotnet/
├── Slopsoil.slnx
├── Directory.Build.props          # net10.0, nullable, TreatWarningsAsErrors, LangVersion
├── Dockerfile                     # libdave build stage → runtime (ffmpeg, libopus, libsodium, VA-API)
├── src/
│   ├── Slopsoil.Common/           # options/env config, redaction, permissions (≈ permissions.py, redact.py)
│   ├── Slopsoil.Discord/          # NEW layer: gateway, voice ws/UDP, go-live, RTP, crypto, DAVE
│   │   └── Native/                # libopus / libsodium / libdave P/Invoke shims (coverage-excluded)
│   ├── Slopsoil.Services/         # Jellyfin client, SyncPlay client, HLS variant, ffprobe
│   ├── Slopsoil.Streaming/        # FFmpeg video player, audio sender + clock follower, stream engine
│   ├── Slopsoil.Commands/         # command router, !media/voice/general modules (≈ cogs/)
│   └── Slopsoil.Host/             # Program.cs, DI wiring (≈ bot.py)
└── tests/
    └── Slopsoil.Tests/            # unit tests per project, test vectors from Python
```

Project references (enforce constitution II): `Common` ← everyone; `Discord` → Common; `Services` → Common;
`Streaming` → Discord, Common; `Commands` → Services, Streaming, Discord, Common; `Host` → all.

**Structure Decision**: separate `dotnet/` tree so the Python app, its CI and its image are untouched until
cut-over; one solution with per-layer projects.

## Delivery phases (drive tasks.md)

0. **Spike (gate)** — libdave shared build in Docker; P/Invoke smoke test; gateway login + voice join +
   go-live with a test pattern. *If the spike fails, stop and revisit R4 with the maintainer.*
1. **Foundation** — solution, CI job, options/redaction/permissions, command router, logging.
2. **US1** — Jellyfin client, pickers, video player, audio sender, engine → watch a title (MVP).
3. **US2** — pause/play/resume/aspect/stop/leave/auto-leave, title fall-through.
3b. **US6** — stability hardening + `!media debug` overlay (diagnostics snapshot, drawtext overlay, periodic
   log line, soak test against SC-008–SC-010).
4. **US3** — playlist + auto-advance.
5. **US4** — SyncPlay.
6. **US5** — image publishing (`dotnet` tags), parity checklist, cut-over PR (`feat!:`).

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| New layer `Slopsoil.Discord` (≈ gateway + voice + go-live + DAVE, in-house) | No .NET library supports user-account voice, go-live video or DAVE; the Python bot relies on a third-party library plus runtime patches for this | Python sidecar for Discord: rejected by the maintainer (in-house requirement). Third-party .NET libs: bot-token only. |
| Native dependencies via P/Invoke (libdave, libopus, libsodium) | Same proven native code the Python bot uses; DAVE must be the official implementation | Managed re-implementations (MLS/DAVE in C#) are large and security-sensitive. |
| Two implementations in one repo until cut-over | Side-by-side rollout requested; operators switch by tag | Big-bang replacement would leave no fallback during the parity period. |
