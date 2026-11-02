"""Tests for cogs/voice.py — on_voice_state_update auto-leave logic."""
from __future__ import annotations

from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from cogs.voice import Voice


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

BOT_USER_ID = 999


def _make_cog():
    bot = MagicMock()
    bot.user.id = BOT_USER_ID
    cog = Voice(bot)
    return cog


def _make_member(guild, user_id: int = 1):
    member = MagicMock()
    member.guild = guild
    member.id = user_id
    member.bot = False  # selfbot accounts always have bot=False
    return member


def _make_voice_state(channel=MagicMock()):
    vs = MagicMock()
    vs.channel = channel
    return vs


def _make_guild(vc_channel, members_in_channel: list, bot_id: int = BOT_USER_ID):
    """Build a guild whose voice client sits in vc_channel with given members."""
    vc = MagicMock()
    vc.channel = vc_channel
    vc.disconnect = AsyncMock()

    for m in members_in_channel:
        m.id = getattr(m, "id", 1)

    vc_channel.members = members_in_channel
    guild = MagicMock()
    guild.id = 42
    guild.voice_client = vc
    return guild


# ---------------------------------------------------------------------------
# Auto-leave triggered
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_leaves_when_last_human_disconnects():
    """Bot disconnects when the only human leaves its channel."""
    cog = _make_cog()
    channel = MagicMock()

    # Only the bot itself remains after the human leaves.
    bot_member = MagicMock()
    bot_member.id = BOT_USER_ID

    guild = _make_guild(channel, members_in_channel=[bot_member])
    member = _make_member(guild)

    before = _make_voice_state(channel=channel)
    after = _make_voice_state(channel=None)  # left entirely

    live_conn = AsyncMock()
    cog.bot.live_connections = {guild.id: live_conn}

    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await cog.on_voice_state_update(member, before, after)

    mock_cancel.assert_called_once_with(cog.bot, guild.id)
    live_conn.disconnect.assert_awaited_once()
    assert guild.id not in cog.bot.live_connections
    guild.voice_client.disconnect.assert_awaited_once_with(force=False)


@pytest.mark.asyncio
async def test_auto_leave_no_golive_connection():
    """Auto-leave works cleanly when no go-live stream is active."""
    cog = _make_cog()
    channel = MagicMock()

    bot_member = MagicMock()
    bot_member.id = BOT_USER_ID
    guild = _make_guild(channel, members_in_channel=[bot_member])
    member = _make_member(guild)
    cog.bot.live_connections = {}  # no go-live active

    before = _make_voice_state(channel=channel)
    after = _make_voice_state(channel=None)

    with patch("cogs.voice.cancel_stream"):
        await cog.on_voice_state_update(member, before, after)

    guild.voice_client.disconnect.assert_awaited_once_with(force=False)


@pytest.mark.asyncio
async def test_stays_when_other_humans_remain():
    """Bot stays if at least one non-bot member is still in the channel."""
    cog = _make_cog()
    channel = MagicMock()

    human1 = MagicMock()
    human1.id = 100
    human2 = MagicMock()
    human2.id = 200

    guild = _make_guild(channel, members_in_channel=[human1, human2])
    member = _make_member(guild, user_id=300)  # a third human leaving

    before = _make_voice_state(channel=channel)
    after = _make_voice_state(channel=None)

    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await cog.on_voice_state_update(member, before, after)

    mock_cancel.assert_not_called()
    guild.voice_client.disconnect.assert_not_awaited()


# ---------------------------------------------------------------------------
# Auto-leave NOT triggered
# ---------------------------------------------------------------------------


@pytest.mark.asyncio
async def test_ignores_mute_deafen_events():
    """Voice state changes that keep the member in the same channel are ignored."""
    cog = _make_cog()
    channel = MagicMock()
    guild = _make_guild(channel, members_in_channel=[])
    member = _make_member(guild)

    # before and after both point to the same channel (mute/deafen event)
    before = _make_voice_state(channel=channel)
    after = _make_voice_state(channel=channel)

    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await cog.on_voice_state_update(member, before, after)

    mock_cancel.assert_not_called()
    guild.voice_client.disconnect.assert_not_awaited()


