"""Tests for the Jellyfin cog helpers and the JellyfinClient API calls."""
from __future__ import annotations

import json
from io import BytesIO
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from cogs.jellyfin import Jellyfin, _fmt_item, _parse_episode_query
from services.jellyfin import JellyfinClient, PlaybackStream


# ---------------------------------------------------------------------------
# Helpers shared across sections
# ---------------------------------------------------------------------------


def _make_ctx(author_id: int = 1, channel_id: int = 10) -> MagicMock:
    ctx = MagicMock()
    ctx.author.id = author_id
    ctx.channel.id = channel_id
    ctx.send = AsyncMock()
    return ctx


def _make_jellyfin_cog(client=None) -> Jellyfin:
    bot = MagicMock()
    bot.user.id = 999
    cog = Jellyfin(bot, client)
    return cog


# ---------------------------------------------------------------------------
# _parse_episode_query
# ---------------------------------------------------------------------------


def test_parse_no_episode_code():
    show, season, episode = _parse_episode_query("inception")
    assert show == "inception"
    assert season is None
    assert episode is None


def test_parse_lowercase():
    show, season, episode = _parse_episode_query("rick and morty s02e01")
    assert show == "rick and morty"
    assert season == 2
    assert episode == 1


def test_parse_uppercase():
    show, season, episode = _parse_episode_query("Breaking Bad S03E10")
    assert show == "Breaking Bad"
    assert season == 3
    assert episode == 10


def test_parse_mixed_case():
    show, season, episode = _parse_episode_query("The Office S01e03")
    assert show == "The Office"
    assert season == 1
    assert episode == 3


def test_parse_leading_trailing_whitespace_stripped():
    show, season, episode = _parse_episode_query("  Arrested Development  s04e01  ")
    assert show == "Arrested Development"
    assert season == 4
    assert episode == 1


def test_parse_only_episode_code_gives_empty_show():
    show, season, episode = _parse_episode_query("s01e05")
    assert show == ""
    assert season == 1
    assert episode == 5


def test_parse_multi_digit_season_and_episode():
    show, season, episode = _parse_episode_query("show s12e24")
    assert show == "show"
    assert season == 12
    assert episode == 24


def test_parse_uses_first_match_only():
    """Extra sXXeYY tokens after the first are ignored."""
    show, season, episode = _parse_episode_query("show s01e01 s02e02")
    assert show == "show"
    assert season == 1
    assert episode == 1


def test_parse_code_not_on_word_boundary_ignored():
    """Substrings like 'season01ep01' should not match."""
    show, season, episode = _parse_episode_query("seas01ep01")
    assert show == "seas01ep01"
    assert season is None
    assert episode is None


# ---------------------------------------------------------------------------
# _fmt_item
# ---------------------------------------------------------------------------


def test_fmt_movie_with_year():
    item = {"Type": "Movie", "Name": "Inception", "ProductionYear": 2010}
    assert _fmt_item(item) == "Inception (2010)"


def test_fmt_movie_without_year():
    item = {"Type": "Movie", "Name": "Inception"}
    assert _fmt_item(item) == "Inception"


def test_fmt_series_with_year():
    item = {"Type": "Series", "Name": "Breaking Bad", "ProductionYear": 2008}
    assert _fmt_item(item) == "Breaking Bad (2008)"


def test_fmt_episode_with_season_and_ep():
    item = {
        "Type": "Episode",
        "Name": "Pilot",
        "SeriesName": "Breaking Bad",
        "ParentIndexNumber": 1,
        "IndexNumber": 1,
    }
    assert _fmt_item(item) == "Breaking Bad — S01E01 — Pilot"


def test_fmt_episode_zero_padded():
    item = {
        "Type": "Episode",
        "Name": "Fly",
        "SeriesName": "Breaking Bad",
        "ParentIndexNumber": 3,
        "IndexNumber": 10,
    }
    assert _fmt_item(item) == "Breaking Bad — S03E10 — Fly"


def test_fmt_episode_without_season_ep():
    item = {
        "Type": "Episode",
        "Name": "Pilot",
        "SeriesName": "Breaking Bad",
    }
    assert _fmt_item(item) == "Breaking Bad — Pilot"


# ---------------------------------------------------------------------------
# JellyfinClient HTTP methods (urlopen mocked)
# ---------------------------------------------------------------------------


def _mock_response(data: dict):
    """Return a context-manager mock that yields a response with JSON body."""
    body = json.dumps(data).encode()
    resp = MagicMock()
    resp.read.return_value = body
    resp.__enter__ = lambda s: s
    resp.__exit__ = MagicMock(return_value=False)
    return resp


