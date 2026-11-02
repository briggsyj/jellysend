"""
golive.py — Discord go-live (screenshare) stream connection.

Protocol flow:
  1. Send op 18 (STREAM_CREATE) on the main gateway to request a go-live stream.
  2. Send op 22 (STREAM_SET_PAUSED, paused=False) to mark the stream as active.
  3. Receive STREAM_CREATE event → rtc_server_id (stream voice server).
  4. Receive STREAM_SERVER_UPDATE event → endpoint + token for the stream server.
  5. Open a separate voice WebSocket to the stream server and run the standard
     voice handshake (IDENTIFY → READY → IP discovery → SELECT_PROTOCOL →
     SESSION_DESCRIPTION).
  6. Send op 12 (VIDEO) on the stream WebSocket to announce video capability.
  7. Stream audio (via GoLiveAudioSender) and video (via H264VideoPlayer) through
     the stream connection's UDP socket — viewers watching the go-live see both.
  8. On teardown, send op 19 (STREAM_DELETE) on the main gateway.
"""

from __future__ import annotations

import asyncio
import fcntl
import io
import logging
import os
import socket
import struct
import threading
import time
from collections.abc import Callable
from typing import TYPE_CHECKING

import discord
import discord.opus as _opus
import nacl.secret
import nacl.utils
from discord.gateway import DiscordVoiceWebSocket
from discord.voice_state import SocketReader

import davey_compat

if TYPE_CHECKING:
    from bot import SlopSoil

log = logging.getLogger(__name__)


def _av_sync_ms() -> int:
    """A/V lip-sync offset in milliseconds (STREAM_AV_SYNC_MS, default 0).

    Audio and video are sent on independent threads with no shared clock, so a
    roughly constant offset can appear. Positive advances audio (discard that
    much backlogged initial audio) to fix "audio behind"; negative delays audio
    start to fix "audio ahead". Dial in by testing. Clamped to +/-5000 ms.
    """
    raw = os.environ.get("STREAM_AV_SYNC_MS", "").strip()
    if raw:
        try:
            return max(-5000, min(5000, int(raw)))
        except ValueError:
            log.warning("STREAM_AV_SYNC_MS=%r not an integer; using 0", raw)
    return 0

# Main gateway opcodes for go-live streaming (not present in discord.py-self)
_OP_STREAM_CREATE = 18
_OP_STREAM_DELETE = 19
_OP_STREAM_SET_PAUSED = 22


# ── GoLiveConnection ──────────────────────────────────────────────────────────


