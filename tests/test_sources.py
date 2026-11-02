"""Tests for services/sources.py — SourceManager persistence and EPG backfill."""
from __future__ import annotations

import json
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

from services.sources import SourceManager, _data_dir


def _mock_urlopen(body: bytes):
    resp = MagicMock()
    resp.read.return_value = body
    resp.__enter__ = lambda s: s
    resp.__exit__ = MagicMock(return_value=False)
    return resp


CH_A = [{"name": "Zorbax One", "tvg_id": "z1", "group": "", "stream_url": "http://a/1"}]
CH_B = [
    {"name": "Flumbo", "tvg_id": "f1", "group": "News", "stream_url": "http://b/1"},
    {"name": "Flumbo 2", "tvg_id": "f2", "group": "", "stream_url": "http://b/2"},
]


@pytest.fixture
def path(tmp_path: Path) -> Path:
    return tmp_path / "sub" / "sources.json"


# ── _data_dir ───────────────────────────────────────────────────────────────


def test_data_dir_uses_xdg(monkeypatch, tmp_path):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    assert _data_dir() == tmp_path / "slopsoil"


def test_data_dir_defaults_to_home(monkeypatch, tmp_path):
    monkeypatch.delenv("XDG_DATA_HOME", raising=False)
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    assert _data_dir() == tmp_path / ".local" / "share" / "slopsoil"


# ── load / save ─────────────────────────────────────────────────────────────


def test_missing_file_starts_empty(path):
    sm = SourceManager(path)
    assert sm.get_sources() == []
    assert sm.tvh_enabled is True
    assert not path.exists()


def test_add_source_persists_and_creates_parent_dir(path):
    sm = SourceManager(path)
    sm.add_source("A", "http://a.m3u", CH_A, epg_url="http://a/epg.xml")
    data = json.loads(path.read_text())
    assert data["tvh_enabled"] is True
    assert data["sources"] == [
        {
            "name": "A",
            "url": "http://a.m3u",
            "channels": CH_A,
            "epg_url": "http://a/epg.xml",
            "enabled": True,
        }
    ]
    # Round-trip through a fresh instance.
    sm2 = SourceManager(path)
    assert sm2.get_sources() == data["sources"]


def test_add_source_without_epg_omits_key(path):
    sm = SourceManager(path)
    sm.add_source("A", "http://a", CH_A)
    assert "epg_url" not in sm.get_sources()[0]


def test_loads_legacy_list_format(path):
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps([{"name": "Old", "url": "u", "channels": [], "enabled": True}]))
    sm = SourceManager(path)
    assert [s["name"] for s in sm.get_sources()] == ["Old"]
    assert sm.tvh_enabled is True


def test_loads_dict_format_with_tvh_disabled(path):
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({"tvh_enabled": False, "sources": []}))
    assert SourceManager(path).tvh_enabled is False


def test_corrupt_file_falls_back_to_empty(path):
    path.parent.mkdir(parents=True)
    path.write_text("{not json")
    sm = SourceManager(path)
    assert sm.get_sources() == []


def test_set_tvh_enabled_persists(path):
    sm = SourceManager(path)
    sm.set_tvh_enabled(False)
    assert sm.tvh_enabled is False
    assert SourceManager(path).tvh_enabled is False


def test_get_sources_returns_copy(path):
    sm = SourceManager(path)
    sm.add_source("A", "u", CH_A)
    sm.get_sources().clear()
    assert len(sm.get_sources()) == 1


# ── add_source replace semantics ────────────────────────────────────────────


def test_add_source_replace_case_insensitive_preserves_disabled(path):
    sm = SourceManager(path)
    sm.add_source("Alpha", "http://old", CH_A)
    sm.set_enabled(0, False)
    sm.add_source("ALPHA", "http://new", CH_B)
    srcs = sm.get_sources()
    assert len(srcs) == 1
    assert srcs[0]["name"] == "ALPHA"
    assert srcs[0]["url"] == "http://new"
    assert srcs[0]["channels"] == CH_B
    assert srcs[0]["enabled"] is False


def test_add_source_replace_preserves_enabled(path):
    sm = SourceManager(path)
    sm.add_source("Alpha", "http://old", CH_A)
    sm.add_source("alpha", "http://new", CH_B)
    assert sm.get_sources()[0]["enabled"] is True


def test_add_source_replace_missing_enabled_defaults_false(path):
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps([{"name": "Legacy", "url": "u", "channels": []}]))
    sm = SourceManager(path)
    sm.add_source("legacy", "u2", CH_A)
    assert sm.get_sources()[0]["enabled"] is False


