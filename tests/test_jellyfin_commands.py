"""Tests for the Jellyfin cog's playback / playlist / SyncPlay features.

Voice resolution, go-live streaming and the Jellyfin HTTP client are faked:
``start_live_stream`` registers a FakePlayer in ``bot.video_players`` the way
the real engine does, and ``cancel_stream`` removes it.
"""
from __future__ import annotations

import asyncio
import logging
import threading
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

import cogs.jellyfin as jf
from cogs.jellyfin import (
    HELP_TEXT,
    PLAYLIST_MAX,
    Jellyfin,
    _GuildState,
    _NowPlaying,
    _runtime_seconds,
    format_timestamp,
    parse_timestamp,
)
from services.jellyfin import PlaybackStream

GID = 1234

MOVIE = {"Id": "m1", "Type": "Movie", "Name": "Inception", "ProductionYear": 2010,
         "RunTimeTicks": 2 * 3600 * 10_000_000}
MOVIE2 = {"Id": "m2", "Type": "Movie", "Name": "Interstellar", "ProductionYear": 2014}
MOVIE3 = {"Id": "m3", "Type": "Movie", "Name": "Tenet", "ProductionYear": 2020}
SERIES = {"Id": "ser1", "Type": "Series", "Name": "Breaking Bad"}
EPISODE = {
    "Id": "e1", "Type": "Episode", "Name": "Pilot", "SeriesName": "Breaking Bad",
    "ParentIndexNumber": 1, "IndexNumber": 1,
}


def _movie(i: int, name: str | None = None) -> dict:
    return {"Id": f"q{i}", "Type": "Movie", "Name": name or f"Film {i}"}


# ── fakes ────────────────────────────────────────────────────────────────────


class FakePlayer:
    def __init__(self, position: float = 0.0, paused: bool = False) -> None:
        self.first_frame_sent = threading.Event()
        self.first_frame_sent.set()
        self.position = position
        self.paused = paused
        self.seeks: list[float] = []
        self.pauses = 0
        self.resumes = 0

    def pause(self) -> None:
        self.pauses += 1
        self.paused = True

    def resume(self) -> None:
        self.resumes += 1
        self.paused = False

    def seek(self, seconds: float) -> None:
        self.seeks.append(seconds)
        self.position = seconds

    aspect = None
    aspect_sets: list = []

    def is_source_active(self) -> bool:
        return getattr(self, "active", True)

    def set_aspect(self, ratio) -> None:
        self.aspect = ratio
        self.aspect_sets = [*self.aspect_sets, ratio]


def _stream_for(item_id: str) -> PlaybackStream:
    return PlaybackStream(f"http://jf/{item_id}.m3u8", f"ps-{item_id}", True, "h264")


def _make_client(**overrides) -> MagicMock:
    client = MagicMock()
    client._api_key = "SECRETKEY"
    client.has_user_credentials = False
    client.search = AsyncMock(return_value=[])
    client.find_episode = AsyncMock(return_value=[])
    client.get_seasons = AsyncMock(return_value=[])
    client.get_episodes = AsyncMock(return_value=[])
    client.get_item = AsyncMock(return_value=None)
    client.stop_encoding = AsyncMock()
    client.start_playback = AsyncMock(side_effect=_stream_for)
    for k, v in overrides.items():
        setattr(client, k, v)
    return client


def _make_ctx(guild=None) -> MagicMock:
    ctx = MagicMock()
    ctx.author.id = 1
    ctx.channel.id = 10
    ctx.guild = guild
    ctx.send = AsyncMock()
    return ctx


def _sent(ctx) -> list[str]:
    return [c.args[0] for c in ctx.send.await_args_list]


async def _drain(cog) -> None:
    """Wait for background tasks (SyncPlay queue sync) spawned by the cog."""
    while cog._tasks:
        await asyncio.gather(*list(cog._tasks))


def _reply(content: str) -> MagicMock:
    msg = MagicMock()
    msg.content = content
    return msg


@pytest.fixture
def env(monkeypatch):
    bot = MagicMock()
    bot.user.id = 999
    bot.video_players = {}
    bot.live_connections = {}

    channel = SimpleNamespace(id=55, name="Movies")
    vc = MagicMock()
    vc.is_connected.return_value = True
    vc.channel = channel
    vc.disconnect = AsyncMock()
    guild = MagicMock()
    guild.id = GID
    guild.name = "Guild"
    guild.voice_client = vc
    bot.guilds = [guild]
    bot.get_guild = lambda gid: guild if gid == GID else None

    resolve = AsyncMock(return_value=(guild, channel, vc))
    monkeypatch.setattr(jf, "resolve_voice", resolve)

    async def fake_start(bot_, send, g, ch, vc_, **kw):
        bot_.video_players[g.id] = FakePlayer(position=kw["start_offset"])

    start = AsyncMock(side_effect=fake_start)
    monkeypatch.setattr(jf, "start_live_stream", start)
    cancel = MagicMock(side_effect=lambda b, gid: b.video_players.pop(gid, None))
    monkeypatch.setattr(jf, "cancel_stream", cancel)
    identity = AsyncMock(side_effect=lambda u: u)
    monkeypatch.setattr(jf, "_extract_hls_variant_url", identity)

    client = _make_client()
    cog = Jellyfin(bot, client)
    return SimpleNamespace(
        bot=bot, cog=cog, client=client, guild=guild, channel=channel, vc=vc,
        resolve=resolve, start=start, cancel=cancel,
    )


def _playing(env, item=MOVIE, position=0.0, paused=False, send=None) -> tuple:
    """Put the guild into a 'now playing' state without going through commands."""
    state = env.cog._state(GID, send or AsyncMock())
    state.now = _NowPlaying(item=item, stream=_stream_for(item["Id"]))
    player = FakePlayer(position=position, paused=paused)
    env.bot.video_players[GID] = player
    return state, player


def _group(closed: bool = False) -> MagicMock:
    group = MagicMock()
    group.closed = closed
    group.group_name = "slopsoil · Guild"
    for name in ("open", "close", "set_queue", "pause", "unpause", "seek"):
        setattr(group, name, AsyncMock())
    return group


# ── parse_timestamp / format_timestamp / _runtime_seconds ────────────────────


@pytest.mark.parametrize(
    "text, seconds",
    [
        ("1:23:45", 5025.0),
        ("1.23.45", 5025.0),
        ("1:23", 4980.0),       # h:mm
        ("1.23", 4980.0),
        ("0:05", 300.0),        # 5 minutes
        ("0:00:30", 30.0),
        ("0.0.7", 7.0),
        ("2:5", 7500.0),        # single-digit minutes are fine
        ("45", 2700.0),         # bare number = minutes
        ("0", 0.0),
        ("120", 7200.0),
        ("1:59:59", 7199.0),
        ("1h30m", 5400.0),
        ("45m", 2700.0),
        ("90s", 90.0),
        ("2h", 7200.0),
        ("1h2m3s", 3723.0),
        ("1h5s", 3605.0),
        ("  1H30M  ", 5400.0),  # case/whitespace insensitive
        ("1 h 30 m", 5400.0),
        (" 1:00 ", 3600.0),
    ],
)
def test_parse_timestamp_valid(text, seconds):
    assert parse_timestamp(text) == seconds


@pytest.mark.parametrize(
    "text",
    [
        "", "   ", "abc", "1:60", "1:23:60", "0:99", "1.75",
        "1:2:3:4",     # too many components
        "1:234",       # 3-digit minutes
        ":30", "1:", "1::2", "1:-5", "-5",
        "30m1h",       # units out of order
        "1x", "h", "m", "1.5h", "tomorrow",
    ],
)
def test_parse_timestamp_invalid(text):
    assert parse_timestamp(text) is None


def test_parse_timestamp_allows_large_first_component():
    assert parse_timestamp("90:00") == 90 * 3600.0  # first part has no cap


