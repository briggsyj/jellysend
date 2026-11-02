"""Tests for cogs/tv.py timezone helpers and services/ytdlp.py format selector."""
from __future__ import annotations

import os
import time
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

import pytest

from cogs.tv import _fmt_time, _get_display_tz
from services.ytdlp import _DEFAULT_YT_FORMAT, _yt_format


# ---------------------------------------------------------------------------
# !channels name filter logic
# ---------------------------------------------------------------------------

IPTV_CHANNELS = [
    {"name": "Zorbax One", "source": "src-a", "tvg_id": "zorb1"},
    {"name": "Zorbax Two", "source": "src-a", "tvg_id": "zorb2"},
    {"name": "Flumbo Network", "source": "src-b", "tvg_id": "flmb"},
    {"name": "Quixel Sports 1", "source": "src-b", "tvg_id": "qxs1"},
]


def _apply_filter(channels: list[dict], query: str) -> list[dict]:
    """Mirrors the inline filter in the channels command."""
    return [ch for ch in channels if not query or query.lower() in ch["name"].lower()]


def test_channels_filter_matches_multiple():
    result = _apply_filter(IPTV_CHANNELS, "zorbax")
    assert [ch["name"] for ch in result] == ["Zorbax One", "Zorbax Two"]


def test_channels_filter_case_insensitive():
    assert _apply_filter(IPTV_CHANNELS, "ZORBAX") == _apply_filter(IPTV_CHANNELS, "zorbax")


def test_channels_filter_partial_match():
    result = _apply_filter(IPTV_CHANNELS, "sports")
    assert len(result) == 1
    assert result[0]["name"] == "Quixel Sports 1"


def test_channels_filter_empty_query_passes_all():
    assert _apply_filter(IPTV_CHANNELS, "") == IPTV_CHANNELS


def test_channels_filter_no_match_returns_empty():
    assert _apply_filter(IPTV_CHANNELS, "gronkvision") == []


def test_channels_filter_empty_channel_list():
    assert _apply_filter([], "zorbax") == []


# ---------------------------------------------------------------------------
# _get_display_tz
# ---------------------------------------------------------------------------


def test_get_display_tz_valid_timezone(monkeypatch):
    monkeypatch.setenv("TIMEZONE", "America/New_York")
    tz = _get_display_tz()
    assert str(tz) == "America/New_York"


def test_get_display_tz_unset_falls_back_to_local(monkeypatch):
    monkeypatch.delenv("TIMEZONE", raising=False)
    tz = _get_display_tz()
    # Should be a valid tzinfo — exactly what the local clock would give
    assert tz is not None
    assert hasattr(tz, "utcoffset")


def test_get_display_tz_empty_string_falls_back_to_local(monkeypatch):
    monkeypatch.setenv("TIMEZONE", "")
    tz = _get_display_tz()
    assert tz is not None
    assert hasattr(tz, "utcoffset")


def test_get_display_tz_invalid_name_falls_back_to_local(monkeypatch):
    monkeypatch.setenv("TIMEZONE", "Not/AReal_Zone")
    tz = _get_display_tz()
    # Falls back — should still return something usable
    assert tz is not None
    assert hasattr(tz, "utcoffset")


def test_get_display_tz_whitespace_only_falls_back(monkeypatch):
    monkeypatch.setenv("TIMEZONE", "   ")
    tz = _get_display_tz()
    assert tz is not None


# ---------------------------------------------------------------------------
# _yt_format
# ---------------------------------------------------------------------------


def test_yt_format_unset_uses_default(monkeypatch):
    monkeypatch.delenv("YTDLP_FORMAT", raising=False)
    assert _yt_format() == _DEFAULT_YT_FORMAT


def test_yt_format_empty_string_falls_back_to_default(monkeypatch):
    monkeypatch.setenv("YTDLP_FORMAT", "")
    assert _yt_format() == _DEFAULT_YT_FORMAT


def test_yt_format_whitespace_only_falls_back_to_default(monkeypatch):
    monkeypatch.setenv("YTDLP_FORMAT", "   ")
    assert _yt_format() == _DEFAULT_YT_FORMAT


def test_yt_format_custom_override(monkeypatch):
    fmt = "bv*[vcodec^=avc1][height<=1080]+ba/b[height<=1080]"
    monkeypatch.setenv("YTDLP_FORMAT", fmt)
    assert _yt_format() == fmt


def test_yt_format_strips_surrounding_whitespace(monkeypatch):
    monkeypatch.setenv("YTDLP_FORMAT", "  best  ")
    assert _yt_format() == "best"


# ---------------------------------------------------------------------------
# _fmt_time
# ---------------------------------------------------------------------------


def test_fmt_time_known_utc_timestamp(monkeypatch):
    """2000-01-01 00:00 UTC → midnight in UTC timezone."""
    monkeypatch.setenv("TIMEZONE", "UTC")
    ts = datetime(2000, 1, 1, 0, 0, 0, tzinfo=timezone.utc).timestamp()
    result = _fmt_time(ts)
    assert result == "12:00 AM"