class GoLiveConnection:
    """
    Manages a separate Discord go-live (screenshare) voice connection.

    Acts as a VoiceConnectionState-compatible object so it can be used with
    DiscordVoiceWebSocket.from_connection_state().  The patched identify(),
    select_protocol(), and client_connect() methods from video_compat.py are
    automatically applied because they replace methods on DiscordVoiceWebSocket
    globally — so this connection gets video-capable handshake for free.

    Also exposes the send_packet / mode / secret_key interface that
    H264VideoPlayer expects from voice_client._connection.
    """

    def __init__(
        self,
        bot: SlopSoil,
        guild_id: int,
        channel_id: int,
        vc: discord.VoiceClient,
    ) -> None:
        self._bot = bot
        self.guild_id = guild_id
        self.channel_id = channel_id
        self._regular_vc = vc  # regular voice connection (for session_id, user)

        # Filled in by connect()
        self.server_id: int | None = None
        self._stream_key: str | None = None
        self.endpoint: str | None = None
        self.token: str | None = None

        # VoiceConnectionState-compatible voice handshake state
        self.ssrc: int = 0
        self.voice_port: int | None = None
        self.endpoint_ip: str | None = None
        self.ip: str | None = None
        self.port: int | None = None
        self.socket: socket.socket | None = None
        self.ws: DiscordVoiceWebSocket | None = None
        self.mode: str = ""
        self.secret_key: list[int] = []

        self.dave_session = None
        self.dave_protocol_version: int = 0
        self.dave_pending_transitions: dict = {}
        self.dave_downgraded: bool = False

        self._poll_task: asyncio.Task | None = None
        self._socket_reader: SocketReader | None = None

    # ── VoiceConnectionState-compatible properties ────────────────────────────
    # DiscordVoiceWebSocket.identify() (patched by video_compat) reads these.

    @property
    def session_id(self) -> str | None:
        return self._regular_vc.session_id

    @property
    def user(self):
        return self._regular_vc.user

    @property
    def voice_client(self) -> discord.VoiceClient:
        # DiscordVoiceWebSocket.from_connection_state() uses voice_client.loop
        # and voice_client._state.http to open the WebSocket.
        return self._regular_vc

    @property
    def supported_modes(self):
        return type(self._regular_vc).supported_modes

    @property
    def max_dave_protocol_version(self) -> int:
        return davey_compat.DAVE_PROTOCOL_VERSION

    @property
    def can_encrypt(self) -> bool:
        return (
            self.dave_protocol_version != 0
            and self.dave_session is not None
            and self.dave_session.ready
        )

    async def reinit_dave_session(self) -> None:
        if self.dave_protocol_version > 0:
            # channel_id for go-live DAVE group is server_id - 1
            dave_channel_id = self.server_id - 1  # type: ignore[operator]
            if self.dave_session is not None:
                self.dave_session.reinit(
                    self.dave_protocol_version, self.user.id, dave_channel_id
                )
            else:
                self.dave_session = davey_compat.DaveSession(  # type: ignore[assignment]
                    self.dave_protocol_version, self.user.id, dave_channel_id
                )
                # Give libdave access to channel members so MLS proposals from
                # users in the voice channel are recognized and not rejected.
                self.dave_session._voice_state = self  # type: ignore[attr-defined]
            if self.dave_session is not None:
                await self.ws.send_binary(  # type: ignore[union-attr]
                    DiscordVoiceWebSocket.MLS_KEY_PACKAGE,
                    self.dave_session.get_serialized_key_package(),
                )
        elif self.dave_session:
            self.dave_session.reset()
            self.dave_session.set_passthrough_mode(True, 10)

    async def _recover_from_invalid_commit(self, transition_id: int) -> None:
        await self.ws.send_as_json(  # type: ignore[union-attr]
            {
                "op": DiscordVoiceWebSocket.MLS_INVALID_COMMIT_WELCOME,
                "d": {"transition_id": transition_id},
            }
        )
        await self.reinit_dave_session()

    async def _execute_transition(self, transition_id: int) -> None:
        log.debug("GoLive: executing transition ID %d", transition_id)
        if transition_id not in self.dave_pending_transitions:
            log.warning(
                "GoLive: received execute transition but no pending"
                " transition for ID %d",
                transition_id,
            )
            return

        old_version = self.dave_protocol_version
        self.dave_protocol_version = self.dave_pending_transitions.pop(transition_id)

        if (
            old_version != self.dave_protocol_version
            and self.dave_protocol_version == 0
        ):
            self.dave_downgraded = True
            log.debug("GoLive: DAVE session downgraded")
        elif transition_id > 0 and self.dave_downgraded:
            self.dave_downgraded = False
            if self.dave_session:
                self.dave_session.set_passthrough_mode(True, 10)
            log.debug("GoLive: DAVE session upgraded")

    # ── Public API ────────────────────────────────────────────────────────────

    async def connect(self, timeout: float = 30.0) -> None:
        """
        Signal go-live to Discord, negotiate the stream voice connection, and
        start the background WebSocket heartbeat task.
        """
        user_id = self._regular_vc.user.id
        stream_key = f"guild:{self.guild_id}:{self.channel_id}:{user_id}"

        main_ws = self._bot.ws

        # Register gateway event futures BEFORE sending op 18 to avoid losing
        # events that arrive before we start listening.
        create_fut = main_ws.wait_for(
            "STREAM_CREATE",
            predicate=lambda d: d.get("stream_key", "") == stream_key,
        )
        server_fut = main_ws.wait_for(
            "STREAM_SERVER_UPDATE",
            predicate=lambda d: d.get("stream_key", "") == stream_key,
        )

        log.info(
            "Sending STREAM_CREATE for guild=%s channel=%s user=%s",
            self.guild_id,
            self.channel_id,
            user_id,
        )
        await main_ws.send_as_json(
            {
                "op": _OP_STREAM_CREATE,
                "d": {
                    "type": "guild",
                    "guild_id": str(self.guild_id),
                    "channel_id": str(self.channel_id),
                    "preferred_region": None,
                },
            }
        )
        await main_ws.send_as_json(
            {
                "op": _OP_STREAM_SET_PAUSED,
                "d": {
                    "stream_key": stream_key,
                    "paused": False,
                },
            }
        )

        # Wait for Discord to respond with stream server credentials
        create_data = await asyncio.wait_for(create_fut, timeout=timeout)
        server_data = await asyncio.wait_for(server_fut, timeout=timeout)

        self.server_id = int(create_data["rtc_server_id"])
        self._stream_key = create_data["stream_key"]
        log.info(
            "STREAM_CREATE received: server_id=%s stream_key=%s",
            self.server_id,
            self._stream_key,
        )

        endpoint = server_data["endpoint"]
        if endpoint.startswith("wss://"):
            endpoint = endpoint[6:]
        self.endpoint = endpoint
        self.token = server_data["token"]
        log.info("STREAM_SERVER_UPDATE received: endpoint=%s", endpoint)

        # Create UDP socket before WebSocket connect — initial_connection() uses
        # it immediately to perform IP discovery.
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.setblocking(False)
        self._socket_reader = SocketReader(self, start_paused=False)  # type: ignore[arg-type]
        self._socket_reader.start()

        # Connect WebSocket to stream server and run the voice handshake.
        # from_connection_state() uses the patched identify() from video_compat
        # which adds video:true and streams descriptors to the IDENTIFY payload.
        self.ws = await DiscordVoiceWebSocket.from_connection_state(
            self, resume=False  # type: ignore[arg-type]
        )

        # Poll until READY completes IP discovery (self.ip is set)
        while not self.ip:
            await self.ws.poll_event()  # type: ignore[union-attr]
        log.info("GoLive: IP discovery complete (%s:%s)", self.ip, self.port)

        # Poll until SESSION_DESCRIPTION arrives (ws.secret_key is set)
        while self.ws.secret_key is None:
            await self.ws.poll_event()
        log.info(
            "GoLive: session established (mode=%s, audio_ssrc=%d, video_ssrc=%d)",
            self.mode,
            self.ssrc,
            self.ssrc + 1,
        )

        # Announce video capability using the patched client_connect() from
        # video_compat, which sends the full VIDEO opcode with video/rtx SSRCs
        # and stream resolution/bitrate metadata.
        await self.ws.client_connect()

        # Keep the stream WebSocket heartbeat alive in the background
        loop = asyncio.get_event_loop()
        self._poll_task = loop.create_task(self._poll_ws(), name="golive-ws-poll")

        log.info(
            "GoLive connection ready: audio SSRC %d, video SSRC %d",
            self.ssrc,
            self.ssrc + 1,
        )

    async def _poll_ws(self) -> None:
        """Continuously poll the stream WebSocket to handle heartbeats."""
        try:
            while True:
                await self.ws.poll_event()  # type: ignore[union-attr]
        except asyncio.CancelledError:
            pass
        except Exception as exc:
            log.warning("GoLive WS poller ended: %s", exc)

    def add_socket_listener(self, callback) -> None:
        if self._socket_reader is not None:
            self._socket_reader.register(callback)

    def remove_socket_listener(self, callback) -> None:
        if self._socket_reader is not None:
            self._socket_reader.unregister(callback)

    def send_packet(self, packet: bytes) -> None:
        """Send a raw RTP packet to the go-live stream server."""
        try:
            self.socket.sendall(packet)  # type: ignore[union-attr]
        except OSError:
            pass

    async def disconnect(self) -> None:
        """Stop the go-live stream and release all resources.

        Idempotent: the stream key is cleared after the first STREAM_DELETE.
        Several teardown paths (!stop, auto-leave, the stream task's finally)
        may all call this, and a repeated STREAM_DELETE for the same
        user/channel key would end a *new* go-live started in the meantime.
        """
        stream_key, self._stream_key = self._stream_key, None
        if stream_key:
            try:
                await self._bot.ws.send_as_json(
                    {
                        "op": _OP_STREAM_DELETE,
                        "d": {"stream_key": stream_key},
                    }
                )
                log.info("Sent STREAM_DELETE for %s", stream_key)
            except Exception:
                log.debug("GoLive: could not send STREAM_DELETE", exc_info=True)

        if self._poll_task and not self._poll_task.done():
            self._poll_task.cancel()
            try:
                await self._poll_task
            except asyncio.CancelledError:
                pass

        try:
            if self.ws:
                await self.ws.close()
        except Exception:
            pass

        if self._socket_reader is not None:
            self._socket_reader.stop()
            self._socket_reader = None

        if self.socket:
            try:
                self.socket.close()
            except Exception:
                pass