@pytest.mark.parametrize(
    "seconds, text",
    [(0, "0:00:00"), (59.9, "0:00:59"), (61, "0:01:01"), (5025, "1:23:45"),
     (-10, "0:00:00"), (36000, "10:00:00")],
)
def test_format_timestamp(seconds, text):
    assert format_timestamp(seconds) == text


@pytest.mark.parametrize(
    "item, expected",
    [({"RunTimeTicks": 600_000_000}, 60.0), ({"RunTimeTicks": 0}, None),
     ({"RunTimeTicks": "600"}, None), ({}, None)],
)
def test_runtime_seconds(item, expected):
    assert _runtime_seconds(item) == expected


# ── state / context helpers ──────────────────────────────────────────────────


async def test_state_default_send_is_awaitable_and_replaced(env):
    state = env.cog._state(GID)
    await state.send("dropped")  # default no-op sender
    send = AsyncMock()
    assert env.cog._state(GID, send) is state
    assert state.send is send
    assert env.cog._state(GID).send is send  # no send → keep current


async def test_context_guild_prefers_voice_then_ctx_then_bot(env):
    ctx = _make_ctx()
    assert await env.cog._context_guild(ctx) is env.guild

    env.resolve.return_value = (None, None, None)
    other = MagicMock()
    assert await env.cog._context_guild(_make_ctx(guild=other)) is other

    assert await env.cog._context_guild(_make_ctx()) is env.guild  # has voice_client

    env.guild.voice_client = None
    assert await env.cog._context_guild(_make_ctx()) is None


# ── help ─────────────────────────────────────────────────────────────────────


async def test_help_without_syncplay(env):
    ctx = _make_ctx()
    await Jellyfin.media_help.callback(env.cog, ctx)
    assert _sent(ctx) == [HELP_TEXT]


async def test_help_with_syncplay_names_group(env):
    env.client.has_user_credentials = True
    ctx = _make_ctx()
    await Jellyfin.media_help.callback(env.cog, ctx)
    (text,) = _sent(ctx)
    assert text.startswith(HELP_TEXT)
    assert "**slopsoil · Guild**" in text


async def test_help_with_syncplay_unknown_guild(env):
    env.client.has_user_credentials = True
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx()
    await Jellyfin.media_help.callback(env.cog, ctx)
    assert "**slopsoil · <server name>**" in _sent(ctx)[0]


async def test_help_unconfigured_client(env):
    env.cog.client = None
    ctx = _make_ctx()
    await Jellyfin.media_help.callback(env.cog, ctx)
    assert _sent(ctx) == [HELP_TEXT]


# ── _play_item / _start_item ─────────────────────────────────────────────────


async def test_play_item_starts_and_records_now_playing(env):
    ctx = _make_ctx()
    assert await env.cog._play_item(ctx, MOVIE, start=42.0) is True
    kwargs = env.start.call_args.kwargs
    assert kwargs["start_offset"] == 42.0
    assert kwargs["url"] == "http://jf/m1.m3u8"
    assert env.start.call_args.args[1] is ctx.send
    state = env.cog._states[GID]
    assert state.now.item is MOVIE and state.generation == 1
    env.client.stop_encoding.assert_not_awaited()


async def test_start_item_stops_previous_transcode(env):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE)
    await env.cog._play_item(ctx, MOVIE2)
    env.client.stop_encoding.assert_awaited_once_with("ps-m1")
    assert env.cog._states[GID].generation == 2


async def test_start_item_go_live_failure_releases_transcode(env):
    env.start.side_effect = None  # registers no player → treated as failure
    ctx = _make_ctx()
    assert await env.cog._play_item(ctx, MOVIE) is False
    state = env.cog._states[GID]
    assert state.now is None
    env.client.stop_encoding.assert_awaited_once_with("ps-m1")


async def test_start_item_failure_keeps_newer_now_playing(env):
    state = env.cog._state(GID, AsyncMock())

    async def start_superseded(*a, **kw):
        state.generation += 1  # something newer started meanwhile
        state.now = "newer"

    env.start.side_effect = start_superseded
    assert await env.cog._play_item(_make_ctx(), MOVIE) is False
    assert state.now == "newer"


async def test_start_item_playback_session_failure(env):
    env.client.start_playback = AsyncMock(return_value=None)
    ctx = _make_ctx()
    assert await env.cog._play_item(ctx, MOVIE) is False
    assert "could not start a Jellyfin playback session" in _sent(ctx)[-1]
    env.start.assert_not_awaited()


async def test_play_item_without_api_key_does_not_redact(env):
    env.client._api_key = ""
    assert await env.cog._play_item(_make_ctx(), MOVIE) is True


# ── SyncPlay group creation / queue sync ─────────────────────────────────────


@pytest.fixture
def syncplay(env, monkeypatch):
    env.client.has_user_credentials = True
    group = _group()
    cls = MagicMock(return_value=group)
    monkeypatch.setattr(jf, "SyncPlayGroup", cls)
    return SimpleNamespace(group=group, cls=cls)


async def test_play_creates_syncplay_group_and_sets_queue(env, syncplay):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE, start=60.0)
    await _drain(env.cog)
    args, kwargs = syncplay.cls.call_args
    assert args == (env.client,)
    assert kwargs["device_id"] == f"slopsoil-{GID}"
    assert kwargs["on_command"].func == env.cog._on_sync_command
    assert kwargs["on_command"].args == (GID,)
    assert kwargs["on_play_queue"].func == env.cog._on_sync_queue
    syncplay.group.open.assert_awaited_once_with("slopsoil · Guild")
    syncplay.group.set_queue.assert_awaited_once_with("m1", 60.0)
    assert any("SyncPlay group **slopsoil · Guild** is open" in m for m in _sent(ctx))
    state = env.cog._states[GID]
    assert state.syncplay is syncplay.group
    assert "m1" in state.sync_sent

    # A second item reuses the open group.
    await env.cog._play_item(ctx, MOVIE2)
    await _drain(env.cog)
    assert syncplay.cls.call_count == 1
    syncplay.group.set_queue.assert_awaited_with("m2", 0.0)


async def test_closed_syncplay_group_is_recreated(env, syncplay):
    state = env.cog._state(GID, AsyncMock())
    state.syncplay = _group(closed=True)
    await env.cog._play_item(_make_ctx(), MOVIE)
    await _drain(env.cog)
    assert state.syncplay is syncplay.group


async def test_syncplay_open_failure_is_tolerated(env, syncplay, caplog):
    syncplay.group.open.side_effect = RuntimeError("403")
    with caplog.at_level(logging.WARNING, logger="cogs.jellyfin"):
        assert await env.cog._play_item(_make_ctx(), MOVIE) is True
        await _drain(env.cog)
    syncplay.group.close.assert_awaited_once()
    syncplay.group.set_queue.assert_not_awaited()
    assert env.cog._states[GID].syncplay is None
    assert "could not create group" in caplog.text


async def test_syncplay_set_queue_failure_is_logged(env, syncplay, caplog):
    syncplay.group.set_queue.side_effect = RuntimeError("500")
    with caplog.at_level(logging.WARNING, logger="cogs.jellyfin"):
        assert await env.cog._play_item(_make_ctx(), MOVIE) is True
        await _drain(env.cog)
    assert "set queue failed" in caplog.text


async def test_ensure_syncplay_without_client(env):
    env.cog.client = None
    assert await env.cog._ensure_syncplay(env.guild) is None


async def test_sync_call_variants(env, caplog):
    await env.cog._sync_call(GID, "pause")  # no state: no-op
    state = env.cog._state(GID)
    await env.cog._sync_call(GID, "pause")  # no group: no-op
    state.syncplay = _group(closed=True)
    await env.cog._sync_call(GID, "pause")
    state.syncplay.pause.assert_not_awaited()
    state.syncplay = _group()
    await env.cog._sync_call(GID, "seek", 12.0)
    state.syncplay.seek.assert_awaited_once_with(12.0)
    state.syncplay.pause.side_effect = RuntimeError("gone")
    with caplog.at_level(logging.WARNING, logger="cogs.jellyfin"):
        await env.cog._sync_call(GID, "pause")
    assert "pause failed: gone" in caplog.text