def test_fmt_time_noon_utc(monkeypatch):
    monkeypatch.setenv("TIMEZONE", "UTC")
    ts = datetime(2000, 6, 15, 12, 0, 0, tzinfo=timezone.utc).timestamp()
    assert _fmt_time(ts) == "12:00 PM"


def test_fmt_time_leading_zero_stripped(monkeypatch):
    """Times like '07:30 PM' should display as '7:30 PM'."""
    monkeypatch.setenv("TIMEZONE", "UTC")
    ts = datetime(2000, 1, 1, 19, 30, 0, tzinfo=timezone.utc).timestamp()
    assert _fmt_time(ts) == "7:30 PM"


def test_fmt_time_respects_timezone_offset(monkeypatch):
    """Same UTC instant reads differently in two different timezones."""
    # 2000-01-01 20:00 UTC = 3:00 PM in America/Chicago (UTC-6 in winter)
    ts = datetime(2000, 1, 1, 21, 0, 0, tzinfo=timezone.utc).timestamp()

    monkeypatch.setenv("TIMEZONE", "UTC")
    utc_result = _fmt_time(ts)

    monkeypatch.setenv("TIMEZONE", "America/New_York")
    ny_result = _fmt_time(ts)

    assert utc_result != ny_result


# ===========================================================================
# TV cog behaviour: helpers, stream start, !channels, !play, !search, setup
# ===========================================================================

import asyncio  # noqa: E402
import urllib.error  # noqa: E402
from unittest.mock import AsyncMock, MagicMock, patch  # noqa: E402

import cogs.tv as tv_mod  # noqa: E402
from cogs.tv import TV, _find_channel, _find_iptv_channel  # noqa: E402

TVH_CHANNELS = [
    {"name": "BBC One", "number": 1, "uuid": "u1"},
    {"name": "BBC Two", "number": 2, "uuid": "u2"},
    {"name": "Channel 4", "number": 4, "uuid": "u4"},
    {"name": "No Number", "uuid": "u9"},
]

IPTV_FULL = [
    {"name": "Zorbax One", "source": "src-a", "tvg_id": "zorb1", "group": "News",
     "stream_url": "http://iptv/zorb1"},
    {"name": "Flumbo Network", "source": "src-b", "tvg_id": "", "group": "",
     "stream_url": "http://iptv/flmb"},
]

GOOD_PROBE = {
    "codec": "h264", "profile": "High", "width": 1280, "height": 720,
    "fps": 25.0, "has_b_frames": True, "has_audio": True, "audio_codec": "aac",
}


@pytest.fixture(autouse=True)
def _clear_epg_cache():
    tv_mod._epg_cache.clear()
    yield
    tv_mod._epg_cache.clear()


def _ctx() -> MagicMock:
    ctx = MagicMock()
    ctx.author.id = 1
    ctx.channel.id = 10
    status = MagicMock()
    status.edit = AsyncMock()
    ctx.send = AsyncMock(return_value=status)
    ctx.status = status
    return ctx


def _sm(iptv=None, epg_sources=None, tvh_enabled=True) -> MagicMock:
    sm = MagicMock()
    sm.get_iptv_channels.return_value = list(iptv or [])
    sm.get_epg_sources.return_value = list(epg_sources or [])
    sm.tvh_enabled = tvh_enabled
    return sm


def _tvh(channels=None, now_playing=None, events=None) -> MagicMock:
    tvh = MagicMock()
    tvh.base_url = "http://tvh"
    tvh.get_channels = AsyncMock(return_value=list(channels or []))
    tvh.get_now_playing = AsyncMock(return_value=dict(now_playing or {}))
    tvh.get_epg_events = AsyncMock(return_value=list(events or []))
    tvh.stream_url = MagicMock(side_effect=lambda uuid: f"http://tvh/stream/{uuid}")
    return tvh


def _cog(tvh=None, sm=None) -> TV:
    bot = MagicMock()
    bot.source_manager = sm
    bot.wait_for = AsyncMock()
    bot.stream_tasks = {}
    return TV(bot, tvh)


def _sent(ctx) -> list[str]:
    return [c.args[0] if c.args else c.kwargs.get("content") for c in ctx.send.await_args_list]


def _edits(ctx) -> list[str]:
    return [c.kwargs["content"] for c in ctx.status.edit.await_args_list]


def _voice(guild_id: int = 42):
    guild = MagicMock()
    guild.id = guild_id
    return guild, MagicMock(name="voice_channel"), MagicMock(name="vc")


# ── finders ───────────────────────────────────────────────────────────────


def test_find_channel_by_number():
    assert _find_channel(TVH_CHANNELS, "4")["uuid"] == "u4"


def test_find_channel_digit_falls_back_to_name():
    # No channel numbered 99 but "Channel 4" doesn't contain "99" either.
    assert _find_channel(TVH_CHANNELS, "99") is None
    chs = [{"name": "Sky 1", "number": 100}]
    assert _find_channel(chs, "1")["name"] == "Sky 1"


