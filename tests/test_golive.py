"""Tests for streaming/golive.py (go-live connection, audio sender, encryption).

No network: the main gateway, voice websocket, UDP socket and SocketReader are
all replaced with in-memory fakes.
"""

from __future__ import annotations

import asyncio
import io
import struct
import threading
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import nacl.secret
import pytest

import streaming.golive as golive
from streaming.golive import (
    GoLiveAudioSender,
    GoLiveConnection,
    _av_sync_ms,
    _encrypt_audio,
    _GoLiveVCProxy,
)

_KEY = list(range(32))
_FRAME = GoLiveAudioSender._FRAME_SIZE


# ── helpers ──────────────────────────────────────────────────────────────────


class _FastEvent(threading.Event):
    def __init__(self) -> None:
        super().__init__()
        self.waits: list[float | None] = []

    def wait(self, timeout=None):
        self.waits.append(timeout)
        return self.is_set()


class _RegularVC:
    supported_modes = ("aead_xchacha20_poly1305_rtpsize", "xsalsa20_poly1305_lite")

    def __init__(self, user_id, session_id):
        self.user = SimpleNamespace(id=user_id)
        self.session_id = session_id


def _make_vc(user_id=42, session_id="sess"):
    return _RegularVC(user_id, session_id)


def _make_conn(**kw) -> GoLiveConnection:
    bot = SimpleNamespace(ws=MagicMock())
    bot.ws.send_as_json = AsyncMock()
    return GoLiveConnection(
        bot, kw.get("guild_id", 1), kw.get("channel_id", 2), _make_vc()
    )


@pytest.fixture(autouse=True)
def _clear_env(monkeypatch):
    monkeypatch.delenv("STREAM_AV_SYNC_MS", raising=False)


# ── _av_sync_ms ──────────────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "raw,expected",
    [
        (None, 0),
        ("", 0),
        ("120", 120),
        ("-80", -80),
        ("99999", 5000),
        ("-99999", -5000),
        ("abc", 0),
        ("1.5", 0),
    ],
)
def test_av_sync_ms(monkeypatch, raw, expected):
    if raw is not None:
        monkeypatch.setenv("STREAM_AV_SYNC_MS", raw)
    assert _av_sync_ms() == expected


# ── _encrypt_audio ───────────────────────────────────────────────────────────

_HDR = bytes(range(12))


def test_encrypt_audio_aead_rtpsize():
    pkt = _encrypt_audio(_HDR, b"opus", "aead_xchacha20_poly1305_rtpsize", _KEY, 5)
    assert pkt[:12] == _HDR and pkt[-4:] == struct.pack(">I", 5)
    plain = nacl.secret.Aead(bytes(_KEY)).decrypt(
        pkt[12:-4], _HDR, pkt[-4:] + bytes(20)
    )
    assert plain == b"opus"


def test_encrypt_audio_xsalsa20():
    pkt = _encrypt_audio(_HDR, b"opus", "xsalsa20_poly1305", _KEY, 0)
    assert (
        nacl.secret.SecretBox(bytes(_KEY)).decrypt(pkt[12:], _HDR + bytes(12))
        == b"opus"
    )


def test_encrypt_audio_suffix():
    pkt = _encrypt_audio(_HDR, b"opus", "xsalsa20_poly1305_suffix", _KEY, 0)
    assert nacl.secret.SecretBox(bytes(_KEY)).decrypt(pkt[12:-24], pkt[-24:]) == b"opus"


def test_encrypt_audio_lite():
    pkt = _encrypt_audio(_HDR, b"opus", "xsalsa20_poly1305_lite", _KEY, 0xDEADBEEF)
    assert pkt[-4:] == b"\xde\xad\xbe\xef"
    plain = nacl.secret.SecretBox(bytes(_KEY)).decrypt(pkt[12:-4], pkt[-4:] + bytes(20))
    assert plain == b"opus"


def test_encrypt_audio_unknown_mode():
    with pytest.raises(ValueError, match="rot13"):
        _encrypt_audio(_HDR, b"", "rot13", _KEY, 0)