# ── pause / play / resume ────────────────────────────────────────────────────


async def test_pause_nothing_playing(env):
    ctx = _make_ctx()
    await Jellyfin.media_pause.callback(env.cog, ctx)
    assert _sent(ctx) == ["nothing is playing"]


async def test_pause_no_guild(env):
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx()
    await Jellyfin.media_pause.callback(env.cog, ctx)
    assert _sent(ctx) == ["nothing is playing"]


async def test_pause_pauses_and_forwards(env):
    state, player = _playing(env, position=125.0)
    state.syncplay = _group()
    ctx = _make_ctx()
    await Jellyfin.media_pause.callback(env.cog, ctx)
    assert player.paused
    assert _sent(ctx) == ["⏸ paused at 0:02:05"]
    state.syncplay.pause.assert_awaited_once()


async def test_pause_already_paused(env):
    _, player = _playing(env, paused=True)
    ctx = _make_ctx()
    await Jellyfin.media_pause.callback(env.cog, ctx)
    assert "already paused" in _sent(ctx)[0]
    assert player.pauses == 0


async def test_play_resumes_and_forwards(env):
    state, player = _playing(env, position=3600.0, paused=True)
    state.syncplay = _group()
    ctx = _make_ctx()
    await Jellyfin.media_play.callback(env.cog, ctx)
    assert not player.paused
    assert _sent(ctx) == ["▶ resumed at 1:00:00"]
    state.syncplay.unpause.assert_awaited_once()


async def test_play_already_playing(env):
    _playing(env)
    ctx = _make_ctx()
    await Jellyfin.media_play.callback(env.cog, ctx)
    assert _sent(ctx) == ["already playing"]


async def test_play_nothing_paused(env):
    ctx = _make_ctx()
    await Jellyfin.media_play.callback(env.cog, ctx)
    assert _sent(ctx) == ["nothing is paused — use `!media <title>` to play something"]


async def test_play_starts_playlist_when_idle(env):
    state = env.cog._state(GID)
    state.queue = [MOVIE2, MOVIE3]
    ctx = _make_ctx()
    await Jellyfin.media_play.callback(env.cog, ctx)
    assert state.now.item is MOVIE2
    assert state.queue == [MOVIE3]


async def test_play_playlist_start_failure_keeps_item(env):
    env.client.start_playback = AsyncMock(return_value=None)
    state = env.cog._state(GID)
    state.queue = [MOVIE2]
    await Jellyfin.media_play.callback(env.cog, _make_ctx())
    assert state.queue == [MOVIE2]


async def test_resume_without_time_acts_as_play(env):
    _, player = _playing(env, paused=True)
    ctx = _make_ctx()
    await Jellyfin.media_resume.callback(env.cog, ctx, time_text=" ")
    assert not player.paused
    assert _sent(ctx)[0].startswith("▶ resumed")


@pytest.mark.parametrize("text", ["1:75", "12x", "1:2:3:4", "5 minutes"])
async def test_resume_invalid_time(env, text):
    _, player = _playing(env)
    ctx = _make_ctx()
    await Jellyfin.media_resume.callback(env.cog, ctx, time_text=text)
    assert _sent(ctx) == [
        f"`{text}` isn't a time — try `1:23:45`, `1.23.45`, `1:23` (h:mm), "
        "`45m` or `90s`"
    ]
    assert player.seeks == []
    env.client.search.assert_not_awaited()


async def test_resume_without_digits_searches_title(env):
    _, player = _playing(env)
    env.client.search.return_value = [MOVIE2]
    ctx = _make_ctx()
    await Jellyfin.media_resume.callback(env.cog, ctx, time_text=" the movie ")
    env.client.search.assert_awaited_once_with("resume the movie")
    assert player.seeks == []
    assert env.cog._states[GID].now.item is MOVIE2


async def test_resume_nothing_playing(env):
    ctx = _make_ctx()
    await Jellyfin.media_resume.callback(env.cog, ctx, time_text="1:00")
    assert _sent(ctx) == ["nothing is playing"]


async def test_resume_past_runtime(env):
    _, player = _playing(env, item=MOVIE)  # 2 h runtime
    ctx = _make_ctx()
    await Jellyfin.media_resume.callback(env.cog, ctx, time_text="2:00")
    assert _sent(ctx) == ["2:00:00 is past the end (2:00:00)"]
    assert player.seeks == []


async def test_resume_seeks_and_forwards(env):
    state, player = _playing(env, item=MOVIE)
    state.syncplay = _group()
    ctx = _make_ctx()
    await Jellyfin.media_resume.callback(env.cog, ctx, time_text="1:23:45")
    assert player.seeks == [5025.0] and not player.paused
    assert _sent(ctx) == ["⏩ jumping to 1:23:45"]
    state.syncplay.seek.assert_awaited_once_with(5025.0)
    state.syncplay.unpause.assert_not_awaited()


async def test_resume_while_paused_also_unpauses_group(env):
    state, player = _playing(env, item=MOVIE2, paused=True)  # no runtime
    state.syncplay = _group()
    await Jellyfin.media_resume.callback(env.cog, _make_ctx(), time_text="90s")
    assert player.seeks == [90.0] and not player.paused
    state.syncplay.seek.assert_awaited_once_with(90.0)
    state.syncplay.unpause.assert_awaited_once()


async def test_resume_player_without_state(env):
    env.bot.video_players[GID] = player = FakePlayer()  # e.g. a !tv stream
    await Jellyfin.media_resume.callback(env.cog, _make_ctx(), time_text="5m")
    assert player.seeks == [300.0]


# ── stop ─────────────────────────────────────────────────────────────────────


async def test_stop_not_in_voice(env):
    env.guild.voice_client = None
    ctx = _make_ctx()
    await Jellyfin.media_stop.callback(env.cog, ctx)
    assert _sent(ctx) == ["not in a voice channel"]


async def test_stop_no_guild(env):
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx()
    await Jellyfin.media_stop.callback(env.cog, ctx)
    assert _sent(ctx) == ["not in a voice channel"]


async def test_stop_leaves_voice_clears_queue_and_closes_syncplay(env):
    state, _ = _playing(env)
    state.queue = [MOVIE2]
    group = state.syncplay = _group()
    live = MagicMock(disconnect=AsyncMock())
    env.bot.live_connections[GID] = live
    ctx = _make_ctx()
    await Jellyfin.media_stop.callback(env.cog, ctx)
    env.cancel.assert_called_once_with(env.bot, GID)
    live.disconnect.assert_awaited_once()
    env.client.stop_encoding.assert_awaited_once_with("ps-m1")
    group.close.assert_awaited_once()
    env.vc.disconnect.assert_awaited_once_with(force=False)
    assert GID not in env.cog._states
    assert env.bot.live_connections == {}
    assert _sent(ctx) == ["⏹ stopped and left the voice channel"]


async def test_stop_tolerates_go_live_disconnect_error(env, caplog):
    env.bot.live_connections[GID] = MagicMock(
        disconnect=AsyncMock(side_effect=RuntimeError("ws closed"))
    )
    with caplog.at_level(logging.WARNING, logger="cogs.jellyfin"):
        await Jellyfin.media_stop.callback(env.cog, _make_ctx())
    assert "error disconnecting go-live" in caplog.text
    env.vc.disconnect.assert_awaited_once()


async def test_stop_stream_without_client(env):
    state, _ = _playing(env)
    env.cog.client = None
    await env.cog._stop_stream(env.guild)
    assert state.now is None


# ── skip ─────────────────────────────────────────────────────────────────────