def test_find_channel_by_name_substring_case_insensitive():
    assert _find_channel(TVH_CHANNELS, "bbc two")["uuid"] == "u2"
    assert _find_channel(TVH_CHANNELS, "nope") is None


def test_find_iptv_channel():
    assert _find_iptv_channel(IPTV_FULL, "FLUMBO")["source"] == "src-b"
    assert _find_iptv_channel(IPTV_FULL, "missing") is None
    assert _find_iptv_channel([{"number": 1}], "x") is None


# ── _tvh_active / _cancel_schedule ────────────────────────────────────────


def test_tvh_active_none_client():
    assert _cog(tvh=None, sm=_sm())._tvh_active() is False


def test_tvh_active_respects_source_toggle():
    assert _cog(tvh=_tvh(), sm=_sm(tvh_enabled=False))._tvh_active() is False
    assert _cog(tvh=_tvh(), sm=_sm(tvh_enabled=True))._tvh_active() is True


def test_tvh_active_without_source_manager_defaults_true():
    assert _cog(tvh=_tvh(), sm=None)._tvh_active() is True


async def test_cancel_schedule_cancels_pending_task():
    cog = _cog()
    task = asyncio.create_task(asyncio.Event().wait())
    cog._schedule_tasks[42] = task
    cog._cancel_schedule(42)
    await asyncio.sleep(0)
    assert task.cancelled()
    assert 42 not in cog._schedule_tasks
    cog._cancel_schedule(42)  # no-op when nothing scheduled


async def test_cancel_schedule_skips_done_task():
    cog = _cog()
    done = MagicMock()
    done.done.return_value = True
    cog._schedule_tasks[42] = done
    cog._cancel_schedule(42)
    done.cancel.assert_not_called()


# ── _start_stream / _start_iptv_stream ────────────────────────────────────


async def test_start_stream_forwards_to_engine():
    cog = _cog()
    guild, vch, vc = _voice()
    send = AsyncMock()
    with patch.object(tv_mod, "start_live_stream", AsyncMock()) as sls:
        await cog._start_stream(send, guild, vch, vc, "Name", "http://u", "#1",
                                live=False, audio=False, probe_size=5)
    sls.assert_awaited_once_with(
        cog.bot, send, guild, vch, vc, title="Name", url="http://u", subtitle="#1",
        live=False, audio=False, probe_size=5,
    )


async def test_start_iptv_stream_unreachable():
    cog = _cog()
    send = AsyncMock()
    with patch.object(tv_mod, "_extract_hls_variant_url", AsyncMock(return_value="http://v")), \
         patch.object(tv_mod, "_probe_stream", AsyncMock(return_value=None)) as probe, \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await cog._start_iptv_stream(send, *_voice(), "Zorb", "http://m")
    probe.assert_awaited_once_with("http://v")
    assert "could not reach the stream for **Zorb**" in send.await_args_list[-1].args[0]
    ss.assert_not_awaited()


async def test_start_iptv_stream_unsupported_codec():
    cog = _cog()
    send = AsyncMock()
    info = {**GOOD_PROBE, "codec": "av1", "width": None}
    with patch.object(tv_mod, "_extract_hls_variant_url", AsyncMock(return_value="http://v")), \
         patch.object(tv_mod, "_probe_stream", AsyncMock(return_value=info)), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await cog._start_iptv_stream(send, *_voice(), "Zorb", "http://m")
    assert "unsupported video codec `av1`" in send.await_args_list[-1].args[0]
    ss.assert_not_awaited()


@pytest.mark.parametrize("has_audio", [True, False])
async def test_start_iptv_stream_success_uses_variant_url(has_audio):
    cog = _cog()
    send = AsyncMock()
    guild, vch, vc = _voice()
    info = {**GOOD_PROBE, "has_audio": has_audio}
    with patch.object(tv_mod, "_extract_hls_variant_url", AsyncMock(return_value="http://v")), \
         patch.object(tv_mod, "_probe_stream", AsyncMock(return_value=info)), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await cog._start_iptv_stream(send, guild, vch, vc, "Zorb", "http://m", "src")
    assert send.await_args_list[0].args[0] == "checking stream…"
    ss.assert_awaited_once_with(
        send, guild, vch, vc, "Zorb", "http://v", "src",
        live=True, audio=has_audio, probe_size=10_000_000,
    )


# ── !channels ─────────────────────────────────────────────────────────────


async def test_channels_nothing_configured():
    cog = _cog(tvh=None, sm=None)
    ctx = _ctx()
    await cog.channels.callback(cog, ctx)
    assert _sent(ctx) == ["no channels found"]


async def test_channels_no_match_with_query():
    cog = _cog(tvh=_tvh(TVH_CHANNELS), sm=_sm(IPTV_FULL))
    ctx = _ctx()
    await cog.channels.callback(cog, ctx, query="  GRONK ")
    assert _sent(ctx) == ["no channels found matching **gronk**"]


