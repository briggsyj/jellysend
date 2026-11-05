# Feature Specification: Help menu, `!help all` and `!language`

**Feature Branch**: `019-help-and-language` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: one command per line with proper term + plain wording; `!ar` clearly the aspect ratio; `!remove` / `!order`
explained with examples; `!help all` documents everything; `!language <audio> <subtitles>` shortcut.

## Requirements

- **FR-001** `!help` is one message (under Discord's limit) headed "📺 Slopsoil — now broadcasting", with sections
  Watch, While watching, Playlist, Picture & sound; one command per line, most-used first.
- **FR-002** `!help <command>` shows that command's full help (what it does, one usage line per form, examples).
- **FR-003** `!help all` sends every command in detail, one message per section, each under 1900 characters.
- **FR-004** `!language <audio> [<subtitles>|off]` (alias `!lang`) sets both tracks with one restart; one word changes
  the audio only; bare shows what is on now (or its help when nothing plays). The help calls it the
  "audio + subtitles shortcut".
