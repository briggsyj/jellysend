"""Tests for the probe_stream and extract_hls_variant_url services."""
from __future__ import annotations

import json
import subprocess
from unittest.mock import MagicMock, patch

import pytest

from services.hls import extract_hls_variant_url
from services.probe import probe_stream


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _mock_urlopen(text: str):
    """Return a context-manager mock that yields a response with the given text body."""
    resp = MagicMock()
    resp.read.return_value = text.encode()
    resp.__enter__ = lambda s: s
    resp.__exit__ = MagicMock(return_value=False)
    return resp


def _make_ffprobe_result(returncode: int, stdout: str = "", stderr: str = "") -> MagicMock:
    r = MagicMock()
    r.returncode = returncode
    r.stdout = stdout
    r.stderr = stderr
    return r


def _streams_json(video: dict | None = None, audio: dict | None = None) -> str:
    streams = []
    if video:
        streams.append({"codec_type": "video", **video})
    if audio:
        streams.append({"codec_type": "audio", **audio})
    return json.dumps({"streams": streams})


# ---------------------------------------------------------------------------
# probe_stream
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_probe_stream_returns_none_on_nonzero_exit():
    r = _make_ffprobe_result(returncode=1, stderr="HTTP Error 404")
    with patch("subprocess.run", return_value=r):
        assert await probe_stream("http://example.com/stream") is None


@pytest.mark.asyncio
async def test_probe_stream_returns_none_on_timeout():
    with patch("subprocess.run", side_effect=subprocess.TimeoutExpired("ffprobe", 30)):
        assert await probe_stream("http://example.com/stream") is None


@pytest.mark.asyncio
async def test_probe_stream_returns_none_on_exception():
    with patch("subprocess.run", side_effect=OSError("no such file")):
        assert await probe_stream("http://example.com/stream") is None


@pytest.mark.asyncio
async def test_probe_stream_returns_none_when_no_video_streams():
    stdout = _streams_json(audio={"codec_name": "aac"})
    r = _make_ffprobe_result(returncode=0, stdout=stdout)
    with patch("subprocess.run", return_value=r):
        assert await probe_stream("http://example.com/stream") is None


@pytest.mark.asyncio
async def test_probe_stream_returns_dict_on_success():
    stdout = _streams_json(
        video={
            "codec_name": "h264",
            "r_frame_rate": "25/1",
            "profile": "High",
            "has_b_frames": 0,
            "width": 1920,
            "height": 1080,
        },
        audio={"codec_name": "aac"},
    )
    r = _make_ffprobe_result(returncode=0, stdout=stdout)
    with patch("subprocess.run", return_value=r):
        result = await probe_stream("http://example.com/stream")

    assert result is not None
    assert result["codec"] == "h264"
    assert result["fps"] == 25.0
    assert result["profile"] == "High"
    assert result["has_b_frames"] is False
    assert result["width"] == 1920
    assert result["height"] == 1080
    assert result["has_audio"] is True
    assert result["audio_codec"] == "aac"


@pytest.mark.asyncio
async def test_probe_stream_video_only_has_no_audio():
    stdout = _streams_json(video={"codec_name": "h264", "r_frame_rate": "30/1"})
    r = _make_ffprobe_result(returncode=0, stdout=stdout)
    with patch("subprocess.run", return_value=r):
        result = await probe_stream("http://example.com/stream")

    assert result is not None
    assert result["has_audio"] is False
    assert result["audio_codec"] is None


@pytest.mark.asyncio
async def test_probe_stream_bad_fps_defaults_to_25():
    stdout = _streams_json(video={"codec_name": "h264", "r_frame_rate": "0/0"})
    r = _make_ffprobe_result(returncode=0, stdout=stdout)
    with patch("subprocess.run", return_value=r):
        result = await probe_stream("http://example.com/stream")

    assert result is not None
    assert result["fps"] == 25.0


@pytest.mark.asyncio
async def test_probe_stream_returns_none_on_invalid_json():
    r = _make_ffprobe_result(returncode=0, stdout="not json at all")
    with patch("subprocess.run", return_value=r):
        assert await probe_stream("http://example.com/stream") is None