async def test_channels_tvh_listing_with_now_playing():
    tvh = _tvh(TVH_CHANNELS, now_playing={"u1": "The News"})
    cog = _cog(tvh=tvh, sm=_sm())
    ctx = _ctx()
    await cog.channels.callback(cog, ctx)
    out = _sent(ctx)[0]
    assert out.startswith("```\n") and out.endswith("\n```")
    body = out.splitlines()[1:-1]
    assert body[0] == f"   1  {'BBC One':<25}  ▶ The News"
    assert body[1] == "   2  BBC Two"
    assert body[3] == "No Number"


async def test_channels_tvh_query_filter():
    cog = _cog(tvh=_tvh(TVH_CHANNELS), sm=None)
    ctx = _ctx()
    await cog.channels.callback(cog, ctx, query="bbc")
    body = _sent(ctx)[0].splitlines()[1:-1]
    assert body == ["   1  BBC One", "   2  BBC Two"]


async def test_channels_tvh_epg_failure_still_lists():
    tvh = _tvh(TVH_CHANNELS)
    tvh.get_now_playing.side_effect = RuntimeError("epg down")
    cog = _cog(tvh=tvh, sm=None)
    ctx = _ctx()
    await cog.channels.callback(cog, ctx)
    assert "BBC One" in _sent(ctx)[0]
    assert "▶" not in _sent(ctx)[0]


@pytest.mark.parametrize(
    "exc, prefix",
    [
        (urllib.error.URLError("refused"), "could not reach TVheadend (showing IPTV only)"),
        (RuntimeError("boom"), "failed to fetch TVheadend channels (showing IPTV only)"),
    ],
)
async def test_channels_tvh_failure_falls_through_to_iptv(exc, prefix):
    tvh = _tvh()
    tvh.get_channels.side_effect = exc
    cog = _cog(tvh=tvh, sm=_sm(IPTV_FULL))
    ctx = _ctx()
    await cog.channels.callback(cog, ctx)
    msgs = _sent(ctx)
    assert msgs[0].startswith(prefix)
    assert "--- src-a ---" in msgs[1]
    assert "  Zorbax One  [News]" in msgs[1]
    assert "--- src-b ---" in msgs[1]
    assert "  Flumbo Network" in msgs[1]


async def test_channels_tvh_disabled_skips_tvh():
    tvh = _tvh(TVH_CHANNELS)
    cog = _cog(tvh=tvh, sm=_sm(IPTV_FULL, tvh_enabled=False))
    ctx = _ctx()
    await cog.channels.callback(cog, ctx, query="zorb")
    tvh.get_channels.assert_not_awaited()
    body = _sent(ctx)[0].splitlines()[1:-1]
    assert body == ["--- src-a ---", "  Zorbax One  [News]"]


async def test_channels_iptv_epg_fetched_and_cached():
    sm = _sm(IPTV_FULL, epg_sources=[("src-a", "http://epg/a")])
    cog = _cog(tvh=None, sm=sm)
    ctx = _ctx()
    fetch = AsyncMock(return_value={"zorb1": "Morning Show"})
    with patch.object(tv_mod, "_fetch_xmltv_now_playing", fetch):
        await cog.channels.callback(cog, ctx)
        await cog.channels.callback(cog, ctx)
    fetch.assert_awaited_once_with("http://epg/a")
    assert f"  {'Zorbax One':<25}  ▶ Morning Show  [News]" in _sent(ctx)[0]
    assert "▶ Morning Show" in _sent(ctx)[1]
    assert tv_mod._epg_cache["src-a"][1] == {"zorb1": "Morning Show"}


async def test_channels_iptv_epg_refresh_failure_uses_stale_cache():
    tv_mod._epg_cache["src-a"] = (0.0, {"zorb1": "Stale Show"})
    sm = _sm(IPTV_FULL, epg_sources=[("src-a", "http://epg/a")])
    cog = _cog(tvh=None, sm=sm)
    ctx = _ctx()
    with patch.object(tv_mod, "_fetch_xmltv_now_playing", AsyncMock(side_effect=OSError("x"))):
        await cog.channels.callback(cog, ctx)
    assert "▶ Stale Show" in _sent(ctx)[0]


async def test_channels_iptv_epg_failure_without_cache():
    sm = _sm(IPTV_FULL, epg_sources=[("src-a", "http://epg/a")])
    cog = _cog(tvh=None, sm=sm)
    ctx = _ctx()
    with patch.object(tv_mod, "_fetch_xmltv_now_playing", AsyncMock(side_effect=OSError("x"))):
        await cog.channels.callback(cog, ctx)
    assert "▶" not in _sent(ctx)[0]
    assert "Zorbax One" in _sent(ctx)[0]


def _many_channels(n: int = 120) -> list[dict]:
    return [{"name": f"Channel Number {i:03d} Extra Long", "number": i, "uuid": f"u{i}"}
            for i in range(n)]


