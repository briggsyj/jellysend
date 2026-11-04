# Soak run (T049, SC-008 / SC-009)

Streams a long local file through the real pipeline — FFmpeg, video pacing, A/V follower, RTP + transport
encryption — with the debug overlay on, **without Discord** (packets are encrypted and discarded), and writes one
diagnostics row per 10 s to a CSV. Run it on the reference hardware before the parity sign-off.

## 1. Get a 2-hour test file

Any 2-hour 1080p item from your library, or generate one (large; ~3 GB):

```bash
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=7200 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=7200 -c:v libx264 -preset veryfast -c:a aac -shortest soak.mp4
```

## 2. Run the soak

In the image (uses the same encoder detection, `/dev/dri` and `STREAM_*` settings as the bot):

```bash
docker run --rm --device /dev/dri -v "$PWD:/soak" -e SLOPSOIL_SOAK=1 -e SLOPSOIL_SOAK_FILE=/soak/soak.mp4 -e SLOPSOIL_SOAK_CSV=/soak/soak.csv -e STREAM_QUALITY=1080p -e STREAM_FPS=30 ghcr.io/briggsyj/slopsoil:latest
```

Or from a checkout: `SLOPSOIL_SOAK=1 SLOPSOIL_SOAK_FILE=soak.mp4 dotnet run --project src/Slopsoil.Host`.
`Ctrl+C` stops early; the CSV is flushed row by row.

## 3. Check the result

```bash
python3 tests/soak/check.py soak.csv
```

| Check | Spec | Computed from |
|---|---|---|
| ≥ 99.5 % of frames on time | SC-008 | `1 − Σ late_frames / Σ frames` (first window skipped) |
| no freeze > 0.5 s | SC-008 | max `max_lateness_ms` and `read_stall_ms` ≤ 500 |
| ≤ 1 audio correction per minute | SC-009 | `Σ (silence_steps + skip_steps) / minutes` |
| A/V offset within ±40 ms | SC-009 / SC-002 | max \|`drift_ms`\| |

Columns: `elapsed_s, position_s, window_s, frames, fps_sent, fps_target, late_frames, late_total_ms,
max_lateness_ms, read_stall_ms, packets_per_s, send_drops, send_errors, drift_ms, silence_steps, skip_steps, encoder`.
Record the `check.py` output in the parity checklist (row D4).