@pytest.fixture
def client():
    return JellyfinClient(url="http://jellyfin.local:8096", api_key="testkey")


@pytest.mark.asyncio
async def test_get_user_id_fetches_and_caches(client):
    users = [{"Id": "user-abc", "Name": "Admin"}]
    with patch("urllib.request.urlopen", return_value=_mock_response(users)):
        uid = await client._get_user_id()
    assert uid == "user-abc"
    # Second call should use cache, not make another request
    with patch("urllib.request.urlopen", side_effect=Exception("should not be called")):
        uid2 = await client._get_user_id()
    assert uid2 == "user-abc"


@pytest.mark.asyncio
async def test_get_user_id_returns_none_on_failure(client):
    with patch("urllib.request.urlopen", side_effect=Exception("403 Forbidden")):
        uid = await client._get_user_id()
    assert uid is None


def _make_playback_response(transcoding_url: str | None = None, direct_url: str | None = None):
    source: dict = {"Id": "source-1"}
    if transcoding_url:
        source["TranscodingUrl"] = transcoding_url
    if direct_url:
        source["DirectStreamUrl"] = direct_url
    return _mock_response({"MediaSources": [source], "PlaySessionId": "sess-1"})


@pytest.mark.asyncio
async def test_get_stream_url_uses_playback_info(client):
    client._user_id = "user-abc"  # pre-cache so no extra fetch
    pb_resp = _make_playback_response(
        transcoding_url="/Videos/item-1/main/stream.m3u8?DeviceId=slopsoil&PlaySessionId=abc"
    )
    with patch("urllib.request.urlopen", return_value=pb_resp):
        url = await client.get_stream_url("item-1")
    assert url is not None
    assert "stream.m3u8" in url
    assert "api_key=testkey" in url
    assert "SubtitleStreamIndex=-1" in url


@pytest.mark.asyncio
async def test_get_stream_url_overrides_subtitle_index(client):
    """Jellyfin may set SubtitleStreamIndex=0 in the URL from user profile settings; we must override it."""
    client._user_id = "user-abc"
    pb_resp = _make_playback_response(
        transcoding_url="/Videos/item-1/stream.m3u8?SubtitleStreamIndex=0&PlaySessionId=abc"
    )
    with patch("urllib.request.urlopen", return_value=pb_resp):
        url = await client.get_stream_url("item-1")
    assert url is not None
    assert "SubtitleStreamIndex=-1" in url
    assert "SubtitleStreamIndex=0" not in url


@pytest.mark.asyncio
async def test_get_stream_url_falls_back_to_direct_stream(client):
    client._user_id = "user-abc"
    pb_resp = _make_playback_response(direct_url="/Videos/item-1/stream?Container=mp4")
    with patch("urllib.request.urlopen", return_value=pb_resp):
        url = await client.get_stream_url("item-1")
    assert url is not None
    assert "api_key=testkey" in url


@pytest.mark.asyncio
async def test_get_stream_url_returns_none_on_empty_sources(client):
    client._user_id = "user-abc"
    with patch("urllib.request.urlopen", return_value=_mock_response({"MediaSources": []})):
        url = await client.get_stream_url("item-1")
    assert url is None


@pytest.mark.asyncio
async def test_get_stream_url_returns_none_on_request_failure(client):
    client._user_id = "user-abc"
    with patch("urllib.request.urlopen", side_effect=Exception("connection refused")):
        url = await client.get_stream_url("item-1")
    assert url is None


@pytest.mark.asyncio
async def test_search_returns_items(client):
    payload = {
        "Items": [
            {"Id": "1", "Name": "Inception", "Type": "Movie", "ProductionYear": 2010},
            {"Id": "2", "Name": "Interstellar", "Type": "Movie", "ProductionYear": 2014},
        ]
    }
    with patch("urllib.request.urlopen", return_value=_mock_response(payload)):
        results = await client.search("ception")
    assert len(results) == 2
    assert results[0]["Name"] == "Inception"


@pytest.mark.asyncio
async def test_search_empty_result(client):
    with patch("urllib.request.urlopen", return_value=_mock_response({"Items": []})):
        results = await client.search("nothing")
    assert results == []


@pytest.mark.asyncio
async def test_get_seasons_sorted(client):
    payload = {
        "Items": [
            {"Id": "s2", "Name": "Season 2", "IndexNumber": 2},
            {"Id": "s1", "Name": "Season 1", "IndexNumber": 1},
            {"Id": "s3", "Name": "Season 3", "IndexNumber": 3},
        ]
    }
    with patch("urllib.request.urlopen", return_value=_mock_response(payload)):
        seasons = await client.get_seasons("series-abc")
    assert [s["IndexNumber"] for s in seasons] == [1, 2, 3]


