# Feature Specification: Loading and pause cards

**Feature Branch**: `015-loading-pause-cards` (implemented on `claude/busy-meitner-7979sz`)
**Status**: Implemented
**Input**: "When the stream is loading, display BBC test card f … When the stream is paused, display this image on stream"

## Decisions

- The images are not committed or baked into the public image (BBC / stock-library copyright): they are configured
  with `STREAM_LOADING_IMAGE` and `STREAM_PAUSED_IMAGE` (file path or http(s) URL, downloaded once at startup).
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
- [x] T003 Options + host download (User-Agent), README / `.env.example` / configuration contract
- [x] T004 Tests: player timing with fake clips, encoder arguments and caching, real FFmpeg encode