# ---------------------------------------------------------------------------
# extract_hls_variant_url
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_extract_hls_returns_original_for_non_hls():
    with patch("urllib.request.urlopen", return_value=_mock_urlopen("not a playlist")):
        result = await extract_hls_variant_url("http://example.com/stream.ts")
    assert result == "http://example.com/stream.ts"


@pytest.mark.asyncio
async def test_extract_hls_returns_original_for_variant_playlist():
    """A playlist with EXTM3U but no EXT-X-STREAM-INF is already a variant — return as-is."""
    variant = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nsegment0.ts\n"
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(variant)):
        result = await extract_hls_variant_url("http://example.com/stream.m3u8")
    assert result == "http://example.com/stream.m3u8"


@pytest.mark.asyncio
async def test_extract_hls_picks_highest_bandwidth():
    master = (
        "#EXTM3U\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=1000000\n"
        "low/stream.m3u8\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=8000000\n"
        "high/stream.m3u8\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=4000000\n"
        "mid/stream.m3u8\n"
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(master)):
        result = await extract_hls_variant_url("http://example.com/master.m3u8")
    assert result == "http://example.com/high/stream.m3u8"


@pytest.mark.asyncio
async def test_extract_hls_resolves_relative_urls():
    master = (
        "#EXTM3U\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=5000000\n"
        "variant/stream.m3u8\n"
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(master)):
        result = await extract_hls_variant_url("http://server.local/hls/master.m3u8")
    assert result == "http://server.local/hls/variant/stream.m3u8"


@pytest.mark.asyncio
async def test_extract_hls_preserves_absolute_variant_urls():
    master = (
        "#EXTM3U\n"
        "#EXT-X-STREAM-INF:BANDWIDTH=5000000\n"
        "http://cdn.example.com/stream.m3u8\n"
    )
    with patch("urllib.request.urlopen", return_value=_mock_urlopen(master)):
        result = await extract_hls_variant_url("http://server.local/master.m3u8")
    assert result == "http://cdn.example.com/stream.m3u8"


@pytest.mark.asyncio
async def test_extract_hls_returns_original_on_fetch_error():
    with patch("urllib.request.urlopen", side_effect=Exception("connection refused")):
        result = await extract_hls_variant_url("http://example.com/master.m3u8")
    assert result == "http://example.com/master.m3u8"


# ---------------------------------------------------------------------------
# IPTVCog commands (!add-source, !sources, !delete-source) and setup()
# ---------------------------------------------------------------------------

import urllib.error  # noqa: E402
from unittest.mock import AsyncMock  # noqa: E402

import cogs.iptv as iptv_mod  # noqa: E402
from cogs.iptv import IPTVCog  # noqa: E402
from services.sources import SourceManager  # noqa: E402

_CHS = [{"name": "Zorbax One", "tvg_id": "z1", "group": "", "stream_url": "http://a/1"}]


def _make_ctx(author_id: int = 1, channel_id: int = 10) -> MagicMock:
    ctx = MagicMock()
    ctx.author.id = author_id
    ctx.channel.id = channel_id
    ctx.send = AsyncMock()
    return ctx


def _make_cog(tmp_path, has_tv: bool = False) -> IPTVCog:
    bot = MagicMock()
    bot.get_cog = MagicMock(return_value=MagicMock() if has_tv else None)
    bot.wait_for = AsyncMock()
    sm = SourceManager(tmp_path / "sources.json")
    return IPTVCog(bot, sm)


def _sent(ctx) -> list[str]:
    return [c.args[0] for c in ctx.send.await_args_list]


def _reply(content: str, author_id: int = 1, channel_id: int = 10) -> MagicMock:
    msg = MagicMock()
    msg.content = content
    msg.author.id = author_id
    msg.channel.id = channel_id
    return msg


# ── !add-source ───────────────────────────────────────────────────────────────