@pytest.mark.asyncio
async def test_get_episodes_sorted(client):
    payload = {
        "Items": [
            {"Id": "e3", "Name": "C", "IndexNumber": 3},
            {"Id": "e1", "Name": "A", "IndexNumber": 1},
            {"Id": "e2", "Name": "B", "IndexNumber": 2},
        ]
    }
    with patch("urllib.request.urlopen", return_value=_mock_response(payload)):
        episodes = await client.get_episodes("series-abc", "season-1")
    assert [e["IndexNumber"] for e in episodes] == [1, 2, 3]


@pytest.mark.asyncio
async def test_find_episode_resolves_via_series(client):
    """find_episode should search for a series, then navigate seasons → episode."""
    series = [{"Id": "series-1", "Name": "Rick and Morty", "Type": "Series"}]
    seasons = [
        {"Id": "s1", "Name": "Season 1", "IndexNumber": 1},
        {"Id": "s2", "Name": "Season 2", "IndexNumber": 2},
    ]
    episodes_s2 = [
        {"Id": "e1", "Name": "Ricksy Business", "IndexNumber": 1, "ParentIndexNumber": 2},
        {"Id": "e2", "Name": "A Rickle in Time", "IndexNumber": 2, "ParentIndexNumber": 2},
    ]

    async def _search(q, limit=25):
        return series

    async def _get_seasons(sid):
        assert sid == "series-1"
        return seasons

    async def _get_episodes(sid, season_id):
        assert sid == "series-1"
        assert season_id == "s2"
        return episodes_s2

    client.search = _search
    client.get_seasons = _get_seasons
    client.get_episodes = _get_episodes

    results = await client.find_episode("rick and morty", season=2, episode=1)
    assert len(results) == 1
    assert results[0]["Id"] == "e1"


@pytest.mark.asyncio
async def test_find_episode_no_matching_season(client):
    series = [{"Id": "series-1", "Name": "Show", "Type": "Series"}]
    seasons = [{"Id": "s1", "Name": "Season 1", "IndexNumber": 1}]

    client.search = AsyncMock(return_value=series)
    client.get_seasons = AsyncMock(return_value=seasons)
    client.get_episodes = AsyncMock(return_value=[])

    results = await client.find_episode("show", season=5, episode=1)
    assert results == []
    client.get_episodes.assert_not_called()


@pytest.mark.asyncio
async def test_find_episode_no_series_found(client):
    client.search = AsyncMock(return_value=[])

    results = await client.find_episode("unknown show", season=1, episode=1)
    assert results == []


# ---------------------------------------------------------------------------
# Jellyfin cog — _wait_for_number
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_wait_for_number_valid_selection():
    cog = _make_jellyfin_cog()
    ctx = _make_ctx()
    reply = MagicMock()
    reply.content = "2"
    cog.bot.wait_for = AsyncMock(return_value=reply)

    result = await cog._wait_for_number(ctx, max_val=3)
    assert result == 2


def test_wait_for_number_check_rejects_non_decimal_digits():
    """'²'.isdigit() is True but int('²') raises — the check must reject it."""
    import asyncio as _asyncio

    cog = _make_jellyfin_cog()
    ctx = _make_ctx()
    captured = {}

    async def _wait_for(event, check, timeout):
        captured["check"] = check
        raise TimeoutError

    cog.bot.wait_for = _wait_for
    _asyncio.run(cog._wait_for_number(ctx, max_val=3))
    msg = MagicMock()
    msg.author.id = ctx.author.id
    msg.channel.id = ctx.channel.id
    msg.content = "²"
    assert captured["check"](msg) is False
    msg.content = "3"
    assert captured["check"](msg) is True


def test_fmt_item_season_zero_special_keeps_code():
    item = {
        "Type": "Episode", "Name": "Pilot", "SeriesName": "Show",
        "ParentIndexNumber": 0, "IndexNumber": 0,
    }
    assert _fmt_item(item) == "Show — S00E00 — Pilot"


@pytest.mark.asyncio
async def test_wait_for_number_out_of_range_returns_none():
    cog = _make_jellyfin_cog()
    ctx = _make_ctx()
    reply = MagicMock()
    reply.content = "99"
    cog.bot.wait_for = AsyncMock(return_value=reply)

    result = await cog._wait_for_number(ctx, max_val=3)
    assert result is None
    ctx.send.assert_awaited_once()
    assert "1 and 3" in ctx.send.call_args[0][0]


