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
| `STREAM_FPS` | no | preset | 1–60, fractional allowed |
| `STREAM_VIDEO_BITRATE` | no | preset | e.g. `2000k` |
| `STREAM_PACKET_PACE` | no | `0.0` | 0.0–0.95 |
| `STREAM_AV_SYNC_MS` | no | `0` | ±5000, offset the follower holds |
| `TVHEADEND_*`, `YTDLP_FORMAT`, `TIMEZONE` | — | — | accepted and ignored in this edition |

# Contract: Container image

- Image `ghcr.io/briggsyj/slopsoil`; .NET builds tagged `dotnet`, `dotnet-sha-<short sha>` until cut-over,
  then `latest` (spec Clarifications). Same volume (`/root/.local/share/slopsoil`, unused by the subset) and
  `/dev/dri` device mapping as today so `docker-compose.yml` needs only the tag change.
