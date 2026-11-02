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

- Discord commands, their arguments, and their documented behavior.
- Environment variables documented in `.env.example` and `README.md`.
- Published container images and the `docker-compose.yml` deployment.
- The saved source data managed by `services/sources.py`.

Internal Python modules and helper functions are not public interfaces.

Layers covered by Principle II:

- `bot.py`: process entry point and startup wiring.
- `cogs/`: Discord commands and user-facing interaction; may call `services/` and `streaming/`.
- `services/`: clients and parsers for external systems and source data; MUST NOT depend on
  `cogs/` or `streaming/`.
- `streaming/`: the go-live, video, and audio pipeline. Its one existing call into `cogs/`, from
  `streaming/engine.py` to `cogs/utils.py`, MUST NOT be extended to other cog modules.
- `permissions.py`, `redact.py`, `davey_compat.py`, `video_compat.py`: shared cross-cutting modules.

## Development Workflow

- Run the unit suite with `ci/test/run.sh`, the same entry point CI uses through
  `.github/actions/unit-tests`.
- Total coverage MUST remain at or above the `fail_under` value in `pyproject.toml`, currently 90%.
- Tests that need FFmpeg are skipped when it is absent; such skips MUST be reported under
  Principle IV.
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

**Version**: 1.1.0 | **Ratified**: 2026-11-03 | **Last Amended**: 2026-11-03
