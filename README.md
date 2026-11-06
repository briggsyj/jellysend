# JellySend - Stream Jellyfin directly to Discord Voice Channels

![Tests](https://github.com/briggsyj/jellysend/actions/workflows/tests.yml/badge.svg) ![.NET](https://img.shields.io/badge/.NET-10-512bd4.svg)

**JellySend** is a Discord self-bot that streams movies and TV from a Jellyfin server into a Discord voice channel, as a screenshare that everyone in the channel can watch together.

JellySend is a fork of [Slopsoil](https://github.com/dev-topsoil/slopsoil), rewritten in .NET. It is not the official Slopsoil project — see [Origin & credits](#origin--credits).

It searches your library, plays the original file and encodes it once with hardware-accelerated H.264 (VA-API or NVIDIA NVENC), keeps audio and video on one clock, and lets Jellyfin apps join in through SyncPlay.

> **Disclaimer:**   
> JellySend is a self-bot. It runs on a real Discord user account, not a bot application.  
> Self-bots violate Discord's [Terms of Service](https://discord.com/terms).  
> Use it on an account you are willing to lose, and do not use it in a way that disrupts other users or servers.  
> The authors take no responsibility for account terminations or other consequences.

> **Keywords:** discord jellyfin bot, stream jellyfin to discord, discord watch party, discord voice channel video, self-bot streaming

---

## Table of Contents

- [Features](#features)
- [How It Works](#how-it-works)
- [Installation - Docker Compose](#installation---docker-compose)
- [Configuration](#configuration)
- [Commands](#commands)
- [Updating](#updating)
- [Hardware Acceleration](#hardware-acceleration)
- [Development](#development)
- [Origin & credits](#origin--credits)
- [Licence](#licence)

---

## Features

- **Jellyfin search and play** - movies, series, seasons and episodes, with numbered pickers; casual arguments such as `!play example show s2e3 10m`
- **Transcode once** - the bot plays the original file and encodes it itself; Jellyfin transcodes only to burn in subtitles
- **Subtitles and audio tracks** - each film starts on the audio and subtitles Jellyfin picks for it (the bot's Jellyfin user's language and subtitle settings); pick others by number or language (`!subs english`, `!audio japanese`), remembered by language until the bot leaves
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

JellySend is a .NET 10 application with its own Discord user-account gateway, voice and go-live clients. It sends H.264 video and Opus audio over Discord's voice UDP protocol, appearing to other users as a screenshare. The pipeline does SPS/VUI rewriting, RFC 6184 FU-A fragmentation, `aead_xchacha20_poly1305_rtpsize` transport encryption, and DAVE E2EE through Discord's [libdave](https://github.com/discord/libdave) (built into the image).

FFmpeg decodes the Jellyfin stream and encodes H.264; both video and audio are read ahead into buffers (`STREAM_BUFFER_SECONDS`) and played out against a single clock, so a slow read never pulls them apart.

| Project | Role |
|---|---|
| `src/JellySend.Common` | configuration, log redaction, permissions |
| `src/JellySend.Discord` | gateway, voice, go-live, RTP, crypto, DAVE |
| `src/JellySend.Services` | Jellyfin and SyncPlay clients |
| `src/JellySend.Streaming` | FFmpeg video player, audio sender, stream engine |
| `src/JellySend.Commands` | Discord commands |
| `src/JellySend.Host` | entry point |

Design history and specs live in [`specs/`](specs/).

---

## Installation - Docker Compose

Images are published to `ghcr.io/briggsyj/jellysend`:

| Tag | What |
|---|---|
| `latest` | newest build of `develop` |
| `sha-<short sha>` | a pinned build |
| `1.2.3`, `1.2`, `1` | releases |

### 1. Create a `docker-compose.yml`

```yaml
services:
  jellysend:
    image: ghcr.io/briggsyj/jellysend:latest
    restart: unless-stopped
    env_file: .env
    volumes:
      - ./data:/data        # saved settings (each server's default voice channel)
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
git clone https://github.com/briggsyj/jellysend.git && cd jellysend
docker compose up -d --build
```

### View logs

```bash
docker compose logs -f jellysend
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
| `JELLYFIN_DIRECT_STREAM` | No | `true` | Play the original file and transcode once in the bot. Files the bot's FFmpeg can't decode (in the image: HEVC, E-AC-3 audio, 10-bit or interlaced H.264) automatically use Jellyfin's transcode. `false` always uses Jellyfin's transcode. |
| `STREAM_QUALITY` | No | `1080p` | Preset: `720p` (30 fps, 10000k), `1080p` (60 fps, 12000k) or `4k` (60 fps, 24000k). Discord caps resolution/fps per account. |
| `STREAM_RESOLUTION` | No | preset | Bounding box `W:H` (or `WxH`). |
| `STREAM_FPS` | No | preset | Frame-rate **ceiling**, 1–60. Each stream follows the source rate at or below it; faster sources are divided evenly (60 → 30, 50 → 25). |
| `STREAM_VIDEO_BITRATE` | No | preset | Constant bitrate, e.g. `7000k`. |
| `STREAM_BUFFER_SECONDS` | No | `8` | Read-ahead of video and audio, 0–30. More smooths out slow reads; it delays the start by the same amount. |
| `STREAM_PACKET_PACE` | No | `0.0` | Spread each frame's packets over this fraction of the frame interval (0.0–0.95). Can reduce freezes on bursty networks. |
| `STREAM_AV_SYNC_MS` | No | `0` | Lip-sync offset, ±5000 ms. Positive plays audio earlier. |
| `STREAM_LOADING_IMAGE` | No | bundled card | Image shown on the stream while it loads (start and after a jump). Default: the test card in [`assets/cards/`](assets/cards/), built into the image. A file path in the container overrides it; `none` turns it off. |
| `STREAM_PAUSED_IMAGE` | No | bundled card | Image shown on the stream while paused (default: the pause symbol in `assets/cards/`); same rules. |
| `MAX_STREAM_HOURS` | No | `12` | Longest a stream plays, counted from a `!play` (through auto-advance and skips). Then it stops, staying in voice and keeping the playlist; `!play` starts again. 0–168, 0 = no limit. |
| `EMPTY_CHANNEL_TIMEOUT_MINUTES` | No | `20` | Stop and leave after the voice channel stayed empty this long, 0–1440 (0 = immediately). |
| `DATA_DIR` | No | `/data` | Where saved settings (`settings.json`) are kept. Mount it as a volume (`./data:/data`) so they survive updates. |

### Logs

Everything goes to the container's stdout (`docker compose logs -f jellysend`). Each play attempt logs:
- **The start:** the mode (original file or Jellyfin transcode), codecs, audio track, source frame rate and start time.
- **FFmpeg:** the full FFmpeg command, any FFmpeg warnings or errors, when the audio FIFO opened, and when the first video frame was sent.
- **Voice and screenshare:** every voice-gateway event, labelled `[voice …]` or `[stream …]`, with Discord's close codes explained.
- **Self events:** the bot's own voice state, voice server and stream (`STREAM_*`) events.
- **Stats per step:** the `!debug` overlay (and the 10 s stats log line) shows each step of the chain:
  - `JELLYFIN`: `original file — not transcoded`, or `Jellyfin transcode (<why>): <codec, size, bitrate, fps, % done, hw or software>`. Jellyfin's own transcode stats are polled every 10 s; the reasons are subtitles burned in, an external audio track, a format the bot can't decode, the original failed to start, not a plain file, or direct streaming turned off.
  - `FFMPEG`: the bot's decode → encode for Discord, with its fps and speed. About 1.00× is healthy once the buffer is full; below 1× while the buffer drains means this step can't keep up. Dropped or duplicated frames are shown when there are any.
  - `STREAM`: the position, and whether the picture is encoded once (bot) or twice (Jellyfin + bot).
  - `VIDEO` / `BUFFER` / `NETWORK`: playout to Discord.
- **The end:** one line saying why the stream ended (reason, position, whether a frame was ever sent, and how the screenshare closed).

If a direct stream of the original file fails before its first frame, the bot retries it once through Jellyfin's transcode and keeps using the transcode for that item.

libdave's (DAVE encryption) debug output is hidden; set `Logging__LogLevel__JellySend.Dave=Debug` to see it. `Logging__LogLevel__Default=Debug` turns on debug logging everywhere.

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
| `!play <title>` | Search and play, e.g. `!play big buck bunny` |
| `!play <show> s03e05` | An episode (also `3x5`, `season 3 episode 5`, `episode 5 season 3`) |
| `!play <show>` | Pick season → episode |
| `!play <show> s03` | Pick an episode of season 3 (also `season 3`) |
| Episode picker: `2,5,8` or `3-6` | Pick several episodes in order. With `!play` the first plays now and the rest are queued to play next; with `!playlist add` they all go to the end of the playlist (max 10). |
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
| `!channel set` | List the voice channels the bot can reach; reply with a number to move there (what's on air carries on) |
| `!channel default` | Pick a server, then the voice channel the bot uses there when you're not in voice (saved in `DATA_DIR`) |
| `!language` / `!lang` | Audio + subtitles shortcut: `!language japanese english`, `!language english off` |
| `!subs` / `!sub` / `!subtitles` | Pick subtitles, or `!subs english`, `!subs 2`, `!subs off` |
| `!audio` | Pick the audio track, or `!audio japanese`, `!audio jpn` |
| `!ar` / `!aspect` / `!aspect-ratio` | Picture shape: `!ar 16:9`, `!ar widescreen`, `!ar scope`, `!ar original` |
| `!res` / `!resolution` | Output resolution: `!res 360p`, `480p`, `540p`, `720p`, `1080p`, `!res default`. Lower resolutions also lower the bitrate; applies now and to later items. |
| `!debug` | Stream stats; `!debug on` / `off` toggles the overlay |

Not in a voice channel? From a server channel, `!play` and `!join` use the bot's current voice channel, or else the server's default voice channel (`!channel default`), or else its first one (not the AFK one). They stop again if nobody joins within the empty-channel timeout (`EMPTY_CHANNEL_TIMEOUT_MINUTES`, default 20), with a note in that voice channel's chat.

Pickers accept a number or a name, and time out after 30 seconds.

### Playlist

| Command | Description |
|---|---|
| `!playlist` | What's playing and up next, numbered. Then reply `add`, `remove` or `order` to change it — the bot asks for the rest. |
| `!playlist add <title>` | Add to the end (max 10, plays from the start); `!playlist add <show> s01e02`, `s1e1-6` |
| `!playlist remove <# or title>` | Take an item off, e.g. `!playlist remove 2` |
| `!playlist order <# or title> <new #>` | Move an item, e.g. `!playlist order 3 1` (item 3 to the top) |

Leave out what comes after `add`, `remove` or `order` and the bot asks for it. The old `!queue`, `!remove` and `!order` still work and point to their new names.

### General

| Command | Description |
|---|---|
| `!help` | All commands; `!help <command>` for one; `!help all` for every command in detail |
| `!ping` | Is the bot alive? |

### Discord Events (screenings)

Schedule a **voice channel** event in Discord and list what to play in its description, under a line `Screenings` (any case, colon optional). Each item is written like `!play`: a title, an episode (`example show s1e2`) or several (`example show s1e1-3`), numbered, bulleted or one per line:

```
Our monthly movie night!

Screenings:
1. Big Buck Bunny
2. Example Show s1e1-3
```

- At the event's start time the bot starts the event and streams the screenings in the event's voice channel, taking over anything already playing (the rest of the playlist plays afterwards). Starting the event by hand in Discord starts them too.
- Only events created by someone allowed to use `!play` count.
- In that voice channel's chat it posts the schedule: when each item starts and when it all ends, shown in each person's own time zone.
- A title that matches several things is asked about in that chat; anyone allowed to `!play` can answer, until shortly before that item is due. Unanswered, the first match is used. The schedule is posted again after each pick. Titles that aren't found are skipped.
- Left alone, the bot plays the whole list, even if the channel is empty (the empty-channel timeout doesn't apply), then ends the event and leaves.
- Change the playlist during the event (`!play`, `!skip`, `!playlist add` / `remove` / `order`) and it's under manual control: the empty-channel timeout applies again, and after the last screening it carries on with the playlist instead of leaving. The event is still ended in Discord.
- The bot's account needs **Manage Events** in the server to start and end events (the screenings still run without it), and permission to post in the voice channel's chat.

#### Jellyfin SyncPlay (group watch)

With `JELLYFIN_USERNAME` / `JELLYFIN_PASSWORD` set, the bot opens a SyncPlay group named after the Discord server. Join it in a Jellyfin app to pause, play and seek the Discord stream together.

### Permission tiers

| Role | Who qualifies |
|---|---|
| **Admin** | User IDs listed in `ALLOWED_USER_IDS` |
| **Friend** | Users on the bot account's friends list — can use every command |
| **Viewer** | Members of any guild the bot account is in — `!help` and `!ping` |
| **None** | Everyone else |

A command run by someone whose role doesn't allow it gets a GIF in reply (a bare link, so Discord plays it inline).

---

## Updating

```bash
docker compose pull && docker compose up -d
```

Built from source:

```bash
git pull && docker compose up -d --build
```

### Upgrading from Slopsoil

The image, the compose service and the image variable were renamed. Use the new image `ghcr.io/briggsyj/jellysend` and service `jellysend` in your compose file, and rename `SLOPSOIL_IMAGE` to `JELLYSEND_IMAGE` in `.env` if you pin a build. Then remove the old container so two bots don't run on the same account:

```bash
docker compose pull && docker compose up -d --remove-orphans
```

---

## Hardware Acceleration

JellySend picks the best working H.264 encoder in this order:

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

---

## Origin & credits

JellySend is a fork of [Slopsoil](https://github.com/dev-topsoil/slopsoil) by topsoil ([dev-topsoil](https://github.com/dev-topsoil)) and its contributors, rewritten in .NET, November 2026. The git history keeps every original commit and its author.

This is a modified version of Slopsoil. It is not the official Slopsoil project and is not endorsed by its authors. If you enjoy it, you can also support Slopsoil's author: [![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-orange?logo=buy-me-a-coffee)](https://www.buymeacoffee.com/devtopsoil)

Copyright (C) 2026 John Briggs  
Slopsoil copyright (C) 2026 its authors.

## Licence

JellySend is free software, distributed under the GNU General Public License version 3, the same licence as Slopsoil — see [`LICENSE`](LICENSE). It comes with no warranty.