@pytest.mark.asyncio
async def test_wait_for_number_timeout_returns_none():
    cog = _make_jellyfin_cog()
    ctx = _make_ctx()
    cog.bot.wait_for = AsyncMock(side_effect=TimeoutError)

    result = await cog._wait_for_number(ctx, max_val=5)
    assert result is None
    ctx.send.assert_awaited_once()
    assert "timed out" in ctx.send.call_args[0][0]


@pytest.mark.asyncio
async def test_wait_for_number_command_cancels():
    """Typing a new command while a prompt is active cancels the selection."""
    cog = _make_jellyfin_cog()
    ctx = _make_ctx()
    reply = MagicMock()
    reply.content = "!play something"
    cog.bot.wait_for = AsyncMock(return_value=reply)

    result = await cog._wait_for_number(ctx, max_val=5)
    assert result is None
    ctx.send.assert_awaited_once()
    assert "cancelled" in ctx.send.call_args[0][0]


@pytest.mark.asyncio
async def test_wait_for_number_boundary_values():
    cog = _make_jellyfin_cog()
    ctx = _make_ctx()

    for value in ("1", "5"):
        ctx.send.reset_mock()
        reply = MagicMock()
        reply.content = value
        cog.bot.wait_for = AsyncMock(return_value=reply)
        result = await cog._wait_for_number(ctx, max_val=5)
        assert result == int(value)


# ---------------------------------------------------------------------------
# Jellyfin cog — unconfigured client
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_media_command_not_configured(monkeypatch):
    """!media should send a helpful error if JELLYFIN_URL/API_KEY are not set."""
    cog = _make_jellyfin_cog(client=None)
    ctx = _make_ctx()

    # Invoke the underlying coroutine directly, bypassing the command decorator.
    await cog.media.callback(cog, ctx, query="inception")

    ctx.send.assert_awaited_once()
    msg = ctx.send.call_args[0][0]
    assert "not configured" in msg.lower()
    assert "JELLYFIN_URL" in msg


# ---------------------------------------------------------------------------
# Jellyfin cog — !media flow and _play_item
# ---------------------------------------------------------------------------

import urllib.error  # noqa: E402

from cogs import jellyfin as jf_mod  # noqa: E402

MOVIE = {"Id": "m1", "Type": "Movie", "Name": "Inception", "ProductionYear": 2010}
MOVIE2 = {"Id": "m2", "Type": "Movie", "Name": "Interstellar", "ProductionYear": 2014}
SERIES = {"Id": "ser1", "Type": "Series", "Name": "Breaking Bad"}
EPISODE = {
    "Id": "e1", "Type": "Episode", "Name": "Pilot", "SeriesName": "Breaking Bad",
    "ParentIndexNumber": 1, "IndexNumber": 1,
}


def _make_client(**overrides) -> MagicMock:
    client = MagicMock()
    client._api_key = "SECRETKEY"
    client.search = AsyncMock(return_value=[])
    client.find_episode = AsyncMock(return_value=[])
    client.get_seasons = AsyncMock(return_value=[])
    client.get_episodes = AsyncMock(return_value=[])
    client.start_playback = AsyncMock(return_value=None)
    url_mock = overrides.pop("get_stream_url", None)
    if url_mock is not None:
        # Legacy shorthand: a URL with no PlaybackInfo metadata (→ ffprobe path).
        url = url_mock.return_value
        client.start_playback = AsyncMock(
            return_value=PlaybackStream(url, "sess", None, None) if url else None
        )
    for k, v in overrides.items():
        setattr(client, k, v)
    return client


def _reply(content: str) -> MagicMock:
    msg = MagicMock()
    msg.content = content
    return msg


def _sent(ctx) -> list[str]:
    return [c.args[0] for c in ctx.send.await_args_list]


@pytest.fixture
def media_cog():
    client = _make_client()
    cog = _make_jellyfin_cog(client)
    cog._play_item = AsyncMock()
    return cog


async def test_media_url_error(media_cog):
    media_cog.client.search.side_effect = urllib.error.URLError("refused")
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="inception")
    assert _sent(ctx) == ["could not reach Jellyfin: <urlopen error refused>"]
    media_cog._play_item.assert_not_awaited()


async def test_media_generic_search_error(media_cog):
    media_cog.client.search.side_effect = ValueError("bad json")
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="inception")
    assert _sent(ctx) == ["Jellyfin search failed: bad json"]


