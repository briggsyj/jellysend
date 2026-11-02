from __future__ import annotations

import asyncio
import functools
import logging
import os
import re
import time
import urllib.error
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, cast

import discord
from discord.ext import commands

from cogs.utils import resolve_voice
from permissions import Role, require_role
from services.hls import extract_hls_variant_url as _extract_hls_variant_url
from services.jellyfin import JellyfinClient, PlaybackStream
from services.probe import probe_stream as _probe_stream
from services.syncplay import SyncPlayGroup
from streaming.engine import cancel_stream, start_live_stream

if TYPE_CHECKING:
    from bot import SlopSoil
    from streaming.video_player import H264VideoPlayer

log = logging.getLogger(__name__)

PLAYLIST_MAX = 10

# (label, forced display aspect ratio). None = keep the media's own ratio.
ASPECT_OPTIONS: list[tuple[str, float | None]] = [
    ("Original (default) — keep the media's own shape", None),
    ("16:9 — widescreen", 16 / 9),
    ("4:3 — classic TV", 4 / 3),
    ("2.39:1 — cinema scope (21:9)", 2.39),
    ("1.85:1 — theatrical widescreen", 1.85),
]
_SUPPORTED_CODECS = ("h264", "hevc", "mpeg2video", "mpeg4", "mjpeg")
# How far (seconds) a SyncPlay position may drift from ours before we seek.
_SYNC_DRIFT_TOLERANCE = 3.0
# Ignore SyncPlay PlayQueue echoes for items we queued ourselves this recently.
_SYNC_ECHO_WINDOW = 10.0
# After we send pause/unpause/seek, don't drift-correct on the echoed command:
# the server's position estimate lags ours by our stream start-up time.
_SYNC_OWN_COMMAND_WINDOW = 5.0

_EPISODE_RE = re.compile(r"\bs(\d+)e(\d+)\b", re.IGNORECASE)
_UNIT_TS_RE = re.compile(r"(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?")


def _parse_episode_query(query: str) -> tuple[str, int | None, int | None]:
    """Split 'show name s02e01' into ('show name', 2, 1). Returns (query, None, None) if no match."""
    m = _EPISODE_RE.search(query)
    if not m:
        return query, None, None
    show = query[: m.start()].strip()
    return show, int(m.group(1)), int(m.group(2))


def parse_timestamp(text: str) -> float | None:
    """Parse a jump-to time into seconds, or None if it isn't one.

    Accepted forms (hours first, seconds optional, ``:`` or ``.`` separators):
      ``1:23:45``  ``1.23.45``  ``1:23`` (1 h 23 m)  ``0:05`` (5 m)
      ``45`` (minutes)  and unit forms ``1h30m``, ``45m``, ``90s``.
    """
    t = text.strip().lower().replace(" ", "")
    if not t:
        return None
    if re.fullmatch(r"\d+(?:[:.]\d{1,2}){0,2}", t):
        parts = [int(p) for p in re.split(r"[:.]", t)]
        if any(p >= 60 for p in parts[1:]):
            return None
        if len(parts) == 1:
            return parts[0] * 60.0
        h, m = parts[0], parts[1]
        s = parts[2] if len(parts) == 3 else 0
        return float(h * 3600 + m * 60 + s)
    m = _UNIT_TS_RE.fullmatch(t)
    if m and any(m.groups()):
        h, mi, s = (int(g) if g else 0 for g in m.groups())
        return float(h * 3600 + mi * 60 + s)
    return None


def format_timestamp(seconds: float) -> str:
    total = max(0, int(seconds))
    h, rem = divmod(total, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}"


def _runtime_seconds(item: dict) -> float | None:
    ticks = item.get("RunTimeTicks")
    return ticks / 10_000_000 if isinstance(ticks, int) and ticks > 0 else None


def _fmt_item(item: dict) -> str:
    """Return a human-readable label for a Jellyfin item."""
    name = item.get("Name", "Unknown")
    kind = item.get("Type", "")
    year = item.get("ProductionYear")

    if kind == "Episode":
        series = item.get("SeriesName", "")
        season = item.get("ParentIndexNumber")
        ep = item.get("IndexNumber")
        ep_str = (
            f"S{season:02d}E{ep:02d}" if season is not None and ep is not None else ""
        )
        return f"{series} — {ep_str} — {name}" if ep_str else f"{series} — {name}"

    return f"{name} ({year})" if year else name


HELP_TEXT = """\
**🎬 Jellyfin — `!media`**

**Play**
`!media <movie>` — e.g. `!media inception`
`!media <show> s03e05` — a specific episode
`!media <show>` — pick season → episode

**Control**
`!media pause` · `!media play`
`!media resume 1:23:45` — jump to a time (`1:23` = 1h 23m; also `45m`, `90s`)
`!media skip` · `!media stop` (stops and leaves voice)
`!media aspect-ratio` — change the picture shape

**Playlist** (max 10, plays in order)
`!media queue <movie>` or `!media queue <show> s01e02`
`!media playlist`
`!media order <#> <new #>` · `!media remove <#>`

-# Title starts with queue, order or remove? Use `!media search <title>`."""