@pytest.mark.asyncio
async def test_ignores_join_events():
    """Member joining (before.channel is None) should not trigger auto-leave."""
    cog = _make_cog()
    channel = MagicMock()
    guild = _make_guild(channel, members_in_channel=[])
    member = _make_member(guild)

    before = _make_voice_state(channel=None)  # was not in any channel
    after = _make_voice_state(channel=channel)  # joined

    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await cog.on_voice_state_update(member, before, after)

    mock_cancel.assert_not_called()


@pytest.mark.asyncio
async def test_ignores_leave_from_different_channel():
    """Member leaving a channel the bot is NOT in should be ignored."""
    cog = _make_cog()
    bot_channel = MagicMock()
    other_channel = MagicMock()

    guild = _make_guild(bot_channel, members_in_channel=[])
    member = _make_member(guild)

    before = _make_voice_state(channel=other_channel)  # left a different channel
    after = _make_voice_state(channel=None)

    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await cog.on_voice_state_update(member, before, after)

    mock_cancel.assert_not_called()
    guild.voice_client.disconnect.assert_not_awaited()


@pytest.mark.asyncio
async def test_ignores_when_bot_not_in_voice():
    """If the bot has no active voice client, do nothing."""
    cog = _make_cog()
    channel = MagicMock()

    guild = MagicMock()
    guild.voice_client = None  # bot not in voice
    member = _make_member(guild)

    before = _make_voice_state(channel=channel)
    after = _make_voice_state(channel=None)

    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await cog.on_voice_state_update(member, before, after)

    mock_cancel.assert_not_called()


@pytest.mark.asyncio
async def test_bot_id_used_not_bot_flag():
    """Uses member ID to identify the bot, not member.bot, because selfbot accounts have bot=False."""
    cog = _make_cog()
    channel = MagicMock()

    # The remaining member IS the bot, but bot=False (selfbot account)
    bot_member = MagicMock()
    bot_member.id = BOT_USER_ID
    bot_member.bot = False  # selfbot — this flag cannot be trusted

    guild = _make_guild(channel, members_in_channel=[bot_member])
    member = _make_member(guild, user_id=100)

    before = _make_voice_state(channel=channel)
    after = _make_voice_state(channel=None)

    with patch("cogs.voice.cancel_stream"):
        await cog.on_voice_state_update(member, before, after)

    # Should disconnect because the only remaining member IS the bot (by ID)
    guild.voice_client.disconnect.assert_awaited_once_with(force=False)


@pytest.mark.asyncio
async def test_member_moves_to_another_channel_no_leave():
    """Member moving between channels (before and after both non-None, different) is a leave from before.channel."""
    cog = _make_cog()
    bot_channel = MagicMock()
    other_channel = MagicMock()

    # Bot is alone in bot_channel after the human moves away
    bot_member = MagicMock()
    bot_member.id = BOT_USER_ID
    guild = _make_guild(bot_channel, members_in_channel=[bot_member])
    member = _make_member(guild)

    before = _make_voice_state(channel=bot_channel)
    after = _make_voice_state(channel=other_channel)  # moved, not fully left

    with patch("cogs.voice.cancel_stream"):
        await cog.on_voice_state_update(member, before, after)

    # before.channel != after.channel, so it IS treated as a leave from bot_channel
    guild.voice_client.disconnect.assert_awaited_once_with(force=False)


@pytest.mark.asyncio
async def test_auto_leave_golive_disconnect_error_still_leaves():
    """A failing go-live disconnect is logged and the voice client still disconnects."""
    cog = _make_cog()
    channel = MagicMock()
    bot_member = MagicMock()
    bot_member.id = BOT_USER_ID
    guild = _make_guild(channel, members_in_channel=[bot_member])
    member = _make_member(guild)
    live_conn = MagicMock()
    live_conn.disconnect = AsyncMock(side_effect=RuntimeError("ws closed"))
    cog.bot.live_connections = {guild.id: live_conn}

    with patch("cogs.voice.cancel_stream"):
        await cog.on_voice_state_update(
            member, _make_voice_state(channel=channel), _make_voice_state(channel=None)
        )

    assert guild.id not in cog.bot.live_connections
    guild.voice_client.disconnect.assert_awaited_once_with(force=False)


