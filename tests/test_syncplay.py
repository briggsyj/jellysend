"""Tests for services/syncplay.py — the Jellyfin SyncPlay group client.

aiohttp is replaced by in-memory fakes: no sockets, and asyncio.sleep in the
keepalive loop is patched so nothing really waits.
"""
from __future__ import annotations

import asyncio
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import aiohttp
import pytest

import services.syncplay as sp
from services.syncplay import (
    SyncPlayGroup,
    seconds_to_ticks,
    ticks_to_seconds,
)

# ── fakes ────────────────────────────────────────────────────────────────────


class FakeResp:
    def __init__(self, status=204, text=""):
        self.status = status
        self._text = text

    async def text(self):
        return self._text

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False


class FakeWS:
    def __init__(self, messages=(), error: Exception | None = None):
        self._messages = list(messages)
        self._error = error
        self.closed = False
        self.sent: list[str] = []
        self.close_calls = 0
        self.send_error: Exception | None = None

    def __aiter__(self):
        return self

    async def __anext__(self):
        if self._messages:
            return self._messages.pop(0)
        if self._error is not None:
            raise self._error
        raise StopAsyncIteration

    async def send_str(self, data):
        if self.send_error is not None:
            raise self.send_error
        self.sent.append(data)

    async def close(self):
        self.close_calls += 1
        self.closed = True


class FakeSession:
    instances: list[FakeSession] = []

    def __init__(self, headers=None):
        self.headers = headers
        self.posts: list[tuple[str, dict]] = []
        self.statuses: dict[str, FakeResp] = {}
        self.ws = FakeWS()
        self.ws_args = None
        self.closed = False
        FakeSession.instances.append(self)

    def post(self, url, data=None):
        self.posts.append((url, json.loads(data)))
        path = url.split("8096", 1)[-1]
        return self.statuses.get(path, FakeResp())

    async def ws_connect(self, url, heartbeat=None):
        self.ws_args = (url, heartbeat)
        return self.ws

    async def close(self):
        self.closed = True


def _msg(kind, data=None):
    return SimpleNamespace(type=kind, data=data)


def _text(obj):
    return _msg(aiohttp.WSMsgType.TEXT, json.dumps(obj))


@pytest.fixture
def fake_aiohttp(monkeypatch):
    FakeSession.instances = []
    monkeypatch.setattr(sp.aiohttp, "ClientSession", FakeSession)


def _client(base="http://jf.local:8096"):
    client = MagicMock()
    client.base_url = base
    client.authenticate = AsyncMock(return_value=("TOKEN", "user-1"))
    return client


def _group(client=None):
    return SyncPlayGroup(
        client or _client(),
        device_id="slopsoil-7",
        on_command=AsyncMock(),
        on_play_queue=AsyncMock(),
    )


def _open_session(group) -> FakeSession:
    """Attach a FakeSession without going through open()."""
    session = FakeSession()
    group._session = session
    group._ws = session.ws
    return session


async def _drain():
    for _ in range(10):
        await asyncio.sleep(0)


# ── tick helpers ─────────────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "seconds, ticks",
    [(0, 0), (1.5, 15_000_000), (3600, 36_000_000_000), (0.00000004, 0)],
)
def test_seconds_to_ticks(seconds, ticks):
    assert seconds_to_ticks(seconds) == ticks


@pytest.mark.parametrize(
    "ticks, seconds",
    [(15_000_000, 1.5), ("20000000", 2.0), (0, 0.0), (None, None), ("abc", None)],
)
def test_ticks_to_seconds(ticks, seconds):
    assert ticks_to_seconds(ticks) == seconds


