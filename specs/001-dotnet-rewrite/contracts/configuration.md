# Contract: Configuration (environment variables)

Same names, meanings and defaults as the Python bot (`README.md` → Configuration). Unknown/out-of-subset
variables are ignored without error so an existing `.env` works unchanged.

| Variable | Required | Default | Notes |
|---|---|---|---|
| `DISCORD_TOKEN` | yes | — | user-account token; never logged |
| `ALLOWED_USER_IDS` | no | empty | comma-separated admin ids |
| `JELLYFIN_URL` | for !media | — | trailing slash trimmed |
| `JELLYFIN_API_KEY` | one of key/login | — | |
| `JELLYFIN_USERNAME` / `JELLYFIN_PASSWORD` | no | — | enables SyncPlay; lazy sign-in |
| `STREAM_QUALITY` | no | `1080p` | presets 720p/1080p/4k |
| `STREAM_RESOLUTION` | no | preset | `W:H` / `WxH` bounding box |
| `STREAM_FPS` | no | preset | 1–60, fractional allowed. A **ceiling** (spec 007): each stream follows the source frame rate at or below it, and evenly divides faster sources (60 → 30, 50 → 25). Unknown source rate → this value. |
| `STREAM_VIDEO_BITRATE` | no | preset | e.g. `2000k` |
| `STREAM_PACKET_PACE` | no | `0.0` | 0.0–0.95 |
| `STREAM_AV_SYNC_MS` | no | `0` | ±5000; .NET: constant offset of the audio schedule on the shared playout clock (positive = audio earlier) |
| `STREAM_BUFFER_SECONDS` | no | `5` | .NET only (spec 004): read-ahead of FFmpeg's video and audio before/while playing (pre-roll at start and after each seek); 0–30, fractional allowed, 0 = no read-ahead; invalid → warning + 5 |
| `JELLYFIN_DIRECT_STREAM` | no | `true` | .NET only (spec 006): play the original file and transcode once in the bot; Jellyfin transcodes only for subtitles / non-file sources |
| `EMPTY_CHANNEL_TIMEOUT_MINUTES` | no | `20` | .NET only (spec 003): stop + leave after the voice channel stayed empty this long; 0–1440, 0 = immediately |
| `TVHEADEND_*`, `YTDLP_FORMAT`, `TIMEZONE` | — | — | accepted and ignored in this edition |

# Contract: Container image

- Image `ghcr.io/briggsyj/slopsoil`; .NET builds tagged `dotnet`, `dotnet-sha-<short sha>` until cut-over,
  then `latest` (spec Clarifications). Same volume (`/root/.local/share/slopsoil`, unused by the subset) and
  `/dev/dri` device mapping as today so `docker-compose.yml` needs only the tag change.