# ── enable / remove / channel merge ─────────────────────────────────────────


def test_set_enabled_and_iptv_channels(path):
    sm = SourceManager(path)
    sm.add_source("A", "ua", CH_A)
    sm.add_source("B", "ub", CH_B)
    chs = sm.get_iptv_channels()
    assert [(c["name"], c["source"]) for c in chs] == [
        ("Zorbax One", "A"), ("Flumbo", "B"), ("Flumbo 2", "B"),
    ]
    # Source dicts themselves are not mutated with the "source" key.
    assert "source" not in sm.get_sources()[0]["channels"][0]
    sm.set_enabled(1, False)
    assert [c["source"] for c in sm.get_iptv_channels()] == ["A"]
    assert SourceManager(path).get_sources()[1]["enabled"] is False


def test_iptv_channels_handles_missing_channels_key(path):
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps([{"name": "X", "url": "u", "enabled": True}]))
    assert SourceManager(path).get_iptv_channels() == []


def test_remove_source(path):
    sm = SourceManager(path)
    sm.add_source("A", "ua", CH_A)
    sm.add_source("B", "ub", CH_B)
    assert sm.remove_source(0) == "A"
    assert [s["name"] for s in SourceManager(path).get_sources()] == ["B"]


def test_remove_source_bad_index_raises(path):
    sm = SourceManager(path)
    with pytest.raises(IndexError):
        sm.remove_source(0)


def test_get_epg_sources_only_enabled_with_url(path):
    sm = SourceManager(path)
    sm.add_source("A", "ua", CH_A, epg_url="http://a/epg")
    sm.add_source("B", "ub", CH_B)
    sm.add_source("C", "uc", [], epg_url="http://c/epg")
    sm.set_enabled(2, False)
    assert sm.get_epg_sources() == [("A", "http://a/epg")]


# ── backfill_epg_urls ───────────────────────────────────────────────────────


async def test_backfill_updates_sources_missing_epg(path):
    sm = SourceManager(path)
    sm.add_source("A", "http://a.m3u", CH_A)
    sm.add_source("B", "http://b.m3u", CH_B, epg_url="http://b/already")
    header = b'#EXTM3U url-tvg="http://a/guide.xml"\n#EXTINF:-1,X\n'
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(header)) as uo:
        n = await sm.backfill_epg_urls()
    assert n == 1
    # Only A was fetched; B already had an EPG URL.
    assert uo.call_count == 1
    req = uo.call_args.args[0]
    assert req.full_url == "http://a.m3u"
    assert req.get_header("Range") == "bytes=0-1023"
    assert SourceManager(path).get_sources()[0]["epg_url"] == "http://a/guide.xml"


async def test_backfill_falls_back_to_plain_get_when_range_fails(path):
    sm = SourceManager(path)
    sm.add_source("A", "http://a.m3u", CH_A)
    header = b'#EXTM3U x-tvg-url="http://a/x.xml"\n'
    with patch(
        "urllib.request.urlopen",
        side_effect=[OSError("416 range not satisfiable"), _mock_urlopen(header)],
    ) as uo:
        n = await sm.backfill_epg_urls()
    assert n == 1
    assert uo.call_count == 2
    second_req = uo.call_args_list[1].args[0]
    assert second_req.get_header("Range") is None
    assert sm.get_sources()[0]["epg_url"] == "http://a/x.xml"


async def test_backfill_both_requests_fail_is_swallowed(path):
    sm = SourceManager(path)
    sm.add_source("A", "http://a.m3u", CH_A)
    mtime_before = path.stat().st_mtime_ns
    with patch("urllib.request.urlopen", side_effect=OSError("down")):
        n = await sm.backfill_epg_urls()
    assert n == 0
    assert "epg_url" not in sm.get_sources()[0]
    assert path.stat().st_mtime_ns == mtime_before


async def test_backfill_no_epg_in_header_and_no_url(path):
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps([
        {"name": "NoUrl", "channels": []},
        {"name": "Plain", "url": "http://p.m3u", "channels": []},
    ]))
    sm = SourceManager(path)
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(b"#EXTM3U\n")) as uo:
        n = await sm.backfill_epg_urls()
    assert n == 0
    assert uo.call_count == 1  # NoUrl was skipped entirely


async def test_backfill_empty(path):
    assert await SourceManager(path).backfill_epg_urls() == 0
