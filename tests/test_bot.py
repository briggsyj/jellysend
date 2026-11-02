"""Tests for bot.py — allowed-id parsing, SlopSoil lifecycle hooks, and main().

Importing bot.py has side effects: it monkey-patches discord.voice_state /
discord.gateway (davey shim + H.264 video patches) and calls load_dotenv().
The ``bot_module`` fixture snapshots every attribute the import touches and
restores them afterwards so other test modules see pristine discord classes,
and stubs out load_dotenv so a developer's local .env cannot leak in.
"""
from __future__ import annotations

import asyncio
import importlib
import logging
import sys
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from discord.ext import commands

_MISSING = object()


def _patched_targets():
    import discord.gateway
    import discord.voice_state

    return [
        (discord.voice_state, "davey"),
        (discord.gateway, "davey"),
        (discord.voice_state.VoiceConnectionState, "reinit_dave_session"),
        (discord.gateway.DiscordVoiceWebSocket, "identify"),
        (discord.gateway.DiscordVoiceWebSocket, "select_protocol"),
        (discord.gateway.DiscordVoiceWebSocket, "client_connect"),
    ]


@pytest.fixture(scope="module")
def bot_module():
    targets = _patched_targets()
    saved = [(obj, name, obj.__dict__.get(name, _MISSING)) for obj, name in targets]
    already_loaded = "bot" in sys.modules
    try:
        with patch("dotenv.load_dotenv"):
            mod = importlib.import_module("bot")
            if already_loaded:
                mod = importlib.reload(mod)
        yield mod
    finally:
        for obj, name, value in saved:
            if value is _MISSING:
                if name in obj.__dict__:
                    delattr(obj, name)
            else:
                setattr(obj, name, value)


@pytest.fixture
def slopsoil(bot_module):
    return bot_module.SlopSoil({1, 2})


# ---------------------------------------------------------------------------
# Import-time patching
# ---------------------------------------------------------------------------


def test_import_installs_davey_shim(bot_module):
    import discord.gateway
    import discord.voice_state

    import davey_compat

    assert discord.voice_state.davey is davey_compat
    assert discord.gateway.davey is davey_compat


def test_import_patches_video_gateway(bot_module):
    import discord.gateway

    import video_compat

    ws = discord.gateway.DiscordVoiceWebSocket
    assert ws.identify is video_compat._patched_identify
    assert ws.select_protocol is video_compat._patched_select_protocol
    assert ws.client_connect is video_compat._patched_client_connect


# ---------------------------------------------------------------------------
# _load_allowed_ids
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "raw, expected",
    [
        ("", set()),
        ("123", {123}),
        ("1,2,3", {1, 2, 3}),
        (" 10 , 20 ,", {10, 20}),
        ("abc,42,-7,4.5,,  ", {42}),
        ("5,5,5", {5}),
    ],
)
def test_load_allowed_ids(bot_module, monkeypatch, raw, expected):
    monkeypatch.setenv("ALLOWED_USER_IDS", raw)
    assert bot_module._load_allowed_ids() == expected


def test_load_allowed_ids_unset(bot_module, monkeypatch):
    monkeypatch.delenv("ALLOWED_USER_IDS", raising=False)
    assert bot_module._load_allowed_ids() == set()


# ---------------------------------------------------------------------------
# SlopSoil construction
# ---------------------------------------------------------------------------


def test_init_state(slopsoil):
    assert slopsoil.allowed_ids == {1, 2}
    assert slopsoil.command_prefix == "!"
    assert slopsoil.help_command is None
    assert slopsoil.stream_tasks == {}
    assert slopsoil.video_players == {}
    assert slopsoil.live_connections == {}
    assert slopsoil.source_manager is None


# ---------------------------------------------------------------------------
# setup_hook
# ---------------------------------------------------------------------------


async def test_setup_hook_loads_all_extensions(slopsoil, monkeypatch):
    monkeypatch.delenv("JELLYFIN_URL", raising=False)
    slopsoil.load_extension = AsyncMock()

    await slopsoil.setup_hook()

    loaded = [c.args[0] for c in slopsoil.load_extension.await_args_list]
    assert loaded == [
        "cogs.general",
        "cogs.voice",
        "cogs.iptv",
        "cogs.tv",
        "cogs.jellyfin",
    ]


async def test_setup_hook_logs_jellyfin_url(slopsoil, monkeypatch, caplog):
    monkeypatch.setenv("JELLYFIN_URL", "http://jf.local")
    slopsoil.load_extension = AsyncMock()
    with caplog.at_level(logging.INFO, logger="bot"):
        await slopsoil.setup_hook()
    assert "http://jf.local" in caplog.text


async def test_setup_hook_warns_without_jellyfin(slopsoil, monkeypatch, caplog):
    monkeypatch.delenv("JELLYFIN_URL", raising=False)
    slopsoil.load_extension = AsyncMock()
    with caplog.at_level(logging.WARNING, logger="bot"):
        await slopsoil.setup_hook()
    assert "unconfigured" in caplog.text


async def _get_global_check(bot):
    bot.load_extension = AsyncMock()
    await bot.setup_hook()
    assert len(bot._checks) == 1
    return bot._checks[0]


def _ctx(author_id: int):
    ctx = MagicMock()
    ctx.author.id = author_id
    return ctx


async def test_global_check_allows_self(slopsoil, bot_module):
    check = await _get_global_check(slopsoil)
    with patch.object(type(slopsoil), "user", new=MagicMock(id=777)), patch.object(
        bot_module, "get_user_role"
    ) as role:
        assert await check(_ctx(777)) is True
        role.assert_not_called()