# ---------------------------------------------------------------------------
# Commands: !join / !leave / !stop
# ---------------------------------------------------------------------------


def _make_ctx(guild=None, bot_guilds=()):
    ctx = MagicMock()
    ctx.guild = guild
    ctx.bot.guilds = list(bot_guilds)
    ctx.send = AsyncMock()
    return ctx


@pytest.mark.asyncio
async def test_join_not_in_voice():
    cog = _make_cog()
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(MagicMock(), None, None))):
        await Voice.join.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("you're not in a voice channel")


@pytest.mark.asyncio
async def test_join_connects_when_not_connected():
    cog = _make_cog()
    ctx = _make_ctx()
    channel = MagicMock()
    channel.connect = AsyncMock()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(MagicMock(), channel, None))):
        await Voice.join.callback(cog, ctx)
    channel.connect.assert_awaited_once_with(self_deaf=True)
    ctx.send.assert_awaited_once_with("joined!")


@pytest.mark.asyncio
async def test_join_moves_when_already_connected():
    cog = _make_cog()
    ctx = _make_ctx()
    channel = MagicMock()
    channel.connect = AsyncMock()
    vc = MagicMock()
    vc.move_to = AsyncMock()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(MagicMock(), channel, vc))):
        await Voice.join.callback(cog, ctx)
    vc.move_to.assert_awaited_once_with(channel)
    channel.connect.assert_not_awaited()
    ctx.send.assert_awaited_once_with("joined!")


@pytest.mark.asyncio
async def test_leave_dm_no_guild_with_voice():
    cog = _make_cog()
    g = MagicMock()
    g.voice_client = None
    ctx = _make_ctx(guild=None, bot_guilds=[g])
    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.leave.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("not in a voice channel")
    mock_cancel.assert_not_called()


@pytest.mark.asyncio
async def test_leave_guild_without_voice_client():
    cog = _make_cog()
    guild = MagicMock()
    guild.voice_client = None
    ctx = _make_ctx(guild=guild)
    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.leave.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("not in a voice channel")
    mock_cancel.assert_not_called()


@pytest.mark.asyncio
async def test_leave_in_guild_disconnects():
    cog = _make_cog()
    guild = MagicMock()
    guild.id = 42
    guild.voice_client.disconnect = AsyncMock()
    ctx = _make_ctx(guild=guild)
    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.leave.callback(cog, ctx)
    mock_cancel.assert_called_once_with(cog.bot, 42)
    guild.voice_client.disconnect.assert_awaited_once_with(force=False)
    ctx.send.assert_awaited_once_with("left!")


@pytest.mark.asyncio
async def test_leave_from_dm_finds_connected_guild():
    cog = _make_cog()
    idle = MagicMock()
    idle.voice_client = None
    active = MagicMock()
    active.id = 7
    active.voice_client.disconnect = AsyncMock()
    ctx = _make_ctx(guild=None, bot_guilds=[idle, active])
    with patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.leave.callback(cog, ctx)
    mock_cancel.assert_called_once_with(cog.bot, 7)
    active.voice_client.disconnect.assert_awaited_once_with(force=False)
    ctx.send.assert_awaited_once_with("left!")


@pytest.mark.asyncio
async def test_stop_no_voice_client():
    cog = _make_cog()
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(MagicMock(), None, None))), \
         patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.stop.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("nothing is playing")
    mock_cancel.assert_not_called()


@pytest.mark.asyncio
async def test_stop_no_guild():
    cog = _make_cog()
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(None, None, MagicMock()))):
        await Voice.stop.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("nothing is playing")


@pytest.mark.asyncio
async def test_stop_idle_voice_client():
    cog = _make_cog()
    cog.bot.stream_tasks = {}
    guild = MagicMock()
    guild.id = 42
    vc = MagicMock()
    vc.is_playing.return_value = False
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(guild, None, vc))), \
         patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.stop.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("nothing is playing")
    mock_cancel.assert_not_called()


