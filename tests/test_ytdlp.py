"""Tests for services/ytdlp.py — download/live extraction with yt_dlp mocked."""
from __future__ import annotations

import asyncio
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

from services import ytdlp
from services.ytdlp import (
    _yt_cleanup_after_stream,
    _yt_download,
    _yt_extract_live_url,
    _yt_remove_dir,
)


@pytest.fixture
def fake_ytdlp(monkeypatch):
    """Install a fake ``yt_dlp`` module; returns the YoutubeDL instance mock."""
    ydl = MagicMock()
    ydl.__enter__ = MagicMock(return_value=ydl)
    ydl.__exit__ = MagicMock(return_value=False)
    mod = MagicMock()
    mod.YoutubeDL = MagicMock(return_value=ydl)
    monkeypatch.setitem(sys.modules, "yt_dlp", mod)
    ydl.module = mod
    return ydl


# ── _yt_download ────────────────────────────────────────────────────────────


async def test_download_uses_requested_downloads_filepath(fake_ytdlp, tmp_path, monkeypatch):
    monkeypatch.setenv("YTDLP_FORMAT", "best[height<=720]")
    out = tmp_path / "vid.mp4"
    out.write_bytes(b"x")
    fake_ytdlp.extract_info.return_value = {
        "title": "My Video",
        "requested_downloads": [{"filepath": str(out)}],
    }
    path, title = await _yt_download("https://yt/v", str(tmp_path))
    assert (path, title) == (str(out), "My Video")
    opts = fake_ytdlp.module.YoutubeDL.call_args.args[0]
    assert opts["format"] == "best[height<=720]"
    assert opts["outtmpl"] == str(tmp_path / "%(id)s.%(ext)s")
    assert opts["noplaylist"] is True
    assert opts["merge_output_format"] == "mp4"
    fake_ytdlp.extract_info.assert_called_once_with("https://yt/v", download=True)


async def test_download_falls_back_to_largest_file(fake_ytdlp, tmp_path):
    (tmp_path / "small.part").write_bytes(b"x")
    (tmp_path / "big.mp4").write_bytes(b"x" * 100)
    (tmp_path / "subdir").mkdir()
    fake_ytdlp.extract_info.return_value = {
        "requested_downloads": [{"filepath": str(tmp_path / "missing.mp4")}],
    }
    path, title = await _yt_download("https://yt/v", str(tmp_path))
    assert path == str(tmp_path / "big.mp4")
    assert title == "video"


async def test_download_info_none_and_no_files_raises(fake_ytdlp, tmp_path):
    fake_ytdlp.extract_info.return_value = None
    with pytest.raises(FileNotFoundError):
        await _yt_download("https://yt/v", str(tmp_path))


async def test_download_propagates_ytdlp_errors(fake_ytdlp, tmp_path):
    fake_ytdlp.extract_info.side_effect = RuntimeError("unsupported URL")
    with pytest.raises(RuntimeError, match="unsupported"):
        await _yt_download("https://yt/v", str(tmp_path))


# ── _yt_extract_live_url ────────────────────────────────────────────────────


async def test_live_returns_none_when_not_live(fake_ytdlp):
    fake_ytdlp.extract_info.return_value = {"live_status": "not_live", "url": "x"}
    assert await _yt_extract_live_url("https://yt/v") is None
    fake_ytdlp.extract_info.assert_called_once_with("https://yt/v", download=False)


async def test_live_returns_none_for_upcoming_or_empty(fake_ytdlp):
    fake_ytdlp.extract_info.return_value = {"live_status": "is_upcoming"}
    assert await _yt_extract_live_url("u") is None
    fake_ytdlp.extract_info.return_value = None
    assert await _yt_extract_live_url("u") is None


async def test_live_picks_best_hls_with_video(fake_ytdlp):
    fake_ytdlp.extract_info.return_value = {
        "live_status": "is_live",
        "title": "Live Now",
        "url": "http://fallback",
        "formats": [
            {"protocol": "m3u8_native", "url": "http://720", "vcodec": "avc1", "height": 720, "tbr": 3000},
            {"protocol": "m3u8_native", "url": "http://1080lo", "vcodec": "avc1", "height": 1080, "tbr": 4000},
            {"protocol": "m3u8_native", "url": "http://1080hi", "vcodec": "avc1", "height": 1080, "tbr": 6000},
            {"protocol": "m3u8_native", "url": "http://audio", "vcodec": "none", "height": None},
            {"protocol": "https", "url": "http://dash", "vcodec": "vp9", "height": 2160},
            {"protocol": "m3u8", "vcodec": "avc1", "height": 4320},  # no url
        ],
    }
    assert await _yt_extract_live_url("u") == ("http://1080hi", "Live Now")


async def test_live_falls_back_to_info_url_and_default_title(fake_ytdlp):
    fake_ytdlp.extract_info.return_value = {
        "live_status": "is_live",
        "title": None,
        "url": "http://direct",
        "formats": [{"protocol": "m3u8", "url": "http://a", "vcodec": None}],
    }
    assert await _yt_extract_live_url("u") == ("http://direct", "YouTube Live")


async def test_live_no_usable_url_returns_none(fake_ytdlp):
    fake_ytdlp.extract_info.return_value = {"live_status": "is_live", "formats": None}
    assert await _yt_extract_live_url("u") is None


# ── cleanup helpers ─────────────────────────────────────────────────────────


def test_remove_dir_deletes_tree(tmp_path):
    d = tmp_path / "yt"
    (d / "nested").mkdir(parents=True)
    (d / "nested" / "f").write_text("x")
    _yt_remove_dir(str(d))
    assert not d.exists()


def test_remove_dir_missing_is_noop(tmp_path):
    _yt_remove_dir(str(tmp_path / "nope"))  # must not raise


def test_remove_dir_swallows_unexpected_errors(tmp_path):
    with patch.object(ytdlp.shutil, "rmtree", side_effect=RuntimeError("boom")):
        _yt_remove_dir(str(tmp_path))  # must not raise


async def test_cleanup_after_stream_success(tmp_path):
    async def _ok():
        return None

    with patch.object(ytdlp, "_yt_remove_dir") as rm:
        await _yt_cleanup_after_stream(asyncio.ensure_future(_ok()), str(tmp_path))
    rm.assert_called_once_with(str(tmp_path))


async def test_cleanup_after_stream_task_error(tmp_path):
    async def _fail():
        raise RuntimeError("ffmpeg died")

    with patch.object(ytdlp, "_yt_remove_dir") as rm:
        await _yt_cleanup_after_stream(asyncio.ensure_future(_fail()), str(tmp_path))
    rm.assert_called_once_with(str(tmp_path))


async def test_cleanup_after_stream_task_cancelled(tmp_path):
    async def _forever():
        await asyncio.Event().wait()

    task = asyncio.ensure_future(_forever())
    await asyncio.sleep(0)
    task.cancel()
    with patch.object(ytdlp, "_yt_remove_dir") as rm:
        await _yt_cleanup_after_stream(task, str(tmp_path))
    rm.assert_called_once_with(str(tmp_path))