# ── _GoLiveVCProxy ────────────────────────────────────────────────────────────


class _GoLiveVCProxy:
    """
    Adapts GoLiveConnection to the VoiceClient interface expected by
    H264VideoPlayer.  H264VideoPlayer uses voice_client.ssrc (from which it
    derives video_ssrc = ssrc + 1) and voice_client._connection (for
    send_packet, mode, secret_key, and dave_session).
    """

    def __init__(self, conn: GoLiveConnection) -> None:
        self.ssrc = conn.ssrc  # audio SSRC; H264VideoPlayer adds 1 for video
        self._connection = conn  # conn implements the _connection interface

    @property
    def mode(self) -> str:
        return self._connection.mode

    @property
    def secret_key(self):
        return self._connection.secret_key


# ── GoLiveAudioSender ─────────────────────────────────────────────────────────


# Audio/video clock follower: how far (s) the audio's media position may drift
# from the video's before the sender corrects by inserting silence (audio
# ahead) or dropping a 20 ms frame (audio behind). Two frames of slack keeps
# it from chattering on normal jitter.
_SYNC_SLACK = 0.04
# Longest the follower holds audio (silence without reading) before giving up
# and re-anchoring. The FIFO only buffers ~0.34 s, so holding longer would
# block FFmpeg's audio write and with it the video: a permanent stall.
_SYNC_MAX_HOLD_FRAMES = 15  # 300 ms


