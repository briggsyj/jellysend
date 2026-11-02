"""Tests for services/tvheadend.py — TVheadendClient API wrapper."""
from __future__ import annotations

import base64
import json
import urllib.error
from unittest.mock import MagicMock, patch

import pytest

from services.tvheadend import TVheadendClient


def _mock_urlopen(payload) -> MagicMock:
    resp = MagicMock()
    resp.read.return_value = json.dumps(payload).encode()
    resp.__enter__ = lambda s: s
    resp.__exit__ = MagicMock(return_value=False)
    return resp


@pytest.fixture
def client() -> TVheadendClient:
    return TVheadendClient("http://tvh.local:9981/", "user", "p@ss:w/rd")


# ── construction ────────────────────────────────────────────────────────────


def test_init_strips_trailing_slash_and_builds_auth(client):
    assert client.base_url == "http://tvh.local:9981"
    expected = base64.b64encode(b"user:p@ss:w/rd").decode()
    assert client._auth == f"Basic {expected}"


def test_from_env_all_set(monkeypatch):
    monkeypatch.setenv("TVHEADEND_URL", "http://h:1")
    monkeypatch.setenv("TVHEADEND_USER", "u")
    monkeypatch.setenv("TVHEADEND_PASS", "p")
    c = TVheadendClient.from_env()
    assert isinstance(c, TVheadendClient)
    assert c.base_url == "http://h:1" and c.user == "u" and c.password == "p"


@pytest.mark.parametrize("missing", ["TVHEADEND_URL", "TVHEADEND_USER", "TVHEADEND_PASS"])
def test_from_env_missing_any_returns_none(monkeypatch, missing):
    for k in ("TVHEADEND_URL", "TVHEADEND_USER", "TVHEADEND_PASS"):
        monkeypatch.setenv(k, "x")
    monkeypatch.delenv(missing)
    assert TVheadendClient.from_env() is None


def test_from_env_empty_value_returns_none(monkeypatch):
    monkeypatch.setenv("TVHEADEND_URL", "http://h")
    monkeypatch.setenv("TVHEADEND_USER", "u")
    monkeypatch.setenv("TVHEADEND_PASS", "")
    assert TVheadendClient.from_env() is None


# ── get_channels ────────────────────────────────────────────────────────────


async def test_get_channels_filters_disabled_and_sorts(client):
    payload = {
        "entries": [
            {"name": "C", "number": 3, "uuid": "c"},
            {"name": "Off", "number": 1, "enabled": False, "uuid": "off"},
            {"name": "NoNum", "uuid": "n"},
            {"name": "A", "number": 1, "enabled": True, "uuid": "a"},
        ]
    }
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(payload)) as uo:
        chs = await client.get_channels()
    assert [c["uuid"] for c in chs] == ["a", "c", "n"]
    req = uo.call_args.args[0]
    assert req.full_url == "http://tvh.local:9981/api/channel/grid?limit=99999"
    assert req.get_header("Authorization") == client._auth


async def test_get_channels_empty(client):
    with patch("urllib.request.urlopen", return_value=_mock_urlopen({})):
        assert await client.get_channels() == []


async def test_get_channels_network_error_propagates(client):
    with patch("urllib.request.urlopen", side_effect=urllib.error.URLError("x")):
        with pytest.raises(urllib.error.URLError):
            await client.get_channels()


# ── get_epg_events ──────────────────────────────────────────────────────────


async def test_get_epg_events_quotes_query_and_filters(client):
    payload = {
        "entries": [
            {"title": "The News & Weather"},
            {"title": "Unrelated"},
            {"other": "no title"},
        ]
    }
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(payload)) as uo:
        events = await client.get_epg_events("news & w", limit=5)
    assert events == [{"title": "The News & Weather"}]
    url = uo.call_args.args[0].full_url
    assert url == "http://tvh.local:9981/api/epg/events/grid?limit=5&title=news%20%26%20w"


# ── get_now_playing ─────────────────────────────────────────────────────────


async def test_get_now_playing_filters_by_time_and_caches(client):
    payload = {
        "entries": [
            {"channelUuid": "a", "start": 900, "stop": 1100, "title": "Now A"},
            {"channelUuid": "a", "start": 950, "stop": 1200, "title": "Dup A"},
            {"channelUuid": "b", "start": 1100, "stop": 1200, "title": "Future B"},
            {"channelUuid": "c", "start": 800, "stop": 1000, "title": "Ended C"},
            {"channelUuid": "", "start": 900, "stop": 1100, "title": "No uuid"},
            {"channelUuid": "d", "start": 1000, "stop": 1001},
        ]
    }
    with patch("services.tvheadend.time.time", return_value=1000.0), patch(
        "urllib.request.urlopen", return_value=_mock_urlopen(payload)
    ) as uo:
        result = await client.get_now_playing()
        assert result == {"a": "Now A", "d": ""}
        # Second call within 60 s returns cached data without another request.
        again = await client.get_now_playing()
    assert again is result
    assert uo.call_count == 1


async def test_get_now_playing_cache_expires(client):
    clock = [1000.0]
    with patch("services.tvheadend.time.time", side_effect=lambda: clock[0]), patch(
        "urllib.request.urlopen",
        side_effect=[_mock_urlopen({"entries": []}), _mock_urlopen({"entries": []})],
    ) as uo:
        await client.get_now_playing()
        clock[0] = 1059.0
        await client.get_now_playing()
        assert uo.call_count == 1
        clock[0] = 1061.0
        await client.get_now_playing()
    assert uo.call_count == 2


# ── stream URLs ─────────────────────────────────────────────────────────────


def test_stream_url_embeds_quoted_credentials(client):
    assert (
        client.stream_url("abc")
        == "http://user:p%40ss%3Aw%2Frd@tvh.local:9981/stream/channel/abc"
    )


def test_stream_url_without_port():
    c = TVheadendClient("https://tvh.example", "u", "p")
    assert c.stream_url("x") == "https://u:p@tvh.example/stream/channel/x"


def test_safe_stream_url_redacts_password(client):
    url = client.safe_stream_url("abc")
    assert url == "http://user:***@tvh.local:9981/stream/channel/abc"
    assert "p@ss" not in url


def test_safe_stream_url_without_port():
    c = TVheadendClient("https://tvh.example", "u", "p")
    assert c.safe_stream_url("x") == "https://u:***@tvh.example/stream/channel/x"