async def test_skip_nothing(env):
    ctx = _make_ctx()
    await Jellyfin.media_skip.callback(env.cog, ctx)
    assert _sent(ctx) == ["nothing to skip"]
    env.cog._state(GID)  # state but idle with empty queue
    await Jellyfin.media_skip.callback(env.cog, ctx)
    assert _sent(ctx)[-1] == "nothing to skip"


async def test_skip_last_item_stops(env):
    state, _ = _playing(env)
    ctx = _make_ctx()
    await Jellyfin.media_skip.callback(env.cog, ctx)
    assert _sent(ctx) == ["playlist is empty — stopped"]
    assert state.now is None and state.generation == 1
    env.cancel.assert_called_once_with(env.bot, GID)
    env.client.stop_encoding.assert_awaited_once_with("ps-m1")
    env.vc.disconnect.assert_not_awaited()  # stays in voice


async def test_skip_plays_next(env):
    state, _ = _playing(env)
    state.queue = [MOVIE2, MOVIE3]
    ctx = _make_ctx()
    await Jellyfin.media_skip.callback(env.cog, ctx)
    assert state.send is ctx.send
    assert "⏭ up next: **Interstellar (2014)**" in _sent(ctx)
    assert state.now.item is MOVIE2
    assert state.queue == [MOVIE3]
    env.client.stop_encoding.assert_awaited_once_with("ps-m1")
    # Next item streams in the bot's current channel.
    assert env.start.call_args.args[3] is env.channel


async def test_skip_when_bot_not_connected_uses_author_voice(env):
    state = env.cog._state(GID)
    state.queue = [MOVIE2]
    env.vc.is_connected.return_value = False
    ctx = _make_ctx()
    await Jellyfin.media_skip.callback(env.cog, ctx)
    env.resolve.assert_awaited()
    assert state.now.item is MOVIE2 and state.queue == []


async def test_skip_when_bot_not_connected_failure_requeues(env):
    state = env.cog._state(GID)
    state.queue = [MOVIE2]
    env.guild.voice_client = None
    env.client.start_playback = AsyncMock(return_value=None)
    await Jellyfin.media_skip.callback(env.cog, _make_ctx())
    assert state.queue == [MOVIE2]


# ── queue ────────────────────────────────────────────────────────────────────


async def test_queue_usage(env):
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="")
    assert _sent(ctx)[0].startswith("usage: `!media queue")


async def test_queue_unconfigured(env):
    env.cog.client = None
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="inception")
    assert "not configured" in _sent(ctx)[0]


async def test_queue_no_guild(env):
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="inception")
    assert _sent(ctx) == ["you're not in a voice channel"]


async def test_queue_when_idle_plays_immediately(env):
    env.client.search.return_value = [MOVIE]
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="inception")
    state = env.cog._states[GID]
    assert state.now.item is MOVIE and state.queue == []
    env.start.assert_awaited_once()


async def test_queue_appends_while_playing(env):
    state, _ = _playing(env)
    env.client.search.return_value = [MOVIE2]
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="interstellar")
    assert state.queue == [MOVIE2]
    assert _sent(ctx)[-1] == "➕ queued **Interstellar (2014)** at #1"
    env.start.assert_not_awaited()


async def test_queue_when_idle_with_playlist_starts_head(env):
    state = env.cog._state(GID)
    state.queue = [MOVIE3]
    env.client.search.return_value = [MOVIE2]
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="interstellar")
    assert "➕ queued **Interstellar (2014)** at #1" in _sent(ctx)
    assert state.now.item is MOVIE3  # playlist head plays first
    assert state.queue == [MOVIE2]
    env.start.assert_awaited_once()


async def test_queue_when_idle_with_playlist_head_failure_requeues(env):
    state = env.cog._state(GID)
    state.queue = [MOVIE3]
    env.client.search.return_value = [MOVIE2]
    env.client.start_playback = AsyncMock(return_value=None)
    await Jellyfin.media_queue.callback(env.cog, _make_ctx(), query="interstellar")
    assert state.queue == [MOVIE3, MOVIE2]
    assert state.now is None


async def test_queue_appends_while_a_start_is_in_progress(env):
    state = env.cog._state(GID)
    env.client.search.return_value = [MOVIE2]
    async with state.start_lock:  # e.g. auto-advance still probing
        await Jellyfin.media_queue.callback(
            env.cog, _make_ctx(), query="interstellar"
        )
    assert state.queue == [MOVIE2]
    env.start.assert_not_awaited()


async def test_queue_resolve_cancelled(env):
    state, _ = _playing(env)
    await Jellyfin.media_queue.callback(env.cog, _make_ctx(), query="nothing")
    assert state.queue == []


async def test_queue_full(env):
    state, _ = _playing(env)
    state.queue = [_movie(i) for i in range(PLAYLIST_MAX)]
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="inception")
    assert _sent(ctx) == [f"the playlist is full ({PLAYLIST_MAX} items)"]
    env.client.search.assert_not_awaited()


async def test_queue_can_reach_cap(env):
    state, _ = _playing(env)
    state.queue = [_movie(i) for i in range(PLAYLIST_MAX - 1)]
    env.client.search.return_value = [MOVIE2]
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="interstellar")
    assert len(state.queue) == PLAYLIST_MAX
    assert _sent(ctx)[-1].endswith(f"at #{PLAYLIST_MAX}")


async def test_queue_fills_up_while_user_is_picking(env):
    state, _ = _playing(env)
    state.queue = [_movie(i) for i in range(PLAYLIST_MAX - 1)]
    env.client.search.return_value = [MOVIE2, MOVIE3]

    async def pick(*a, **kw):
        state.queue.append(_movie(99))  # someone else queued meanwhile
        return _reply("1")

    env.bot.wait_for = AsyncMock(side_effect=pick)
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="in")
    assert _sent(ctx)[-1] == f"the playlist is full ({PLAYLIST_MAX} items)"
    assert len(state.queue) == PLAYLIST_MAX and MOVIE2 not in state.queue


async def test_queue_series_requires_episode_pick(env):
    state, _ = _playing(env)
    seasons = [{"Id": "s1", "Name": "Season 1"}]
    episodes = [{"Id": "e1", "Name": "Pilot", "IndexNumber": 1, "Type": "Episode",
                 "SeriesName": "Breaking Bad", "ParentIndexNumber": 1}]
    env.client.search.return_value = [SERIES]
    env.client.get_seasons = AsyncMock(return_value=seasons)
    env.client.get_episodes = AsyncMock(return_value=episodes)
    env.bot.wait_for = AsyncMock(return_value=_reply("1"))
    ctx = _make_ctx()
    await Jellyfin.media_queue.callback(env.cog, ctx, query="breaking bad")
    env.client.get_episodes.assert_awaited_once_with("ser1", "s1")
    assert state.queue == episodes
    assert _sent(ctx)[-1] == "➕ queued **Breaking Bad — S01E01 — Pilot** at #1"


async def test_queue_episode_code_queues_directly(env):
    state, _ = _playing(env)
    env.client.find_episode.return_value = [EPISODE]
    await Jellyfin.media_queue.callback(
        env.cog, _make_ctx(), query="breaking bad s01e01"
    )
    env.client.find_episode.assert_awaited_once_with("breaking bad", 1, 1)
    assert state.queue == [EPISODE]


# ── order / remove ───────────────────────────────────────────────────────────


@pytest.fixture
def queued(env):
    state, _ = _playing(env)
    state.queue = [MOVIE2, MOVIE3, EPISODE]
    return state


@pytest.mark.parametrize("args", ["", "3", "3 x", "tenet"])
async def test_order_usage(env, queued, args):
    ctx = _make_ctx()
    await Jellyfin.media_order.callback(env.cog, ctx, args=args)
    assert _sent(ctx) == ["usage: `!media order <# or title> <new position>`"]


