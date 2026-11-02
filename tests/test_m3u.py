"""Tests for services/m3u.py — M3U parsing, EPG header extraction, fetching."""
from __future__ import annotations

import urllib.error
from unittest.mock import MagicMock, patch

import pytest

from services.m3u import _get_epg_url, _parse_attrs, fetch_and_parse, parse_m3u


def _mock_urlopen(body: bytes):
    resp = MagicMock()
    resp.read.return_value = body
    resp.__enter__ = lambda s: s
    resp.__exit__ = MagicMock(return_value=False)
    return resp


PLAYLIST = (
    '#EXTM3U url-tvg="http://epg.example/guide.xml"\n'
    '#EXTINF:-1 tvg-id="zorb1" tvg-name="Zorbax" group-title="News",Zorbax One\n'
    "http://stream.example/zorb1.m3u8\n"
    "\n"
    '#EXTINF:-1 tvg-id="flmb" group-title="Sports",Flumbo Network\n'
    "#EXTVLCOPT:http-user-agent=foo\n"
    "http://stream.example/flmb.ts\n"
)


# ── _parse_attrs ────────────────────────────────────────────────────────────


def test_parse_attrs_extracts_hyphenated_keys():
    attrs = _parse_attrs('#EXTINF:-1 tvg-id="a" group-title="B C",Name')
    assert attrs == {"tvg-id": "a", "group-title": "B C"}


def test_parse_attrs_empty_values_and_no_attrs():
    assert _parse_attrs('#EXTINF:-1 tvg-id="",X') == {"tvg-id": ""}
    assert _parse_attrs("#EXTINF:-1,X") == {}


# ── parse_m3u ───────────────────────────────────────────────────────────────


def test_parse_m3u_basic_channels():
    chs = parse_m3u(PLAYLIST)
    assert chs == [
        {
            "name": "Zorbax One",
            "tvg_id": "zorb1",
            "group": "News",
            "stream_url": "http://stream.example/zorb1.m3u8",
        },
        {
            "name": "Flumbo Network",
            "tvg_id": "flmb",
            "group": "Sports",
            "stream_url": "http://stream.example/flmb.ts",
        },
    ]


def test_parse_m3u_rejects_missing_header():
    with pytest.raises(ValueError, match="EXTM3U"):
        parse_m3u("<html>not a playlist</html>")


def test_parse_m3u_rejects_empty_text():
    with pytest.raises(ValueError):
        parse_m3u("")


def test_parse_m3u_header_with_leading_whitespace_ok():
    assert parse_m3u("   #EXTM3U\n") == []


def test_parse_m3u_falls_back_to_tvg_name_when_display_empty():
    text = '#EXTM3U\n#EXTINF:-1 tvg-name="Fallback",\nhttp://x/1\n'
    assert parse_m3u(text)[0]["name"] == "Fallback"


def test_parse_m3u_unknown_when_no_comma_and_no_tvg_name():
    text = "#EXTM3U\n#EXTINF:-1\nhttp://x/1\n"
    ch = parse_m3u(text)[0]
    assert ch["name"] == "unknown"
    assert ch["tvg_id"] == ""
    assert ch["group"] == ""


def test_parse_m3u_ignores_url_without_extinf():
    text = "#EXTM3U\nhttp://orphan/1\n#EXTINF:-1,Real\nhttp://x/real\n"
    chs = parse_m3u(text)
    assert [c["name"] for c in chs] == ["Real"]


def test_parse_m3u_extinf_without_url_is_dropped():
    text = "#EXTM3U\n#EXTINF:-1,Dangling\n"
    assert parse_m3u(text) == []


def test_parse_m3u_later_extinf_overrides_pending():
    text = "#EXTM3U\n#EXTINF:-1,First\n#EXTINF:-1,Second\nhttp://x/2\n"
    chs = parse_m3u(text)
    assert len(chs) == 1 and chs[0]["name"] == "Second"


def test_parse_m3u_display_name_with_comma_keeps_rest():
    text = '#EXTM3U\n#EXTINF:-1 tvg-id="a",News, Weather & Sport\nhttp://x\n'
    assert parse_m3u(text)[0]["name"] == "News, Weather & Sport"


# ── _get_epg_url ────────────────────────────────────────────────────────────


def test_get_epg_url_url_tvg():
    assert _get_epg_url(PLAYLIST) == "http://epg.example/guide.xml"


def test_get_epg_url_x_tvg_url():
    assert _get_epg_url('#EXTM3U x-tvg-url="http://e/x.xml.gz"\n') == "http://e/x.xml.gz"


def test_get_epg_url_prefers_url_tvg():
    text = '#EXTM3U x-tvg-url="http://b" url-tvg="http://a"\n'
    assert _get_epg_url(text) == "http://a"


def test_get_epg_url_none_cases():
    assert _get_epg_url("") is None
    assert _get_epg_url("not m3u\n") is None
    assert _get_epg_url("#EXTM3U\n") is None
    assert _get_epg_url('#EXTM3U url-tvg=""\n') is None


# ── fetch_and_parse ─────────────────────────────────────────────────────────


async def test_fetch_and_parse_success():
    with patch(
        "urllib.request.urlopen", return_value=_mock_urlopen(PLAYLIST.encode())
    ) as uo:
        chs, epg = await fetch_and_parse("http://pl.example/list.m3u")
    assert len(chs) == 2
    assert epg == "http://epg.example/guide.xml"
    req = uo.call_args.args[0]
    assert req.full_url == "http://pl.example/list.m3u"
    assert req.get_header("User-agent") == "slopsoil/1.0"
    assert uo.call_args.kwargs["timeout"] == 30


async def test_fetch_and_parse_no_epg():
    body = b"#EXTM3U\n#EXTINF:-1,A\nhttp://x\n"
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        chs, epg = await fetch_and_parse("http://pl")
    assert len(chs) == 1 and epg is None


async def test_fetch_and_parse_decodes_invalid_utf8_with_replacement():
    body = b"#EXTM3U\n#EXTINF:-1,Caf\xff\nhttp://x\n"
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        chs, _ = await fetch_and_parse("http://pl")
    assert chs[0]["name"] == "Caf�"


async def test_fetch_and_parse_invalid_playlist_raises_value_error():
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(b"<html>")):
        with pytest.raises(ValueError):
            await fetch_and_parse("http://pl")


async def test_fetch_and_parse_network_error_propagates():
    with patch(
        "urllib.request.urlopen", side_effect=urllib.error.URLError("refused")
    ):
        with pytest.raises(urllib.error.URLError):
            await fetch_and_parse("http://pl")