# ── open / close ─────────────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "base, ws_prefix",
    [("http://jf.local:8096", "ws://jf.local:8096"), ("https://jf.local:8096", "wss://jf.local:8096")],
)
async def test_open_signs_in_connects_and_creates_group(fake_aiohttp, base, ws_prefix):
    client = _client(base)
    group = _group(client)
    await group.open("slopsoil · Guild")
    client.authenticate.assert_awaited_once_with("slopsoil-7")
    (session,) = FakeSession.instances
    auth = session.headers["Authorization"]
    assert 'Token="TOKEN"' in auth and 'DeviceId="slopsoil-7"' in auth
    assert session.headers["Content-Type"] == "application/json"
    assert session.ws_args == (
        f"{ws_prefix}/socket?api_key=TOKEN&deviceId=slopsoil-7", 30
    )
    assert session.posts == [
        (f"{base}/SyncPlay/New", {"GroupName": "slopsoil · Guild"}),
        (f"{base}/SyncPlay/SetIgnoreWait", {"IgnoreWait": True}),
    ]
    assert group.group_name == "slopsoil · Guild"
    assert group._reader is not None
    await group.close()


async def test_open_new_group_http_error_raises(fake_aiohttp, monkeypatch):
    orig_init = FakeSession.__init__

    def init(self, headers=None):
        orig_init(self, headers)
        self.statuses["/SyncPlay/New"] = FakeResp(403, "forbidden" * 50)

    monkeypatch.setattr(FakeSession, "__init__", init)
    group = _group()
    with pytest.raises(RuntimeError) as exc:
        await group.open("g")
    msg = str(exc.value)
    assert "/SyncPlay/New → HTTP 403" in msg
    assert len(msg.split(": ", 1)[1]) == 200  # body truncated
    assert group.group_name is None
    await group.close()


async def test_close_leaves_cancels_and_is_idempotent(fake_aiohttp):
    group = _group()
    session = _open_session(group)
    reader = asyncio.create_task(asyncio.sleep(3600))
    keepalive = asyncio.create_task(asyncio.sleep(3600))
    group._reader, group._keepalive = reader, keepalive
    await group.close()
    await _drain()
    assert group.closed
    assert session.posts[-1] == ("http://jf.local:8096/SyncPlay/Leave", {})
    assert reader.cancelled() and keepalive.cancelled()
    assert session.ws.close_calls == 1 and session.closed

    await group.close()  # second close is a no-op
    assert session.ws.close_calls == 1
    assert len(session.posts) == 1


async def test_close_tolerates_leave_and_ws_failures():
    group = _group()
    session = _open_session(group)
    session.statuses["/SyncPlay/Leave"] = FakeResp(500, "boom")
    session.ws.close = AsyncMock(side_effect=RuntimeError("ws gone"))
    done = asyncio.create_task(asyncio.sleep(0))
    await done
    group._reader = done  # already finished tasks are left alone
    await group.close()
    assert group.closed and session.closed


async def test_close_before_open():
    group = _group()
    await group.close()  # Leave fails (not open) and is swallowed
    assert group.closed


# ── REST requests ────────────────────────────────────────────────────────────


async def test_post_requires_open_session():
    with pytest.raises(RuntimeError, match="not open"):
        await _group().pause()


async def test_requests_bodies():
    group = _group()
    session = _open_session(group)
    await group.set_queue("item-9", 90.5)
    await group.set_queue("item-1")
    await group.pause()
    await group.unpause()
    await group.seek(12.25)
    base = "http://jf.local:8096/SyncPlay"
    assert session.posts == [
        (f"{base}/SetNewQueue", {
            "PlayingQueue": ["item-9"], "PlayingItemPosition": 0,
            "StartPositionTicks": 905_000_000,
        }),
        (f"{base}/SetNewQueue", {
            "PlayingQueue": ["item-1"], "PlayingItemPosition": 0,
            "StartPositionTicks": 0,
        }),
        (f"{base}/Pause", {}),
        (f"{base}/Unpause", {}),
        (f"{base}/Seek", {"PositionTicks": 122_500_000}),
    ]


async def test_request_error_status_raises():
    group = _group()
    session = _open_session(group)
    session.statuses["/SyncPlay/Seek"] = FakeResp(400, "bad ticks")
    with pytest.raises(RuntimeError, match=r"/SyncPlay/Seek → HTTP 400: bad ticks"):
        await group.seek(1)