async def test_order_empty_playlist(env):
    ctx = _make_ctx()
    await Jellyfin.media_order.callback(env.cog, ctx, args="1 2")
    assert _sent(ctx) == ["the playlist is empty"]


async def test_order_no_guild(env):
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx()
    await Jellyfin.media_order.callback(env.cog, ctx, args="1 2")
    assert _sent(ctx) == ["the playlist is empty"]


async def test_order_by_number(env, queued):
    ctx = _make_ctx()
    await Jellyfin.media_order.callback(env.cog, ctx, args="3 1")
    assert queued.queue == [EPISODE, MOVIE2, MOVIE3]
    assert _sent(ctx) == ["moved **Breaking Bad — S01E01 — Pilot** to #1"]


async def test_order_by_title(env, queued):
    ctx = _make_ctx()
    await Jellyfin.media_order.callback(env.cog, ctx, args="interstellar 3")
    assert queued.queue == [MOVIE3, EPISODE, MOVIE2]


async def test_order_by_multiword_title(env, queued):
    await Jellyfin.media_order.callback(env.cog, _make_ctx(), args="breaking bad 2")
    assert queued.queue == [MOVIE2, EPISODE, MOVIE3]


@pytest.mark.parametrize(
    "args, message",
    [
        ("4 1", "there's no #4 in the playlist (1–3)"),
        ("0 1", "there's no #0 in the playlist (1–3)"),
        ("e 1", "`e` matches several items — use its number from `!media playlist`"),
        ("matrix 1", "nothing in the playlist matches `matrix`"),
        ("1 4", "position must be between 1 and 3"),
        ("1 0", "position must be between 1 and 3"),
    ],
)
async def test_order_errors(env, queued, args, message):
    ctx = _make_ctx()
    await Jellyfin.media_order.callback(env.cog, ctx, args=args)
    assert _sent(ctx) == [message]
    assert queued.queue == [MOVIE2, MOVIE3, EPISODE]


async def test_remove_usage(env, queued):
    ctx = _make_ctx()
    await Jellyfin.media_remove.callback(env.cog, ctx, target=" ")
    assert _sent(ctx) == ["usage: `!media remove <# or title>`"]


async def test_remove_empty_playlist(env):
    ctx = _make_ctx()
    await Jellyfin.media_remove.callback(env.cog, ctx, target="1")
    assert _sent(ctx) == ["the playlist is empty"]


async def test_remove_by_number(env, queued):
    ctx = _make_ctx()
    await Jellyfin.media_remove.callback(env.cog, ctx, target="2")
    assert queued.queue == [MOVIE2, EPISODE]
    assert _sent(ctx) == ["removed **Tenet (2020)** from the playlist"]


async def test_remove_by_title_case_insensitive(env, queued):
    await Jellyfin.media_remove.callback(env.cog, _make_ctx(), target="PILOT")
    assert queued.queue == [MOVIE2, MOVIE3]


@pytest.mark.parametrize(
    "target, message",
    [
        ("9", "there's no #9 in the playlist (1–3)"),
        ("e", "`e` matches several items — use its number from `!media playlist`"),
        ("matrix", "nothing in the playlist matches `matrix`"),
    ],
)
async def test_remove_errors(env, queued, target, message):
    ctx = _make_ctx()
    await Jellyfin.media_remove.callback(env.cog, ctx, target=target)
    assert _sent(ctx) == [message]
    assert len(queued.queue) == 3


# ── playlist ─────────────────────────────────────────────────────────────────


async def test_playlist_empty(env):
    ctx = _make_ctx()
    await Jellyfin.media_playlist.callback(env.cog, ctx)
    assert _sent(ctx) == ["nothing playing and the playlist is empty"]


async def test_playlist_now_playing_and_queue(env):
    state, _ = _playing(env, item=MOVIE, position=754.0, paused=True)
    state.queue = [MOVIE2, EPISODE]
    ctx = _make_ctx()
    await Jellyfin.media_playlist.callback(env.cog, ctx)
    assert _sent(ctx) == [
        "**Now playing:** Inception (2010) — 0:12:34 / 2:00:00 (paused)\n"
        f"**Up next** (2/{PLAYLIST_MAX}):\n"
        "  `1` Interstellar (2014)\n"
        "  `2` Breaking Bad — S01E01 — Pilot"
    ]


async def test_playlist_now_playing_without_runtime_and_empty_queue(env):
    _playing(env, item=MOVIE2, position=5.0)
    ctx = _make_ctx()
    await Jellyfin.media_playlist.callback(env.cog, ctx)
    assert _sent(ctx) == [
        "**Now playing:** Interstellar (2014) — 0:00:05\n"
        "_playlist is empty — add with `!media queue <title>`_"
    ]


async def test_playlist_queue_only(env):
    state = env.cog._state(GID)
    state.queue = [MOVIE3]
    ctx = _make_ctx()
    await Jellyfin.media_playlist.callback(env.cog, ctx)
    assert _sent(ctx) == [f"**Up next** (1/{PLAYLIST_MAX}):\n  `1` Tenet (2020)"]


async def test_playlist_no_guild(env):
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx()
    await Jellyfin.media_playlist.callback(env.cog, ctx)
    assert _sent(ctx) == ["nothing playing and the playlist is empty"]


# ── auto-advance ─────────────────────────────────────────────────────────────


async def test_auto_advance_chain(env):
    send = AsyncMock()
    ctx = _make_ctx()
    ctx.send = send
    await env.cog._play_item(ctx, MOVIE)
    state = env.cog._states[GID]
    state.queue = [MOVIE2, MOVIE3]

    on_end = env.start.call_args.kwargs["on_end"]
    await on_end()  # MOVIE finished on its own
    assert state.now.item is MOVIE2
    env.client.stop_encoding.assert_awaited_with("ps-m1")

    await env.start.call_args.kwargs["on_end"]()
    assert state.now.item is MOVIE3 and state.queue == []

    await env.start.call_args.kwargs["on_end"]()
    assert state.now is None
    env.client.stop_encoding.assert_awaited_with("ps-m3")
    assert env.start.await_count == 3
    sent = _sent(ctx)
    assert "⏭ up next: **Interstellar (2014)**" in sent
    assert "⏭ up next: **Tenet (2020)**" in sent


async def test_stale_on_end_is_ignored(env):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE)
    stale = env.start.call_args.kwargs["on_end"]
    await env.cog._play_item(ctx, MOVIE2)  # replaces MOVIE
    state = env.cog._states[GID]
    state.queue = [MOVIE3]
    env.client.stop_encoding.reset_mock()
    await stale()
    assert state.now.item is MOVIE2 and state.queue == [MOVIE3]
    env.client.stop_encoding.assert_not_awaited()


async def test_on_end_after_stop_does_not_advance(env):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE)
    on_end = env.start.call_args.kwargs["on_end"]
    state = env.cog._states[GID]
    state.queue = [MOVIE2]
    await env.cog._stop_stream(env.guild)
    await on_end()
    assert state.now is None and state.queue == [MOVIE2]
    assert env.start.await_count == 1


async def test_on_end_for_forgotten_guild(env):
    await env.cog._on_stream_end(GID, 1)  # no state: nothing happens
    env.start.assert_not_awaited()


async def test_on_end_without_client(env):
    state, _ = _playing(env)
    env.cog.client = None
    await env.cog._on_stream_end(GID, state.generation)
    assert state.now is None


async def test_advance_skips_items_that_fail_to_start(env):
    state = env.cog._state(GID, AsyncMock())
    state.queue = [MOVIE2, MOVIE3]
    env.client.start_playback = AsyncMock(side_effect=[None, _stream_for("m3")])
    assert await env.cog._advance(GID) is True
    assert state.now.item is MOVIE3 and state.queue == []


