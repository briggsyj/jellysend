from __future__ import annotations

import logging
from typing import TYPE_CHECKING, cast

import discord
from discord.ext import commands

from cogs.utils import resolve_voice
from streaming.engine import cancel_stream
from permissions import Role, require_role

if TYPE_CHECKING:
    from bot import SlopSoil

log = logging.getLogger(__name__)


class Voice(commands.Cog):
    def __init__(self, bot: commands.Bot):
        self.bot = cast("SlopSoil", bot)

    @require_role(Role.FRIEND)
    @commands.command()
    async def join(self, ctx: commands.Context):
        guild, voice_channel, vc = await resolve_voice(ctx)

        if not voice_channel:
            log.debug(
                "join rejected: %s is not in a voice channel (guild: %s)",
                ctx.author,
                guild,
            )
            await ctx.send("you're not in a voice channel")
            return

        if vc:
            log.info("moving to voice channel '%s' in guild '%s'", voice_channel, guild)
            await vc.move_to(voice_channel)
        else:
            log.info(
                "connecting to voice channel '%s' in guild '%s'", voice_channel, guild
            )
            await voice_channel.connect(self_deaf=True)

        await ctx.send("joined!")

    @require_role(Role.FRIEND)
    @commands.command()
    async def leave(self, ctx: commands.Context):
        guild = ctx.guild
        if guild is None:
            for g in ctx.bot.guilds:
                if g.voice_client:
                    guild = g
                    break

        if guild is None:
            await ctx.send("not in a voice channel")
            return

        vc = guild.voice_client
        if not vc:
            log.debug("leave rejected: not in a voice channel (guild: %s)", guild)
            await ctx.send("not in a voice channel")
            return

        log.info("disconnecting from voice in guild '%s'", guild)
        # Stop the stream (incl. the go-live screenshare) before leaving voice.
        await self._stop_streams(guild, vc, "leave")
        await vc.disconnect(force=False)
        await ctx.send("left!")

    @require_role(Role.FRIEND)
    @commands.command()
    async def stop(self, ctx: commands.Context):
        guild, _, vc = await resolve_voice(ctx)

        if not guild or not vc:
            log.debug("stop rejected: nothing playing (guild: %s)", guild)
            await ctx.send("nothing is playing")
            return

        if not vc.is_playing() and guild.id not in self.bot.stream_tasks:
            log.debug("stop rejected: nothing playing (guild: %s)", guild)
            await ctx.send("nothing is playing")
            return

        log.info("stopping stream in guild '%s'", guild)
        await self._stop_streams(guild, vc, "stop")
        await ctx.send("stopped")

    async def _stop_streams(self, guild, vc, reason: str) -> None:
        """Stop everything streaming in ``guild``: the stream task/player, any
        plain voice audio, and the go-live (screenshare) connection.

        The go-live is disconnected explicitly: the task's finally block also
        does it, but the task may have already finished (e.g. the video player
        died early) leaving the GoLiveConnection — and the screenshare — open.
        """
        cancel_stream(self.bot, guild.id)
        if vc is not None and vc.is_playing():
            vc.stop()
        live_conn = self.bot.live_connections.pop(guild.id, None)
        if live_conn is not None:
            try:
                await live_conn.disconnect()
            except Exception:
                log.warning("error disconnecting go-live on %s", reason, exc_info=True)

    @commands.Cog.listener()
    async def on_voice_state_update(
        self,
        member: discord.Member,
        before: discord.VoiceState,
        after: discord.VoiceState,
    ) -> None:
        # Only act when someone leaves a channel (not mute/deafen/move-within).
        if before.channel is None or before.channel == after.channel:
            return

        vc = member.guild.voice_client
        if vc is None or vc.channel != before.channel:
            return

        # Stay if any members other than the bot itself are still present.
        # Can't use m.bot here — selfbot accounts have bot=False on their user object.
        bot_id = self.bot.user.id if self.bot.user else None
        if any(m.id != bot_id for m in vc.channel.members):
            return

        log.info(
            "all users left '%s' in guild '%s' — disconnecting",
            vc.channel,
            member.guild,
        )
        await self._stop_streams(member.guild, vc, "auto-leave")
        await vc.disconnect(force=False)


async def setup(bot: commands.Bot):
    await bot.add_cog(Voice(bot))
