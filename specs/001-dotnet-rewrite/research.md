# Research: .NET Rewrite — Jellyfin-First Edition

Each entry: **Decision** / **Rationale** / **Alternatives considered**. Reference implementation for every
protocol detail is the Python bot (`streaming/`, `davey_compat.py`, `video_compat.py`) and `STREAMING.md`.

## R1. Runtime and hosting

- **Decision**: .NET 10 LTS (SDK 10.0.1xx), C# 14, `Microsoft.Extensions.Hosting` generic host, nullable
  enabled, warnings as errors. Single self-contained Linux x64 container.
- **Rationale**: User-mandated LTS; the generic host gives DI, configuration from environment variables and
  structured logging without extra dependencies.
- **Alternatives**: .NET 8 LTS (older, EOL Nov 2026); NativeAOT (blocked by reflection in System.Text.Json
  source-gen gaps and P/Invoke-heavy code — revisit later).

## R2. Discord gateway (user account)

- **Decision**: In-house client over `System.Net.WebSockets.ClientWebSocket`, gateway v9 with the same
  identify payload shape `discord.py-self` sends for user accounts (properties, capabilities, presence), JSON
  via `System.Text.Json` source generation, no transport compression initially.
- **Rationale**: No .NET library supports user-account voice + go-live (Discord.Net, DSharpPlus, NetCord are
  bot-token only). The gateway surface the subset needs is small: HELLO/heartbeat, IDENTIFY/RESUME,
  READY, GUILD_CREATE (voice states, members), VOICE_STATE_UPDATE, VOICE_SERVER_UPDATE, MESSAGE_CREATE,
  STREAM_CREATE/SERVER_UPDATE/DELETE (ops 18–20), and op 4 voice state.
- **Alternatives**: Wrapping discord.py-self via a Python sidecar (rejected by user: in-house); zlib-stream
  compression (adds complexity, negligible gain for one account).

## R3. Voice + go-live transport

- **Decision**: Voice gateway v8 over `ClientWebSocket`; UDP via `System.Net.Sockets.UdpClient` with IP
  discovery; RTP packetisation for Opus (PT 120) and H.264 (PT 101, RFC 6184 single NAL + FU-A, MTU 1200);
  encryption modes `aead_xchacha20_poly1305_rtpsize` (preferred) and `xsalsa20_poly1305_lite` fallback;
  SPS VUI rewrite (`bitstream_restriction_flag=1`, `max_num_reorder_frames=0`) ported from
  `video_player.py`; video capability negotiation ported from `video_compat.py`.
- **Rationale**: Byte-for-byte parity with a pipeline proven to work against Discord today.
- **Alternatives**: WebRTC stacks (SIPSorcery) — Discord voice is not standard WebRTC signalling.

## R4. End-to-end encryption (DAVE)

- **Decision**: P/Invoke into **libdave's C API** (`cpp/includes/dave/dave.h`, `DAVESessionHandle`,
  encryptor handles, `DAVE_CODEC_OPUS` / `DAVE_CODEC_H264`) behind an `IDaveSession` abstraction. libdave is
  built as a shared library (`BUILD_SHARED_LIBS=ON`) in a dedicated Docker build stage and copied into the
  runtime image.
- **Rationale**: Same library the Python bot uses (via dave.py) and Discord's clients use; a C ABI makes
  P/Invoke straightforward. The abstraction lets unit tests use a fake.
- **Alternatives**: Re-implementing MLS + DAVE in C# (large, security-sensitive, rejected); extracting the
  native lib from the dave.py wheel (statically linked into a Python extension, no stable ABI).
- **Risk**: libdave build needs OpenSSL/BoringSSL and mlspp via vcpkg — first spike task validates it.

## R5. Codecs and crypto primitives

- **Decision**: Opus encode via P/Invoke to system `libopus`; secretbox/AEAD via P/Invoke to system
  `libsodium` (only `crypto_aead_xchacha20poly1305_ietf_encrypt` and `crypto_secretbox_easy` are needed).
  Thin hand-written bindings in one `Native` folder.
- **Rationale**: Same native libraries discord.py uses; two-to-four functions each don't justify a package.
- **Alternatives**: Concentus (managed Opus, slower); NSec/Sodium.Core packages (extra dependency surface).

## R6. Media pipeline

- **Decision**: FFmpeg child process exactly as `H264VideoPlayer._ffmpeg_cmd` builds it (H.264 Annex-B on
  stdout, s16le PCM to a named FIFO), encoder detection NVENC → VA-API → libx264 → libopenh264 with `-bf 0`,
  fit-to-box `scale`/`scale_vaapi` expression with forced-aspect substitution, exact `-r`. Video pacing on a
  dedicated thread; audio sender follows the video clock (FR-054: per-segment settling ≤300 ms, persistent-drift only, restart handshake,
  stale-audio drain) as in `golive.py`.
- **Rationale**: These behaviours were debugged in PRs #4–#8; the rewrite must not regress them.
- **Alternatives**: FFmpeg.AutoGen in-process (complex native interop, no benefit).

## R7. Jellyfin and SyncPlay

- **Decision**: `HttpClient` (typed client) for REST; `ClientWebSocket` for `/socket`; models with
  `System.Text.Json`. Endpoints/payloads identical to `services/jellyfin.py` and `services/syncplay.py`.
- **Rationale**: Straight port; behaviour already reviewed.
- **Alternatives**: Jellyfin.Sdk NuGet (generated, heavy, user-session assumptions).

## R8. Testing and coverage

- **Decision**: xUnit v3 + `coverlet.collector`; fakes for Discord/Jellyfin/FFmpeg seams; protocol encoders
  (RTP, SPS rewrite, crypto framing, time parsing) tested with fixed vectors produced by the Python
  implementation. Coverage gate 90% lines excluding `Native/` P/Invoke shims and live-network adapters.
  Real-FFmpeg tests skip when `ffmpeg` is absent (reported, per constitution Principle IV).
- **Rationale**: Mirrors the Python suite's approach and gate.

## R9. CI and image publishing

- **Decision**: New `dotnet` job in `.github/workflows/tests.yml` (setup-dotnet 10.0.x, `dotnet test` with
  coverage gate) running alongside the Python job; `docker-publish.yml` gains a `.NET` image build tagged
  `dotnet` / `dotnet-sha-<sha>` from `develop`; the `latest` cut-over is a later, explicit change once the
  parity checklist passes (spec Clarifications).
- **Rationale**: Constitution Principle IV — the .NET suite becomes an established suite and must gate PRs.

## R10. Configuration and redaction

- **Decision**: Read the same env var names (`DISCORD_TOKEN`, `ALLOWED_USER_IDS`, `JELLYFIN_*`, `STREAM_*`,
  `TIMEZONE` unused by the subset) through a single options class with the Python defaults; a logging
  formatter/enricher applies the same redaction regexes as `redact.py` to every message and exception.