# ── GoLiveConnection: properties & DAVE state ────────────────────────────────


def test_connection_properties():
    conn = _make_conn(guild_id=10, channel_id=20)
    assert conn.guild_id == 10 and conn.channel_id == 20
    assert conn.session_id == "sess"
    assert conn.user.id == 42
    assert conn.voice_client is conn._regular_vc
    assert conn.supported_modes == _RegularVC.supported_modes
    assert conn.max_dave_protocol_version == golive.davey_compat.DAVE_PROTOCOL_VERSION
    assert conn.ssrc == 0 and conn.mode == "" and conn.secret_key == []


def test_can_encrypt():
    conn = _make_conn()
    assert conn.can_encrypt is False
    conn.dave_protocol_version = 1
    assert conn.can_encrypt is False
    conn.dave_session = SimpleNamespace(ready=False)
    assert conn.can_encrypt is False
    conn.dave_session.ready = True
    assert conn.can_encrypt is True


async def test_reinit_dave_creates_session(monkeypatch):
    conn = _make_conn()
    conn.server_id = 1000
    conn.dave_protocol_version = 1
    conn.ws = SimpleNamespace(send_binary=AsyncMock())
    created = MagicMock()
    created.get_serialized_key_package.return_value = b"kp"
    factory = MagicMock(return_value=created)
    monkeypatch.setattr(golive.davey_compat, "DaveSession", factory)
    await conn.reinit_dave_session()
    factory.assert_called_once_with(1, 42, 999)  # channel id = server_id - 1
    assert conn.dave_session is created
    assert created._voice_state is conn
    conn.ws.send_binary.assert_awaited_once_with(
        golive.DiscordVoiceWebSocket.MLS_KEY_PACKAGE, b"kp"
    )


async def test_reinit_dave_reuses_existing_session():
    conn = _make_conn()
    conn.server_id = 50
    conn.dave_protocol_version = 1
    conn.ws = SimpleNamespace(send_binary=AsyncMock())
    sess = MagicMock()
    sess.get_serialized_key_package.return_value = b"kp2"
    conn.dave_session = sess
    await conn.reinit_dave_session()
    sess.reinit.assert_called_once_with(1, 42, 49)
    conn.ws.send_binary.assert_awaited_once()


async def test_reinit_dave_version_zero_resets_to_passthrough():
    conn = _make_conn()
    sess = MagicMock()
    conn.dave_session = sess
    await conn.reinit_dave_session()
    sess.reset.assert_called_once()
    sess.set_passthrough_mode.assert_called_once_with(True, 10)


async def test_reinit_dave_version_zero_no_session_is_noop():
    conn = _make_conn()
    await conn.reinit_dave_session()
    assert conn.dave_session is None


async def test_recover_from_invalid_commit():
    conn = _make_conn()
    conn.ws = SimpleNamespace(send_as_json=AsyncMock())
    conn.reinit_dave_session = AsyncMock()
    await conn._recover_from_invalid_commit(7)
    conn.ws.send_as_json.assert_awaited_once_with(
        {
            "op": golive.DiscordVoiceWebSocket.MLS_INVALID_COMMIT_WELCOME,
            "d": {"transition_id": 7},
        }
    )
    conn.reinit_dave_session.assert_awaited_once()


async def test_execute_transition_unknown_id_is_ignored():
    conn = _make_conn()
    conn.dave_protocol_version = 1
    await conn._execute_transition(3)
    assert conn.dave_protocol_version == 1


async def test_execute_transition_downgrade_then_upgrade():
    conn = _make_conn()
    conn.dave_protocol_version = 1
    conn.dave_session = MagicMock()
    conn.dave_pending_transitions = {5: 0, 6: 1}
    await conn._execute_transition(5)
    assert conn.dave_protocol_version == 0 and conn.dave_downgraded is True
    await conn._execute_transition(6)
    assert conn.dave_protocol_version == 1 and conn.dave_downgraded is False
    conn.dave_session.set_passthrough_mode.assert_called_once_with(True, 10)
    assert conn.dave_pending_transitions == {}


