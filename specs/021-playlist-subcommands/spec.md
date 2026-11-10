# Feature Specification: `!playlist add/remove/order` and the playlist menu

**Feature Branch**: `021-playlist-subcommands` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "make `!queue` into `!playlist add <title>`; `!playlist` should remain as is; `!remove` should be
`!playlist remove <number>`; `!order` should be `!playlist order`. When `!playlist` is used, give the user options to
reply to perform an action … then prompt for the arguments."

## Requirements

- **FR-001** `!playlist add <title>` behaves as `!queue` did (episodes, ranges, the episode picker, idle start).
- **FR-002** `!playlist remove <# or title>` and `!playlist order <# or title> <new #>` behave as `!remove` / `!order` did.
- **FR-003** `!playlist` shows the playlist as before, then a menu: `add` always, `remove` with at least one item,
  `order` with at least two. The invoking user replies with the word within 30 s; other chat is ignored; a timeout
  or a new command ends the menu silently.
- **FR-004** An action without its arguments (from the menu or typed bare) asks for them with an example; typed bare,
  remove/order list the playlist first. The prompt takes the user's next message; a command cancels it ("Cancelled."),
  30 s without an answer cancels it ("⌛ No answer — cancelled.").
- **FR-005** `!queue`, `!remove` and `!order` still work, after "-# `!queue` is now `!playlist add`." (and alike).
  They are not listed in the help.
- **FR-006** Messages say "playlist" (not "schedule"), matching the command name.