def _reply(content: str) -> MagicMock:
    m = MagicMock()
    m.content = content
    return m


async def test_channels_pagination_yes_then_stop():
    cog = _cog(tvh=_tvh(_many_channels()), sm=None)
    ctx = _ctx()
    cog.bot.wait_for.side_effect = [_reply("Yes"), _reply("nah")]
    await cog.channels.callback(cog, ctx)
    msgs = _sent(ctx)
    pages = [m for m in msgs if m.startswith("```")]
    assert len(pages) == 2
    assert all(len(p) <= 1800 + 10 for p in pages)
    assert msgs[1] == "more channels available — see next page? (yes/no)"
    assert msgs[-1] == "ok, stopping here"
    # The check only accepts replies from the same author in the same channel.
    check = cog.bot.wait_for.await_args.kwargs["check"]
    good = MagicMock(author=ctx.author, channel=ctx.channel)
    bad = MagicMock(author=MagicMock(), channel=ctx.channel)
    assert check(good) is True
    assert check(bad) is False


async def test_channels_pagination_all_pages():
    cog = _cog(tvh=_tvh(_many_channels()), sm=None)
    ctx = _ctx()
    cog.bot.wait_for.side_effect = lambda *a, **k: _reply("y")
    await cog.channels.callback(cog, ctx)
    msgs = _sent(ctx)
    pages = [m for m in msgs if m.startswith("```")]
    assert len(pages) >= 3
    joined = "".join(pages)
    for i in (0, 60, 119):
        assert f"Channel Number {i:03d}" in joined
    assert msgs[-1].startswith("```")


async def test_channels_pagination_timeout_edits_prompt():
    cog = _cog(tvh=_tvh(_many_channels()), sm=None)
    ctx = _ctx()
    cog.bot.wait_for.side_effect = TimeoutError
    await cog.channels.callback(cog, ctx)
    assert _edits(ctx) == ["channels: timed out waiting for reply"]


# ── !play ─────────────────────────────────────────────────────────────────


def _patch_voice(in_voice: bool = True):
    guild, vch, vc = _voice()
    ret = (guild, vch if in_voice else None, vc)
    return patch.object(tv_mod, "resolve_voice", AsyncMock(return_value=ret)), (guild, vch, vc)


async def test_play_not_in_voice():
    cog = _cog()
    ctx = _ctx()
    p, _ = _patch_voice(in_voice=False)
    with p:
        await cog.play.callback(cog, ctx, query="bbc")
    assert _sent(ctx) == ["you're not in a voice channel"]


async def test_play_cgi_url_streams_directly():
    cog = _cog()
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    with p, patch.object(cog, "_start_iptv_stream", AsyncMock()) as sis, \
         patch.object(tv_mod, "_yt_extract_live_url", AsyncMock()) as live:
        await cog.play.callback(cog, ctx, query="http://cam.local/video/Feed.CGI?x=1")
    sis.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "feed.cgi", "http://cam.local/video/Feed.CGI?x=1"
    )
    live.assert_not_awaited()


async def test_play_live_url():
    cog = _cog()
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    with p, patch.object(cog, "_start_iptv_stream", AsyncMock()) as sis, \
         patch.object(tv_mod, "_yt_extract_live_url",
                      AsyncMock(return_value=("http://hls/live.m3u8", "Live Event"))), \
         patch.object(tv_mod, "_yt_download", AsyncMock()) as dl:
        await cog.play.callback(cog, ctx, query="https://youtube.com/watch?v=x")
    assert _edits(ctx) == ["starting **Live Event**…"]
    sis.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "Live Event", "http://hls/live.m3u8"
    )
    dl.assert_not_awaited()


async def test_play_url_download_failure_cleans_up(tmp_path):
    cog = _cog()
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(tv_mod, "_yt_extract_live_url", AsyncMock(side_effect=RuntimeError("x"))), \
         patch.object(tv_mod.tempfile, "mkdtemp", return_value=str(tmp_path)), \
         patch.object(tv_mod, "_yt_download", AsyncMock(side_effect=RuntimeError("403"))), \
         patch.object(tv_mod, "_yt_remove_dir") as rm, \
         patch.object(tv_mod, "start_live_stream", AsyncMock()) as sls:
        await cog.play.callback(cog, ctx, query="https://vid.example/v")
    assert _edits(ctx) == ["downloading…", "download failed: 403"]
    rm.assert_called_once_with(str(tmp_path))
    sls.assert_not_awaited()


