"""Tests for credential scrubbing in log output."""
from __future__ import annotations

import logging
from unittest.mock import MagicMock, patch

import pytest

from redact import redact_text, redact_url

KEY = "0123456789abcdeffedcba9876543210"
JF_URL = (
    "http://jellyfin:8096/videos/abc/main.m3u8?DeviceId=d&ApiKey=" + KEY
    + "&PlaySessionId=p&api_key=" + KEY
)


def test_redact_url_drops_secret_params_and_userinfo():
    out = redact_url("http://user:pw@h:81/x?Token=a&PASSWORD=b&q=1&X-Emby-Token=c")
    assert out == "http://h:81/x?q=1"


@pytest.mark.parametrize(
    "text",
    [
        f"[hls @ 0x1] HTTP error 404 Not Found for {JF_URL}",
        f"Opening '{JF_URL}' for reading",
        f'url="{JF_URL}"',
        "http://u:secretpw@host/x?token=abc",
    ],
)
def test_redact_text_masks_secrets_inside_free_text(text):
    out = redact_text(text)
    assert KEY not in out and "secretpw" not in out and "token=abc" not in out
    assert "***" in out


def test_redact_text_leaves_plain_text_alone():
    msg = "Past duration 0.999 too large; keyframe interval 30"
    assert redact_text(msg) == msg


async def test_probe_logs_are_redacted(caplog):
    from services import probe

    result = MagicMock(returncode=1, stdout="", stderr=f"HTTP error 403 for {JF_URL}")
    with patch.object(probe.subprocess, "run", return_value=result), \
         caplog.at_level(logging.WARNING, logger="services.probe"):
        assert await probe.probe_stream(JF_URL) is None
    assert KEY not in caplog.text
    assert "ffprobe failed" in caplog.text


async def test_probe_timeout_and_error_logs_are_redacted(caplog):
    import subprocess

    from services import probe

    with caplog.at_level(logging.WARNING, logger="services.probe"):
        with patch.object(
            probe.subprocess,
            "run",
            side_effect=subprocess.TimeoutExpired("ffprobe", 30),
        ):
            await probe.probe_stream(JF_URL)
        with patch.object(
            probe.subprocess, "run", side_effect=OSError(f"bad {JF_URL}")
        ):
            await probe.probe_stream(JF_URL)
    assert KEY not in caplog.text
    assert "timed out" in caplog.text and "ffprobe error" in caplog.text


async def test_hls_variant_log_is_redacted(caplog):
    from services import hls

    master = (
        "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\n"
        f"main.m3u8?ApiKey={KEY}&x=1\n"
    )
    resp = MagicMock()
    resp.read.return_value = master.encode()
    resp.__enter__ = lambda s: s
    resp.__exit__ = lambda *a: False
    with patch("urllib.request.urlopen", return_value=resp), \
         caplog.at_level(logging.INFO, logger="services.hls"):
        url = await hls.extract_hls_variant_url(f"http://jf/master.m3u8?api_key={KEY}")
    assert KEY in url  # the returned URL keeps its credentials for FFmpeg
    assert KEY not in caplog.text and "HLS master → variant" in caplog.text


def test_ffmpeg_stderr_lines_are_redacted(caplog):
    from streaming import video_player as vpmod

    vp = object.__new__(vpmod.H264VideoPlayer)
    vp._proc = MagicMock()
    vp._proc.stderr = [
        f"[hls @ 0x5] HTTP error 404 for {JF_URL}\n".encode(),
        f"DecodeFrame failed {JF_URL}\n".encode(),
    ]
    with caplog.at_level(logging.DEBUG, logger="streaming.video_player"):
        vp._drain_stderr()
    assert KEY not in caplog.text
    assert "ffmpeg video" in caplog.text and "ffmpeg (startup)" in caplog.text
