"""Tests for streaming/engine.py — stream lifecycle orchestration.

ensure_voice, the H.264 player, the go-live connection and the audio sender
are all faked; no FFmpeg, no network.  Audio FIFOs are plain files in tmp_path
so the real asyncio.to_thread(open, ...) path is exercised.
"""

from __future__ import annotations

import asyncio
import threading
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

import streaming.engine as engine
from streaming.engine import (
    _safe_url,
    cancel_live_stream,
    cancel_stream,
    start_live_stream,
    start_stream,
)

GID = 111


# ── fakes ────────────────────────────────────────────────────────────────────


class FakePlayer:
    instances: list[FakePlayer] = []
    fifo_path = ""

    def __init__(
        self, url, voice_client, fps, live, audio, probe_size, start_offset=0.0,
        aspect=None,
    ):
        self.aspect = aspect
        self.url = url
        self.start_offset = start_offset
        self.pause_gate = threading.Event()
        self.pause_gate.set()
        self.voice_client = voice_client
        self.fps = fps
        self.live = live
        self.audio = audio
        self.probe_size = probe_size
        self.started = False
        self.stopped = 0
        self.join_event = None  # threading-free: set → join returns
        FakePlayer.instances.append(self)

    @property
    def audio_fifo(self):
        return FakePlayer.fifo_path

    def start(self):
        self.started = True

    def stop(self):
        self.stopped += 1

    def is_source_active(self):
        return True

    @property
    def stop_requested(self):
        return self.stopped > 0

    def join(self):
        return None


class FakeAudioSender:
    instances: list[FakeAudioSender] = []

    def __init__(self, file_obj, conn, is_source_active, pause_gate=None):
        self.pause_gate = pause_gate
        self.file_obj = file_obj
        self.conn = conn
        self.is_source_active = is_source_active
        self.started = False
        self.stopped = False
        self.alive = True
        FakeAudioSender.instances.append(self)

    def start(self):
        self.started = True

    def stop(self):
        self.stopped = True

    def is_alive(self):
        return self.alive

    def join(self, timeout=None):
        return None


class FakeGoLive:
    instances: list[FakeGoLive] = []
    connect_error: Exception | None = None

    def __init__(self, bot, guild_id, channel_id, vc):
        self.bot, self.guild_id, self.channel_id, self.vc = (
            bot,
            guild_id,
            channel_id,
            vc,
        )
        self.ssrc = 900
        self.mode = "xsalsa20_poly1305_lite"
        self.secret_key = [0] * 32
        self.connect = AsyncMock(side_effect=FakeGoLive.connect_error)
        self.disconnect = AsyncMock()
        FakeGoLive.instances.append(self)


class FakeVC:
    def __init__(self, playing=False):
        self.channel = SimpleNamespace(id=5, name="vc")
        self.ws = SimpleNamespace(client_connect=AsyncMock())
        self._playing = playing
        self.stopped = 0
        self.played = []  # (source, after)

    def is_playing(self):
        return self._playing

    def stop(self):
        self.stopped += 1
        self._playing = False

    def play(self, src, after):
        self.played.append((src, after))
        self._playing = True


@pytest.fixture
def env(monkeypatch, tmp_path):
    FakePlayer.instances = []
    FakeAudioSender.instances = []
    FakeGoLive.instances = []
    FakeGoLive.connect_error = None
    fifo = tmp_path / "audio.fifo"
    fifo.write_bytes(b"\x00" * 3840)
    FakePlayer.fifo_path = str(fifo)

    vc = FakeVC()
    ensure = AsyncMock(return_value=vc)
    monkeypatch.setattr(engine, "ensure_voice", ensure)
    monkeypatch.setattr(engine, "_VIDEO_ENCODER", object())
    monkeypatch.setattr(engine, "H264VideoPlayer", FakePlayer)
    monkeypatch.setattr(engine, "GoLiveConnection", FakeGoLive)
    monkeypatch.setattr(engine, "GoLiveAudioSender", FakeAudioSender)
    monkeypatch.setattr(engine, "_stream_fps", lambda: 30.0)
    ffmpeg_pcm = MagicMock(name="FFmpegPCMAudio")
    monkeypatch.setattr(engine.discord, "FFmpegPCMAudio", ffmpeg_pcm)
    monkeypatch.setattr(
        engine.discord,
        "PCMVolumeTransformer",
        lambda raw, volume: SimpleNamespace(raw=raw, volume=volume),
    )

    bot = SimpleNamespace(stream_tasks={}, video_players={}, live_connections={})
    guild = SimpleNamespace(id=GID, change_voice_state=AsyncMock())
    channel = SimpleNamespace(id=222)
    send = AsyncMock()
    return SimpleNamespace(
        bot=bot,
        guild=guild,
        channel=channel,
        send=send,
        vc=vc,
        ensure=ensure,
        ffmpeg_pcm=ffmpeg_pcm,
        fifo=fifo,
    )