async def test_advance_all_fail(env):
    state = env.cog._state(GID, AsyncMock())
    state.queue = [MOVIE2]
    env.client.start_playback = AsyncMock(return_value=None)
    assert await env.cog._advance(GID) is False
    assert state.queue == []


async def test_advance_stops_when_bot_left_voice(env):
    state = env.cog._state(GID, AsyncMock())
    state.queue = [MOVIE2]
    env.vc.is_connected.return_value = False
    assert await env.cog._advance(GID) is False
    assert state.queue == [MOVIE2]
    env.guild.voice_client = None
    assert await env.cog._advance(GID) is False


async def test_advance_unknown_guild_or_state(env):
    assert await env.cog._advance(GID) is False  # no state
    env.cog._state(999)
    assert await env.cog._advance(999) is False  # guild not found


async def test_advance_empty_queue(env):
    env.cog._state(GID)
    assert await env.cog._advance(GID) is False


# ── SyncPlay commands from other members ─────────────────────────────────────


async def test_sync_command_without_player_or_state(env):
    await env.cog._on_sync_command(GID, "Pause", None)  # nothing at all
    env.bot.video_players[GID] = player = FakePlayer()
    await env.cog._on_sync_command(GID, "Pause", None)  # player but no state
    assert player.pauses == 0


async def test_sync_pause(env):
    send = AsyncMock()
    _, player = _playing(env, position=65.0, send=send)
    await env.cog._on_sync_command(GID, "Pause", 65.0)
    assert player.paused
    send.assert_awaited_once_with("⏸ paused from Jellyfin at 0:01:05")


async def test_sync_pause_echo_is_noop(env):
    send = AsyncMock()
    _, player = _playing(env, paused=True, send=send)
    await env.cog._on_sync_command(GID, "Pause", 0.0)
    assert player.pauses == 0
    send.assert_not_awaited()


async def test_sync_unpause_resumes(env):
    send = AsyncMock()
    _, player = _playing(env, position=100.0, paused=True, send=send)
    await env.cog._on_sync_command(GID, "Unpause", 101.0)  # within tolerance
    assert not player.paused and player.seeks == []
    send.assert_awaited_once_with("▶ resumed from Jellyfin")


async def test_sync_unpause_with_drift_seeks(env):
    _, player = _playing(env, position=100.0, paused=True)
    await env.cog._on_sync_command(GID, "Unpause", 200.0)
    assert player.seeks == [200.0] and not player.paused


async def test_sync_unpause_echo_is_noop(env):
    send = AsyncMock()
    _, player = _playing(env, position=100.0, send=send)
    await env.cog._on_sync_command(GID, "Unpause", 102.0)
    await env.cog._on_sync_command(GID, "Unpause", None)
    assert player.seeks == [] and player.resumes == 0
    send.assert_not_awaited()


async def test_sync_seek_with_drift(env):
    send = AsyncMock()
    _, player = _playing(env, position=10.0, send=send)
    await env.cog._on_sync_command(GID, "Seek", 3725.0)
    assert player.seeks == [3725.0]
    send.assert_awaited_once_with("⏩ Jellyfin jumped to 1:02:05")


@pytest.mark.parametrize("position", [12.5, 7.0, None])
async def test_sync_seek_within_tolerance_is_noop(env, position):
    send = AsyncMock()
    _, player = _playing(env, position=10.0, send=send)
    await env.cog._on_sync_command(GID, "Seek", position)
    assert player.seeks == []
    send.assert_not_awaited()


async def test_sync_seek_echo_of_own_resume_is_noop(env):
    """!media resume seeks locally, then the group echoes the Seek back."""
    state, player = _playing(env, item=MOVIE)
    state.syncplay = group = _group()

    async def echo(seconds):
        await env.cog._on_sync_command(GID, "Seek", seconds)

    group.seek.side_effect = echo
    await Jellyfin.media_resume.callback(env.cog, _make_ctx(), time_text="30m")
    assert player.seeks == [1800.0]  # not seeked a second time
    state.send.assert_not_awaited()


async def test_sync_stop(env):
    send = AsyncMock()
    state, _ = _playing(env, send=send)
    await env.cog._on_sync_command(GID, "Stop", None)
    env.cancel.assert_called_once_with(env.bot, GID)
    assert state.now is None
    send.assert_awaited_once_with("⏹ stopped from Jellyfin")
    env.vc.disconnect.assert_not_awaited()


async def test_sync_stop_unknown_guild(env):
    env.bot.get_guild = lambda gid: None
    send = AsyncMock()
    _playing(env, send=send)
    await env.cog._on_sync_command(GID, "Stop", None)
    env.cancel.assert_not_called()
    send.assert_not_awaited()


async def test_sync_unknown_command_ignored(env):
    send = AsyncMock()
    _, player = _playing(env, send=send)
    await env.cog._on_sync_command(GID, "Buffering", 50.0)
    assert player.seeks == [] and player.pauses == 0
    send.assert_not_awaited()


# ── SyncPlay queue changes from other members ────────────────────────────────


async def test_sync_queue_plays_new_item(env):
    send = AsyncMock()
    state, _ = _playing(env, send=send)
    env.client.get_item = AsyncMock(return_value=MOVIE2)
    await env.cog._on_sync_queue(GID, "m2", 30.0, True)
    env.client.get_item.assert_awaited_once_with("m2")
    send.assert_any_await("🔀 Jellyfin group switched to **Interstellar (2014)**")
    assert state.now.item is MOVIE2
    assert env.start.call_args.kwargs["start_offset"] == 30.0
    assert env.start.call_args.args[3] is env.channel


async def test_sync_queue_does_not_echo_back_to_group(env, syncplay):
    state, _ = _playing(env)
    state.syncplay = syncplay.group
    env.client.get_item = AsyncMock(return_value=MOVIE2)
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)
    syncplay.group.set_queue.assert_not_awaited()


async def test_sync_queue_ignored_cases(env):
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)  # no state
    state, _ = _playing(env)
    await env.cog._on_sync_queue(GID, None, 0.0, True)  # no item
    await env.cog._on_sync_queue(GID, "m1", 0.0, True)  # already playing it
    env.client.get_item.assert_not_awaited()
    await env.cog._on_sync_queue(GID, "zz", 0.0, True)  # get_item → None
    env.client.get_item.assert_awaited_once_with("zz")
    env.start.assert_not_awaited()
    env.cog.client = None
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)  # unconfigured


async def test_sync_queue_echo_window(env, monkeypatch):
    state, _ = _playing(env)
    env.client.get_item = AsyncMock(return_value=MOVIE2)
    now = [1000.0]
    monkeypatch.setattr(jf.time, "monotonic", lambda: now[0])
    state.sync_sent["m2"] = 995.0
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)  # 5 s ago: our own echo
    env.client.get_item.assert_not_awaited()
    now[0] = 995.0 + jf._SYNC_ECHO_WINDOW + 0.1
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)
    env.client.get_item.assert_awaited_once()
    assert state.now.item is MOVIE2


async def test_sync_queue_records_send_time(env, syncplay, monkeypatch):
    monkeypatch.setattr(jf.time, "monotonic", lambda: 42.0)
    await env.cog._play_item(_make_ctx(), MOVIE)
    await _drain(env.cog)
    assert env.cog._states[GID].sync_sent == {"m1": 42.0}


async def test_sync_queue_requires_bot_in_voice(env):
    _playing(env)
    env.vc.is_connected.return_value = False
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)
    env.guild.voice_client = None
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)
    env.bot.get_guild = lambda gid: None
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)
    env.client.get_item.assert_not_awaited()


async def test_sync_echo_window_uses_real_clock(env):
    state, _ = _playing(env)
    state.sync_sent["m2"] = time.monotonic()
    await env.cog._on_sync_queue(GID, "m2", 0.0, True)
    env.client.get_item.assert_not_awaited()


# ── teardown when the bot leaves voice ───────────────────────────────────────


def _vs(channel):
    return SimpleNamespace(channel=channel)