# ── handle_message ───────────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "data, interval", [(60, 30.0), ("8", 5.0), (None, 30.0), (30, 15.0)]
)
async def test_force_keepalive_starts_loop_once(monkeypatch, data, interval):
    group = _group()
    _open_session(group)
    loop_mock = AsyncMock()
    monkeypatch.setattr(group, "_keepalive_loop", loop_mock)
    await group.handle_message({"MessageType": "ForceKeepAlive", "Data": data})
    first = group._keepalive
    await group.handle_message({"MessageType": "ForceKeepAlive", "Data": data})
    await _drain()
    loop_mock.assert_awaited_once_with(interval)
    # Once the first loop is done, a new ForceKeepAlive starts another.
    assert first.done()
    await group.handle_message({"MessageType": "ForceKeepAlive", "Data": data})
    await _drain()
    assert loop_mock.await_count == 2


async def test_sync_command_forwarded():
    group = _group()
    await group.handle_message({
        "MessageType": "SyncPlayCommand",
        "Data": {"Command": "Seek", "PositionTicks": 600_000_000},
    })
    await group.handle_message(
        {"MessageType": "SyncPlayCommand", "Data": {"Command": "Pause"}}
    )
    assert group._on_command.await_args_list[0].args == ("Seek", 60.0)
    assert group._on_command.await_args_list[1].args == ("Pause", None)


@pytest.mark.parametrize(
    "msg",
    [
        {"MessageType": "SyncPlayCommand", "Data": {"PositionTicks": 1}},  # no command
        {"MessageType": "SyncPlayCommand", "Data": "not a dict"},
        {"MessageType": "SyncPlayGroupUpdate", "Data": None},
        {"MessageType": "SyncPlayGroupUpdate",
         "Data": {"Type": "PlayQueue", "Data": []}},
        {"MessageType": "SyncPlayGroupUpdate",
         "Data": {"Type": "GroupJoined", "Data": "x"}},
        {"MessageType": "SyncPlayGroupUpdate",
         "Data": {"Type": "StateUpdate", "Data": {}}},
        {"MessageType": "UserDataChanged", "Data": {}},
        {},
    ],
)
async def test_ignored_messages(msg):
    group = _group()
    await group.handle_message(msg)
    group._on_command.assert_not_awaited()
    group._on_play_queue.assert_not_awaited()
    assert group.group_id is None


async def test_group_joined_sets_group_id():
    group = _group()
    await group.handle_message({
        "MessageType": "SyncPlayGroupUpdate",
        "Data": {"Type": "GroupJoined", "Data": {"GroupId": "g-42"}},
    })
    assert group.group_id == "g-42"


@pytest.mark.parametrize(
    "payload, expected",
    [
        (
            {"Playlist": [{"ItemId": "a"}, {"ItemId": "b"}], "PlayingItemIndex": 1,
             "StartPositionTicks": 300_000_000, "IsPlaying": True},
            ("b", 30.0, True),
        ),
        ({"Playlist": [{"ItemId": "a"}]}, ("a", 0.0, False)),
        ({"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": 5}, (None, 0.0, False)),
        ({"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": -1}, (None, 0.0, False)),
        ({"Playlist": [{"ItemId": "a"}], "PlayingItemIndex": "0"}, (None, 0.0, False)),
        ({"Playlist": None, "StartPositionTicks": "junk"}, (None, 0.0, False)),
    ],
)
async def test_play_queue_update(payload, expected):
    group = _group()
    await group.handle_message({
        "MessageType": "SyncPlayGroupUpdate",
        "Data": {"Type": "PlayQueue", "Data": payload},
    })
    group._on_play_queue.assert_awaited_once_with(*expected)


@pytest.mark.parametrize(
    "kind, level, text",
    [
        ("GroupLeft", "INFO", "GroupLeft"),
        ("NotInGroup", "INFO", "NotInGroup"),
        ("GroupDoesNotExist", "INFO", "GroupDoesNotExist"),
        ("CreateGroupDenied", "WARNING", "SyncPlay permission"),
        ("JoinGroupDenied", "WARNING", "SyncPlay permission"),
        ("LibraryAccessDenied", "WARNING", "SyncPlay permission"),
    ],
)
async def test_group_status_updates_are_logged(caplog, kind, level, text):
    group = _group()
    with caplog.at_level("INFO", logger="services.syncplay"):
        await group.handle_message({
            "MessageType": "SyncPlayGroupUpdate", "Data": {"Type": kind},
        })
    rec = [r for r in caplog.records if text in r.getMessage()]
    assert rec and rec[0].levelname == level