async def test_media_no_results(media_cog):
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="nothing here")
    media_cog.client.search.assert_awaited_once_with("nothing here")
    assert _sent(ctx) == ["nothing found in Jellyfin for `nothing here`"]


async def test_media_episode_query_uses_find_episode(media_cog):
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="breaking bad s02e03")
    media_cog.client.find_episode.assert_awaited_once_with("breaking bad", 2, 3)
    media_cog.client.search.assert_not_awaited()
    assert _sent(ctx) == ["S02E03 of `breaking bad` not found in Jellyfin"]


async def test_media_episode_query_single_hit_plays(media_cog):
    media_cog.client.find_episode.return_value = [EPISODE]
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="breaking bad s01e01")
    media_cog._play_item.assert_awaited_once_with(ctx, EPISODE)


async def test_media_single_movie_plays_immediately(media_cog):
    media_cog.client.search.return_value = [MOVIE]
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="inception")
    media_cog._play_item.assert_awaited_once_with(ctx, MOVIE)
    ctx.send.assert_not_awaited()


async def test_media_single_series_goes_to_season_picker(media_cog):
    media_cog.client.search.return_value = [SERIES]
    media_cog._pick_season_then_episode = AsyncMock()
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="breaking bad")
    media_cog._pick_season_then_episode.assert_awaited_once_with(ctx, SERIES)
    media_cog._play_item.assert_not_awaited()


async def test_media_multiple_results_lists_and_plays_choice(media_cog):
    media_cog.client.search.return_value = [MOVIE, MOVIE2]
    media_cog.bot.wait_for = AsyncMock(return_value=_reply("2"))
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="in")
    listing = _sent(ctx)[0]
    assert "Found 2 result(s) for `in`" in listing
    assert "`1` [Movie] Inception (2010)" in listing
    assert "`2` [Movie] Interstellar (2014)" in listing
    media_cog._play_item.assert_awaited_once_with(ctx, MOVIE2)


async def test_media_multiple_results_series_choice(media_cog):
    media_cog.client.search.return_value = [MOVIE, SERIES]
    media_cog.bot.wait_for = AsyncMock(return_value=_reply(" 2 "))
    media_cog._pick_season_then_episode = AsyncMock()
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="b")
    media_cog._pick_season_then_episode.assert_awaited_once_with(ctx, SERIES)
    media_cog._play_item.assert_not_awaited()


async def test_media_multiple_results_timeout(media_cog):
    media_cog.client.search.return_value = [MOVIE, MOVIE2]
    media_cog.bot.wait_for = AsyncMock(side_effect=TimeoutError)
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="in")
    assert _sent(ctx)[-1] == "timed out — cancelled"
    media_cog._play_item.assert_not_awaited()


async def test_media_multiple_results_invalid_choice(media_cog):
    media_cog.client.search.return_value = [MOVIE, MOVIE2]
    media_cog.bot.wait_for = AsyncMock(return_value=_reply("0"))
    ctx = _make_ctx()
    await Jellyfin.media.callback(media_cog, ctx, query="in")
    assert "between 1 and 2" in _sent(ctx)[-1]
    media_cog._play_item.assert_not_awaited()


async def test_wait_for_number_check_predicate():
    """The wait_for check only accepts digit/command replies from the same author+channel."""
    cog = _make_jellyfin_cog()
    ctx = _make_ctx(author_id=1, channel_id=10)
    cog.bot.wait_for = AsyncMock(return_value=_reply("1"))
    await cog._wait_for_number(ctx, 3)
    kwargs = cog.bot.wait_for.call_args.kwargs
    assert cog.bot.wait_for.call_args.args == ("message",)
    assert kwargs["timeout"] == 10.0
    check = kwargs["check"]

    def msg(author, channel, content):
        m = MagicMock()
        m.author.id = author
        m.channel.id = channel
        m.content = content
        return m

    assert check(msg(1, 10, "2"))
    assert check(msg(1, 10, "!stop"))
    assert not check(msg(2, 10, "2"))
    assert not check(msg(1, 11, "2"))
    assert not check(msg(1, 10, "hello"))


# ── season / episode picking ─────────────────────────────────────────────────


async def test_pick_season_fetch_error():
    cog = _make_jellyfin_cog(_make_client(get_seasons=AsyncMock(side_effect=RuntimeError("500"))))
    ctx = _make_ctx()
    await cog._pick_season_then_episode(ctx, SERIES)
    assert _sent(ctx) == ["failed to fetch seasons: 500"]


async def test_pick_season_none_found():
    cog = _make_jellyfin_cog(_make_client())
    ctx = _make_ctx()
    await cog._pick_season_then_episode(ctx, SERIES)
    assert _sent(ctx) == ["no seasons found for **Breaking Bad**"]