class GoLiveAudioSender(threading.Thread):
    """
    Reads raw PCM S16LE stereo 48 kHz from an open FIFO file object, encodes
    each 20-ms frame to Opus, builds an RTP audio packet, encrypts it using
    the go-live connection's negotiated cipher, and sends it via the go-live
    UDP socket.
    """

    _SAMPLES_PER_FRAME: int = _opus.Encoder.SAMPLES_PER_FRAME  # 960 @ 48 kHz
    _FRAME_SIZE: int = _opus.Encoder.FRAME_SIZE  # 3840 bytes = 20 ms PCM
    _FRAME_DURATION: float = _opus.Encoder.FRAME_LENGTH / 1000.0  # 0.020 s

    def __init__(
        self,
        file_obj,
        conn: GoLiveConnection,
        is_source_active: Callable[[], bool] | None = None,
        pause_gate: threading.Event | None = None,
        sync_source=None,
    ) -> None:
        super().__init__(name="GoLiveAudio", daemon=True)
        self._f = file_obj
        self._conn = conn
        self._end = threading.Event()
        # Returns True while the video player may still produce output, incl. the
        # FIFO gap during a software-encoder fallback restart.  When set, a short
        # read is treated as that gap (wait for the new writer) rather than EOF.
        self._is_source_active = is_source_active
        # Shared with H264VideoPlayer: cleared while playback is paused.
        self._pause_gate = pause_gate
        # The H264VideoPlayer (or None): its ``position`` is the master clock,
        # ``restart_seq`` / ``segment_offset`` mark FFmpeg restarts (seek,
        # aspect change, encoder fallback) and ``first_frame_sent`` says video
        # is flowing. Audio follows it instead of free-running on its own timer.
        self._sync = sync_source
        self._seen_seq: int = getattr(sync_source, "restart_seq", 0)
        self._seg_base: float = getattr(sync_source, "segment_offset", 0.0)
        self._seg_frames: int = 0  # 20 ms frames consumed since the restart
        self._hold_frames: int = 0  # consecutive silence frames sent to hold
        # After releasing a hold, don't hold again until video moves past this.
        self._released_at: float | None = None
        self.corrections = {"silence": 0, "dropped": 0, "released": 0}
        self._drained: threading.Event | None = None
        register = getattr(sync_source, "register_audio_follower", None)
        if callable(register):
            self._drained = register()
        # Position moves in 1/fps steps; keep the dead band wider than a step
        # so low frame rates don't flip-flop between silence and drops.
        fps = getattr(sync_source, "fps", None)
        self._slack = max(
            _SYNC_SLACK,
            0.5 / fps + 0.02 if isinstance(fps, (int, float)) and fps > 0 else 0.0,
        )

        self._seq: int = 0
        self._ts: int = 0
        self._nonce: int = 0  # running nonce counter for lite/rtpsize modes

    def stop(self) -> None:
        self._end.set()

    def _check_restart(self) -> bool:
        """Handle an FFmpeg restart (drain + re-anchor). True if one happened."""
        sync = self._sync
        if sync is None or sync.restart_seq == self._seen_seq:
            return False
        self._seen_seq = sync.restart_seq
        dropped = self._drain_stale()
        self._seg_base = sync.segment_offset
        self._seg_frames = 0
        self._hold_frames = 0
        if self._drained is not None:
            self._drained.set()
        log.info(
            "GoLiveAudio: source restarted at %.1fs — dropped %d stale bytes",
            self._seg_base, dropped,
        )
        return True

    @staticmethod
    def _video_flowing(sync) -> bool:
        """True once the current FFmpeg segment has emitted video. Uses the
        per-segment frame count so a restart doesn't arm the hold before the
        new process produces its first frame."""
        frames = getattr(sync, "segment_frames", None)
        if isinstance(frames, int):
            return frames > 0
        return sync.first_frame_sent.is_set()

    def _audio_position(self) -> float:
        return self._seg_base + self._seg_frames * self._FRAME_DURATION

    def _drain_stale(self) -> int:
        """Discard audio the previous FFmpeg left in the FIFO (up to ~0.3 s),
        so it isn't played after a seek/restart. Returns bytes discarded."""
        try:
            fd = self._f.fileno()
            flags = fcntl.fcntl(fd, fcntl.F_GETFL)
        except (OSError, ValueError, io.UnsupportedOperation):
            return 0
        dropped = 0
        fcntl.fcntl(fd, fcntl.F_SETFL, flags | os.O_NONBLOCK)
        try:
            while True:
                try:
                    chunk = self._f.read(65536)
                except BlockingIOError:
                    break
                if not chunk:
                    break
                dropped += len(chunk)
        finally:
            fcntl.fcntl(fd, fcntl.F_SETFL, flags)
        return dropped

    def run(self) -> None:
        try:
            self._send_audio()
        except Exception:
            log.exception("GoLiveAudioSender error")
        log.info("GoLiveAudioSender stopped")

    def _send_audio(self) -> None:
        encoder = _opus.Encoder()

        # A/V lip-sync offset (STREAM_AV_SYNC_MS). Positive advances audio by
        # discarding backlogged initial audio (fixes "audio behind"); negative
        # holds audio start (fixes "audio ahead").
        offset_ms = _av_sync_ms()
        target = offset_ms / 1000.0  # desired audio-minus-video position
        if offset_ms > 0:
            for _ in range(int(offset_ms / (self._FRAME_DURATION * 1000))):
                if self._end.is_set() or len(self._f.read(self._FRAME_SIZE)) < self._FRAME_SIZE:
                    break
                self._seg_frames += 1
        elif offset_ms < 0:
            self._end.wait(timeout=(-offset_ms) / 1000.0)

        log.info("GoLiveAudio: starting audio transmission (av_sync_ms=%d)", offset_ms)
        t0 = time.perf_counter()
        loops = 0

        while not self._end.is_set():
            if self._pause_gate is not None and not self._pause_gate.is_set():
                while not self._pause_gate.wait(timeout=0.1):
                    if self._end.is_set():
                        return
                    # A seek/aspect change while paused restarts FFmpeg now;
                    # drain the old audio before the new process fills the FIFO.
                    self._check_restart()
                t0 = time.perf_counter()  # restart pacing after the pause
                loops = 0
                continue

            sync = self._sync
            if self._check_restart():
                # FFmpeg restarted: old audio dropped, position re-anchored.
                t0 = time.perf_counter()
                loops = 0
                continue

            pcm: bytes | None = None
            if sync is not None and getattr(sync, "seek_pending", False) is True:
                # Seek requested but the old FFmpeg isn't gone yet: its audio
                # is stale, so neither play it nor "catch up" through it.
                pcm = bytes(self._FRAME_SIZE)
            elif sync is not None and self._video_flowing(sync):
                video_pos = sync.position
                if self._released_at is not None and video_pos > self._released_at:
                    self._released_at = None  # video is moving again
                drift = self._audio_position() - video_pos - target
                if drift > self._slack and self._released_at is not None:
                    pass  # still stalled after a release: let audio play on
                elif drift > self._slack and self._hold_frames < _SYNC_MAX_HOLD_FRAMES:
                    # Audio ahead (video stalled/late): hold the audio by
                    # sending 20 ms of silence without consuming any PCM.
                    pcm = bytes(self._FRAME_SIZE)
                    self._hold_frames += 1
                    self.corrections["silence"] += 1
                elif drift > self._slack:
                    # Held as long as the FIFO allows: release and re-anchor
                    # so we don't immediately start holding again.
                    self._seg_frames = max(
                        0,
                        round(
                            (video_pos + target - self._seg_base)
                            / self._FRAME_DURATION
                        ),
                    )
                    self._hold_frames = 0
                    self._released_at = video_pos
                    self.corrections["released"] += 1
                    log.warning(
                        "GoLiveAudio: video stalled — releasing audio hold "
                        "after %d ms", _SYNC_MAX_HOLD_FRAMES * 20,
                    )
                elif drift < -self._slack:
                    # Audio behind: skip one 20 ms frame to catch up.
                    if len(self._f.read(self._FRAME_SIZE)) == self._FRAME_SIZE:
                        self._seg_frames += 1
                        self.corrections["dropped"] += 1
            if pcm is None:
                self._hold_frames = 0
                pcm = self._f.read(self._FRAME_SIZE)
                if len(pcm) == self._FRAME_SIZE:
                    self._seg_frames += 1
            if len(pcm) < self._FRAME_SIZE:
                # Short read = current FFmpeg closed the FIFO write end.  If the
                # player is mid-fallback (restarting FFmpeg), wait for the new
                # writer and resume; the read fd stays valid across the gap.
                if self._is_source_active is not None and self._is_source_active():
                    if self._end.wait(timeout=0.05):
                        break
                    t0 = time.perf_counter()  # reset pacing after the gap
                    loops = 0
                    continue
                break

            encoded = encoder.encode(pcm, self._SAMPLES_PER_FRAME)

            # DAVE E2EE encryption (applied before transport encryption)
            if self._conn.can_encrypt and self._conn.dave_session is not None:
                encoded = self._conn.dave_session.encrypt_opus(encoded)

            # Build RTP audio header (PT=120 for Opus, SSRC = go-live audio SSRC)
            hdr = bytearray(12)
            hdr[0] = 0x80
            hdr[1] = 0x78  # marker=0, PT=120
            struct.pack_into(">H", hdr, 2, self._seq & 0xFFFF)
            struct.pack_into(">I", hdr, 4, self._ts & 0xFFFFFFFF)
            struct.pack_into(">I", hdr, 8, self._conn.ssrc & 0xFFFFFFFF)

            packet = _encrypt_audio(
                bytes(hdr),
                encoded,
                self._conn.mode,
                self._conn.secret_key,
                self._nonce,
            )
            self._nonce = (self._nonce + 1) & 0xFFFFFFFF

            self._conn.send_packet(packet)

            self._seq = (self._seq + 1) & 0xFFFF
            self._ts = (self._ts + self._SAMPLES_PER_FRAME) & 0xFFFFFFFF
            loops += 1

            # Wall-clock pacing — same technique as H264VideoPlayer
            next_time = t0 + loops * self._FRAME_DURATION
            delay = next_time - time.perf_counter()
            if delay > 0.001 and self._end.wait(timeout=delay):
                break