# ── websocket reader ─────────────────────────────────────────────────────────


async def test_read_loop_dispatches_text_and_skips_other_types(caplog):
    group = _group()
    session = _open_session(group)
    session.ws._messages = [
        _msg(aiohttp.WSMsgType.BINARY, b"\x00"),
        _msg(aiohttp.WSMsgType.PING),
        _text({"MessageType": "SyncPlayCommand", "Data": {"Command": "Unpause"}}),
        _msg(aiohttp.WSMsgType.TEXT, "{not json"),
        _text({"MessageType": "SyncPlayCommand", "Data": {"Command": "Pause"}}),
    ]
    with caplog.at_level("WARNING", logger="services.syncplay"):
        await group._read_loop()
    commands = [c.args[0] for c in group._on_command.await_args_list]
    assert commands == ["Unpause", "Pause"]
    assert "error handling message" in caplog.text
    assert "websocket closed" in caplog.text  # ended while the group was open


@pytest.mark.parametrize("kind", [aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR])
async def test_read_loop_stops_on_close_or_error(kind):
    group = _group()
    session = _open_session(group)
    session.ws._messages = [
        _msg(kind),
        _text({"MessageType": "SyncPlayCommand", "Data": {"Command": "Pause"}}),
    ]
    await group._read_loop()
    group._on_command.assert_not_awaited()


async def test_read_loop_handler_error_is_logged_and_loop_continues(caplog):
    group = _group()
    session = _open_session(group)
    group._on_command.side_effect = [RuntimeError("cb"), None]
    cmd = {"MessageType": "SyncPlayCommand", "Data": {"Command": "Seek"}}
    session.ws._messages = [_text(cmd), _text(cmd)]
    with caplog.at_level("ERROR", logger="services.syncplay"):
        await group._read_loop()
    assert group._on_command.await_count == 2
    assert "error handling message" in caplog.text


async def test_read_loop_iteration_error_logged_quietly_when_closed(caplog):
    group = _group()
    session = _open_session(group)
    session.ws._error = aiohttp.ClientError("reset")
    group.closed = True
    with caplog.at_level("WARNING", logger="services.syncplay"):
        await group._read_loop()
    assert "websocket reader ended" in caplog.text
    assert "websocket closed for group" not in caplog.text


async def test_read_loop_propagates_cancellation():
    group = _group()
    session = _open_session(group)
    session.ws._error = asyncio.CancelledError()
    with pytest.raises(asyncio.CancelledError):
        await group._read_loop()


# ── keepalive ────────────────────────────────────────────────────────────────


async def test_keepalive_loop_sends_until_ws_closes(monkeypatch):
    group = _group()
    session = _open_session(group)
    sleeps = []

    async def fake_sleep(seconds):
        sleeps.append(seconds)
        if len(sleeps) == 3:
            session.ws.closed = True

    monkeypatch.setattr(sp.asyncio, "sleep", fake_sleep)
    await group._keepalive_loop(15.0)
    assert sleeps == [15.0, 15.0, 15.0]
    sent = [json.loads(s) for s in session.ws.sent]
    assert sent == [{"MessageType": "KeepAlive"}] * 3


async def test_keepalive_loop_stops_on_send_failure(monkeypatch):
    group = _group()
    session = _open_session(group)
    session.ws.send_error = ConnectionResetError("gone")
    monkeypatch.setattr(sp.asyncio, "sleep", AsyncMock())
    await group._keepalive_loop(1.0)  # returns instead of looping forever
    assert session.ws.sent == []


async def test_keepalive_loop_without_ws():
    await _group()._keepalive_loop(1.0)  # no socket → returns immediately