async def test_add_source_success_with_epg(tmp_path):
    cog = _make_cog(tmp_path)
    ctx = _make_ctx()
    with patch.object(
        iptv_mod, "fetch_and_parse", AsyncMock(return_value=(_CHS, "http://epg"))
    ) as fp:
        await cog.add_source.callback(cog, ctx, "Alpha", url="http://pl.m3u")
    fp.assert_awaited_once_with("http://pl.m3u")
    msgs = _sent(ctx)
    assert "fetching" in msgs[0]
    assert "added source **Alpha** with 1 channel(s)" in msgs[1]
    assert "EPG found (http://epg)" in msgs[1]
    src = cog.sm.get_sources()[0]
    assert src["name"] == "Alpha" and src["epg_url"] == "http://epg" and src["enabled"]


async def test_add_source_success_without_epg(tmp_path):
    cog = _make_cog(tmp_path)
    ctx = _make_ctx()
    with patch.object(iptv_mod, "fetch_and_parse", AsyncMock(return_value=([], None))):
        await cog.add_source.callback(cog, ctx, "Alpha", url="http://pl.m3u")
    assert "no EPG URL in playlist" in _sent(ctx)[-1]
    assert "0 channel(s)" in _sent(ctx)[-1]


@pytest.mark.parametrize(
    "exc, expected",
    [
        (urllib.error.URLError("refused"), "could not fetch playlist"),
        (ValueError("missing #EXTM3U"), "invalid playlist: missing #EXTM3U"),
        (RuntimeError("weird"), "failed to load playlist: weird"),
    ],
)
async def test_add_source_errors_do_not_persist(tmp_path, exc, expected):
    cog = _make_cog(tmp_path)
    ctx = _make_ctx()
    with patch.object(iptv_mod, "fetch_and_parse", AsyncMock(side_effect=exc)):
        await cog.add_source.callback(cog, ctx, "Alpha", url="http://pl.m3u")
    assert expected in _sent(ctx)[-1]
    assert cog.sm.get_sources() == []


# ── !sources (list) ───────────────────────────────────────────────────────────


async def test_sources_list_empty_no_tvh(tmp_path):
    cog = _make_cog(tmp_path)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx)
    assert "no sources configured" in _sent(ctx)[0]


async def test_sources_list_with_tvh_and_iptv(tmp_path):
    cog = _make_cog(tmp_path, has_tv=True)
    cog.sm.add_source("Alpha", "u", _CHS)
    cog.sm.add_source("Beta", "u2", [])
    cog.sm.set_enabled(1, False)
    cog.sm.set_tvh_enabled(False)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx)
    out = _sent(ctx)[0]
    assert out.splitlines() == [
        "**Sources**",
        "  [✗] **TVheadend**",
        "  [✓] **Alpha** — 1 channel(s)",
        "  [✗] **Beta** — 0 channel(s)",
    ]


async def test_sources_list_tvh_only(tmp_path):
    cog = _make_cog(tmp_path, has_tv=True)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx)
    assert "[✓] **TVheadend**" in _sent(ctx)[0]


async def test_sources_unknown_action_lists(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha", "u", _CHS)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "bogus", name="")
    assert _sent(ctx)[0].startswith("**Sources**")


# ── !sources enable/disable ───────────────────────────────────────────────────


async def test_sources_enable_requires_name(tmp_path):
    cog = _make_cog(tmp_path)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "enable", name="")
    assert _sent(ctx) == ["usage: `!sources enable <source name>`"]


async def test_sources_disable_tvheadend_by_substring(tmp_path):
    cog = _make_cog(tmp_path, has_tv=True)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "DISABLE", name="TVH")
    assert _sent(ctx) == ["**TVheadend** disabled"]
    assert cog.sm.tvh_enabled is False
    await cog.set_source.callback(cog, ctx, "enable", name="tvheadend")
    assert cog.sm.tvh_enabled is True


async def test_sources_tvheadend_name_ignored_without_tv_cog(tmp_path):
    cog = _make_cog(tmp_path, has_tv=False)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "disable", name="tvh")
    assert _sent(ctx) == ['no source matching "tvh" found']
    assert cog.sm.tvh_enabled is True


