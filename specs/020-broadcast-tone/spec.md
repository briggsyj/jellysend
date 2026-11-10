# Feature Specification: Retro-broadcast tone for every bot message

**Feature Branch**: `020-broadcast-tone` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "Retro broadcast is perfect", sentence case; soft problems friendly ("😅 Technical difficulties — … Don't
worry, this happens sometimes!"); hard problems say what to do next; no jargon in the channel.

## Requirements

- **FR-001** One leading emoji by kind: ▶️ / ⏸ / ⏹ / ➕ actions, 📡 working, 😅 soft problem the bot is handling,
  ❌ hard problem (with a next step), 🙅 user error, 🔎 not found.
- **FR-002** Sentence case and full stops on complete sentences.
- **FR-003** No exception text or internal terms (go-live, probe, transcode, H.264) in the channel; the logs keep them.
- **FR-004** Fallback to Jellyfin's transcode: "😅 Technical difficulties — that copy wouldn't play. Don't worry, this
  happens sometimes! Trying a backup feed now…", then "📺 Now showing: **X** (backup feed)".
- **FR-005** A dropped screenshare: "😅 Technical difficulties — the stream dropped. Don't worry, reconnecting (1 of 3)…".
- **FR-006** One message per ending: with an end callback the engine stays quiet and the media layer posts either the
  recovery (FR-004/FR-005) or the failure (❌ / 📴) — never both.