async def _settle(n: int = 20) -> None:
    for _ in range(n):
        await asyncio.sleep(0)


async def _wait_for(pred, timeout=2.0):
    loop = asyncio.get_running_loop()
    end = loop.time() + timeout
    while not pred():
        if loop.time() > end:
            raise AssertionError("condition not reached")
        await asyncio.sleep(0.005)


# ── _safe_url ────────────────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "url,expected",
    [
        ("http://user:pw@host:8080/live.ts", "http://host:8080/live.ts"),
        ("http://host/x?api_key=SECRET&channel=5", "http://host/x?channel=5"),
        ("http://h/x?Token=a&PASSWORD=b&q=1&flag=", "http://h/x?q=1&flag="),
        ("https://h/path", "https://h/path"),
        ("/local/file.mkv", "/local/file.mkv"),
    ],
)
def test_safe_url(url, expected):
    assert _safe_url(url) == expected


# ── cancel_stream / cancel_live_stream ──────────────────────────────────────


async def test_cancel_stream_cancels_task_and_stops_player():
    task = asyncio.create_task(asyncio.sleep(3600))
    vp = MagicMock()
    bot = SimpleNamespace(stream_tasks={GID: task}, video_players={GID: vp})
    cancel_stream(bot, GID)
    await _settle()
    assert task.cancelled()
    vp.stop.assert_called_once()
    assert bot.stream_tasks == {} and bot.video_players == {}


async def test_cancel_stream_done_task_and_failing_player():
    task = asyncio.create_task(asyncio.sleep(0))
    await task
    vp = MagicMock()
    vp.stop.side_effect = RuntimeError("boom")
    bot = SimpleNamespace(stream_tasks={GID: task}, video_players={GID: vp})
    cancel_stream(bot, GID)  # swallowed
    assert bot.video_players == {}


def test_cancel_stream_nothing_active():
    bot = SimpleNamespace(stream_tasks={}, video_players={})
    cancel_stream(bot, GID)
    cancel_live_stream(bot, GID)


async def test_cancel_live_stream_reuses_slots_but_leaves_connection():
    task = asyncio.create_task(asyncio.sleep(3600))
    vp = MagicMock()
    conn = object()
    bot = SimpleNamespace(
        stream_tasks={GID: task}, video_players={GID: vp}, live_connections={GID: conn}
    )
    cancel_live_stream(bot, GID)
    await _settle()
    assert task.cancelled()
    vp.stop.assert_called_once()
    # live_connections is the task's finally block's job
    assert bot.live_connections == {GID: conn}


# ── start_stream (camera path) ──────────────────────────────────────────────


async def _start(env, **kw):
    kw.setdefault("title", "News")
    kw.setdefault("url", "http://u:p@host/live.ts")
    await start_stream(env.bot, env.send, env.guild, env.channel, None, **kw)


async def test_start_stream_with_video_registers_and_plays(env):
    await _start(env, subtitle="ch 5", live=False, audio=False, probe_size=9)
    env.ensure.assert_awaited_once_with(env.channel, None)
    env.guild.change_voice_state.assert_awaited_once_with(
        channel=env.vc.channel, self_deaf=True, self_video=True
    )
    env.vc.ws.client_connect.assert_awaited_once()
    env.send.assert_awaited_with("▶ **News** (ch 5)")

    (vp,) = FakePlayer.instances
    assert vp.started
    assert (vp.url, vp.voice_client, vp.fps) == (
        "http://u:p@host/live.ts",
        env.vc,
        30.0,
    )
    assert (vp.live, vp.audio, vp.probe_size) == (False, False, 9)
    assert env.bot.video_players[GID] is vp
    task = env.bot.stream_tasks[GID]

    await _wait_for(lambda: env.vc.played)
    src, after = env.vc.played[0]
    assert isinstance(src.raw, engine._AudioPipeSource)
    assert src.volume == 1.0
    assert src.raw.read() == b"\x00" * 3840  # reads the opened FIFO

    after(None)  # playback finished cleanly
    after(None)  # second call ignored (future already done)
    await task
    assert GID not in env.bot.stream_tasks
    assert GID not in env.bot.video_players
    assert vp.stopped == 1
    src.raw.cleanup()


