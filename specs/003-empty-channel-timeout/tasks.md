# Tasks: Empty voice channel timeout

- [X] T001 `EMPTY_CHANNEL_TIMEOUT_MINUTES` in `SlopsoilOptions` (default 20, 0–1440, warn + default on invalid) with tests; row in `specs/001-dotnet-rewrite/contracts/configuration.md`
- [X] T002 `VoiceModule`: countdown per guild (start when the bot's channel empties, cancel on a join, on leave, restart on re-empty, start when the bot is moved into an empty channel; expiry re-checks and stops + leaves; 0 = immediate) with tests in `dotnet/tests/Slopsoil.Tests/Commands/VoiceModuleTests.cs`
- [X] T003 Host passes the option; parity checklist row V6 and README updated