@pytest.mark.asyncio
async def test_stop_playing_stops_and_disconnects_golive():
    cog = _make_cog()
    guild = MagicMock()
    guild.id = 42
    vc = MagicMock()
    vc.is_playing.return_value = True
    live_conn = MagicMock()
    live_conn.disconnect = AsyncMock()
    cog.bot.stream_tasks = {}
    cog.bot.live_connections = {42: live_conn}
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(guild, None, vc))), \
         patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.stop.callback(cog, ctx)
    mock_cancel.assert_called_once_with(cog.bot, 42)
    vc.stop.assert_called_once()
    live_conn.disconnect.assert_awaited_once()
    assert 42 not in cog.bot.live_connections
    ctx.send.assert_awaited_once_with("stopped")


@pytest.mark.asyncio
async def test_stop_with_task_but_not_playing():
    """A pending stream task counts as 'playing' even if vc.is_playing() is False."""
    cog = _make_cog()
    guild = MagicMock()
    guild.id = 42
    vc = MagicMock()
    vc.is_playing.return_value = False
    cog.bot.stream_tasks = {42: MagicMock()}
    cog.bot.live_connections = {}
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(guild, None, vc))), \
         patch("cogs.voice.cancel_stream") as mock_cancel:
        await Voice.stop.callback(cog, ctx)
    mock_cancel.assert_called_once_with(cog.bot, 42)
    vc.stop.assert_not_called()
    ctx.send.assert_awaited_once_with("stopped")


@pytest.mark.asyncio
async def test_stop_golive_disconnect_error_still_reports_stopped():
    cog = _make_cog()
    guild = MagicMock()
    guild.id = 42
    vc = MagicMock()
    vc.is_playing.return_value = True
    live_conn = MagicMock()
    live_conn.disconnect = AsyncMock(side_effect=OSError("gone"))
    cog.bot.stream_tasks = {}
    cog.bot.live_connections = {42: live_conn}
    ctx = _make_ctx()
    with patch("cogs.voice.resolve_voice", AsyncMock(return_value=(guild, None, vc))), \
         patch("cogs.voice.cancel_stream"):
        await Voice.stop.callback(cog, ctx)
    ctx.send.assert_awaited_once_with("stopped")


@pytest.mark.asyncio
async def test_voice_setup_adds_cog():
    from cogs.voice import setup

    bot = MagicMock()
    bot.add_cog = AsyncMock()
    await setup(bot)
    assert isinstance(bot.add_cog.call_args[0][0], Voice)


@pytest.mark.asyncio
async def test_leave_stops_stream_and_golive_before_disconnecting():
    """!leave must end the screenshare too, not just drop the voice channel."""
    cog = _make_cog()
    order: list[str] = []
    live_conn = MagicMock()
    live_conn.disconnect = AsyncMock(side_effect=lambda: order.append("golive"))
    cog.bot.live_connections = {42: live_conn}
    guild = MagicMock()
    guild.id = 42
    guild.voice_client.is_playing.return_value = True
    guild.voice_client.stop.side_effect = lambda: order.append("vc.stop")
    guild.voice_client.disconnect = AsyncMock(
        side_effect=lambda **k: order.append("voice")
    )
    ctx = _make_ctx(guild=guild)
    with patch(
        "cogs.voice.cancel_stream", side_effect=lambda *a: order.append("cancel")
    ):
        await Voice.leave.callback(cog, ctx)
    assert order == ["cancel", "vc.stop", "golive", "voice"]
    assert cog.bot.live_connections == {}
    ctx.send.assert_awaited_once_with("left!")


@pytest.mark.asyncio
async def test_leave_golive_disconnect_error_still_leaves():
    cog = _make_cog()
    live_conn = MagicMock()
    live_conn.disconnect = AsyncMock(side_effect=RuntimeError("gateway gone"))
    cog.bot.live_connections = {42: live_conn}
    guild = MagicMock()
    guild.id = 42
    guild.voice_client.is_playing.return_value = False
    guild.voice_client.disconnect = AsyncMock()
    ctx = _make_ctx(guild=guild)
    with patch("cogs.voice.cancel_stream"):
        await Voice.leave.callback(cog, ctx)
    guild.voice_client.disconnect.assert_awaited_once_with(force=False)
    ctx.send.assert_awaited_once_with("left!")