async def test_start_stream_no_subtitle_label(env):
    await _start(env)
    env.send.assert_awaited_with("▶ **News**")
    env.bot.stream_tasks[GID].cancel()
    await _settle()


async def test_start_stream_audio_only_without_encoder(env, monkeypatch):
    monkeypatch.setattr(engine, "_VIDEO_ENCODER", None)
    await _start(env, url="http://host/audio.mp3")
    env.guild.change_voice_state.assert_not_awaited()
    env.vc.ws.client_connect.assert_not_awaited()
    assert FakePlayer.instances == []
    assert GID not in env.bot.video_players
    await _wait_for(lambda: env.vc.played)
    env.ffmpeg_pcm.assert_called_once()
    args, kwargs = env.ffmpeg_pcm.call_args
    assert args == ("http://host/audio.mp3",)
    assert "-reconnect 1" in kwargs["before_options"]
    assert kwargs["options"] == "-vn -ar 48000 -ac 2"
    src, after = env.vc.played[0]
    assert src.raw is env.ffmpeg_pcm.return_value
    task = env.bot.stream_tasks[GID]
    after(None)
    await task
    assert env.bot.stream_tasks == {}


async def test_start_stream_playback_error_reports_and_cleans_up(env):
    await _start(env)
    task = env.bot.stream_tasks[GID]
    await _wait_for(lambda: env.vc.played)
    _, after = env.vc.played[0]
    after(RuntimeError("decoder died"))
    await task
    env.send.assert_awaited_with("stream error: decoder died", delete_after=10)
    assert env.bot.stream_tasks == {} and env.bot.video_players == {}
    assert FakePlayer.instances[0].stopped == 1


async def test_start_stream_error_report_failure_is_swallowed(env):
    await _start(env)
    env.send.side_effect = RuntimeError("channel deleted")
    task = env.bot.stream_tasks[GID]
    await _wait_for(lambda: env.vc.played)
    env.vc.played[0][1](RuntimeError("x"))
    await task  # no exception escapes
    assert env.bot.stream_tasks == {}


async def test_start_stream_fifo_timeout(env, monkeypatch):
    async def timeout(aw, timeout):
        aw.close()
        raise TimeoutError

    monkeypatch.setattr(engine.asyncio, "wait_for", timeout)
    await _start(env)
    task = env.bot.stream_tasks[GID]
    await task
    env.send.assert_awaited_with("stream failed to start", delete_after=10)
    assert env.vc.played == []
    assert env.bot.stream_tasks == {} and env.bot.video_players == {}
    assert FakePlayer.instances[0].stopped == 1


async def test_start_stream_cancel_cleans_up(env):
    await _start(env)
    await _wait_for(lambda: env.vc.played)
    vp = FakePlayer.instances[0]
    cancel_stream(env.bot, GID)
    await _settle()
    assert env.bot.stream_tasks == {} and env.bot.video_players == {}
    assert vp.stopped >= 1


async def test_start_stream_replaces_previous_and_stops_playback(env):
    env.vc._playing = True
    old_task = asyncio.create_task(asyncio.sleep(3600))
    old_vp = MagicMock()
    env.bot.stream_tasks[GID] = old_task
    env.bot.video_players[GID] = old_vp
    await _start(env)
    assert env.vc.stopped == 1
    await _settle()
    assert old_task.cancelled()
    old_vp.stop.assert_called_once()
    assert env.bot.video_players[GID] is FakePlayer.instances[0]
    env.bot.stream_tasks[GID].cancel()
    await _settle()


async def test_stale_stream_cleanup_preserves_newer_stream_when_loop_yields(env):
    """With a send() that yields (like a real HTTP send) the old task's finally
    runs before the new stream registers, so the new stream survives."""

    async def yielding_send(*a, **k):
        await asyncio.sleep(0)
        await asyncio.sleep(0)

    env.send = yielding_send
    await _start(env, title="A")
    await _wait_for(lambda: env.vc.played)
    await _start(env, title="B")
    await _settle()
    new_vp = FakePlayer.instances[1]
    assert env.bot.video_players.get(GID) is new_vp
    assert GID in env.bot.stream_tasks
    assert new_vp.stopped == 0
    env.bot.stream_tasks[GID].cancel()
    await _settle()


async def test_stale_stream_cleanup_does_not_clobber_newer_stream(env):
    # env.send is an AsyncMock: it never yields, so the old task's finally
    # only gets to run after the new stream has registered its entries.
    await _start(env, title="A")
    await _wait_for(lambda: env.vc.played)
    await _start(env, title="B")
    await _settle()
    new_vp = FakePlayer.instances[1]
    try:
        assert env.bot.video_players.get(GID) is new_vp
        assert GID in env.bot.stream_tasks
        assert new_vp.stopped == 0
    finally:
        for t in list(env.bot.stream_tasks.values()):
            t.cancel()
        await _settle()