async def test_pick_season_single_season_skips_prompt():
    season = {"Id": "s1", "Name": "Season 1"}
    cog = _make_jellyfin_cog(_make_client(get_seasons=AsyncMock(return_value=[season])))
    cog._pick_episode = AsyncMock()
    cog.bot.wait_for = AsyncMock()
    ctx = _make_ctx()
    await cog._pick_season_then_episode(ctx, SERIES)
    cog._pick_episode.assert_awaited_once_with(ctx, "ser1", "Breaking Bad", season)
    cog.bot.wait_for.assert_not_awaited()


async def test_pick_season_multiple_prompts_then_picks():
    seasons = [{"Id": "s1", "Name": "Season 1"}, {"Id": "s2"}]
    cog = _make_jellyfin_cog(_make_client(get_seasons=AsyncMock(return_value=seasons)))
    cog._pick_episode = AsyncMock()
    cog.bot.wait_for = AsyncMock(return_value=_reply("2"))
    ctx = _make_ctx()
    await cog._pick_season_then_episode(ctx, SERIES)
    listing = _sent(ctx)[0]
    assert "**Breaking Bad** — 2 seasons" in listing
    assert "`1` Season 1" in listing
    assert "`2` Season 2" in listing  # falls back to "Season {i}" when Name missing
    cog._pick_episode.assert_awaited_once_with(ctx, "ser1", "Breaking Bad", seasons[1])


async def test_pick_season_multiple_cancelled():
    seasons = [{"Id": "s1"}, {"Id": "s2"}]
    cog = _make_jellyfin_cog(_make_client(get_seasons=AsyncMock(return_value=seasons)))
    cog._pick_episode = AsyncMock()
    cog.bot.wait_for = AsyncMock(return_value=_reply("!media other"))
    ctx = _make_ctx()
    await cog._pick_season_then_episode(ctx, SERIES)
    assert _sent(ctx)[-1] == "cancelled"
    cog._pick_episode.assert_not_awaited()


async def test_pick_episode_fetch_error():
    cog = _make_jellyfin_cog(_make_client(get_episodes=AsyncMock(side_effect=RuntimeError("x"))))
    ctx = _make_ctx()
    await cog._pick_episode(ctx, "ser1", "Breaking Bad", {"Id": "s1", "Name": "Season 1"})
    assert _sent(ctx) == ["failed to fetch episodes: x"]


async def test_pick_episode_none_found():
    cog = _make_jellyfin_cog(_make_client())
    ctx = _make_ctx()
    await cog._pick_episode(ctx, "ser1", "Breaking Bad", {"Id": "s1", "Name": "Season 1"})
    assert _sent(ctx) == ["no episodes found for **Season 1** of **Breaking Bad**"]


async def test_pick_episode_lists_and_plays_choice():
    episodes = [
        {"Id": "e1", "Name": "Pilot", "IndexNumber": 1},
        {"Id": "e2", "Name": "Cat's in the Bag"},  # no IndexNumber
    ]
    client = _make_client(get_episodes=AsyncMock(return_value=episodes))
    cog = _make_jellyfin_cog(client)
    cog._play_item = AsyncMock()
    cog.bot.wait_for = AsyncMock(return_value=_reply("2"))
    ctx = _make_ctx()
    await cog._pick_episode(ctx, "ser1", "Breaking Bad", {"Id": "s1", "Name": "Season 1"})
    client.get_episodes.assert_awaited_once_with("ser1", "s1")
    listing = _sent(ctx)[0]
    assert "**Breaking Bad — Season 1**" in listing
    assert "`1` E01 — Pilot" in listing
    assert "`2` Cat's in the Bag" in listing
    cog._play_item.assert_awaited_once_with(ctx, episodes[1])


async def test_pick_episode_timeout_does_not_play():
    client = _make_client(get_episodes=AsyncMock(return_value=[{"Id": "e1"}]))
    cog = _make_jellyfin_cog(client)
    cog._play_item = AsyncMock()
    cog.bot.wait_for = AsyncMock(side_effect=TimeoutError)
    ctx = _make_ctx()
    await cog._pick_episode(ctx, "ser1", "BB", {"Id": "s1"})
    cog._play_item.assert_not_awaited()