SYNCPLAY_HELP = (
    "\n\n**Jellyfin SyncPlay** — join **{group}** in a Jellyfin app to "
    "pause, play and seek this stream together."
)


@dataclass
class _NowPlaying:
    item: dict
    stream: PlaybackStream


@dataclass
class _GuildState:
    send: Callable
    queue: list[dict] = field(default_factory=list)
    now: _NowPlaying | None = None
    syncplay: SyncPlayGroup | None = None
    generation: int = 0
    # item id → monotonic time we last set it as the SyncPlay queue
    sync_sent: dict[str, float] = field(default_factory=dict)
    # monotonic time of our last pause/unpause/seek sent to the group
    sync_own_command_at: float = float("-inf")
    # Serialises stream starts (commands, auto-advance, SyncPlay switches) and
    # SyncPlay group creation so concurrent callers can't race each other.
    start_lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    sync_lock: asyncio.Lock = field(default_factory=asyncio.Lock)


class Jellyfin(commands.Cog):
    def __init__(self, bot: commands.Bot, client: JellyfinClient | None) -> None:
        self.bot = cast("SlopSoil", bot)
        self.client = client
        self._states: dict[int, _GuildState] = {}
        # guild id → forced aspect ratio; survives leaving voice (a preference)
        self._aspect: dict[int, float] = {}
        self._tasks: set[asyncio.Task] = set()  # keep background tasks alive

    def _spawn(self, coro) -> None:
        task = asyncio.create_task(coro)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    # ── Helpers ───────────────────────────────────────────────────────────────

    def _state(self, guild_id: int, send: Callable | None = None) -> _GuildState:
        state = self._states.get(guild_id)
        if state is None:
            state = _GuildState(send=send or (lambda *a, **k: asyncio.sleep(0)))
            self._states[guild_id] = state
        elif send is not None:
            state.send = send
        return state

    def _player(self, guild_id: int) -> H264VideoPlayer | None:
        return self.bot.video_players.get(guild_id)

    async def _require_client(self, ctx: commands.Context) -> bool:
        if self.client is None:
            await ctx.send(
                "Jellyfin is not configured. "
                "Set `JELLYFIN_URL` and `JELLYFIN_API_KEY` (or `JELLYFIN_USERNAME` / "
                "`JELLYFIN_PASSWORD`) in `.env` and restart the bot."
            )
            return False
        return True

    async def _context_guild(self, ctx: commands.Context) -> discord.Guild | None:
        """The guild a command applies to: where the author or the bot is in voice."""
        guild, _, _ = await resolve_voice(ctx)
        if guild is not None:
            return guild
        if ctx.guild is not None:
            return ctx.guild
        return next((g for g in self.bot.guilds if g.voice_client), None)

    async def _wait_for_number(self, ctx: commands.Context, max_val: int) -> int | None:
        """Wait up to 10 s for the user to reply with a number in [1, max_val].

        Returns None on timeout or if the user starts a new command.
        New commands are not swallowed — they still run through the normal pipeline.
        """
        def _check(msg) -> bool:
            return (
                msg.author.id == ctx.author.id
                and msg.channel.id == ctx.channel.id
                and (
                    msg.content.strip().isdecimal()
                    or msg.content.strip().startswith("!")
                )
            )

        try:
            reply = await self.bot.wait_for("message", check=_check, timeout=10.0)
        except TimeoutError:
            await ctx.send("timed out — cancelled")
            return None

        if reply.content.strip().startswith("!"):
            await ctx.send("cancelled")
            return None

        choice = int(reply.content.strip())
        if not 1 <= choice <= max_val:
            await ctx.send(f"invalid selection — pick a number between 1 and {max_val}")
            return None
        return choice

    # ── Item resolution (search → pick) ───────────────────────────────────────

    async def _resolve(self, ctx: commands.Context, query: str) -> dict | None:
        """Search Jellyfin and walk the user to one playable movie/episode."""
        assert self.client is not None
        show, season, episode = _parse_episode_query(query)
        log.info(
            "Jellyfin search for %r by %s (season=%s episode=%s)",
            show, ctx.author, season, episode,
        )

        try:
            if season is not None and episode is not None:
                results = await self.client.find_episode(show, season, episode)
            else:
                results = await self.client.search(show)
        except urllib.error.URLError as exc:
            log.exception("could not reach Jellyfin: %s", exc)
            await ctx.send(f"could not reach Jellyfin: {exc}")
            return None
        except Exception as exc:
            log.exception("Jellyfin search failed: %s", exc)
            await ctx.send(f"Jellyfin search failed: {exc}")
            return None

        if not results:
            if season is not None and episode is not None:
                await ctx.send(
                    f"S{season:02d}E{episode:02d} of `{show}` not found in Jellyfin"
                )
            else:
                await ctx.send(f"nothing found in Jellyfin for `{query}`")
            return None

        if len(results) == 1:
            item = results[0]
        else:
            lines = [f"Found {len(results)} result(s) for `{query}`. Pick a number:\n"]
            for i, it in enumerate(results, 1):
                lines.append(f"  `{i}` [{it.get('Type', '')}] {_fmt_item(it)}")
            await ctx.send("\n".join(lines))
            choice = await self._wait_for_number(ctx, len(results))
            if choice is None:
                return None
            item = results[choice - 1]

        if item.get("Type") == "Series":
            return await self._pick_season_then_episode(ctx, item)
        return item

    async def _pick_episode(
        self, ctx: commands.Context, series_id: str, series_name: str, season: dict
    ) -> dict | None:
        """List episodes for a season and ask the user to pick one."""
        assert self.client is not None
        try:
            episodes = await self.client.get_episodes(series_id, season["Id"])
        except Exception as exc:
            log.exception("failed to fetch episodes: %s", exc)
            await ctx.send(f"failed to fetch episodes: {exc}")
            return None

        if not episodes:
            await ctx.send(f"no episodes found for **{season.get('Name')}** of **{series_name}**")
            return None

        season_name = season.get("Name", "Unknown")
        lines = [f"**{series_name} — {season_name}**. Pick an episode:\n"]
        for i, ep in enumerate(episodes, 1):
            ep_num = ep.get("IndexNumber")
            ep_name = ep.get("Name", "Unknown")
            ep_str = f"E{ep_num:02d} — {ep_name}" if ep_num is not None else ep_name
            lines.append(f"  `{i}` {ep_str}")
        await ctx.send("\n".join(lines))

        choice = await self._wait_for_number(ctx, len(episodes))
        if choice is None:
            return None
        return episodes[choice - 1]

    async def _pick_season_then_episode(
        self, ctx: commands.Context, series: dict
    ) -> dict | None:
        """Fetch seasons for a series, prompt for one (or skip if only one), then pick an episode."""
        assert self.client is not None
        series_id = series["Id"]
        series_name = series.get("Name", "Unknown")

        try:
            seasons = await self.client.get_seasons(series_id)
        except Exception as exc:
            log.exception("failed to fetch seasons: %s", exc)
            await ctx.send(f"failed to fetch seasons: {exc}")
            return None

        if not seasons:
            await ctx.send(f"no seasons found for **{series_name}**")
            return None

        if len(seasons) == 1:
            return await self._pick_episode(ctx, series_id, series_name, seasons[0])

        lines = [f"**{series_name}** — {len(seasons)} seasons. Pick a season:\n"]
        for i, s in enumerate(seasons, 1):
            lines.append(f"  `{i}` {s.get('Name', f'Season {i}')}")
        await ctx.send("\n".join(lines))

        choice = await self._wait_for_number(ctx, len(seasons))
        if choice is None:
            return None
        return await self._pick_episode(ctx, series_id, series_name, seasons[choice - 1])

    # ── Playback ──────────────────────────────────────────────────────────────

    async def _play_item(
        self, ctx: commands.Context, item: dict, start: float = 0.0
    ) -> bool:
        """Play ``item`` in the command author's voice channel."""
        guild, voice_channel, vc = await resolve_voice(ctx)
        if not voice_channel:
            await ctx.send("you're not in a voice channel")
            return False
        assert guild is not None
        self._state(guild.id, ctx.send)
        return await self._start_item(guild, voice_channel, vc, item, start)

    async def _start_item(
        self,
        guild: discord.Guild,
        voice_channel: discord.VoiceChannel | discord.StageChannel,
        vc: discord.VoiceClient | None,
        item: dict,
        start: float = 0.0,
        sync: bool = True,
    ) -> bool:
        """Start a Jellyfin playback session for ``item`` and stream it.

        Serialised per guild. The generation is bumped up front so a stream
        that ends naturally while this start is still resolving/probing can't
        auto-advance the playlist underneath it.
        """
        state = self._state(guild.id)
        async with state.start_lock:
            state.generation += 1
            generation = state.generation
            had_player = self._player(guild.id) is not None
            started = await self._start_item_locked(
                guild, voice_channel, vc, item, start, sync, generation
            )
            if (
                not started
                and had_player
                and state.generation == generation
                and self._player(guild.id) is not None
            ):
                # Failed before replacing the current stream: let it keep
                # auto-advancing when it ends.
                state.generation = generation - 1
            return started

    async def _start_item_locked(
        self,
        guild: discord.Guild,
        voice_channel: discord.VoiceChannel | discord.StageChannel,
        vc: discord.VoiceClient | None,
        item: dict,
        start: float,
        sync: bool,
        generation: int,
    ) -> bool:
        assert self.client is not None
        state = self._state(guild.id)
        send = state.send

        label = _fmt_item(item)
        item_id = item.get("Id", "")
        log.info("Jellyfin: streaming '%s' (id: %s) from %.0fs", label, item_id, start)

        await send("checking stream…")

        # Ask Jellyfin to start a transcoding session and give us the HLS URL.
        stream = await self.client.start_playback(item_id)
        if stream is None:
            await send(
                f"could not start a Jellyfin playback session for **{label}** "
                "— check that the server is reachable and the credentials are valid"
            )
            return False

        stream_url = stream.url
        api_key = self.client._api_key
        safe_url = stream_url.replace(api_key, "***") if api_key else stream_url
        log.info("Jellyfin: stream URL: %s", safe_url)

        # Resolve HLS master playlist → highest-bandwidth variant.
        resolved_url = await _extract_hls_variant_url(stream_url)
        safe_resolved = resolved_url.replace(api_key, "***") if api_key else resolved_url
        if resolved_url != stream_url:
            log.info("Jellyfin: resolved variant URL: %s", safe_resolved)
        else:
            log.info("Jellyfin: using URL as-is (no HLS variants): %s", safe_resolved)

        if stream.has_audio is not None and stream.video_codec is not None:
            # PlaybackInfo already told us what we need — skip ffprobe, which
            # costs several seconds and makes Jellyfin spin up the transcode
            # twice (once for the probe, once for FFmpeg).
            codec = stream.video_codec
            has_audio = stream.has_audio
            log.info(
                "Jellyfin metadata: '%s' → codec=%s audio=%s", label, codec, has_audio
            )
        else:
            info = await _probe_stream(resolved_url)
            if info is None:
                await send(
                    f"could not probe the Jellyfin stream for **{label}** "
                    "— transcoding may have failed on the server"
                )
                await self.client.stop_encoding(stream.play_session_id)
                return False

            codec = info["codec"]
            has_audio = info.get("has_audio", True)
            log.info(
                "Jellyfin probe: '%s' → codec=%s fps=%.3f audio=%s",
                label, codec, info["fps"], has_audio,
            )

        if codec not in _SUPPORTED_CODECS:
            await send(f"unsupported video codec `{codec}` from Jellyfin for **{label}**")
            await self.client.stop_encoding(stream.play_session_id)
            return False

        if not has_audio:
            log.info("Jellyfin stream '%s' has no audio — injecting silence", label)

        previous = state.now
        state.now = _NowPlaying(item=item, stream=stream)

        await start_live_stream(
            self.bot,
            send,
            guild,
            voice_channel,
            vc,
            title=label,
            url=resolved_url,
            live=True,
            audio=has_audio,
            probe_size=10_000_000,
            start_offset=start,
            on_end=functools.partial(self._on_stream_end, guild.id, generation),
            aspect=self._aspect.get(guild.id),
        )

        if previous is not None and previous.stream is not stream:
            await self.client.stop_encoding(previous.stream.play_session_id)

        if self._player(guild.id) is None:
            # start_live_stream reported its own error (e.g. go-live failed).
            if state.generation == generation:
                state.now = None
            await self.client.stop_encoding(stream.play_session_id)
            return False

        if sync and self.client.has_user_credentials:
            # In the background: waits for the first frame so the group's
            # clock starts when Discord viewers actually see the item.
            self._spawn(self._sync_queue(guild, item_id, start, self._player(guild.id)))
        return True

    async def _on_stream_end(self, guild_id: int, generation: int) -> None:
        """A stream finished on its own: release the transcode, play the next item."""
        state = self._states.get(guild_id)
        if state is None or state.generation != generation:
            return
        finished, state.now = state.now, None
        if finished is not None and self.client is not None:
            await self.client.stop_encoding(finished.stream.play_session_id)
        await self._advance(guild_id)

    async def _advance(self, guild_id: int) -> bool:
        """Start the next playable playlist item in the bot's voice channel."""
        state = self._states.get(guild_id)
        guild = self.bot.get_guild(guild_id)
        if state is None or guild is None:
            return False
        while state.queue:
            vc = cast("discord.VoiceClient | None", guild.voice_client)
            if vc is None or not vc.is_connected():
                return False
            item = state.queue.pop(0)
            await state.send(f"⏭ up next: **{_fmt_item(item)}**")
            if await self._start_item(guild, vc.channel, vc, item):
                return True
        return False

    async def _stop_stream(self, guild: discord.Guild) -> None:
        """Stop the go-live stream (stays in voice) and release the transcode."""
        state = self._states.get(guild.id)
        if state is not None:
            state.generation += 1  # suppress auto-advance for the cancelled stream
            finished, state.now = state.now, None
            if finished is not None and self.client is not None:
                await self.client.stop_encoding(finished.stream.play_session_id)
        cancel_stream(self.bot, guild.id)
        live_conn = self.bot.live_connections.pop(guild.id, None)
        if live_conn is not None:
            try:
                await live_conn.disconnect()
            except Exception:
                log.warning("error disconnecting go-live on media stop", exc_info=True)

    async def _teardown(self, guild_id: int) -> None:
        """Forget a guild's session: playlist, SyncPlay group, transcode."""
        state = self._states.pop(guild_id, None)
        if state is None:
            return
        state.generation += 1
        if state.now is not None and self.client is not None:
            await self.client.stop_encoding(state.now.stream.play_session_id)
        if state.syncplay is not None:
            await state.syncplay.close()

    # ── SyncPlay ──────────────────────────────────────────────────────────────

    async def _ensure_syncplay(self, guild: discord.Guild) -> SyncPlayGroup | None:
        if self.client is None or not self.client.has_user_credentials:
            return None
        state = self._state(guild.id)
        if state.syncplay is not None and not state.syncplay.closed:
            return state.syncplay
        group = SyncPlayGroup(
            self.client,
            device_id=f"slopsoil-{guild.id}",
            on_command=functools.partial(self._on_sync_command, guild.id),
            on_play_queue=functools.partial(self._on_sync_queue, guild.id),
        )
        try:
            await group.open(f"slopsoil · {guild.name}")
        except Exception as exc:
            log.warning("SyncPlay: could not create group: %s", exc, exc_info=True)
            await group.close()
            return None
        state.syncplay = group
        await state.send(
            f"👥 Jellyfin SyncPlay group **{group.group_name}** is open — join it "
            "from a Jellyfin app to control playback together"
        )
        return group

    async def _sync_queue(
        self,
        guild: discord.Guild,
        item_id: str,
        start: float,
        player: H264VideoPlayer | None = None,
    ) -> None:
        if player is not None:
            # Start the group's clock from what Discord viewers actually see,
            # not from before the (possibly cold) transcode produced a frame.
            shown = await asyncio.to_thread(player.first_frame_sent.wait, 60.0)
            if self._player(guild.id) is not player:
                return  # replaced or stopped meanwhile
            if shown:
                start = player.position
        state = self._state(guild.id)
        async with state.sync_lock:
            group = await self._ensure_syncplay(guild)
        if group is None:
            return
        state.sync_sent[item_id] = time.monotonic()
        try:
            await group.set_queue(item_id, start)
        except Exception as exc:
            log.warning("SyncPlay: set queue failed: %s", exc)

    async def _sync_call(self, guild_id: int, action: str, *args) -> None:
        """Forward a local !media action to the SyncPlay group, if any."""
        state = self._states.get(guild_id)
        group = state.syncplay if state else None
        if group is None or group.closed:
            return
        state.sync_own_command_at = time.monotonic()
        try:
            await getattr(group, action)(*args)
        except Exception as exc:
            log.warning("SyncPlay: %s failed: %s", action, exc)

    async def _on_sync_command(
        self, guild_id: int, command: str, position: float | None
    ) -> None:
        """Apply a Pause/Unpause/Seek/Stop issued by another SyncPlay member.

        Our own requests echo back here too; they're no-ops because the player
        is already in the requested state / within the drift tolerance.
        """
        player = self._player(guild_id)
        state = self._states.get(guild_id)
        if player is None or state is None:
            return
        drift = abs(position - player.position) if position is not None else 0.0
        if time.monotonic() - state.sync_own_command_at < _SYNC_OWN_COMMAND_WINDOW:
            drift = 0.0  # echo of our own request: trust our position
        log.info(
            "SyncPlay %s at %s (local %.1fs)", command,
            "?" if position is None else f"{position:.1f}s", player.position,
        )
        if command == "Pause":
            if not player.paused:
                player.pause()
                await state.send(f"⏸ paused from Jellyfin at {format_timestamp(player.position)}")
        elif command == "Unpause":
            if position is not None and drift > _SYNC_DRIFT_TOLERANCE:
                player.seek(position)
            if player.paused:
                player.resume()
                await state.send("▶ resumed from Jellyfin")
        elif command == "Seek":
            if position is not None and drift > _SYNC_DRIFT_TOLERANCE:
                player.seek(position)
                await state.send(f"⏩ Jellyfin jumped to {format_timestamp(position)}")
        elif command == "Stop":
            guild = self.bot.get_guild(guild_id)
            if guild is not None:
                await self._stop_stream(guild)
                await state.send("⏹ stopped from Jellyfin")

    async def _on_sync_queue(
        self, guild_id: int, item_id: str | None, start: float, is_playing: bool
    ) -> None:
        """Another member picked a different item in the SyncPlay group: play it."""
        state = self._states.get(guild_id)
        if state is None or not item_id or self.client is None:
            return
        if state.now is not None and state.now.item.get("Id") == item_id:
            return
        sent_at = state.sync_sent.get(item_id)
        if sent_at is not None and time.monotonic() - sent_at < _SYNC_ECHO_WINDOW:
            return  # late echo of our own queue change
        guild = self.bot.get_guild(guild_id)
        vc = cast("discord.VoiceClient | None", guild.voice_client) if guild else None
        if guild is None or vc is None or not vc.is_connected():
            return
        item = await self.client.get_item(item_id)
        if item is None:
            return
        await state.send(f"🔀 Jellyfin group switched to **{_fmt_item(item)}**")
        await self._start_item(guild, vc.channel, vc, item, start, sync=False)

    @commands.Cog.listener()
    async def on_voice_state_update(
        self,
        member: discord.Member,
        before: discord.VoiceState,
        after: discord.VoiceState,
    ) -> None:
        # When the bot itself leaves voice (by !leave, !media stop, auto-leave or
        # a kick) drop the playlist and the SyncPlay group for that guild.
        bot_user = self.bot.user
        if bot_user is None or member.id != bot_user.id:
            return
        if before.channel is not None and after.channel is None:
            state = self._states.get(member.guild.id)
            if state is not None and state.start_lock.locked():
                # ensure_voice() dropping a stale client mid-start — not a leave.
                return
            await self._teardown(member.guild.id)

    # ── Commands ──────────────────────────────────────────────────────────────

    @require_role(Role.FRIEND)
    @commands.group(name="media", invoke_without_command=True)
    async def media(self, ctx: commands.Context, *, query: str = "") -> None:
        """
        Search Jellyfin for a movie, series, or episode and play it.
        Accepts an optional sXXeYY suffix to target a specific episode directly.
        See `!media help` for playback and playlist commands.
        """
        if not query.strip():
            await self._send_help(ctx)
            return
        if not await self._require_client(ctx):
            return
        item = await self._resolve(ctx, query)
        if item is not None:
            await self._play_item(ctx, item)

    async def _title_fallthrough(
        self, ctx: commands.Context, word: str, rest: str
    ) -> bool:
        """`!media stop making sense` is a title, not `stop` + junk: search it."""
        if not rest.strip():
            return False
        await self.media.callback(self, ctx, query=f"{word} {rest.strip()}")
        return True

    @require_role(Role.FRIEND)
    @media.command(name="search")
    async def media_search(self, ctx: commands.Context, *, query: str) -> None:
        await self.media.callback(self, ctx, query=query)

    @require_role(Role.FRIEND)
    @media.command(name="help")
    async def media_help(self, ctx: commands.Context, *, rest: str = "") -> None:
        if await self._title_fallthrough(ctx, "help", rest):
            return
        await self._send_help(ctx)

    async def _send_help(self, ctx: commands.Context) -> None:
        text = HELP_TEXT
        if self.client is not None and self.client.has_user_credentials:
            guild = await self._context_guild(ctx)
            name = guild.name if guild is not None else "<server name>"
            text += SYNCPLAY_HELP.format(group=f"slopsoil · {name}")
        await ctx.send(text)

    @require_role(Role.FRIEND)
    @media.command(name="pause")
    async def media_pause(self, ctx: commands.Context, *, rest: str = "") -> None:
        if await self._title_fallthrough(ctx, "pause", rest):
            return
        guild = await self._context_guild(ctx)
        player = self._player(guild.id) if guild else None
        if guild is None or player is None:
            await ctx.send("nothing is playing")
            return
        if player.paused:
            await ctx.send("already paused — `!media play` to carry on")
            return
        player.pause()
        await ctx.send(f"⏸ paused at {format_timestamp(player.position)}")
        await self._sync_call(guild.id, "pause")

    @require_role(Role.FRIEND)
    @media.command(name="play")
    async def media_play(self, ctx: commands.Context, *, rest: str = "") -> None:
        if await self._title_fallthrough(ctx, "play", rest):
            return
        guild = await self._context_guild(ctx)
        player = self._player(guild.id) if guild else None
        if guild is not None and player is not None:
            if not player.paused:
                await ctx.send("already playing")
                return
            player.resume()
            await ctx.send(f"▶ resumed at {format_timestamp(player.position)}")
            await self._sync_call(guild.id, "unpause")
            return
        # Nothing on screen: start the playlist if there is one.
        state = self._states.get(guild.id) if guild else None
        if state is not None and state.queue and self.client is not None:
            item = state.queue.pop(0)
            if not await self._play_item(ctx, item):
                state.queue.insert(0, item)
            return
        await ctx.send("nothing is paused — use `!media <title>` to play something")

    @require_role(Role.FRIEND)
    @media.command(name="resume")
    async def media_resume(self, ctx: commands.Context, *, time_text: str = "") -> None:
        if not time_text.strip():
            await self.media_play.callback(self, ctx)
            return
        seconds = parse_timestamp(time_text)
        if seconds is None and not any(c.isdigit() for c in time_text):
            # e.g. `!media resume the movie` — a title, not a time.
            await self._title_fallthrough(ctx, "resume", time_text)
            return
        if seconds is None:
            await ctx.send(
                f"`{time_text}` isn't a time — try `1:23:45`, `1.23.45`, `1:23` (h:mm), "
                "`45m` or `90s`"
            )
            return
        guild = await self._context_guild(ctx)
        player = self._player(guild.id) if guild else None
        if guild is None or player is None:
            await ctx.send("nothing is playing")
            return
        state = self._states.get(guild.id)
        runtime = _runtime_seconds(state.now.item) if state and state.now else None
        if runtime is not None and seconds >= runtime:
            await ctx.send(
                f"{format_timestamp(seconds)} is past the end "
                f"({format_timestamp(runtime)})"
            )
            return
        was_paused = player.paused
        player.seek(seconds)
        player.resume()
        await ctx.send(f"⏩ jumping to {format_timestamp(seconds)}")
        await self._sync_call(guild.id, "seek", seconds)
        if was_paused:
            await self._sync_call(guild.id, "unpause")

    @require_role(Role.FRIEND)
    @media.command(name="stop")
    async def media_stop(self, ctx: commands.Context, *, rest: str = "") -> None:
        if await self._title_fallthrough(ctx, "stop", rest):
            return
        guild = await self._context_guild(ctx)
        vc = guild.voice_client if guild else None
        if guild is None or vc is None:
            await ctx.send("not in a voice channel")
            return
        await self._stop_stream(guild)
        await self._teardown(guild.id)
        await vc.disconnect(force=False)
        await ctx.send("⏹ stopped and left the voice channel")

    @require_role(Role.FRIEND)
    @media.command(name="skip")
    async def media_skip(self, ctx: commands.Context, *, rest: str = "") -> None:
        if await self._title_fallthrough(ctx, "skip", rest):
            return
        guild = await self._context_guild(ctx)
        state = self._states.get(guild.id) if guild else None
        if guild is None or state is None or (not state.queue and state.now is None):
            await ctx.send("nothing to skip")
            return
        if not state.queue:
            await self._stop_stream(guild)
            await ctx.send("playlist is empty — stopped")
            return
        state.send = ctx.send
        vc = cast("discord.VoiceClient | None", guild.voice_client)
        if vc is None or not vc.is_connected():
            item = state.queue.pop(0)
            if not await self._play_item(ctx, item):
                state.queue.insert(0, item)
            return
        await self._advance(guild.id)

    @require_role(Role.FRIEND)
    @media.command(name="queue")
    async def media_queue(self, ctx: commands.Context, *, query: str = "") -> None:
        if not query.strip():
            await ctx.send(
                "usage: `!media queue <movie>` or `!media queue <show> sXXeYY`"
            )
            return
        if not await self._require_client(ctx):
            return
        guild = await self._context_guild(ctx)
        if guild is None:
            await ctx.send("you're not in a voice channel")
            return
        state = self._state(guild.id, ctx.send)
        if len(state.queue) >= PLAYLIST_MAX:
            await ctx.send(f"the playlist is full ({PLAYLIST_MAX} items)")
            return
        item = await self._resolve(ctx, query)
        if item is None:
            return
        if self._player(guild.id) is None and not state.start_lock.locked():
            # Idle: play now — or, if a playlist is waiting, queue this and
            # start the playlist from its head.
            if not state.queue:
                await self._play_item(ctx, item)
                return
            state.queue.append(item)
            head = state.queue.pop(0)
            await ctx.send(f"➕ queued **{_fmt_item(item)}** at #{len(state.queue)}")
            if not await self._play_item(ctx, head):
                state.queue.insert(0, head)
            return
        if len(state.queue) >= PLAYLIST_MAX:  # filled while the user was picking
            await ctx.send(f"the playlist is full ({PLAYLIST_MAX} items)")
            return
        state.queue.append(item)
        await ctx.send(f"➕ queued **{_fmt_item(item)}** at #{len(state.queue)}")

    def _find_queued(self, queue: list[dict], target: str) -> int | str:
        """Return the 0-based index of ``target`` (a number or title), or an error."""
        target = target.strip()
        if target.isdecimal():
            n = int(target)
            if 1 <= n <= len(queue):
                return n - 1
            return f"there's no #{n} in the playlist (1–{len(queue)})"
        matches = [i for i, it in enumerate(queue) if target.lower() in _fmt_item(it).lower()]
        if not matches:
            return f"nothing in the playlist matches `{target}`"
        if len(matches) > 1:
            return f"`{target}` matches several items — use its number from `!media playlist`"
        return matches[0]

    @require_role(Role.FRIEND)
    @media.command(name="order")
    async def media_order(self, ctx: commands.Context, *, args: str = "") -> None:
        parts = args.rsplit(maxsplit=1)
        if len(parts) != 2 or not parts[1].isdecimal():
            await ctx.send("usage: `!media order <# or title> <new position>`")
            return
        guild = await self._context_guild(ctx)
        state = self._states.get(guild.id) if guild else None
        if state is None or not state.queue:
            await ctx.send("the playlist is empty")
            return
        found = self._find_queued(state.queue, parts[0])
        if isinstance(found, str):
            await ctx.send(found)
            return
        position = int(parts[1])
        if not 1 <= position <= len(state.queue):
            await ctx.send(f"position must be between 1 and {len(state.queue)}")
            return
        item = state.queue.pop(found)
        state.queue.insert(position - 1, item)
        await ctx.send(f"moved **{_fmt_item(item)}** to #{position}")

    @require_role(Role.FRIEND)
    @media.command(name="remove")
    async def media_remove(self, ctx: commands.Context, *, target: str = "") -> None:
        if not target.strip():
            await ctx.send("usage: `!media remove <# or title>`")
            return
        guild = await self._context_guild(ctx)
        state = self._states.get(guild.id) if guild else None
        if state is None or not state.queue:
            await ctx.send("the playlist is empty")
            return
        found = self._find_queued(state.queue, target)
        if isinstance(found, str):
            await ctx.send(found)
            return
        item = state.queue.pop(found)
        await ctx.send(f"removed **{_fmt_item(item)}** from the playlist")

    @require_role(Role.FRIEND)
    @media.command(name="aspect-ratio", aliases=["aspect", "ar"])
    async def media_aspect(self, ctx: commands.Context, *, choice: str = "") -> None:
        """Pick a picture shape from a numbered list (or `!media aspect-ratio 2`)."""
        guild = await self._context_guild(ctx)
        if guild is None:
            await ctx.send("you're not in a voice channel")
            return
        current = self._aspect.get(guild.id)

        if choice.strip():
            if not choice.strip().isdecimal() or not (
                1 <= int(choice) <= len(ASPECT_OPTIONS)
            ):
                await ctx.send(
                    f"pick a number between 1 and {len(ASPECT_OPTIONS)} — "
                    "see `!media aspect-ratio`"
                )
                return
            index = int(choice) - 1
        else:
            lines = ["**Aspect ratio** — reply with a number:"]
            for i, (label, ratio) in enumerate(ASPECT_OPTIONS, 1):
                mark = "  ← current" if ratio == current else ""
                lines.append(f"`{i}` {label}{mark}")
            await ctx.send("\n".join(lines))
            picked = await self._wait_for_number(ctx, len(ASPECT_OPTIONS))
            if picked is None:
                return
            index = picked - 1

        label, ratio = ASPECT_OPTIONS[index]
        if ratio is None:
            self._aspect.pop(guild.id, None)
        else:
            self._aspect[guild.id] = ratio
        name = label.split(" — ")[0]
        player = self._player(guild.id)
        if player is not None and getattr(player, "aspect", None) != ratio:
            player.set_aspect(ratio)
            await ctx.send(f"📐 aspect ratio set to **{name}** — applying now")
        else:
            await ctx.send(f"📐 aspect ratio set to **{name}**")

    @require_role(Role.FRIEND)
    @media.command(name="playlist")
    async def media_playlist(self, ctx: commands.Context, *, rest: str = "") -> None:
        if await self._title_fallthrough(ctx, "playlist", rest):
            return
        guild = await self._context_guild(ctx)
        state = self._states.get(guild.id) if guild else None
        player = self._player(guild.id) if guild else None
        lines: list[str] = []
        if state is not None and state.now is not None and player is not None:
            pos = format_timestamp(player.position)
            runtime = _runtime_seconds(state.now.item)
            total = f" / {format_timestamp(runtime)}" if runtime else ""
            flag = " (paused)" if player.paused else ""
            lines.append(f"**Now playing:** {_fmt_item(state.now.item)} — {pos}{total}{flag}")
        queue = state.queue if state else []
        if queue:
            lines.append(f"**Up next** ({len(queue)}/{PLAYLIST_MAX}):")
            lines.extend(f"  `{i}` {_fmt_item(it)}" for i, it in enumerate(queue, 1))
        elif lines:
            lines.append("_playlist is empty — add with `!media queue <title>`_")
        if not lines:
            await ctx.send("nothing playing and the playlist is empty")
            return
        await ctx.send("\n".join(lines))


async def setup(bot: commands.Bot) -> None:
    url = os.environ.get("JELLYFIN_URL", "").strip()
    api_key = os.environ.get("JELLYFIN_API_KEY", "").strip()
    username = os.environ.get("JELLYFIN_USERNAME", "").strip()
    password = os.environ.get("JELLYFIN_PASSWORD", "")
    if url and (api_key or (username and password)):
        client: JellyfinClient | None = JellyfinClient(
            url=url, api_key=api_key, username=username, password=password
        )
        log.info(
            "Jellyfin client configured for %s (SyncPlay %s)",
            client.base_url,
            "enabled" if client.has_user_credentials else "disabled — set "
            "JELLYFIN_USERNAME/JELLYFIN_PASSWORD to enable",
        )
    else:
        client = None
        log.warning("JELLYFIN_URL/API_KEY not set — !media will report unconfigured")
    await bot.add_cog(Jellyfin(bot, client))
