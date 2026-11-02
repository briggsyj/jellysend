"""Tests for services/epg.py — XMLTV datetime parsing and now-playing extraction."""
from __future__ import annotations

import gzip
from datetime import UTC, datetime, timedelta, timezone
from unittest.mock import MagicMock, patch

import pytest

from services.epg import _parse_xmltv_dt, fetch_xmltv_now_playing


def _mock_urlopen(body: bytes):
    resp = MagicMock()
    resp.read.return_value = body
    resp.__enter__ = lambda s: s
    resp.__exit__ = MagicMock(return_value=False)
    return resp


def _fmt(dt: datetime) -> str:
    return dt.strftime("%Y%m%d%H%M%S +0000")


NOW = datetime.now(tz=UTC)
PAST = _fmt(NOW - timedelta(hours=2))
RECENT = _fmt(NOW - timedelta(minutes=30))
SOON = _fmt(NOW + timedelta(minutes=30))
LATER = _fmt(NOW + timedelta(hours=2))


def _xmltv(*programmes: str) -> bytes:
    return (
        '<?xml version="1.0"?><tv><channel id="a"><display-name>A</display-name></channel>'
        + "".join(programmes)
        + "</tv>"
    ).encode()


def _prog(ch: str, start: str, stop: str, title: str | None) -> str:
    t = f"<title>{title}</title>" if title is not None else ""
    return f'<programme channel="{ch}" start="{start}" stop="{stop}">{t}<desc>d</desc></programme>'


# ── _parse_xmltv_dt ─────────────────────────────────────────────────────────


def test_parse_dt_utc_offset():
    assert _parse_xmltv_dt("20260519120000 +0000") == datetime(2026, 5, 19, 12, tzinfo=UTC)


def test_parse_dt_positive_offset():
    dt = _parse_xmltv_dt("20260519120000 +0130")
    assert dt.utcoffset() == timedelta(hours=1, minutes=30)
    assert dt.astimezone(UTC) == datetime(2026, 5, 19, 10, 30, tzinfo=UTC)


def test_parse_dt_negative_offset():
    dt = _parse_xmltv_dt(" 20260519120000 -0500 ")
    assert dt.utcoffset() == timedelta(hours=-5)
    assert dt.astimezone(UTC) == datetime(2026, 5, 19, 17, tzinfo=UTC)


def test_parse_dt_no_offset_defaults_utc():
    assert _parse_xmltv_dt("20260519120000").tzinfo is UTC


def test_parse_dt_invalid_raises():
    with pytest.raises(ValueError):
        _parse_xmltv_dt("")
    with pytest.raises(ValueError):
        _parse_xmltv_dt("garbage")


# ── fetch_xmltv_now_playing ─────────────────────────────────────────────────


async def test_now_playing_selects_only_airing_programmes():
    body = _xmltv(
        _prog("a", PAST, RECENT, "Old Show"),
        _prog("a", RECENT, SOON, "Current A"),
        _prog("b", SOON, LATER, "Future B"),
        _prog("c", RECENT, SOON, "  Current C  "),
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)) as uo:
        result = await fetch_xmltv_now_playing("http://epg/guide.xml")
    assert result == {"a": "Current A", "c": "Current C"}
    req = uo.call_args.args[0]
    assert req.full_url == "http://epg/guide.xml"
    assert "gzip" in req.get_header("Accept-encoding")


async def test_now_playing_first_match_wins_per_channel():
    body = _xmltv(
        _prog("a", RECENT, SOON, "First"),
        _prog("a", RECENT, LATER, "Second"),
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        assert await fetch_xmltv_now_playing("http://epg/u") == {"a": "First"}


async def test_now_playing_handles_gzip():
    body = gzip.compress(_xmltv(_prog("a", RECENT, SOON, "Zipped")))
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        assert await fetch_xmltv_now_playing("http://epg/u") == {"a": "Zipped"}


async def test_now_playing_skips_bad_dates_and_continues():
    body = _xmltv(
        _prog("bad", "not-a-date", SOON, "Broken"),
        '<programme channel="nostop" start="%s"><title>X</title></programme>' % RECENT,
        _prog("a", RECENT, SOON, "Good"),
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        assert await fetch_xmltv_now_playing("http://epg/u") == {"a": "Good"}


async def test_now_playing_ignores_missing_or_blank_title():
    body = _xmltv(
        _prog("a", RECENT, SOON, None),
        _prog("b", RECENT, SOON, "   "),
        _prog("a", RECENT, SOON, "Later Titled"),
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        # A title-less programme doesn't claim the channel; the next one does.
        assert await fetch_xmltv_now_playing("http://epg/u") == {"a": "Later Titled"}


async def test_now_playing_respects_timezone_offsets():
    # Airing now in +0500 local time.
    tz = timezone(timedelta(hours=5))
    start = (NOW - timedelta(minutes=10)).astimezone(tz).strftime("%Y%m%d%H%M%S +0500")
    stop = (NOW + timedelta(minutes=10)).astimezone(tz).strftime("%Y%m%d%H%M%S +0500")
    body = _xmltv(_prog("tz", start, stop, "Offset Show"))
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(body)):
        assert await fetch_xmltv_now_playing("http://epg/u") == {"tz": "Offset Show"}


async def test_now_playing_empty_guide():
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(b"<tv></tv>")):
        assert await fetch_xmltv_now_playing("http://epg/u") == {}


async def test_now_playing_network_error_propagates():
    with patch("urllib.request.urlopen", side_effect=OSError("down")):
        with pytest.raises(OSError):
            await fetch_xmltv_now_playing("http://epg/u")


async def test_now_playing_malformed_xml_raises():
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(b"<tv><programme")):
        with pytest.raises(Exception):
            await fetch_xmltv_now_playing("http://epg/u")