async def test_media_series_end_to_end_to_play_item():
    """Single series → multiple seasons → episode pick → _play_item with the episode."""
    seasons = [{"Id": "s1", "Name": "Season 1"}, {"Id": "s2", "Name": "Season 2"}]
    episodes = [{"Id": "e1", "Name": "A", "IndexNumber": 1}, {"Id": "e2", "Name": "B", "IndexNumber": 2}]
    client = _make_client(
        search=AsyncMock(return_value=[SERIES]),
        get_seasons=AsyncMock(return_value=seasons),
        get_episodes=AsyncMock(return_value=episodes),
    )
    cog = _make_jellyfin_cog(client)
    cog._play_item = AsyncMock()
    cog.bot.wait_for = AsyncMock(side_effect=[_reply("2"), _reply("1")])
    ctx = _make_ctx()
    await Jellyfin.media.callback(cog, ctx, query="breaking bad")
    client.get_episodes.assert_awaited_once_with("ser1", "s2")
    cog._play_item.assert_awaited_once_with(ctx, episodes[0])


# ── _play_item ───────────────────────────────────────────────────────────────


@pytest.fixture
def play_env():
    guild, channel, vc = MagicMock(), MagicMock(), MagicMock()
    with patch.object(jf_mod, "resolve_voice", AsyncMock(return_value=(guild, channel, vc))) as rv, \
         patch.object(jf_mod, "_extract_hls_variant_url", AsyncMock(side_effect=lambda u: u)) as hls, \
         patch.object(jf_mod, "_probe_stream", AsyncMock(return_value=None)) as probe, \
         patch.object(jf_mod, "start_live_stream", AsyncMock()) as start:
        yield {
            "guild": guild, "channel": channel, "vc": vc,
            "resolve_voice": rv, "hls": hls, "probe": probe, "start": start,
        }


async def test_play_item_not_in_voice(play_env):
    play_env["resolve_voice"].return_value = (None, None, None)
    client = _make_client()
    cog = _make_jellyfin_cog(client)
    ctx = _make_ctx()
    await cog._play_item(ctx, MOVIE)
    assert _sent(ctx) == ["you're not in a voice channel"]
    client.start_playback.assert_not_awaited()
    play_env["start"].assert_not_awaited()


async def test_play_item_no_stream_url(play_env):
    client = _make_client()
    cog = _make_jellyfin_cog(client)
    ctx = _make_ctx()
    await cog._play_item(ctx, MOVIE)
    client.start_playback.assert_awaited_once_with("m1")
    sent = _sent(ctx)
    assert sent[0] == "checking stream…"
    assert "could not start a Jellyfin playback session for **Inception (2010)**" in sent[1]
    play_env["hls"].assert_not_awaited()
    play_env["start"].assert_not_awaited()


async def test_play_item_probe_fails(play_env):
    url = "http://jf/stream.m3u8?api_key=SECRETKEY"
    client = _make_client(get_stream_url=AsyncMock(return_value=url))
    cog = _make_jellyfin_cog(client)
    ctx = _make_ctx()
    await cog._play_item(ctx, MOVIE)
    play_env["probe"].assert_awaited_once_with(url)
    assert "could not probe the Jellyfin stream" in _sent(ctx)[-1]
    play_env["start"].assert_not_awaited()


async def test_play_item_unsupported_codec(play_env):
    client = _make_client(get_stream_url=AsyncMock(return_value="http://jf/x"))
    play_env["probe"].return_value = {"codec": "av1", "fps": 24.0}
    cog = _make_jellyfin_cog(client)
    ctx = _make_ctx()
    await cog._play_item(ctx, MOVIE)
    assert _sent(ctx)[-1] == "unsupported video codec `av1` from Jellyfin for **Inception (2010)**"
    play_env["start"].assert_not_awaited()


async def test_play_item_success_uses_resolved_variant(play_env, caplog):
    master = "http://jf/master.m3u8?api_key=SECRETKEY"
    variant = "http://jf/variant.m3u8?api_key=SECRETKEY"
    client = _make_client(get_stream_url=AsyncMock(return_value=master))
    play_env["hls"].side_effect = None
    play_env["hls"].return_value = variant
    play_env["probe"].return_value = {"codec": "h264", "fps": 23.976, "has_audio": True}
    cog = _make_jellyfin_cog(client)
    ctx = _make_ctx()

    import logging
    with caplog.at_level(logging.INFO, logger="cogs.jellyfin"):
        await cog._play_item(ctx, EPISODE)

    play_env["hls"].assert_awaited_once_with(master)
    play_env["probe"].assert_awaited_once_with(variant)
    play_env["start"].assert_awaited_once_with(
        cog.bot,
        ctx.send,
        play_env["guild"],
        play_env["channel"],
        play_env["vc"],
        title="Breaking Bad — S01E01 — Pilot",
        url=variant,
        live=True,
        audio=True,
        probe_size=10_000_000,
    )
    # The API key must never appear in logs.
    assert "SECRETKEY" not in caplog.text
    assert "***" in caplog.text