# ── Encryption ────────────────────────────────────────────────────────────────


def _encrypt_audio(
    header: bytes,
    payload: bytes,
    mode: str,
    secret_key: list[int],
    nonce: int,
) -> bytes:
    """
    Encrypt one audio RTP packet using the negotiated voice encryption mode.
    Mirrors _encrypt() in video_player.py but takes the nonce as an int
    (caller increments it).
    """
    key = bytes(secret_key)

    if mode == "aead_xchacha20_poly1305_rtpsize":
        box = nacl.secret.Aead(key)  # type: ignore[assignment]
        nonce_buf = bytearray(24)
        struct.pack_into(">I", nonce_buf, 0, nonce)
        ct = box.encrypt(payload, bytes(header), bytes(nonce_buf)).ciphertext
        return bytes(header) + ct + bytes(nonce_buf[:4])

    if mode == "xsalsa20_poly1305":
        box = nacl.secret.SecretBox(key)  # type: ignore[assignment]
        nonce_buf = bytearray(24)
        nonce_buf[:12] = header
        return bytes(header) + box.encrypt(payload, bytes(nonce_buf)).ciphertext

    if mode == "xsalsa20_poly1305_suffix":
        box = nacl.secret.SecretBox(key)  # type: ignore[assignment]
        nonce_bytes = nacl.utils.random(24)
        ct = box.encrypt(payload, nonce_bytes).ciphertext
        return bytes(header) + ct + nonce_bytes

    if mode == "xsalsa20_poly1305_lite":
        box = nacl.secret.SecretBox(key)  # type: ignore[assignment]
        nonce_buf = bytearray(24)
        struct.pack_into(">I", nonce_buf, 0, nonce)
        ct = box.encrypt(payload, bytes(nonce_buf)).ciphertext
        return bytes(header) + ct + bytes(nonce_buf[:4])

    raise ValueError(f"Unknown voice encryption mode: {mode!r}")
