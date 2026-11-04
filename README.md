![](images/slopsoil-banner.png)  
![](https://github.com/dev-topsoil/slopsoil/actions/workflows/tests.yml/badge.svg) ![.NET](https://img.shields.io/badge/.NET-10-512bd4.svg) [![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-orange?logo=buy-me-a-coffee)](https://www.buymeacoffee.com/devtopsoil)  
# Slopsoil - Stream Jellyfin directly to Discord Voice Channels

**Slopsoil** is a Discord self-bot that streams movies and TV from a Jellyfin server into a Discord voice channel, as a screenshare that everyone in the channel can watch together.

It searches your library, plays the original file and encodes it once with hardware-accelerated H.264 (VA-API or NVIDIA NVENC), keeps audio and video on one clock, and lets Jellyfin apps join in through SyncPlay.

> **Disclaimer:**   
> Slopsoil is a self-bot. It runs on a real Discord user account, not a bot application.  
> Self-bots violate Discord's [Terms of Service](https://discord.com/terms).  
> Use it on an account you are willing to lose, and do not use it in a way that disrupts other users or servers.  
> The authors take no responsibility for account terminations or other consequences.

> **Keywords:** discord jellyfin bot, stream jellyfin to discord, discord watch party, discord voice channel video, self-bot streaming

---

## Table of Contents

- [Screenshots](#screenshots)
- [Features](#features)
- [How It Works](#how-it-works)
- [Installation - Docker Compose](#installation---docker-compose)
- [Configuration](#configuration)
- [Commands](#commands)
- [Updating](#updating)
- [Hardware Acceleration](#hardware-acceleration)
- [Development](#development)

---

## Screenshots

| Picker | Stream Running |
|---|---|
| ![Numbered picker in a Discord channel](images/slopsoil4.png) | ![Bot streaming video in a Discord voice channel](images/slopsoil3.png) |

---

## Features

- **Jellyfin search and play** - movies, series, seasons and episodes, with numbered pickers; casual arguments such as `!play the office s2e3 10m`
- **Transcode once** - the bot plays the original file and encodes it itself; Jellyfin transcodes only to burn in subtitles
- **Subtitles and audio tracks** - pick by number or language (`!subs english`, `!audio japanese`); the choice is remembered by language
- **Source frame rate** - streams follow the film's own frame rate (23.976 stays 23.976) up to `STREAM_FPS`
- **One clock for audio and video** - a read-ahead buffer keeps them in sync, even for high-bitrate files
- **Playlist** - queue up to 10 items; they play in order
- **Jellyfin SyncPlay** - Jellyfin app users can pause, play and seek the Discord stream together
- **Go-live / screenshare delivery** - streams appear as a screenshare so all members in the channel can watch
- **H.264 hardware acceleration** - auto-detects NVIDIA NVENC, VA-API (Intel/AMD), or falls back to software encoding
- **Discord DAVE E2EE support** - handles Discord's end-to-end encryption for voice channels
- **Auto-leave** - stops and leaves after the voice channel has been empty for a while (default 20 minutes)
- **Debug overlay** - `!debug on` burns frame rate, A/V drift, buffer and network stats into the stream
- **Role-based access control** - admin, friend, viewer and none tiers; friends list and guild membership are used automatically

---

## How It Works

slopsoil is a .NET 10 application with its own Discord user-account gateway, voice and go-live clients. It sends H.264 video and Opus audio over Discord's voice UDP protocol, appearing to other users as a screenshare. The pipeline does SPS/VUI rewriting, RFC 6184 FU-A fragmentation, `aead_xchacha20_poly1305_rtpsize` transport encryption, and DAVE E2EE through Discord's [libdave](https://github.com/discord/libdave) (built into the image).

FFmpeg decodes the Jellyfin stream and encodes H.264; both video and audio are read ahead into buffers (`STREAM_BUFFER_SECONDS`) and played out against a single clock, so a slow read never pulls them apart.

| Project | Role |
|---|---|
| `src/Slopsoil.Common` | configuration, log redaction, permissions |
| `src/Slopsoil.Discord` | gateway, voice, go-live, RTP, crypto, DAVE |
| `src/Slopsoil.Services` | Jellyfin and SyncPlay clients |
| `src/Slopsoil.Streaming` | FFmpeg video player, audio sender, stream engine |
| `src/Slopsoil.Commands` | Discord commands |
| `src/Slopsoil.Host` | entry point |

Design history and specs live in [`specs/`](specs/).

---

## Installation - Docker Compose

Images are published to `ghcr.io/briggsyj/slopsoil`:

| Tag | What |
|---|---|
| `latest` | newest build of `develop` |
| `sha-<short sha>` | a pinned build |
| `1.2.3`, `1.2`, `1` | releases |

### 1. Create a `docker-compose.yml`

```yaml
services:
  slopsoil:
    image: ghcr.io/briggsyj/slopsoil:latest
    restart: unless-stopped
    env_file: .env
    devices:
      - /dev/dri:/dev/dri   # VA-API hardware encoding (Intel/AMD)
```

### 2. Create your `.env`

Copy [`.env.example`](.env.example) and fill in at least `DISCORD_TOKEN` and the Jellyfin settings.

### 3. Pull and start

```bash
docker compose pull && docker compose up -d
```

### Build from source

```bash
git clone https://github.com/briggsyj/slopsoil.git && cd slopsoil
docker compose up -d --build
```

### View logs

```bash
docker compose logs -f slopsoil
```

### Publishing your own image (forks)

The **Publish Docker image** workflow pushes to `ghcr.io/<owner>/<repo>` with the built-in `GITHUB_TOKEN`, so a fork publishes its own image with no setup. Set the `DOCKERHUB_USERNAME` repository variable and `DOCKERHUB_TOKEN` secret to also push to Docker Hub.

---

## Configuration

All settings are environment variables (see [`.env.example`](.env.example)). Invalid values fall back to the default with a warning in the log.

| Variable | Required | Default | Description |
|---|---|---|---|
| `DISCORD_TOKEN` | Yes | — | Your Discord account token (not a bot token). Never logged. |
| `ALLOWED_USER_IDS` | No | empty | Comma-separated Discord user IDs with admin access. |
| `JELLYFIN_URL` | For playback | — | Jellyfin server URL, e.g. `http://192.168.1.100:8096`. |
| `JELLYFIN_API_KEY` | Key or login | — | Jellyfin API key. |
| `JELLYFIN_USERNAME` / `JELLYFIN_PASSWORD` | No | — | Sign in as a Jellyfin user; enables SyncPlay group watch. |
| `JELLYFIN_DIRECT_STREAM` | No | `true` | Play the original file and transcode once in the bot. `false` always uses Jellyfin's transcode. |
| `STREAM_QUALITY` | No | `1080p` | Preset: `720p` (30 fps, 10000k), `1080p` (60 fps, 12000k) or `4k` (60 fps, 24000k). Discord caps resolution/fps per account. |
| `STREAM_RESOLUTION` | No | preset | Bounding box `W:H` (or `WxH`). |
| `STREAM_FPS` | No | preset | Frame-rate **ceiling**, 1–60. Each stream follows the source rate at or below it; faster sources are divided evenly (60 → 30, 50 → 25). |
| `STREAM_VIDEO_BITRATE` | No | preset | Constant bitrate, e.g. `7000k`. |
| `STREAM_BUFFER_SECONDS` | No | `5` | Read-ahead of video and audio, 0–30. More smooths out slow reads; it delays the start by the same amount. |
| `STREAM_PACKET_PACE` | No | `0.0` | Spread each frame's packets over this fraction of the frame interval (0.0–0.95). Can reduce freezes on bursty networks. |
| `STREAM_AV_SYNC_MS` | No | `0` | Lip-sync offset, ±5000 ms. Positive plays audio earlier. |
| `EMPTY_CHANNEL_TIMEOUT_MINUTES` | No | `20` | Stop and leave after the voice channel stayed empty this long, 0–1440 (0 = immediately). |

Variables the old Python bot used (`TVHEADEND_*`, `YTDLP_FORMAT`, `TIMEZONE`) are ignored, so an existing `.env` keeps working.

### Finding your Discord token

1. Open Discord in a browser
2. Open DevTools (F12) → Network tab
3. Filter for requests to `discord.com/api`
4. Look for the `Authorization` header on any request - that value is your token

> **Security:** Keep your token private. Anyone with your token can access your Discord account.

---

## Commands

Every command works as a short form (`!play`) and under `!media` (`!media play`). Names are case-insensitive. `!help <command>` shows the details of one command.

### Play

| Command | Description |
|---|---|
| `!play <title>` | Search and play, e.g. `!play inception` |
| `!play <show> s03e05` | An episode (also `3x5`, `season 3 episode 5`, `episode 5 season 3`) |
| `!play <show>` | Pick season → episode |
| `!play <show> s03` | Pick an episode of season 3 (also `season 3`) |
| `!play <title> 1:23:45` | Start at a time (also `45m`, `90s`, `1h30m`) |
| `!play "<title>"` | Quotes: read it all as the title |

Arguments are recognised by their format, in any order. If they find nothing, the whole text is searched as a title.

### Control

| Command | Description |
|---|---|
| `!play` | Resume, or start the playlist |
| `!pause` | Pause |
| `!time` | Show the position; `!time 1:23:45` jumps (`1:23` = 1 h 23 min). `!play 1:23:45` also jumps while something plays. |
| `!skip` | Next playlist item |
| `!stop` | Stop, stay in voice |
| `!leave` | Stop and leave the voice channel |
| `!join` | Join your voice channel |
| `!subs` / `!sub` / `!subtitles` | Pick subtitles, or `!subs english`, `!subs 2`, `!subs off` |
| `!audio` | Pick the audio track, or `!audio japanese`, `!audio jpn` |
| `!ar` / `!aspect` / `!aspect-ratio` | Picture shape: `!ar 16:9`, `!ar widescreen`, `!ar scope`, `!ar original` |
| `!res` / `!resolution` | Output resolution: `!res 360p`, `480p`, `540p`, `720p`, `1080p`, `!res default`. Lower resolutions also lower the bitrate; applies now and to later items. |
| `!debug` | Stream stats; `!debug on` / `off` toggles the overlay |

Pickers accept a number or a name, and time out after 30 seconds.

### Playlist

| Command | Description |
|---|---|
| `!queue <title>` | Add to the playlist (max 10, plays from the start); `!queue <show> s01e02` |
| `!playlist` | What's playing and up next |
| `!order <# or title> <new #>` | Move an item |
| `!remove <# or title>` | Remove an item |

### General

| Command | Description |
|---|---|
| `!help` | All commands; `!help <command>` for one |
| `!ping` | Is the bot alive? |

#### Jellyfin SyncPlay (group watch)

With `JELLYFIN_USERNAME` / `JELLYFIN_PASSWORD` set, the bot opens a SyncPlay group named after the Discord server. Join it in a Jellyfin app to pause, play and seek the Discord stream together.

### Permission tiers

| Role | Who qualifies |
|---|---|
| **Admin** | User IDs listed in `ALLOWED_USER_IDS` |
| **Friend** | Users on the bot account's friends list — can use every command |
| **Viewer** | Members of any guild the bot account is in — `!help` and `!ping` |
| **None** | Everyone else |

A command run by someone whose role doesn't allow it gets the reply `_quack_ no.`

---

## Updating

```bash
docker compose pull && docker compose up -d
```

Built from source:

```bash
git pull && docker compose up -d --build
```

---

## Hardware Acceleration

slopsoil picks the best working H.264 encoder in this order:

| Encoder | Type | Requires |
|---|---|---|
| `h264_nvenc` | NVIDIA GPU | NVIDIA driver + `nvidia-container-toolkit` |
| `h264_vaapi` | VA-API (Intel/AMD) | `/dev/dri` device |
| `libopenh264` | Cisco software | Included in the image |

### VA-API in Docker

Map the render device in `docker-compose.yml`:

```yaml
devices:
  - /dev/dri:/dev/dri
```

### NVIDIA NVENC in Docker

Install [nvidia-container-toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html), then add:

```yaml
deploy:
  resources:
    reservations:
      devices:
        - driver: nvidia
          count: 1
          capabilities: [gpu]
```

### Why libx264 isn't used

The image is based on Fedora's `ffmpeg-free`, which does not include `libx264`. More importantly, `libx264`-encoded streams cause Discord to silently drop the stream after the first frame; `libopenh264` and the hardware encoders do not.

---

## Development

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), `libsodium` and `libopus` (Debian/Ubuntu: `libsodium23 libopus0`), and FFmpeg for the end-to-end tests.

```bash
ci/test.sh
```

`ci/test.sh` builds the solution, runs the tests with coverage and fails below 90 % line coverage. CI runs the same script. The long-run soak harness is described in [`tests/soak/README.md`](tests/soak/README.md).

Features are planned with [Spec Kit](https://github.com/github/spec-kit): specs, plans and tasks live in [`specs/`](specs/), and the project principles in [`.specify/memory/constitution.md`](.specify/memory/constitution.md).
