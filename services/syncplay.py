"""Jellyfin SyncPlay (group watch) client.

The bot joins Jellyfin's SyncPlay as a regular group member so people watching
in Jellyfin apps and the Discord go-live stay in step:

* ``open()`` signs in as the configured Jellyfin user under a per-guild device
  id (a Jellyfin session can only be in one group), opens the server websocket,
  creates a group and sets ``IgnoreWait`` so the group never stalls waiting for
  the bot to "buffer".
* ``set_queue()`` / ``pause()`` / ``unpause()`` / ``seek()`` forward the bot's own
  ``!media`` commands to the group.
* Commands from other members (``SyncPlayCommand``: Pause / Unpause / Seek /
  Stop) and play-queue changes (``SyncPlayGroupUpdate`` → ``PlayQueue``) are
  delivered to the ``on_command`` / ``on_play_queue`` callbacks.

Protocol reference: jellyfin-web ``src/plugins/syncPlay`` and
jellyfin-apiclient ``requestSyncPlay*``.
"""

from __future__ import annotations

import asyncio
import json
import logging
from collections.abc import Awaitable, Callable
from typing import Any

import aiohttp

from services.jellyfin import JellyfinClient, auth_header

log = logging.getLogger(__name__)

TICKS_PER_SECOND = 10_000_000

# (command, position_seconds | None)
CommandHandler = Callable[[str, float | None], Awaitable[None]]
# (item_id | None, start_position_seconds, is_playing)
QueueHandler = Callable[[str | None, float, bool], Awaitable[None]]


def seconds_to_ticks(seconds: float) -> int:
    return int(round(seconds * TICKS_PER_SECOND))


def ticks_to_seconds(ticks: Any) -> float | None:
    try:
        return int(ticks) / TICKS_PER_SECOND
    except (TypeError, ValueError):
        return None


