# Feature Specification: Loading and pause cards

**Feature Branch**: `015-loading-pause-cards` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "When the stream is loading, display BBC test card f … When the stream is paused, display this image on stream"

## Decisions

- The cards live in the repo under `assets/cards/` (`loading.*`, `paused.*`, credited in its README) and are copied into
  the image as `/app/cards/`; the user picked a Creative Commons test card (IRIB, FuBK version) instead of the BBC /
  stock images first suggested. `STREAM_LOADING_IMAGE` / `STREAM_PAUSED_IMAGE` override with a local path or `none`.
  Nothing is downloaded at startup.
- Each image is encoded once per encoder setup and frame rate into a 2 s looping H.264 clip with a keyframe every
  second (SPS/PPS on every keyframe), with the stream's own encoder, box and bitrate; 4:4:4 / RGB images are converted
  to 4:2:0 for the software encoders.
- **Loading**: while a segment's read-ahead fills (start, and after a jump), the card is sent at the stream's frame rate.
- **Paused**: while paused, the card is sent at the frame rate. On resume the card continues until the film's next
  keyframe (≤ ~1 s), which is sent with the film's SPS/PPS, so the picture never decodes against the card.
- Cards share the RTP clock but not the film's frame count: position, the A/V anchor and diagnostics are unchanged;
  `FirstFrameSent` still means the first film frame. A card that is not encoded yet is skipped (plain wait).

## Tasks

- [x] T001 `StillClip` (encode, parameter sets, loop), `StillCursor`, `StillImages` cache
- [x] T002 `VideoPlayer`: loading card during pre-roll, pause card while held, keyframe switch-back
- [x] T003 Options + bundled cards (`assets/cards`, Dockerfile), README / `.env.example` / configuration contract
- [ ] T005 Add `assets/cards/loading.jpg` (IRIB FuBK test card) and a `paused.*` card with their licences
- [x] T004 Tests: player timing with fake clips, encoder arguments and caching, real FFmpeg encode