async def test_global_check_uses_role(slopsoil, bot_module):
    check = await _get_global_check(slopsoil)
    with patch.object(type(slopsoil), "user", new=MagicMock(id=777)), patch.object(
        bot_module, "get_user_role", return_value=bot_module.Role.VIEWER
    ) as role:
        assert await check(_ctx(5)) is True
        role.assert_called_once_with(slopsoil, 5)


async def test_global_check_rejects_unknown(slopsoil, bot_module):
    check = await _get_global_check(slopsoil)
    with patch.object(type(slopsoil), "user", new=None), patch.object(
        bot_module, "get_user_role", return_value=bot_module.Role.NONE
    ):
        assert await check(_ctx(5)) is False


# ---------------------------------------------------------------------------
# close()
# ---------------------------------------------------------------------------


async def test_close_cancels_tasks_and_stops_players(slopsoil):
    started = asyncio.Event()

    async def _forever():
        started.set()
        await asyncio.sleep(3600)

    running = asyncio.create_task(_forever())
    await started.wait()

    async def _noop():
        return None

    finished = asyncio.create_task(_noop())
    await finished

    slopsoil.stream_tasks = {1: running, 2: finished}
    vp1, vp2 = MagicMock(), MagicMock()
    slopsoil.video_players = {1: vp1, 2: vp2}

    with patch.object(commands.Bot, "close", new=AsyncMock()) as super_close:
        await slopsoil.close()

    assert running.cancelled()
    assert not finished.cancelled()
    vp1.stop.assert_called_once()
    vp2.stop.assert_called_once()
    super_close.assert_awaited_once()


async def test_close_with_nothing_active(slopsoil):
    with patch.object(commands.Bot, "close", new=AsyncMock()) as super_close:
        await slopsoil.close()
    super_close.assert_awaited_once()


async def test_close_tolerates_task_exceptions(slopsoil):
    async def _boom():
        raise RuntimeError("ffmpeg died")

    t = asyncio.create_task(_boom())
    await asyncio.sleep(0)
    slopsoil.stream_tasks = {1: t}
    with patch.object(commands.Bot, "close", new=AsyncMock()) as super_close:
        await slopsoil.close()  # must not raise
    super_close.assert_awaited_once()


# ---------------------------------------------------------------------------
# on_ready / on_command
# ---------------------------------------------------------------------------


async def test_on_ready_logs(slopsoil, caplog):
    user = MagicMock(id=123)
    user.__str__ = lambda s: "selfbot#0001"
    with patch.object(type(slopsoil), "user", new=user), patch.object(
        type(slopsoil), "guilds", new=[MagicMock(), MagicMock()]
    ), caplog.at_level(logging.INFO, logger="bot"):
        await slopsoil.on_ready()
    assert "selfbot#0001" in caplog.text
    assert "serving 2 guild(s)" in caplog.text


async def test_on_command_logs_dm(slopsoil, caplog):
    ctx = MagicMock()
    ctx.command = "ping"
    ctx.guild = None
    with caplog.at_level(logging.INFO, logger="bot"):
        await slopsoil.on_command(ctx)
    assert "command [ping]" in caplog.text
    assert "DM" in caplog.text


# ---------------------------------------------------------------------------
# on_command_error
# ---------------------------------------------------------------------------


async def test_on_command_error_check_failure_silent(slopsoil, caplog):
    with caplog.at_level(logging.DEBUG, logger="bot"):
        await slopsoil.on_command_error(MagicMock(), commands.CheckFailure("no"))
    assert not [r for r in caplog.records if r.levelno >= logging.WARNING]


async def test_on_command_error_check_failure_subclass_silent(slopsoil, caplog):
    with caplog.at_level(logging.DEBUG, logger="bot"):
        await slopsoil.on_command_error(MagicMock(), commands.NotOwner())
    assert not [r for r in caplog.records if r.levelno >= logging.WARNING]


async def test_on_command_error_not_found_silent(slopsoil, caplog):
    with caplog.at_level(logging.DEBUG, logger="bot"):
        await slopsoil.on_command_error(MagicMock(), commands.CommandNotFound("x"))
    assert caplog.records == []


async def test_on_command_error_other_logs_exception(slopsoil, caplog):
    err = commands.CommandInvokeError(ValueError("bad"))
    with caplog.at_level(logging.DEBUG, logger="bot"):
        await slopsoil.on_command_error(MagicMock(), err)
    errors = [r for r in caplog.records if r.levelno == logging.ERROR]
    assert len(errors) == 1
    assert "unhandled error" in errors[0].getMessage()
    assert errors[0].exc_info is not None


# ---------------------------------------------------------------------------
# main()
# ---------------------------------------------------------------------------


def test_main_without_token_exits(bot_module, monkeypatch):
    monkeypatch.delenv("DISCORD_TOKEN", raising=False)
    with pytest.raises(SystemExit, match="DISCORD_TOKEN"):
        bot_module.main()


def test_main_empty_token_exits(bot_module, monkeypatch):
    monkeypatch.setenv("DISCORD_TOKEN", "")
    with pytest.raises(SystemExit):
        bot_module.main()


def test_main_runs_bot_with_token(bot_module, monkeypatch):
    monkeypatch.setenv("DISCORD_TOKEN", "tok")
    monkeypatch.setenv("ALLOWED_USER_IDS", "11,22")
    fake = MagicMock()
    with patch.object(bot_module, "SlopSoil", return_value=fake) as cls:
        bot_module.main()
    cls.assert_called_once_with({11, 22})
    fake.run.assert_called_once_with("tok", log_handler=None)