@pytest.mark.parametrize("codec", ["hevc", "mpeg2video", "mpeg4", "mjpeg"])
async def test_play_item_other_supported_codecs(play_env, codec):
    client = _make_client(get_stream_url=AsyncMock(return_value="http://jf/x"))
    play_env["probe"].return_value = {"codec": codec, "fps": 25.0}
    cog = _make_jellyfin_cog(client)
    await cog._play_item(_make_ctx(), MOVIE)
    play_env["start"].assert_awaited_once()
    # has_audio missing from probe → defaults to True
    assert play_env["start"].call_args.kwargs["audio"] is True


async def test_play_item_no_audio_passes_audio_false(play_env):
    client = _make_client(get_stream_url=AsyncMock(return_value="http://jf/x"))
    play_env["probe"].return_value = {"codec": "h264", "fps": 30.0, "has_audio": False}
    cog = _make_jellyfin_cog(client)
    await cog._play_item(_make_ctx(), MOVIE)
    kwargs = play_env["start"].call_args.kwargs
    assert kwargs["audio"] is False
    assert kwargs["url"] == "http://jf/x"


# ── setup() ──────────────────────────────────────────────────────────────────


async def test_setup_unconfigured(monkeypatch):
    monkeypatch.delenv("JELLYFIN_URL", raising=False)
    monkeypatch.setenv("JELLYFIN_API_KEY", "  ")
    bot = MagicMock()
    bot.add_cog = AsyncMock()
    await jf_mod.setup(bot)
    cog = bot.add_cog.call_args[0][0]
    assert isinstance(cog, Jellyfin)
    assert cog.client is None


async def test_setup_configured(monkeypatch):
    monkeypatch.setenv("JELLYFIN_URL", " http://jf.local:8096 ")
    monkeypatch.setenv("JELLYFIN_API_KEY", "abc")
    bot = MagicMock()
    bot.add_cog = AsyncMock()
    await jf_mod.setup(bot)
    cog = bot.add_cog.call_args[0][0]
    assert isinstance(cog.client, JellyfinClient)
    assert cog.client._api_key == "abc"


# ── PlaybackInfo metadata (skips ffprobe) ────────────────────────────────────


async def test_play_item_uses_playback_info_metadata_and_skips_probe(play_env):
    stream = PlaybackStream("http://jf/x", "sess", has_audio=False, video_codec="h264")
    client = _make_client(start_playback=AsyncMock(return_value=stream))
    cog = _make_jellyfin_cog(client)
    await cog._play_item(_make_ctx(), MOVIE)
    play_env["probe"].assert_not_awaited()
    assert play_env["start"].call_args.kwargs["audio"] is False


async def test_play_item_metadata_unsupported_codec(play_env):
    stream = PlaybackStream("http://jf/x", "sess", has_audio=True, video_codec="av1")
    client = _make_client(start_playback=AsyncMock(return_value=stream))
    cog = _make_jellyfin_cog(client)
    ctx = _make_ctx()
    await cog._play_item(ctx, MOVIE)
    assert "unsupported video codec `av1`" in _sent(ctx)[-1]
    play_env["start"].assert_not_awaited()


@pytest.mark.parametrize(
    "source, expected",
    [
        ({"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [{"Type": "Video"}, {"Type": "Audio"}]}, (True, "h264")),
        ({"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [{"Type": "Video", "Codec": "hevc"}]}, (False, "h264")),
        ({"DirectStreamUrl": "/d?a=1", "MediaStreams": [{"Type": "Video", "Codec": "HEVC"}, {"Type": "Audio"}]}, (True, "hevc")),
        ({"DirectStreamUrl": "/d?a=1", "MediaStreams": [{"Type": "Audio"}]}, (True, None)),
        ({"TranscodingUrl": "/t.m3u8?a=1"}, (None, None)),
        ({"TranscodingUrl": "/t.m3u8?a=1", "MediaStreams": [{"Type": "Audio"}]}, (True, None)),
    ],
)
async def test_start_playback_reports_stream_metadata(client, source, expected):
    client._user_id = "u"
    resp = _mock_response({"MediaSources": [source], "PlaySessionId": "ps-9"})
    with patch("urllib.request.urlopen", return_value=resp):
        stream = await client.start_playback("item-1")
    assert (stream.has_audio, stream.video_codec) == expected
    assert stream.play_session_id == "ps-9"
