# Tasks: .NET cut-over (spec 009)

- [x] T001 Delete the Python bot, tests, CI scripts, Dockerfile and docs
- [x] T002 Move `dotnet/` to the repository root (solution, `src/`, `tests/`, `ci/test.sh`, `Dockerfile`)
- [x] T003 CI: one image (root `Dockerfile`), `latest` from `develop`, no `:dotnet` tags; tests via `ci/test.sh`
- [x] T004 README, `.env.example`, `docker-compose.yml`, `.dockerignore`, `.gitignore`
- [x] T005 Constitution 1.2.0: .NET layers and workflow
- [x] T006 Spec 001: T013 / T043 done, parity sign-off, container contract
- [ ] T007 Merge `001-dotnet-rewrite` into `develop` and confirm the `latest` publish