async def test_bot_leaving_voice_tears_down(env):
    state, _ = _playing(env)
    group = state.syncplay = _group()
    state.queue = [MOVIE2]
    member = SimpleNamespace(id=999, guild=env.guild)
    await env.cog.on_voice_state_update(member, _vs(env.channel), _vs(None))
    assert GID not in env.cog._states
    group.close.assert_awaited_once()
    env.client.stop_encoding.assert_awaited_once_with("ps-m1")
    assert state.generation == 1  # pending on_end callbacks become no-ops


@pytest.mark.parametrize(
    "member_id, before, after",
    [
        (5, "chan", None),          # someone else left
        (999, None, "chan"),        # bot joined
        (999, "chan", "other"),     # bot moved channels
    ],
)
async def test_voice_updates_that_keep_state(env, member_id, before, after):
    _playing(env)
    member = SimpleNamespace(id=member_id, guild=env.guild)
    await env.cog.on_voice_state_update(member, _vs(before), _vs(after))
    assert GID in env.cog._states


async def test_voice_update_before_login(env):
    _playing(env)
    env.bot.user = None
    member = SimpleNamespace(id=999, guild=env.guild)
    await env.cog.on_voice_state_update(member, _vs("chan"), _vs(None))
    assert GID in env.cog._states


async def test_teardown_variants(env):
    await env.cog._teardown(GID)  # nothing to forget
    env.cog._state(GID)  # idle, no group
    await env.cog._teardown(GID)
    assert GID not in env.cog._states
    env.client.stop_encoding.assert_not_awaited()
    _playing(env)
    env.cog.client = None
    await env.cog._teardown(GID)
    assert GID not in env.cog._states


# ── setup() credentials ──────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "env_vars, configured, syncplay_on",
    [
        ({"JELLYFIN_URL": "http://jf", "JELLYFIN_USERNAME": "bot",
          "JELLYFIN_PASSWORD": "pw"}, True, True),
        ({"JELLYFIN_URL": "http://jf", "JELLYFIN_API_KEY": "k",
          "JELLYFIN_USERNAME": "bot", "JELLYFIN_PASSWORD": "pw"}, True, True),
        ({"JELLYFIN_URL": "http://jf", "JELLYFIN_API_KEY": "k"}, True, False),
        ({"JELLYFIN_URL": "http://jf", "JELLYFIN_USERNAME": "bot"}, False, False),
        ({"JELLYFIN_URL": "http://jf", "JELLYFIN_PASSWORD": "pw"}, False, False),
        ({"JELLYFIN_USERNAME": "bot", "JELLYFIN_PASSWORD": "pw"}, False, False),
    ],
)
async def test_setup_credentials(
    monkeypatch, caplog, env_vars, configured, syncplay_on
):
    for var in (
        "JELLYFIN_URL", "JELLYFIN_API_KEY", "JELLYFIN_USERNAME", "JELLYFIN_PASSWORD"
    ):
        monkeypatch.delenv(var, raising=False)
    for k, v in env_vars.items():
        monkeypatch.setenv(k, v)
    bot = MagicMock()
    bot.add_cog = AsyncMock()
    with caplog.at_level(logging.INFO, logger="cogs.jellyfin"):
        await jf.setup(bot)
    cog = bot.add_cog.call_args.args[0]
    if not configured:
        assert cog.client is None
        return
    assert cog.client.has_user_credentials is syncplay_on
    assert ("SyncPlay enabled" in caplog.text) is syncplay_on
    assert cog.client._api_key == env_vars.get("JELLYFIN_API_KEY", "")


def test_guild_state_defaults():
    state = _GuildState(send=AsyncMock())
    assert state.queue == [] and state.now is None and state.syncplay is None
    assert state.generation == 0 and state.sync_sent == {}


async def test_sync_unpause_echo_of_own_play_does_not_seek():
    """Our own !media play echoes back as Unpause with the server's (lagging)
    position estimate; within the own-command window we must not seek."""
    import cogs.jellyfin as jf

    cog = jf.Jellyfin(MagicMock(), MagicMock())
    player = MagicMock(paused=True, position=600.0)
    cog.bot.video_players = {7: player}
    state = cog._state(7, AsyncMock())
    state.sync_own_command_at = jf.time.monotonic()
    await cog._on_sync_command(7, "Unpause", 590.0)  # 10 s "drift"
    player.seek.assert_not_called()
    player.resume.assert_called_once()


# ── review fixes: start serialisation, generation restore, background sync ───


async def test_starts_are_serialised_per_guild(env):
    release = asyncio.Event()
    order: list[str] = []

    async def slow_start(bot_, send, g, ch, vc_, **kw):
        order.append(f"start {kw['url']}")
        if kw["url"].endswith("m1.m3u8"):
            await release.wait()
        bot_.video_players[g.id] = FakePlayer(position=kw["start_offset"])

    env.start.side_effect = slow_start
    orig_playback = env.client.start_playback.side_effect

    def playback(item_id):
        order.append(f"playback {item_id}")
        return orig_playback(item_id)

    env.client.start_playback.side_effect = playback
    ctx = _make_ctx()
    first = asyncio.create_task(env.cog._play_item(ctx, MOVIE))
    second = asyncio.create_task(env.cog._play_item(ctx, MOVIE2))
    for _ in range(20):
        await asyncio.sleep(0)
    assert order == ["playback m1", "start http://jf/m1.m3u8"]  # second waits
    assert env.cog._states[GID].start_lock.locked()
    release.set()
    assert await asyncio.gather(first, second) == [True, True]
    assert order[2:] == ["playback m2", "start http://jf/m2.m3u8"]
    state = env.cog._states[GID]
    assert state.now.item is MOVIE2 and state.generation == 2


async def test_failed_start_restores_generation_for_running_stream(env):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE)
    on_end = env.start.call_args.kwargs["on_end"]
    state = env.cog._states[GID]
    state.queue = [MOVIE3]
    env.client.start_playback = AsyncMock(side_effect=[None, _stream_for("m3")])
    assert await env.cog._play_item(ctx, MOVIE2) is False  # MOVIE keeps playing
    assert state.generation == 1 and state.now.item is MOVIE
    await on_end()  # so MOVIE's natural end still auto-advances
    assert state.now.item is MOVIE3


async def test_failed_start_without_running_stream_keeps_generation(env):
    env.client.start_playback = AsyncMock(return_value=None)
    await env.cog._play_item(_make_ctx(), MOVIE)
    assert env.cog._states[GID].generation == 1


async def test_failed_go_live_after_replacing_stream_keeps_generation(env):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE)

    async def replace_then_fail(bot_, send, g, *a, **kw):
        bot_.video_players.pop(g.id, None)  # old stream cancelled, new failed

    env.start.side_effect = replace_then_fail
    assert await env.cog._play_item(ctx, MOVIE2) is False
    assert env.cog._states[GID].generation == 2


async def test_natural_end_during_start_does_not_advance(env):
    ctx = _make_ctx()
    await env.cog._play_item(ctx, MOVIE)
    on_end = env.start.call_args.kwargs["on_end"]
    state = env.cog._states[GID]
    state.queue = [MOVIE3]
    probing = asyncio.Event()
    finish = asyncio.Event()

    async def slow_playback(item_id):
        probing.set()
        await finish.wait()
        return _stream_for(item_id)

    env.client.start_playback = AsyncMock(side_effect=slow_playback)
    task = asyncio.create_task(env.cog._play_item(ctx, MOVIE2))
    await probing.wait()
    await on_end()  # MOVIE ends while MOVIE2 is still starting
    assert state.queue == [MOVIE3]
    finish.set()
    assert await task is True
    assert state.now.item is MOVIE2 and state.queue == [MOVIE3]


async def test_no_background_sync_without_user_credentials(env):
    await env.cog._play_item(_make_ctx(), MOVIE)
    assert env.cog._tasks == set()