class SyncPlayGroup:
    def __init__(
        self,
        client: JellyfinClient,
        device_id: str,
        on_command: CommandHandler,
        on_play_queue: QueueHandler,
    ) -> None:
        self._client = client
        self._device_id = device_id
        self._on_command = on_command
        self._on_play_queue = on_play_queue
        self._session: aiohttp.ClientSession | None = None
        self._ws: aiohttp.ClientWebSocketResponse | None = None
        self._reader: asyncio.Task | None = None
        self._keepalive: asyncio.Task | None = None
        self.group_id: str | None = None
        self.group_name: str | None = None
        self.closed = False

    # ── Lifecycle ─────────────────────────────────────────────────────────────

    async def open(self, group_name: str) -> None:
        token, _user_id = await self._client.authenticate(self._device_id)
        self._session = aiohttp.ClientSession(
            headers={
                "Authorization": auth_header(token, self._device_id),
                "Content-Type": "application/json",
            }
        )
        base = self._client.base_url
        ws_url = (
            base.replace("https://", "wss://", 1).replace("http://", "ws://", 1)
            + f"/socket?api_key={token}&deviceId={self._device_id}"
        )
        self._ws = await self._session.ws_connect(ws_url, heartbeat=30)
        self._reader = asyncio.create_task(self._read_loop())
        await self._post("/SyncPlay/New", {"GroupName": group_name})
        await self._post("/SyncPlay/SetIgnoreWait", {"IgnoreWait": True})
        self.group_name = group_name
        log.info("SyncPlay: created group %r", group_name)

    async def close(self) -> None:
        if self.closed:
            return
        self.closed = True
        try:
            await self._post("/SyncPlay/Leave")
        except Exception:
            log.debug("SyncPlay: leave failed", exc_info=True)
        for task in (self._keepalive, self._reader):
            if task is not None and not task.done():
                task.cancel()
        if self._ws is not None:
            try:
                await self._ws.close()
            except Exception:
                pass
        if self._session is not None:
            await self._session.close()
        log.info("SyncPlay: left group %r", self.group_name)

    # ── Requests (bot → group) ────────────────────────────────────────────────

    async def set_queue(self, item_id: str, start_seconds: float = 0.0) -> None:
        await self._post(
            "/SyncPlay/SetNewQueue",
            {
                "PlayingQueue": [item_id],
                "PlayingItemPosition": 0,
                "StartPositionTicks": seconds_to_ticks(start_seconds),
            },
        )

    async def pause(self) -> None:
        await self._post("/SyncPlay/Pause")

    async def unpause(self) -> None:
        await self._post("/SyncPlay/Unpause")

    async def seek(self, seconds: float) -> None:
        await self._post("/SyncPlay/Seek", {"PositionTicks": seconds_to_ticks(seconds)})

    async def _post(self, path: str, body: dict | None = None) -> None:
        if self._session is None:
            raise RuntimeError("SyncPlay group is not open")
        async with self._session.post(
            self._client.base_url + path, data=json.dumps(body or {})
        ) as resp:
            if resp.status >= 400:
                text = await resp.text()
                raise RuntimeError(f"SyncPlay {path} → HTTP {resp.status}: {text[:200]}")

    # ── Websocket (group → bot) ───────────────────────────────────────────────

    async def _read_loop(self) -> None:
        assert self._ws is not None
        try:
            async for msg in self._ws:
                if msg.type != aiohttp.WSMsgType.TEXT:
                    if msg.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                        break
                    continue
                try:
                    await self.handle_message(json.loads(msg.data))
                except Exception:
                    log.exception("SyncPlay: error handling message")
        except asyncio.CancelledError:
            raise
        except Exception:
            log.warning("SyncPlay: websocket reader ended", exc_info=True)
        if not self.closed:
            # Connection lost (server restart, network blip): mark the group
            # dead so the next play opens a fresh one instead of reusing it.
            log.warning("SyncPlay: websocket closed for group %r", self.group_name)
            self.closed = True
            if self._keepalive is not None and not self._keepalive.done():
                self._keepalive.cancel()
            if self._session is not None:
                try:
                    await self._session.close()
                except Exception:
                    pass

    async def handle_message(self, msg: dict) -> None:
        kind = msg.get("MessageType")
        data = msg.get("Data")

        if kind == "ForceKeepAlive":
            # Server asks for a KeepAlive at least every `Data` seconds.
            interval = max(5.0, float(data or 60) / 2)
            if self._keepalive is None or self._keepalive.done():
                self._keepalive = asyncio.create_task(self._keepalive_loop(interval))
        elif kind == "SyncPlayCommand" and isinstance(data, dict):
            command = data.get("Command")
            if command:
                await self._on_command(command, ticks_to_seconds(data.get("PositionTicks")))
        elif kind == "SyncPlayGroupUpdate" and isinstance(data, dict):
            await self._handle_group_update(data)

    async def _handle_group_update(self, update: dict) -> None:
        kind = update.get("Type")
        payload = update.get("Data")
        if kind == "GroupJoined" and isinstance(payload, dict):
            self.group_id = payload.get("GroupId")
        elif kind == "PlayQueue" and isinstance(payload, dict):
            playlist = payload.get("Playlist") or []
            index = payload.get("PlayingItemIndex", 0)
            item_id = None
            if isinstance(index, int) and 0 <= index < len(playlist):
                item_id = playlist[index].get("ItemId")
            start = ticks_to_seconds(payload.get("StartPositionTicks")) or 0.0
            await self._on_play_queue(item_id, start, bool(payload.get("IsPlaying")))
        elif kind in ("GroupLeft", "NotInGroup", "GroupDoesNotExist"):
            log.info("SyncPlay: %s for group %r", kind, self.group_name)
        elif kind in ("CreateGroupDenied", "JoinGroupDenied", "LibraryAccessDenied"):
            log.warning(
                "SyncPlay: %s — check the Jellyfin user's SyncPlay permission", kind
            )

    async def _keepalive_loop(self, interval: float) -> None:
        while self._ws is not None and not self._ws.closed:
            await asyncio.sleep(interval)
            try:
                await self._ws.send_str(json.dumps({"MessageType": "KeepAlive"}))
            except Exception:
                log.debug("SyncPlay: keepalive failed", exc_info=True)
                return