async def test_execute_transition_same_version_no_flags():
    conn = _make_conn()
    conn.dave_protocol_version = 1
    conn.dave_pending_transitions = {0: 1}
    await conn._execute_transition(0)
    assert conn.dave_downgraded is False


# ── GoLiveConnection: socket helpers ────────────────────────────────────────


def test_socket_listener_registration():
    conn = _make_conn()
    cb = object()
    conn.add_socket_listener(cb)  # no reader yet → no-op
    conn.remove_socket_listener(cb)
    reader = MagicMock()
    conn._socket_reader = reader
    conn.add_socket_listener(cb)
    conn.remove_socket_listener(cb)
    reader.register.assert_called_once_with(cb)
    reader.unregister.assert_called_once_with(cb)


def test_send_packet_and_oserror_swallowed():
    conn = _make_conn()
    conn.socket = MagicMock()
    conn.send_packet(b"pkt")
    conn.socket.sendall.assert_called_once_with(b"pkt")
    conn.socket.sendall.side_effect = OSError("unreachable")
    conn.send_packet(b"pkt")  # no raise


# ── GoLiveConnection: connect() ─────────────────────────────────────────────


class _FakeVoiceWS:
    """Stand-in for DiscordVoiceWebSocket driving the READY/SESSION handshake."""

    def __init__(self, conn):
        self.conn = conn
        self.secret_key = None
        self.polls = 0
        self.client_connect = AsyncMock()
        self.close = AsyncMock()

    async def poll_event(self):
        self.polls += 1
        if self.conn.ip is None:
            self.conn.ip, self.conn.port, self.conn.ssrc = "1.2.3.4", 5000, 77
        elif self.secret_key is None:
            self.secret_key = _KEY
            self.conn.mode = "aead_xchacha20_poly1305_rtpsize"
        else:
            await asyncio.sleep(3600)


@pytest.fixture
def net(monkeypatch):
    """Patch UDP socket, SocketReader and the voice websocket factory."""
    ns = SimpleNamespace()
    ns.sock = MagicMock()
    fake_socket_mod = SimpleNamespace(
        socket=MagicMock(return_value=ns.sock), AF_INET=2, SOCK_DGRAM=2
    )
    monkeypatch.setattr(golive, "socket", fake_socket_mod)
    ns.reader = MagicMock()
    ns.reader_cls = MagicMock(return_value=ns.reader)
    monkeypatch.setattr(golive, "SocketReader", ns.reader_cls)

    async def from_state(conn, resume=False):
        ns.ws = _FakeVoiceWS(conn)
        return ns.ws

    monkeypatch.setattr(
        golive.DiscordVoiceWebSocket, "from_connection_state", staticmethod(from_state)
    )
    return ns


def _gateway(conn, create=None, server=None):
    """Make bot.ws.wait_for return futures, resolving them if data given."""
    loop = asyncio.get_running_loop()
    seen = {}

    def wait_for(event, predicate):
        fut = loop.create_future()
        seen[event] = (fut, predicate)
        data = create if event == "STREAM_CREATE" else server
        if data is not None:
            fut.set_result(data)
        return fut

    conn._bot.ws.wait_for = MagicMock(side_effect=wait_for)
    return seen