async def test_sources_disable_iptv_by_substring(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha Prime", "u", _CHS)
    cog.sm.add_source("Beta", "u2", _CHS)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "disable", name="prime")
    assert _sent(ctx) == ["**Alpha Prime** disabled"]
    assert cog.sm.get_sources()[0]["enabled"] is False
    assert cog.sm.get_sources()[1]["enabled"] is True


async def test_sources_ambiguous_match(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Sports A", "u", _CHS)
    cog.sm.add_source("Sports B", "u2", _CHS)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "disable", name="sports")
    msg = _sent(ctx)[0]
    assert msg.startswith('ambiguous name "sports"')
    assert "**Sports A**" in msg and "**Sports B**" in msg
    assert all(s["enabled"] for s in cog.sm.get_sources())


async def test_sources_no_match(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha", "u", _CHS)
    ctx = _make_ctx()
    await cog.set_source.callback(cog, ctx, "enable", name="zzz")
    assert _sent(ctx) == ['no source matching "zzz" found']


# ── !delete-source ────────────────────────────────────────────────────────────


async def test_delete_source_none_configured(tmp_path):
    cog = _make_cog(tmp_path)
    ctx = _make_ctx()
    await cog.delete_source.callback(cog, ctx)
    assert "no sources added yet" in _sent(ctx)[0]
    cog.bot.wait_for.assert_not_awaited()


async def test_delete_source_by_number(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha", "u", _CHS)
    cog.sm.add_source("Beta", "u2", [])
    cog.bot.wait_for.return_value = _reply(" 2 ")
    ctx = _make_ctx()
    await cog.delete_source.callback(cog, ctx)
    listing = _sent(ctx)[0]
    assert "1. **Alpha** — 1 channel(s)" in listing
    assert "2. **Beta** — 0 channel(s)" in listing
    assert _sent(ctx)[-1] == "removed source **Beta**"
    assert [s["name"] for s in cog.sm.get_sources()] == ["Alpha"]
    assert cog.bot.wait_for.await_args.kwargs["timeout"] == 60.0


async def test_delete_source_cancel(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha", "u", _CHS)
    cog.bot.wait_for.return_value = _reply("Cancel")
    ctx = _make_ctx()
    await cog.delete_source.callback(cog, ctx)
    assert _sent(ctx)[-1] == "cancelled"
    assert len(cog.sm.get_sources()) == 1


async def test_delete_source_timeout(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha", "u", _CHS)
    cog.bot.wait_for.side_effect = TimeoutError
    ctx = _make_ctx()
    await cog.delete_source.callback(cog, ctx)
    assert _sent(ctx)[-1] == "no response — cancelled"
    assert len(cog.sm.get_sources()) == 1


async def test_delete_source_check_predicate(tmp_path):
    cog = _make_cog(tmp_path)
    cog.sm.add_source("Alpha", "u", _CHS)
    cog.sm.add_source("Beta", "u2", _CHS)
    cog.bot.wait_for.side_effect = TimeoutError
    ctx = _make_ctx(author_id=1, channel_id=10)
    await cog.delete_source.callback(cog, ctx)
    check = cog.bot.wait_for.await_args.kwargs["check"]
    assert check(_reply("1")) is True
    assert check(_reply("2")) is True
    assert check(_reply("cancel")) is True
    assert check(_reply("0")) is False
    assert check(_reply("3")) is False
    assert check(_reply("abc")) is False
    assert check(_reply("1", author_id=2)) is False
    assert check(_reply("1", channel_id=11)) is False


# ── setup() ───────────────────────────────────────────────────────────────────


async def test_setup_registers_cog_and_backfills(tmp_path):
    bot = MagicMock()
    bot.add_cog = AsyncMock()
    with patch.object(iptv_mod, "_data_dir", return_value=tmp_path), patch.object(
        SourceManager, "backfill_epg_urls", AsyncMock(return_value=2)
    ) as bf:
        await iptv_mod.setup(bot)
    assert isinstance(bot.source_manager, SourceManager)
    cog = bot.add_cog.await_args.args[0]
    assert isinstance(cog, IPTVCog) and cog.sm is bot.source_manager
    bf.assert_awaited_once()
