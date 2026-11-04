# Slopsoil Constitution

## Core Principles

### I. Public API Compatibility

Changes MUST preserve every public interface listed under Project Surfaces and Layers. Existing
commands, arguments, environment variables, and persisted data MUST keep working with their
documented behavior. New capabilities MUST be additive, and new settings MUST have defaults that
leave existing deployments unchanged. A breaking change requires explicit maintainer approval,
recorded in the feature plan with its justification and upgrade path, before implementation.

Rationale: operators run published images against existing `.env` files and saved data, so an
upgrade must not silently break a working deployment.

### II. Service Boundaries

Changes MUST follow the existing layers and their responsibilities. New code MUST be placed in the
layer that owns the responsibility, and MUST NOT add a dependency that crosses a boundary in a new
direction. Moving a responsibility between layers or changing the permitted dependencies requires
justification in the feature plan.

Rationale: the layers keep external clients and the streaming pipeline testable without a live
Discord session.

### III. Reversible Database Migrations

Every database migration MUST include a rollback plan before it is merged. The plan MUST state how
to restore the previous schema and data, and what data, if any, the rollback cannot recover.

Slopsoil has no database today, so this principle takes effect when one is introduced.

Rationale: a schema change that cannot be undone turns a failed upgrade into data loss.

### IV. Established Test Suites

Before a change is merged, the repository's established unit and integration test suites MUST be
run and MUST pass. Results MUST be reported, including any suite or test that was skipped or could
not be run. Changes MUST NOT bypass a suite or lower its coverage gate to get a passing result.

Slopsoil has no separate integration suite today, so the unit suite is the required gate until one
is established. Any integration suite added later becomes required under this principle.

Rationale: CI and local runs share one entry point, so a passing result means the same everywhere.

## Project Surfaces and Layers

Public interfaces covered by Principle I:

- Discord commands, their arguments, and their documented behavior (`specs/001-dotnet-rewrite/contracts/commands.md`).
- Environment variables documented in `.env.example` and `README.md`.
- Published container images and the `docker-compose.yml` deployment.

Internal .NET types and helper methods are not public interfaces.

Layers covered by Principle II (enforced by project references):

- `src/Slopsoil.Host`: process entry point and startup wiring.
- `src/Slopsoil.Commands`: Discord commands and user-facing interaction; may use every other project.
- `src/Slopsoil.Services`: Jellyfin and SyncPlay clients; MUST depend only on `Slopsoil.Common`.
- `src/Slopsoil.Streaming`: the FFmpeg video and audio pipeline and stream engine; MUST depend only on
  `Slopsoil.Discord` and `Slopsoil.Common`.
- `src/Slopsoil.Discord`: gateway, voice, go-live, RTP, transport crypto and DAVE; MUST depend only on
  `Slopsoil.Common`.
- `src/Slopsoil.Common`: configuration, redaction and permissions shared by all layers.

## Development Workflow

- Run the unit suite with `ci/test.sh`, the same entry point CI uses.
- Line coverage MUST remain at or above the `COVERAGE_MIN` default in `ci/test.sh`, currently 90%.
- Tests that need FFmpeg or `libdave` are skipped when they are absent; such skips MUST be reported
  under Principle IV.
- Feature plans MUST state which public interfaces and layers they touch, and whether they include
  a database migration.
- Commit messages and pull request titles MUST follow
  [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/). A breaking change
  approved under Principle I MUST be marked with `!` or a `BREAKING CHANGE:` footer. Commits made
  before version 1.1.0 of this constitution are not rewritten.

## Governance

This constitution takes precedence over conflicting Spec Kit artifacts for this repository.
`AGENTS.md` files continue to govern agent working practices where they do not conflict.

Amendments MUST be made by a reviewed change to this file that states the reason and updates the
version and Last Amended date.

Versioning follows semantic versioning:

- MAJOR: a principle is removed or redefined in a backward-incompatible way.
- MINOR: a principle or section is added, or guidance is materially expanded.
- PATCH: clarifications and wording changes with no change in meaning.

Every feature plan and pull request review MUST check compliance with these principles. Any
exception MUST be justified in the feature plan and approved by a maintainer.

**Version**: 1.2.0 | **Ratified**: 2026-11-03 | **Last Amended**: 2026-11-04 (the Python bot was replaced by the .NET edition, spec 009)