async def test_connect_full_handshake(net):
    conn = _make_conn(guild_id=1, channel_id=2)
    key = "guild:1:2:42"
    seen = _gateway(
        conn,
        create={"rtc_server_id": "9001", "stream_key": key},
        server={"endpoint": "wss://stream.discord.media:443", "token": "tok"},
    )
    await conn.connect(timeout=1)
    try:
        sent = [c.args[0] for c in conn._bot.ws.send_as_json.await_args_list]
        assert sent[0] == {
            "op": 18,
            "d": {
                "type": "guild",
                "guild_id": "1",
                "channel_id": "2",
                "preferred_region": None,
            },
        }
        assert sent[1] == {"op": 22, "d": {"stream_key": key, "paused": False}}
        # predicates filter on our stream key
        for _fut, pred in seen.values():
            assert pred({"stream_key": key}) is True
            assert pred({"stream_key": "other"}) is False
            assert pred({}) is False
        assert conn.server_id == 9001
        assert conn._stream_key == key
        assert conn.endpoint == "stream.discord.media:443"
        assert conn.token == "tok"
        assert conn.socket is net.sock
        net.sock.setblocking.assert_called_once_with(False)
        net.reader_cls.assert_called_once_with(conn, start_paused=False)
        net.reader.start.assert_called_once()
        assert conn.ws is net.ws
        assert conn.ip == "1.2.3.4" and net.ws.secret_key == _KEY
        net.ws.client_connect.assert_awaited_once()
        assert conn._poll_task is not None and not conn._poll_task.done()
    finally:
        await conn.disconnect()
    assert conn._poll_task.cancelled() or conn._poll_task.done()


async def test_connect_endpoint_without_scheme(net):
    conn = _make_conn()
    _gateway(
        conn,
        create={"rtc_server_id": "1", "stream_key": "k"},
        server={"endpoint": "plain.host:80", "token": "t"},
    )
    await conn.connect(timeout=1)
    assert conn.endpoint == "plain.host:80"
    await conn.disconnect()


async def test_connect_times_out_waiting_for_stream_create(net):
    conn = _make_conn()
    _gateway(conn)  # never resolved
    with pytest.raises(asyncio.TimeoutError):
        await conn.connect(timeout=0.01)
    net.reader_cls.assert_not_called()
    assert conn.server_id is None


# ── GoLiveConnection: _poll_ws / disconnect ──────────────────────────────────


async def test_poll_ws_stops_on_error(caplog):
    conn = _make_conn()
    conn.ws = SimpleNamespace(
        poll_event=AsyncMock(side_effect=[None, RuntimeError("x")])
    )
    with caplog.at_level("WARNING", logger="streaming.golive"):
        await conn._poll_ws()
    assert "poller ended" in caplog.text


async def test_poll_ws_swallows_cancel():
    conn = _make_conn()

    async def forever():
        await asyncio.sleep(3600)

    conn.ws = SimpleNamespace(poll_event=forever)
    task = asyncio.create_task(conn._poll_ws())
    await asyncio.sleep(0)
    task.cancel()
    await task  # CancelledError handled inside → task completes normally
    assert task.done() and not task.cancelled()


async def test_disconnect_releases_everything():
    conn = _make_conn()
    conn._stream_key = "guild:1:2:42"
    conn.ws = SimpleNamespace(close=AsyncMock())
    reader = MagicMock()
    conn._socket_reader = reader
    conn.socket = MagicMock()

    async def forever():
        await asyncio.sleep(3600)

    conn._poll_task = asyncio.create_task(forever())
    await asyncio.sleep(0)
    await conn.disconnect()
    conn._bot.ws.send_as_json.assert_awaited_once_with(
        {"op": 19, "d": {"stream_key": "guild:1:2:42"}}
    )
    assert conn._poll_task.cancelled()
    conn.ws.close.assert_awaited_once()
    reader.stop.assert_called_once()
    assert conn._socket_reader is None
    conn.socket.close.assert_called_once()


async def test_disconnect_tolerates_failures():
    conn = _make_conn()
    conn._stream_key = "k"
    conn._bot.ws.send_as_json.side_effect = RuntimeError("gateway gone")
    conn.ws = SimpleNamespace(close=AsyncMock(side_effect=RuntimeError("ws")))
    conn.socket = MagicMock()
    conn.socket.close.side_effect = OSError
    await conn.disconnect()  # nothing raises


async def test_disconnect_before_connect_is_noop():
    conn = _make_conn()
    await conn.disconnect()
    conn._bot.ws.send_as_json.assert_not_awaited()


