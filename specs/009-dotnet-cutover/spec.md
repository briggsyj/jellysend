# Feature Specification: .NET edition becomes the only implementation

**Feature Branch**: `009-dotnet-cutover` (implemented on `001-dotnet-rewrite`, merged into `develop`)
**Status**: Implemented
**Input**: "the .net implementation is ready to be promoted to :latest and the old python implementation removed"

## Clarifications (2026-11-04)

- Merge `001-dotnet-rewrite` into `develop` (the merge publishes `:latest`).
- Move the .NET code from `dotnet/` to the repository root.
- No final Python rollback image.
- Stop publishing the `:dotnet` / `:dotnet-sha-*` tags; the mainline tags are the .NET image.
- Delete the Python code, its tests, its CI jobs and its Dockerfile; keep the specs.
- Accept T013 (spike run), the parity checklist and the soak run on the maintainer's say-so.

## Breaking change (constitution Principle I, approved by the maintainer)

Removed with the Python bot: TVheadend live TV and EPG search, IPTV/M3U sources, yt-dlp / URL playback,
`!hello`, and the saved IPTV source data. Their environment variables are ignored. Spec 008 also replaced
`!media <title>` / `!media search` / `!media resume` with `!play` / `!time`. Upgrade path: none for the removed
features; pin an older `sha-*` image of the Python bot if they are still needed.

## Requirements

- **FR-001** The repository root holds `Slopsoil.slnx`, `src/`, `tests/`, `ci/test.sh`, `Dockerfile`.
- **FR-002** `docker-publish.yml` builds the root `Dockerfile`; a merged PR into `develop` publishes `latest` and `sha-*`, version tags publish releases; the test job runs `ci/test.sh` first.
- **FR-003** `tests.yml` runs `ci/test.sh` through `.github/actions/unit-tests`.
- **FR-004** README, `.env.example`, `docker-compose.yml` and the constitution describe only the .NET bot.