# ── start_live_stream (go-live path) ─────────────────────────────────────────


async def _start_live(env, **kw):
    kw.setdefault("title", "Match")
    kw.setdefault("url", "http://host/live.ts")
    await start_live_stream(env.bot, env.send, env.guild, env.channel, None, **kw)


async def test_start_live_requires_encoder(env, monkeypatch):
    monkeypatch.setattr(engine, "_VIDEO_ENCODER", None)
    await _start_live(env)
    env.send.assert_awaited_once_with(
        "go-live requires a working H.264 encoder; none found"
    )
    env.ensure.assert_not_awaited()
    assert env.bot.stream_tasks == {}


async def test_start_live_full_lifecycle(env):
    env.vc._playing = True
    await _start_live(env, subtitle="HD", live=None, audio=False, probe_size=5)
    assert env.vc.stopped == 1
    env.send.assert_any_await("📺 **Match** (HD) (go-live)")
    (conn,) = FakeGoLive.instances
    assert (conn.bot, conn.guild_id, conn.channel_id, conn.vc) == (
        env.bot,
        GID,
        222,
        env.vc,
    )
    conn.connect.assert_awaited_once_with(timeout=30.0)
    assert env.bot.live_connections[GID] is conn

    (vp,) = FakePlayer.instances
    assert isinstance(vp.voice_client, engine._GoLiveVCProxy)
    assert vp.voice_client._connection is conn
    assert vp.voice_client.ssrc == 900
    assert (vp.live, vp.audio, vp.probe_size) == (None, False, 5)
    assert vp.started and env.bot.video_players[GID] is vp

    task = env.bot.stream_tasks[GID]
    await task  # FakePlayer.join returns immediately → stream "ends"
    (sender,) = FakeAudioSender.instances
    assert sender.started and sender.conn is conn
    assert sender.is_source_active == vp.is_source_active
    assert sender.file_obj.closed  # FIFO reader released on cleanup
    assert sender.stopped
    assert vp.stopped == 1
    conn.disconnect.assert_awaited_once()
    assert env.bot.stream_tasks == {}
    assert env.bot.video_players == {}
    assert env.bot.live_connections == {}
    sender.file_obj.close()


async def test_start_live_dead_audio_sender_not_stopped(env, monkeypatch):
    orig_start = FakeAudioSender.start

    def start_and_die(self):
        orig_start(self)
        self.alive = False

    monkeypatch.setattr(FakeAudioSender, "start", start_and_die)
    await _start_live(env)
    await env.bot.stream_tasks[GID]
    sender = FakeAudioSender.instances[0]
    assert not sender.stopped
    sender.file_obj.close()


async def test_start_live_connect_failure(env):
    FakeGoLive.connect_error = RuntimeError("no STREAM_CREATE")
    await _start_live(env)
    env.send.assert_awaited_with(
        "go-live connection failed: no STREAM_CREATE", delete_after=10
    )
    assert env.bot.live_connections == {}
    assert FakePlayer.instances == []
    assert env.bot.stream_tasks == {}


async def test_start_live_connect_failure_disconnects(env):
    FakeGoLive.connect_error = RuntimeError("handshake failed")
    await _start_live(env)
    FakeGoLive.instances[0].disconnect.assert_awaited_once()


async def test_start_live_closes_audio_fifo(env):
    await _start_live(env)
    await env.bot.stream_tasks[GID]
    f = FakeAudioSender.instances[0].file_obj
    try:
        assert f.closed
    finally:
        f.close()


async def test_start_live_fifo_timeout(env, monkeypatch):
    async def timeout(aw, timeout):
        aw.close()
        raise TimeoutError

    monkeypatch.setattr(engine.asyncio, "wait_for", timeout)
    await _start_live(env)
    await env.bot.stream_tasks[GID]
    env.send.assert_awaited_with("go-live stream failed to start", delete_after=10)
    assert FakeAudioSender.instances == []
    FakeGoLive.instances[0].disconnect.assert_awaited_once()
    assert env.bot.live_connections == {} and env.bot.video_players == {}