# ── _GoLiveVCProxy ───────────────────────────────────────────────────────────


def test_vc_proxy_exposes_connection_state():
    conn = _make_conn()
    conn.ssrc = 500
    conn.mode = "xsalsa20_poly1305_lite"
    conn.secret_key = _KEY
    proxy = _GoLiveVCProxy(conn)
    assert proxy.ssrc == 500
    assert proxy._connection is conn
    assert proxy.mode == "xsalsa20_poly1305_lite"
    assert proxy.secret_key is _KEY
    conn.mode = "aead_xchacha20_poly1305_rtpsize"
    assert proxy.mode == "aead_xchacha20_poly1305_rtpsize"  # live view


# ── GoLiveAudioSender ────────────────────────────────────────────────────────


class _FakeEncoder:
    def __init__(self):
        self.calls = 0

    def encode(self, pcm, frame_size):
        assert len(pcm) == _FRAME and frame_size == 960
        self.calls += 1
        return b"OPUS" + bytes([pcm[0]])


class _AudioConn:
    def __init__(self, mode="xsalsa20_poly1305_lite", dave=None):
        self.mode = mode
        self.secret_key = _KEY
        self.ssrc = 0x1234
        self.dave_session = dave
        self.can_encrypt = dave is not None
        self.sent: list[bytes] = []

    def send_packet(self, pkt):
        self.sent.append(pkt)


def _pcm(n_frames: int, extra: int = 0) -> io.BytesIO:
    return io.BytesIO(
        b"".join(bytes([i]) * _FRAME for i in range(n_frames)) + b"\x00" * extra
    )


def _decrypt_lite(pkt):
    plain = nacl.secret.SecretBox(bytes(_KEY)).decrypt(pkt[12:-4], pkt[-4:] + bytes(20))
    return pkt[:12], plain, struct.unpack(">I", pkt[-4:])[0]


@pytest.fixture
def fake_opus(monkeypatch):
    monkeypatch.setattr(golive._opus, "Encoder", _FakeEncoder)


def _sender(f, conn, **kw):
    s = GoLiveAudioSender(f, conn, **kw)
    s._end = _FastEvent()
    return s


def test_audio_sender_sends_rtp_packets(fake_opus):
    conn = _AudioConn()
    s = _sender(_pcm(3, extra=100), conn)
    s._send_audio()
    assert len(conn.sent) == 3
    for i, pkt in enumerate(conn.sent):
        hdr, payload, nonce = _decrypt_lite(pkt)
        assert hdr[0] == 0x80 and hdr[1] == 0x78
        seq, ts, ssrc = struct.unpack(">HII", hdr[2:])
        assert (seq, ts, ssrc) == (i, i * 960, 0x1234)
        assert payload == b"OPUS" + bytes([i])
        assert nonce == i
    assert s._seq == 3 and s._ts == 2880 and s._nonce == 3


def test_audio_sender_dave_encrypts_when_ready(fake_opus):
    dave = MagicMock()
    dave.encrypt_opus.side_effect = lambda b: b"E2E" + b
    conn = _AudioConn(dave=dave)
    _sender(_pcm(1), conn)._send_audio()
    assert _decrypt_lite(conn.sent[0])[1] == b"E2EOPUS\x00"


def test_audio_sender_positive_av_sync_discards_backlog(fake_opus, monkeypatch):
    monkeypatch.setenv("STREAM_AV_SYNC_MS", "40")  # 2 frames
    conn = _AudioConn()
    _sender(_pcm(5), conn)._send_audio()
    payloads = [_decrypt_lite(p)[1] for p in conn.sent]
    assert payloads == [b"OPUS\x02", b"OPUS\x03", b"OPUS\x04"]


def test_audio_sender_positive_av_sync_stops_at_eof(fake_opus, monkeypatch):
    monkeypatch.setenv("STREAM_AV_SYNC_MS", "1000")
    conn = _AudioConn()
    _sender(_pcm(2), conn)._send_audio()
    assert conn.sent == []