async def test_play_url_download_success_schedules_cleanup(tmp_path):
    cog = _cog()
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    stream_task = MagicMock()
    cog.bot.stream_tasks = {guild.id: stream_task}
    cleanup = AsyncMock()
    with p, patch.object(tv_mod, "_yt_extract_live_url", AsyncMock(return_value=None)), \
         patch.object(tv_mod.tempfile, "mkdtemp", return_value=str(tmp_path)), \
         patch.object(tv_mod, "_yt_download", AsyncMock(return_value=("/f.mp4", "Clip"))), \
         patch.object(tv_mod, "_yt_cleanup_after_stream", cleanup), \
         patch.object(tv_mod, "_yt_remove_dir") as rm, \
         patch.object(tv_mod, "start_live_stream", AsyncMock()) as sls:
        await cog.play.callback(cog, ctx, query="https://vid.example/v")
        await asyncio.sleep(0)
    assert _edits(ctx) == ["downloading…", "starting **Clip**…"]
    sls.assert_awaited_once_with(
        cog.bot, ctx.send, guild, vch, vc, title="Clip", url="/f.mp4",
        live=False, audio=True, probe_size=2_000_000,
    )
    cleanup.assert_awaited_once_with(stream_task, str(tmp_path))
    rm.assert_not_called()


async def test_play_url_download_success_no_stream_task_removes_dir(tmp_path):
    cog = _cog()
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(tv_mod, "_yt_extract_live_url", AsyncMock(return_value=None)), \
         patch.object(tv_mod.tempfile, "mkdtemp", return_value=str(tmp_path)), \
         patch.object(tv_mod, "_yt_download", AsyncMock(return_value=("/f.mp4", "Clip"))), \
         patch.object(tv_mod, "_yt_remove_dir") as rm, \
         patch.object(tv_mod, "start_live_stream", AsyncMock()):
        await cog.play.callback(cog, ctx, query="https://vid.example/v")
    rm.assert_called_once_with(str(tmp_path))


async def test_play_url_stream_start_error_cleans_up_and_reraises(tmp_path):
    cog = _cog()
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(tv_mod, "_yt_extract_live_url", AsyncMock(return_value=None)), \
         patch.object(tv_mod.tempfile, "mkdtemp", return_value=str(tmp_path)), \
         patch.object(tv_mod, "_yt_download", AsyncMock(return_value=("/f.mp4", "Clip"))), \
         patch.object(tv_mod, "_yt_remove_dir") as rm, \
         patch.object(tv_mod, "start_live_stream", AsyncMock(side_effect=RuntimeError("ff"))):
        with pytest.raises(RuntimeError, match="ff"):
            await cog.play.callback(cog, ctx, query="https://vid.example/v")
    rm.assert_called_once_with(str(tmp_path))


async def test_play_tvh_channel_by_number_cancels_schedule():
    tvh = _tvh(TVH_CHANNELS)
    cog = _cog(tvh=tvh, sm=_sm(IPTV_FULL))
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    with p, patch.object(cog, "_start_stream", AsyncMock()) as ss, \
         patch.object(cog, "_cancel_schedule") as cancel:
        await cog.play.callback(cog, ctx, query="2")
    cancel.assert_called_once_with(guild.id)
    ss.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "BBC Two", "http://tvh/stream/u2", "#2"
    )


@pytest.mark.parametrize("exc", [urllib.error.URLError("down"), RuntimeError("bad")])
async def test_play_tvh_error_falls_through_to_iptv(exc):
    tvh = _tvh()
    tvh.get_channels.side_effect = exc
    cog = _cog(tvh=tvh, sm=_sm(IPTV_FULL))
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    with p, patch.object(cog, "_start_iptv_stream", AsyncMock()) as sis:
        await cog.play.callback(cog, ctx, query="zorbax")
    sis.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "Zorbax One", "http://iptv/zorb1", "src-a"
    )


async def test_play_iptv_without_tvh():
    cog = _cog(tvh=None, sm=_sm(IPTV_FULL))
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(cog, "_start_iptv_stream", AsyncMock()) as sis:
        await cog.play.callback(cog, ctx, query="flumbo")
    assert sis.await_args.args[4:] == ("Flumbo Network", "http://iptv/flmb", "src-b")


async def test_play_channel_not_found():
    cog = _cog(tvh=_tvh(TVH_CHANNELS), sm=None)
    ctx = _ctx()
    p, _ = _patch_voice()
    with p:
        await cog.play.callback(cog, ctx, query="gronkvision")
    assert _sent(ctx) == [
        "channel not found: `gronkvision` — use `!channels` to see what's available"
    ]


# ── !search ───────────────────────────────────────────────────────────────

NOW_TS = 1_000_000.0


def _event(start, stop, title="Show", uuid="u1", name="BBC One", number=1):
    return {"start": start, "stop": stop, "title": title, "channelUuid": uuid,
            "channelName": name, "channelNumber": number}


def _yn(content: str, author_id: int = 1, channel_id: int = 10) -> MagicMock:
    m = MagicMock()
    m.content = content
    m.author.id = author_id
    m.channel.id = channel_id
    return m


async def test_search_not_in_voice():
    cog = _cog()
    ctx = _ctx()
    p, _ = _patch_voice(in_voice=False)
    with p:
        await cog.search.callback(cog, ctx, query="news")
    assert _sent(ctx) == ["you're not in a voice channel"]


