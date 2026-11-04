# Feature Specification: Command revision

**Feature Branch**: `008-command-revision` (implemented on `001-dotnet-rewrite`)
**Status**: Implemented
**Input**: revise the command list: `!play <title>` replaces `!media <title>`, short forms for everything, casual
arguments recognised by format, per-command help.

## Clarifications (2026-11-04)

- `!media play <title>` (shortcut `!play <title>`) does what `!media <title>` did; `!media <title>` and `!media search` are removed.
- Bare `!play` resumes, or starts the playlist; `!media resume` is removed; the jump is `!media time <time>` / `!time <time>`.
- Every `!media` subcommand gets a top-level short form of the same name.
- Episodes: `s<n>e<n>` (leading zeros), `<n>x<n>`, `season X episode Y`, `episode Y season X`, strictly and case-insensitive; `s<n>` / `season X` → season's episode picker; `episode X` alone is a title.
- Years are not parsed. Quotes are optional and force a title.
- Arguments are recognised by format in any order, with a full-text fallback search.
- A command that needs arguments, run without them, shows its own help; `!help <command>` added.
- `!media <unknown words>` shows the help, after a one-line hint for the removed forms.
- `!play <time>` jumps while something plays or is paused, else searches the text as a title.
- `!ar` / `!subs` / `!audio` take names (`widescreen`, `english`, `jpn`); without an argument (or with no match) they list the options to pick by number — the picker also takes names.
- No other bot shares the prefix: the prefix stays `!`.

## Requirements

- **FR-001** Each command in `CommandCatalog` is registered as `!<name>` and `!media <name>` with the same aliases and role.
- **FR-002** `PlayQuery.Parse` recognises the formats above; the remaining words are the title.
- **FR-003** When the parsed search finds nothing, the whole text is searched; a double miss reports the episode (`S02E03 of `x` not found`) or the whole text.
- **FR-004** `!queue` parses episodes but keeps time-like words in the title.
- **FR-005** `!order`, `!remove`, `!queue` without arguments reply with their help; `!help <name>` replies with any command's help.
- **FR-006** `!time` without arguments replies with the position, runtime and paused flag.
- **FR-007** Command names are case-insensitive.
- **FR-008** Commands without arguments ignore extra words (no more "title fall-through").