def test_audio_sender_negative_av_sync_delays_start(fake_opus, monkeypatch):
    monkeypatch.setenv("STREAM_AV_SYNC_MS", "-250")
    conn = _AudioConn()
    s = _sender(_pcm(1), conn)
    s._send_audio()
    assert s._end.waits[0] == pytest.approx(0.25)
    assert len(conn.sent) == 1


def test_audio_sender_waits_through_fallback_gap(fake_opus):
    """A short read while the source is still active is a FIFO gap, not EOF."""

    class GapFile:
        def __init__(self):
            self.reads = [bytes([1]) * _FRAME, b"", b"", bytes([2]) * _FRAME, b""]

        def read(self, n):
            return self.reads.pop(0) if self.reads else b""

    f = GapFile()
    active = iter([True, True, False])
    conn = _AudioConn()
    s = _sender(f, conn, is_source_active=lambda: next(active))
    s._send_audio()
    assert [_decrypt_lite(p)[1] for p in conn.sent] == [b"OPUS\x01", b"OPUS\x02"]
    assert 0.05 in s._end.waits


def test_audio_sender_stop_during_gap(fake_opus):
    conn = _AudioConn()
    s = _sender(io.BytesIO(b""), conn, is_source_active=lambda: True)
    s._end.wait = lambda timeout=None: True  # stop arrives while waiting
    s._send_audio()
    assert conn.sent == []


def test_audio_sender_stop_breaks_loop(fake_opus):
    conn = _AudioConn()
    s = _sender(_pcm(10), conn)
    orig = conn.send_packet

    def send_then_stop(pkt):
        orig(pkt)
        s.stop()

    conn.send_packet = send_then_stop
    s._send_audio()
    assert len(conn.sent) == 1


def test_audio_sender_paces_with_stop_event(fake_opus, monkeypatch):
    # Freeze the clock so every frame is "early" and must wait.
    monkeypatch.setattr(golive.time, "perf_counter", lambda: 100.0)
    conn = _AudioConn()
    s = _sender(_pcm(3), conn)
    s._send_audio()
    assert s._end.waits == pytest.approx([0.02, 0.04, 0.06])


def test_audio_sender_run_logs_errors(caplog, monkeypatch):
    class Boom:
        def __init__(self):
            raise RuntimeError("no opus")

    monkeypatch.setattr(golive._opus, "Encoder", Boom)
    s = GoLiveAudioSender(io.BytesIO(b""), _AudioConn())
    with caplog.at_level("ERROR", logger="streaming.golive"):
        s.run()
    assert "GoLiveAudioSender error" in caplog.text


def test_audio_sender_thread_end_to_end(fake_opus):
    conn = _AudioConn(mode="aead_xchacha20_poly1305_rtpsize")
    s = GoLiveAudioSender(_pcm(2), conn)
    s._end = _FastEvent()
    s.start()
    s.join(timeout=5)
    assert not s.is_alive()
    assert len(conn.sent) == 2
    assert s.daemon and s.name == "GoLiveAudio"


def test_audio_sender_with_real_opus():
    import discord.opus as opus

    if not opus.is_loaded():
        try:
            opus._load_default()
        except Exception:
            pass
    if not opus.is_loaded():
        pytest.skip("libopus not available")
    conn = _AudioConn()
    s = _sender(_pcm(2), conn)
    s._send_audio()
    assert len(conn.sent) == 2
    _, payload, _ = _decrypt_lite(conn.sent[0])
    assert 0 < len(payload) < _FRAME  # real Opus is compressed


async def test_disconnect_is_idempotent_single_stream_delete():
    """A second disconnect() must not re-send STREAM_DELETE: the key is reused
    for the next go-live in the same channel, so a late duplicate would end it."""
    conn = _make_conn()
    conn._stream_key = "guild:1:2:42"
    await conn.disconnect()
    await conn.disconnect()
    conn._bot.ws.send_as_json.assert_awaited_once()
    assert conn._stream_key is None