async def test_start_live_error_reports_and_disconnect_errors_swallowed(
    env, monkeypatch
):
    def boom(self):
        raise RuntimeError("thread exploded")

    monkeypatch.setattr(FakeAudioSender, "start", boom)
    await _start_live(env)
    conn = FakeGoLive.instances[0]
    conn.disconnect.side_effect = RuntimeError("ws already closed")
    await env.bot.stream_tasks[GID]
    env.send.assert_awaited_with("go-live error: thread exploded", delete_after=10)
    assert env.bot.live_connections == {}
    FakeAudioSender.instances[0].file_obj.close()


async def test_start_live_error_report_failure_swallowed(env, monkeypatch):
    monkeypatch.setattr(
        FakeAudioSender, "start", lambda self: (_ for _ in ()).throw(RuntimeError("x"))
    )
    await _start_live(env)
    env.send.side_effect = RuntimeError("cannot send")
    await env.bot.stream_tasks[GID]
    assert env.bot.stream_tasks == {}
    FakeAudioSender.instances[0].file_obj.close()


async def test_start_live_cancel_tears_down_connection(env, monkeypatch):
    blocker = asyncio.Event()
    real_to_thread = asyncio.to_thread

    async def to_thread(fn, *a):
        if fn == FakePlayer.instances[0].join:
            await blocker.wait()  # stream "running" until cancelled
            return None
        return await real_to_thread(fn, *a)

    monkeypatch.setattr(engine.asyncio, "to_thread", to_thread)
    await _start_live(env)
    await _wait_for(lambda: FakeAudioSender.instances)
    conn = FakeGoLive.instances[0]
    cancel_live_stream(env.bot, GID)
    await _wait_for(lambda: conn.disconnect.await_count == 1)
    assert env.bot.live_connections == {}
    assert FakeAudioSender.instances[0].stopped
    FakeAudioSender.instances[0].file_obj.close()


# ── start_live_stream: start offset, pause gate, on_end ──────────────────────


async def test_start_live_passes_offset_and_pause_gate(env):
    await _start_live(env, start_offset=95.0)
    await env.bot.stream_tasks[GID]
    (vp,) = FakePlayer.instances
    assert vp.start_offset == 95.0
    (sender,) = FakeAudioSender.instances
    assert sender.pause_gate is vp.pause_gate
    sender.file_obj.close()


async def test_start_live_on_end_runs_after_natural_end(env):
    on_end = AsyncMock()
    await _start_live(env, on_end=on_end)
    await env.bot.stream_tasks[GID]
    await _wait_for(lambda: on_end.await_count == 1)
    # Cleanup ran before the callback was scheduled.
    assert env.bot.video_players == {} and env.bot.live_connections == {}
    FakeAudioSender.instances[0].file_obj.close()


async def test_start_live_on_end_skipped_when_stopped(env, monkeypatch):
    on_end = AsyncMock()

    def join_after_stop(self):
        self.stopped += 1  # stop() was called (replaced / !media stop)

    monkeypatch.setattr(FakePlayer, "join", join_after_stop)
    await _start_live(env, on_end=on_end)
    await env.bot.stream_tasks[GID]
    await _settle()
    on_end.assert_not_awaited()
    FakeAudioSender.instances[0].file_obj.close()


async def test_start_live_on_end_skipped_when_cancelled(env, monkeypatch):
    on_end = AsyncMock()
    blocker = asyncio.Event()
    real_to_thread = asyncio.to_thread

    async def to_thread(fn, *a):
        if fn == FakePlayer.instances[0].join:
            await blocker.wait()
            return None
        return await real_to_thread(fn, *a)

    monkeypatch.setattr(engine.asyncio, "to_thread", to_thread)
    await _start_live(env, on_end=on_end)
    await _wait_for(lambda: FakeAudioSender.instances)
    task = env.bot.stream_tasks[GID]
    cancel_live_stream(env.bot, GID)
    await _wait_for(task.done)
    await _settle()
    on_end.assert_not_awaited()
    FakeAudioSender.instances[0].file_obj.close()


async def test_start_live_on_end_without_callback(env):
    await _start_live(env)  # on_end=None: natural end, nothing scheduled
    await env.bot.stream_tasks[GID]
    await _settle()
    FakeAudioSender.instances[0].file_obj.close()


async def test_run_on_end_logs_exceptions(caplog):
    on_end = AsyncMock(side_effect=RuntimeError("advance failed"))
    with caplog.at_level("ERROR", logger="streaming.engine"):
        await engine._run_on_end(on_end, "Movie")
    on_end.assert_awaited_once()
    assert "end-of-stream callback failed for 'Movie'" in caplog.text



async def test_start_live_passes_forced_aspect(env):
    await _start_live(env, aspect=4 / 3)
    await env.bot.stream_tasks[GID]
    assert FakePlayer.instances[-1].aspect == 4 / 3