@pytest.mark.parametrize("exc", [None, urllib.error.URLError("x"), RuntimeError("y")])
async def test_search_no_events_matches_iptv(exc):
    tvh = _tvh()
    if exc:
        tvh.get_epg_events.side_effect = exc
    cog = _cog(tvh=tvh, sm=_sm(IPTV_FULL))
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    with p, patch.object(cog, "_start_iptv_stream", AsyncMock()) as sis:
        await cog.search.callback(cog, ctx, query="zorbax")
    sis.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "Zorbax One", "http://iptv/zorb1", "src-a"
    )


async def test_search_nothing_found():
    cog = _cog(tvh=None, sm=None)
    ctx = _ctx()
    p, _ = _patch_voice()
    with p:
        await cog.search.callback(cog, ctx, query="gronk")
    assert _sent(ctx) == ["nothing found in the TV guide for `gronk`"]


async def test_search_airing_now_plays_immediately():
    tvh = _tvh(events=[_event(NOW_TS - 60, NOW_TS + 60, uuid="u4", name="Channel 4", number=4)])
    cog = _cog(tvh=tvh, sm=None)
    ctx = _ctx()
    p, (guild, vch, vc) = _patch_voice()
    with p, patch.object(tv_mod.time, "time", return_value=NOW_TS), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await cog.search.callback(cog, ctx, query="show")
    ss.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "Channel 4", "http://tvh/stream/u4", "#4"
    )


async def test_search_airing_now_missing_uuid():
    tvh = _tvh(events=[_event(NOW_TS - 60, NOW_TS + 60, uuid="")])
    cog = _cog(tvh=tvh, sm=None)
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(tv_mod.time, "time", return_value=NOW_TS), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await cog.search.callback(cog, ctx, query="show")
    assert "channel UUID is missing — try `!play BBC One` instead" in _sent(ctx)[0]
    ss.assert_not_awaited()


async def test_search_only_beyond_24h_or_past():
    events = [
        _event(NOW_TS + 25 * 3600, NOW_TS + 26 * 3600),
        _event(NOW_TS - 7200, NOW_TS - 3600),
    ]
    cog = _cog(tvh=_tvh(events=events), sm=None)
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(tv_mod.time, "time", return_value=NOW_TS):
        await cog.search.callback(cog, ctx, query="Show")
    assert _sent(ctx) == ["**Show** isn't in the guide for the next 24 hours."]


async def test_search_upcoming_missing_uuid():
    cog = _cog(tvh=_tvh(events=[_event(NOW_TS + 600, NOW_TS + 1200, uuid="")]), sm=None)
    ctx = _ctx()
    p, _ = _patch_voice()
    with p, patch.object(tv_mod.time, "time", return_value=NOW_TS):
        await cog.search.callback(cog, ctx, query="show")
    assert "channel UUID is missing — try `!play BBC One` manually" in _sent(ctx)[0]
    cog.bot.wait_for.assert_not_awaited()


async def _run_upcoming(reply, start_offset=3600, events=None):
    events = events or [_event(NOW_TS + start_offset, NOW_TS + start_offset + 1800)]
    cog = _cog(tvh=_tvh(events=events), sm=None)
    ctx = _ctx()
    if isinstance(reply, BaseException) or reply is TimeoutError:
        cog.bot.wait_for.side_effect = reply
    else:
        cog.bot.wait_for.return_value = reply
    p, voice = _patch_voice()
    with p, patch.object(tv_mod.time, "time", return_value=NOW_TS), \
         patch.object(tv_mod, "_fmt_time", return_value="9:00 PM"), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await cog.search.callback(cog, ctx, query="show")
    return cog, ctx, ss, voice


async def test_search_upcoming_prompt_picks_earliest_and_timeout():
    events = [
        _event(NOW_TS + 7200, NOW_TS + 9000, title="Later", name="BBC Two", number=2),
        _event(NOW_TS + 3600, NOW_TS + 5400, title="Sooner"),
    ]
    cog, ctx, ss, _ = await _run_upcoming(TimeoutError, events=events)
    msgs = _sent(ctx)
    assert msgs[0] == "**Sooner** is on **BBC One** (#1) at 9:00 PM. Schedule a viewing? (y/n)"
    assert msgs[-1] == "no response — schedule cancelled"
    ss.assert_not_awaited()


async def test_search_upcoming_yn_check_predicate():
    cog, ctx, _, _ = await _run_upcoming(TimeoutError)
    check = cog.bot.wait_for.await_args.kwargs["check"]
    assert check(_yn("Y")) and check(_yn(" no ")) and check(_yn("!stop"))
    assert not check(_yn("maybe"))
    assert not check(_yn("y", author_id=2))
    assert not check(_yn("y", channel_id=11))


async def test_search_upcoming_other_command_cancels():
    _, ctx, ss, _ = await _run_upcoming(_yn("!play bbc"))
    assert _sent(ctx)[-1] == "schedule cancelled"
    ss.assert_not_awaited()


