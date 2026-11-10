# Quickstart / Validation: .NET Rewrite — Jellyfin-First Edition

## Prerequisites

- .NET SDK 10.0.1xx (`dotnet --list-sdks`)
- For local runs: `ffmpeg`, `libopus`, `libsodium`, and a libdave shared library (or use the container)
- A Jellyfin server and a Discord account/server you can test on (same `.env` as the Python bot)

## Build and unit tests

```bash
cd dotnet && dotnet test --collect:"XPlat Code Coverage"
```

Expected: all tests pass; coverage gate ≥ 90% (excluding `Native/` and live adapters); FFmpeg-dependent
tests reported as skipped when ffmpeg is absent.

## Run in a container

```bash
docker build -f dotnet/Dockerfile -t slopsoil:dotnet .
```

```bash
docker run --rm --env-file .env --device /dev/dri slopsoil:dotnet
```

Or set `SLOPSOIL_IMAGE=ghcr.io/briggsyj/slopsoil:dotnet` in `.env` and `docker compose up -d`.

## Validation scenarios (map to spec)

| # | Scenario | Expected |
|---|---|---|
| 1 | Spike: start with `SLOPSOIL_SPIKE=testpattern`, `!join` in a DAVE-enabled channel | test-pattern screenshare + tone visible/audible to another client |
| 2 | US1: `!media <movie>` / `!media <show> s01e02` / series picker | picture + audio within 15 s (SC-001) |
| 3 | US2: pause, play, `resume 1:00:00`, `aspect-ratio 3`, `stop`, last human leaves | screenshare never drops; A/V aligned (SC-002/003) |
| 4 | US3: queue 3, `order`, `remove`, `skip`, let one finish | plays in order without commands |
| 5 | US4: join SyncPlay group from Jellyfin web, pause/seek both ways | other side follows ≤ 2 s |
| 6 | US5: same `.env`, swap tag Python ↔ dotnet | works both ways; `grep -Ei "api_key=|token=" logs` finds nothing unredacted |
| 7 | Parity: run [contracts/commands.md](contracts/commands.md) rows against both bots | identical replies (SC-004) |
