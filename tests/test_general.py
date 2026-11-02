"""Tests for cogs/general.py — ping, hello, and the role/cog-aware help text."""
from __future__ import annotations

from unittest.mock import AsyncMock, MagicMock

import discord
import pytest

from cogs.general import General, setup


def _make_bot(cogs: set[str] = frozenset(), admin_ids: set[int] = frozenset()):
    bot = MagicMock()
    bot.user.id = 999
    bot.allowed_ids = set(admin_ids)
    bot.relationships = []
    bot.guilds = []
    bot.get_cog.side_effect = lambda name: MagicMock() if name in cogs else None
    return bot


def _make_ctx(bot, author_id: int = 1):
    ctx = MagicMock()
    ctx.bot = bot
    ctx.author.id = author_id
    ctx.send = AsyncMock()
    return ctx


async def _help_text(cogs=frozenset(), admin_ids=frozenset(), author_id=1) -> str:
    bot = _make_bot(cogs, admin_ids)
    cog = General(bot)
    ctx = _make_ctx(bot, author_id)
    await General.help_command.callback(cog, ctx)
    ctx.send.assert_awaited_once()
    return ctx.send.call_args[0][0]


async def test_ping():
    bot = _make_bot()
    cog = General(bot)
    ctx = _make_ctx(bot)
    await General.ping.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("pong!")


async def test_hello():
    bot = _make_bot()
    cog = General(bot)
    ctx = _make_ctx(bot)
    await General.hello.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("Hello, world!")


def test_help_registered_under_help_name():
    assert General.help_command.name == "help"


async def test_help_base_sections_only():
    text = await _help_text()
    assert "**General**" in text
    assert "**Voice**" in text
    assert "`!ping`" in text and "`!stop`" in text
    assert "**Streaming**" not in text
    assert "**Jellyfin**" not in text
    assert "**IPTV**" not in text


async def test_help_includes_tv_section():
    text = await _help_text(cogs={"TV"})
    assert "**Streaming**" in text
    assert "`!channels" in text and "`!play" in text and "`!search" in text


async def test_help_includes_jellyfin_section():
    text = await _help_text(cogs={"Jellyfin"})
    assert "**Jellyfin**" in text
    assert "`!media <title>`" in text
    assert "**Streaming**" not in text


async def test_help_iptv_hidden_for_non_admin():
    text = await _help_text(cogs={"IPTV"}, admin_ids={42}, author_id=1)
    assert "**IPTV**" not in text
    assert "add-source" not in text


async def test_help_iptv_shown_for_admin():
    text = await _help_text(cogs={"IPTV"}, admin_ids={42}, author_id=42)
    assert "**IPTV**" in text
    assert "`!add-source <name> <url>`" in text
    assert "`!delete-source`" in text


async def test_help_iptv_hidden_when_cog_missing_even_for_admin():
    text = await _help_text(cogs=set(), admin_ids={42}, author_id=42)
    assert "**IPTV**" not in text


async def test_help_all_sections_in_order():
    text = await _help_text(cogs={"TV", "Jellyfin", "IPTV"}, admin_ids={7}, author_id=7)
    order = [text.index(s) for s in ("**General**", "**Voice**", "**Streaming**", "**Jellyfin**", "**IPTV**")]
    assert order == sorted(order)


# ---------------------------------------------------------------------------
# Role checks attached by @require_role
# ---------------------------------------------------------------------------


def _friend_rel(uid: int):
    rel = MagicMock()
    rel.type = discord.RelationshipType.friend
    rel.user.id = uid
    return rel


@pytest.mark.parametrize(
    "command_name, role_setup, allowed",
    [
        ("ping", "viewer", True),
        ("ping", "none", False),
        ("hello", "admin", True),
        ("hello", "friend", False),
        ("hello", "viewer", False),
        ("help_command", "viewer", True),
        ("help_command", "none", False),
    ],
)
async def test_command_role_checks(command_name, role_setup, allowed):
    uid = 5
    bot = _make_bot()
    guild = MagicMock()
    guild.get_member.return_value = MagicMock() if role_setup == "viewer" else None
    bot.guilds = [guild]
    if role_setup == "admin":
        bot.allowed_ids = {uid}
    if role_setup == "friend":
        bot.relationships = [_friend_rel(uid)]
    ctx = _make_ctx(bot, uid)

    cmd = getattr(General, command_name)
    assert len(cmd.checks) == 1
    assert await cmd.checks[0](ctx) is allowed


async def test_setup_adds_cog():
    bot = MagicMock()
    bot.add_cog = AsyncMock()
    await setup(bot)
    bot.add_cog.assert_awaited_once()
    assert isinstance(bot.add_cog.call_args[0][0], General)