class _FrameEvent:
    def __init__(self, shown: bool) -> None:
        self.shown = shown
        self.timeouts: list[float] = []

    def wait(self, timeout=None):
        self.timeouts.append(timeout)
        return self.shown


async def test_sync_queue_uses_position_once_first_frame_shown(env, syncplay):
    player = FakePlayer(position=63.5)
    player.first_frame_sent = _FrameEvent(shown=True)
    env.bot.video_players[GID] = player
    await env.cog._sync_queue(env.guild, "m1", 60.0, player)
    assert player.first_frame_sent.timeouts == [60.0]
    syncplay.group.set_queue.assert_awaited_once_with("m1", 63.5)


async def test_sync_queue_keeps_start_when_no_frame_yet(env, syncplay):
    player = FakePlayer(position=99.0)
    player.first_frame_sent = _FrameEvent(shown=False)
    env.bot.video_players[GID] = player
    await env.cog._sync_queue(env.guild, "m1", 60.0, player)
    syncplay.group.set_queue.assert_awaited_once_with("m1", 60.0)


async def test_sync_queue_skipped_when_player_replaced(env, syncplay):
    player = FakePlayer()
    env.bot.video_players[GID] = FakePlayer()  # a newer stream
    await env.cog._sync_queue(env.guild, "m1", 0.0, player)
    syncplay.cls.assert_not_called()
    syncplay.group.set_queue.assert_not_awaited()


async def test_sync_queue_without_player(env, syncplay):
    await env.cog._sync_queue(env.guild, "m1", 5.0)
    syncplay.group.set_queue.assert_awaited_once_with("m1", 5.0)


async def test_concurrent_syncs_create_one_group(env, syncplay):
    gate = asyncio.Event()

    async def slow_open(name):
        await gate.wait()

    syncplay.group.open.side_effect = slow_open
    tasks = [
        asyncio.create_task(env.cog._sync_queue(env.guild, i, 0.0)) for i in ("a", "b")
    ]
    for _ in range(10):
        await asyncio.sleep(0)
    gate.set()
    await asyncio.gather(*tasks)
    assert syncplay.cls.call_count == 1
    assert syncplay.group.set_queue.await_count == 2


async def test_spawned_task_is_tracked_until_done(env):
    done = asyncio.Event()

    async def job():
        await done.wait()

    env.cog._spawn(job())
    assert len(env.cog._tasks) == 1
    done.set()
    await _drain(env.cog)
    assert env.cog._tasks == set()


# ── review fixes: title fall-through for command words ───────────────────────


@pytest.mark.parametrize(
    "command, word",
    [
        (Jellyfin.media_help, "help"),
        (Jellyfin.media_pause, "pause"),
        (Jellyfin.media_play, "play"),
        (Jellyfin.media_stop, "stop"),
        (Jellyfin.media_skip, "skip"),
        (Jellyfin.media_playlist, "playlist"),
    ],
)
async def test_command_word_with_text_searches_title(env, command, word):
    env.client.search.return_value = [MOVIE]
    ctx = _make_ctx()
    await command.callback(env.cog, ctx, rest="  making sense ")
    env.client.search.assert_awaited_once_with(f"{word} making sense")
    assert env.cog._states[GID].now.item is MOVIE
    env.vc.disconnect.assert_not_awaited()


async def test_title_fallthrough_ignores_blank_rest(env):
    ctx = _make_ctx()
    assert await env.cog._title_fallthrough(ctx, "stop", "   ") is False
    env.client.search.assert_not_awaited()


# ── review fixes: teardown skipped while a start holds the lock ──────────────


async def test_voice_leave_during_start_keeps_state(env):
    state, _ = _playing(env)
    member = SimpleNamespace(id=999, guild=env.guild)
    async with state.start_lock:  # ensure_voice dropping a stale client
        await env.cog.on_voice_state_update(member, _vs(env.channel), _vs(None))
    assert env.cog._states[GID] is state
    await env.cog.on_voice_state_update(member, _vs(env.channel), _vs(None))
    assert GID not in env.cog._states



# ── !media aspect-ratio ──────────────────────────────────────────────────────


async def test_aspect_lists_options_and_applies_choice_live(env):
    _, player = _playing(env)
    env.bot.wait_for = AsyncMock(return_value=_reply("3"))
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx)
    listing = _sent(ctx)[0]
    assert "reply with a number" in listing
    assert "`1` Original (default)" in listing and "← current" in listing
    assert "`5` 1.85:1" in listing
    assert player.aspect_sets == [4 / 3]
    assert env.cog._aspect[GID] == 4 / 3
    assert _sent(ctx)[-1] == "📐 aspect ratio set to **4:3** — applying now"


async def test_aspect_direct_number_and_original_resets(env):
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="2")
    assert env.cog._aspect[GID] == 16 / 9
    assert _sent(ctx)[-1] == "📐 aspect ratio set to **16:9**"  # nothing playing
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="1")
    assert GID not in env.cog._aspect
    assert _sent(ctx)[-1] == "📐 aspect ratio set to **Original (default)**"


async def test_aspect_marks_current_choice(env):
    env.cog._aspect[GID] = 2.39
    env.bot.wait_for = AsyncMock(side_effect=TimeoutError)
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx)
    listing = _sent(ctx)[0]
    assert "`4` 2.39:1 — cinema scope (21:9)  ← current" in listing
    assert "`1` Original (default) — keep the media's own shape\n" in listing + "\n"
    assert _sent(ctx)[-1] == "timed out — cancelled"
    assert env.cog._aspect[GID] == 2.39


@pytest.mark.parametrize("bad", ["0", "6", "-1", "2x"])
async def test_aspect_rejects_bad_choice(env, bad):
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice=bad)
    assert "pick a number between 1 and 5" in _sent(ctx)[0]
    assert GID not in env.cog._aspect


async def test_aspect_same_as_player_does_not_restart(env):
    _, player = _playing(env)
    player.aspect = 16 / 9
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="2")
    assert player.aspect_sets == []
    assert _sent(ctx)[-1] == "📐 aspect ratio set to **16:9**"


async def test_aspect_no_guild(env):
    env.resolve.return_value = (None, None, None)
    env.bot.guilds = []
    ctx = _make_ctx(None)
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="2")
    assert _sent(ctx) == ["you're not in a voice channel"]


async def test_aspect_is_passed_to_new_streams_and_survives_teardown(env):
    env.cog._aspect[GID] = 1.85
    ctx = _make_ctx(env.guild)
    await env.cog._play_item(ctx, MOVIE)
    assert env.start.call_args.kwargs["aspect"] == 1.85
    await env.cog._teardown(GID)
    assert env.cog._aspect[GID] == 1.85



async def test_aspect_with_title_text_searches_instead(env):
    env.cog.media = MagicMock()
    env.cog.media.callback = AsyncMock()
    ctx = _make_ctx(env.guild)
    ctx.invoked_with = "ar"
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="rahman")
    env.cog.media.callback.assert_awaited_once_with(env.cog, ctx, query="ar rahman")
    assert GID not in env.cog._aspect


async def test_aspect_does_not_restart_non_jellyfin_stream(env):
    """A TV/IPTV player in video_players must not be restarted (live -ss stall)."""
    player = FakePlayer()
    env.bot.video_players[GID] = player  # no Jellyfin state.now
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="2")
    assert player.aspect_sets == []
    assert env.cog._aspect[GID] == 16 / 9
    assert _sent(ctx)[-1].endswith("applies to the next Jellyfin item")


async def test_aspect_finished_player_not_restarted(env):
    _, player = _playing(env)
    player.active = False
    ctx = _make_ctx(env.guild)
    await Jellyfin.media_aspect.callback(env.cog, ctx, choice="3")
    assert player.aspect_sets == []
    assert _sent(ctx)[-1] == "📐 aspect ratio set to **4:3**"