async def test_search_upcoming_declined():
    _, ctx, ss, _ = await _run_upcoming(_yn("n"))
    assert _sent(ctx)[-1] == "ok, not scheduling"
    ss.assert_not_awaited()


async def test_search_upcoming_imminent_starts_now():
    # Starts in 20 s → 30 s lead time already passed → stream right away.
    cog, ctx, ss, (guild, vch, vc) = await _run_upcoming(_yn("yes"), start_offset=20)
    ss.assert_awaited_once_with(
        ctx.send, guild, vch, vc, "BBC One", "http://tvh/stream/u1", "#1"
    )
    assert cog._schedule_tasks == {}


async def _schedule(start_offset=3600):
    cog, ctx, ss, voice = await _run_upcoming(_yn("y"), start_offset=start_offset)
    guild = voice[0]
    task = cog._schedule_tasks[guild.id]
    return cog, ctx, task, guild


async def test_search_upcoming_schedules_and_fires():
    cog, ctx, task, guild = await _schedule(start_offset=3600)
    assert _sent(ctx)[-1] == (
        "Scheduled! I'll switch to **BBC One** for **Show** in 59m 30s."
    )
    g = MagicMock()
    member = MagicMock()
    g.get_member.return_value = member
    cog.bot.get_guild.return_value = g
    ctx.channel.send = AsyncMock(side_effect=[RuntimeError("send fails"), None])
    sleep = AsyncMock()
    with patch.object(tv_mod.asyncio, "sleep", sleep), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await task
        sleep.assert_awaited_once_with(3570.0)
        args = ss.await_args.args
        assert args[1:] == (g, member.voice.channel, g.voice_client,
                            "BBC One", "http://tvh/stream/u1", "#1")
        # The wrapped send swallows errors from the text channel.
        send = args[0]
        await send("hello")
        await send("again", embed=None)
    assert ctx.channel.send.await_count == 2
    assert guild.id not in cog._schedule_tasks
    g.get_member.assert_called_once_with(ctx.author.id)


async def test_search_schedule_short_wait_formats_seconds():
    _, ctx, task, _ = await _schedule(start_offset=75)
    assert _sent(ctx)[-1].endswith("in 45s.")
    task.cancel()
    await asyncio.gather(task, return_exceptions=True)


async def test_scheduled_play_guild_gone():
    cog, ctx, task, _ = await _schedule()
    cog.bot.get_guild.return_value = None
    with patch.object(tv_mod.asyncio, "sleep", AsyncMock()), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await task
    ss.assert_not_awaited()


@pytest.mark.parametrize("send_raises", [False, True])
async def test_scheduled_play_user_left_voice(send_raises):
    cog, ctx, task, _ = await _schedule()
    g = MagicMock()
    member = MagicMock()
    member.voice = None
    g.get_member.return_value = member
    cog.bot.get_guild.return_value = g
    ctx.channel.send = AsyncMock(side_effect=RuntimeError("x") if send_raises else None)
    with patch.object(tv_mod.asyncio, "sleep", AsyncMock()), \
         patch.object(cog, "_start_stream", AsyncMock()) as ss:
        await task
    ss.assert_not_awaited()
    ctx.channel.send.assert_awaited_once_with(
        "Scheduled stream cancelled — you're not in a voice channel."
    )


async def test_scheduled_play_cancelled_by_new_play():
    cog, ctx, task, guild = await _schedule()
    await asyncio.sleep(0)  # let the task start sleeping
    cog._cancel_schedule(guild.id)
    await asyncio.gather(task, return_exceptions=True)
    # CancelledError is caught inside the coroutine, so the task completes.
    assert task.done()
    assert guild.id not in cog._schedule_tasks


async def test_search_reschedule_cancels_previous():
    cog, ctx, first, guild = await _schedule()
    # A second scheduling for the same guild replaces (and cancels) the first.
    cog.bot.wait_for.return_value = _yn("y")
    p, _ = _patch_voice()
    with p, patch.object(tv_mod.time, "time", return_value=NOW_TS), \
         patch.object(tv_mod, "_fmt_time", return_value="9:00 PM"):
        await cog.search.callback(cog, ctx, query="show")
    await asyncio.sleep(0)
    assert first.done()
    second = cog._schedule_tasks[guild.id]
    assert second is not first
    second.cancel()
    await asyncio.gather(second, return_exceptions=True)


# ── setup ─────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("configured", [True, False])
async def test_setup_adds_cog(monkeypatch, configured):
    for k in ("TVHEADEND_URL", "TVHEADEND_USER", "TVHEADEND_PASS"):
        if configured:
            monkeypatch.setenv(k, "http://h" if k.endswith("URL") else "x")
        else:
            monkeypatch.delenv(k, raising=False)
    bot = MagicMock()
    bot.add_cog = AsyncMock()
    await tv_mod.setup(bot)
    cog = bot.add_cog.await_args.args[0]
    assert isinstance(cog, TV)
    assert (cog.tvh is not None) is configured
