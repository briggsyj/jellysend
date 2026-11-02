"""Tests for stream-profile env knobs in streaming/video_player.py.

Profile is chosen by the STREAM_QUALITY preset (720p/1080p/4k, default 1080p),
with STREAM_RESOLUTION / STREAM_FPS / STREAM_VIDEO_BITRATE overriding any axis.
"""

from __future__ import annotations

import pytest

from streaming.video_player import (
    _packet_pace_fraction,
    _stream_bitrate,
    _stream_fps,
    _stream_preset,
    _stream_resolution,
)


@pytest.fixture(autouse=True)
def _clear_profile_env(monkeypatch):
    """Start each test from a clean profile env so the 1080p default applies."""
    for var in (
        "STREAM_QUALITY",
        "STREAM_RESOLUTION",
        "STREAM_FPS",
        "STREAM_VIDEO_BITRATE",
        "STREAM_PACKET_PACE",
    ):
        monkeypatch.delenv(var, raising=False)


# ---------------------------------------------------------------------------
# _stream_preset (STREAM_QUALITY)
# ---------------------------------------------------------------------------


def test_preset_default_is_1080p():
    assert _stream_preset() == ("1920:1080", 60.0, "12000k")


def test_preset_720p(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "720p")
    assert _stream_preset() == ("1280:720", 30.0, "10000k")


def test_preset_4k_case_insensitive(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "4K")
    assert _stream_preset() == ("3840:2160", 60.0, "24000k")


def test_preset_unknown_falls_back_to_default(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "potato")
    assert _stream_preset() == ("1920:1080", 60.0, "12000k")


# ---------------------------------------------------------------------------
# _stream_resolution
# ---------------------------------------------------------------------------


def test_resolution_follows_preset():
    assert _stream_resolution() == "1920:1080"


def test_resolution_follows_720p_preset(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "720p")
    assert _stream_resolution() == "1280:720"


def test_resolution_override_beats_preset(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "720p")
    monkeypatch.setenv("STREAM_RESOLUTION", "1600:900")
    assert _stream_resolution() == "1600:900"


def test_resolution_x_form_normalized(monkeypatch):
    monkeypatch.setenv("STREAM_RESOLUTION", "1280x720")
    assert _stream_resolution() == "1280:720"


def test_resolution_invalid_falls_back_to_preset(monkeypatch):
    monkeypatch.setenv("STREAM_RESOLUTION", "huge")
    assert _stream_resolution() == "1920:1080"


# ---------------------------------------------------------------------------
# _stream_bitrate
# ---------------------------------------------------------------------------


def test_bitrate_follows_preset():
    assert _stream_bitrate() == "12000k"


def test_bitrate_follows_720p_preset(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "720p")
    assert _stream_bitrate() == "10000k"


def test_bitrate_override_beats_preset(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "1080p")
    monkeypatch.setenv("STREAM_VIDEO_BITRATE", "4000k")
    assert _stream_bitrate() == "4000k"


def test_bitrate_blank_falls_back_to_preset(monkeypatch):
    monkeypatch.setenv("STREAM_VIDEO_BITRATE", "   ")
    assert _stream_bitrate() == "12000k"


# ---------------------------------------------------------------------------
# _stream_fps
# ---------------------------------------------------------------------------


def test_fps_follows_preset():
    assert _stream_fps() == 60.0


def test_fps_follows_720p_preset(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "720p")
    assert _stream_fps() == 30.0


def test_fps_override_beats_preset(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "1080p")
    monkeypatch.setenv("STREAM_FPS", "24")
    assert _stream_fps() == 24.0


def test_fps_out_of_range_falls_back_to_preset(monkeypatch):
    monkeypatch.setenv("STREAM_FPS", "240")
    assert _stream_fps() == 60.0


def test_fps_non_numeric_falls_back_to_preset(monkeypatch):
    monkeypatch.setenv("STREAM_FPS", "fast")
    assert _stream_fps() == 60.0


# ---------------------------------------------------------------------------
# _packet_pace_fraction (STREAM_PACKET_PACE)
# ---------------------------------------------------------------------------


def test_pace_default_is_disabled():
    assert _packet_pace_fraction() == 0.0


def test_pace_override(monkeypatch):
    monkeypatch.setenv("STREAM_PACKET_PACE", "0.75")
    assert _packet_pace_fraction() == 0.75


def test_pace_clamps_to_max(monkeypatch):
    monkeypatch.setenv("STREAM_PACKET_PACE", "2")
    assert _packet_pace_fraction() == 0.95


def test_pace_clamps_negative_to_zero(monkeypatch):
    monkeypatch.setenv("STREAM_PACKET_PACE", "-1")
    assert _packet_pace_fraction() == 0.0


def test_pace_non_numeric_falls_back_to_disabled(monkeypatch):
    monkeypatch.setenv("STREAM_PACKET_PACE", "smooth")
    assert _packet_pace_fraction() == 0.0


# ===========================================================================
# Behavioural tests for the rest of streaming/video_player.py
# ===========================================================================

import io  # noqa: E402
import shutil  # noqa: E402
import struct  # noqa: E402
import subprocess  # noqa: E402
import threading  # noqa: E402

import nacl.secret  # noqa: E402

import streaming.video_player as vpmod  # noqa: E402
from streaming.video_player import (  # noqa: E402
    _BR,
    _BW,
    _MTU,
    _VIDEO_NONCE_BASE,
    H264VideoPlayer,
    _AudioPipeSource,
    _EncoderConfig,
    _encrypt,
    _ep_add,
    _ep_remove,
    _nal_type,
    _packetize_nal,
    _rtp_header,
    rewrite_sps_vui,
)

_KEY = list(range(32))
_HW = _EncoderConfig(
    name="h264_vaapi",
    pre_input=["-vaapi_device", "/dev/x"],
    post_codec=["-b:v", "1k"],
    vf="hwupload",
)
_SW = _EncoderConfig(
    name="libopenh264", pre_input=[], post_codec=["-b:v", "1k"], vf="scale=1:1"
)


# ── helpers ──────────────────────────────────────────────────────────────────


class _FastEvent(threading.Event):
    """Event whose wait() never blocks (so pacing never sleeps in tests)."""

    def __init__(self) -> None:
        super().__init__()
        self.waits: list[float | None] = []

    def wait(self, timeout=None):  # noqa: D401
        self.waits.append(timeout)
        return self.is_set()


class _FakeConn:
    def __init__(self, mode="xsalsa20_poly1305_lite", dave=None, fail=False):
        self.mode = mode
        self.secret_key = _KEY
        self.dave_session = dave
        self.dave_protocol_version = 1 if dave else 0
        self.sent: list[bytes] = []
        self._fail = fail
        self.on_send = None

    def send_packet(self, pkt: bytes) -> None:
        if self._fail:
            raise OSError("boom")
        self.sent.append(pkt)
        if self.on_send:
            self.on_send(pkt)


class _FakeVC:
    def __init__(self, conn: _FakeConn, ssrc: int = 1000):
        self.ssrc = ssrc
        self._connection = conn

    @property
    def mode(self):
        return self._connection.mode

    @property
    def secret_key(self):
        return self._connection.secret_key


class _ChunkReader:
    """stdout stand-in returning data in fixed-size chunks."""

    def __init__(self, data: bytes, step: int) -> None:
        self._d = data
        self._step = step

    def read(self, n: int) -> bytes:
        out, self._d = self._d[: min(n, self._step)], self._d[min(n, self._step) :]
        return out


class _FakeProc:
    def __init__(self, stdout=b"", stderr=b"", running=False, wait_timeout=False):
        self.stdout = stdout if hasattr(stdout, "read") else io.BytesIO(stdout)
        self.stderr = io.BytesIO(stderr)
        self.running = running
        self.wait_timeout = wait_timeout
        self.terminated = 0
        self.killed = 0
        self.returncode = 0

    def poll(self):
        return None if self.running else 0

    def terminate(self):
        self.terminated += 1

    def kill(self):
        self.killed += 1
        self.running = False

    def wait(self, timeout=None):
        if self.wait_timeout and timeout is not None:
            raise subprocess.TimeoutExpired("ffmpeg", timeout)
        self.running = False
        return 0


def _decrypt_lite(pkt: bytes) -> tuple[bytes, bytes]:
    hdr, ct, nonce4 = pkt[:12], pkt[12:-4], pkt[-4:]
    plain = nacl.secret.SecretBox(bytes(_KEY)).decrypt(ct, nonce4 + bytes(20))
    return hdr, plain


@pytest.fixture
def no_fifo(monkeypatch):
    monkeypatch.setattr(H264VideoPlayer, "_ensure_fifo", lambda self: None)
    monkeypatch.setattr(H264VideoPlayer, "_cleanup_fifo", lambda self: None)


@pytest.fixture
def make_player(no_fifo, monkeypatch):
    monkeypatch.setattr(vpmod, "_ENCODER", _SW)
    monkeypatch.setattr(vpmod, "_SW_ENCODER", None)

    def _make(conn=None, **kw):
        conn = conn or _FakeConn()
        kw.setdefault("url", "http://user:pw@host:8080/live.ts")
        kw.setdefault("fps", 25.0)
        p = H264VideoPlayer(voice_client=_FakeVC(conn), **kw)
        p._end = _FastEvent()
        return p, conn

    return _make


# ── emulation prevention / bit reader+writer ────────────────────────────────


def test_ep_roundtrip():
    raw = b"\x00\x00\x01\x00\x00\x00\x00\x00\x03\xff"
    escaped = _ep_add(raw)
    assert b"\x00\x00\x01" not in escaped and b"\x00\x00\x00" not in escaped
    assert _ep_remove(escaped) == raw


def test_bitreader_writer_roundtrip():
    w = _BW()
    w.u(3, 5)
    w.ue(0)
    w.ue(7)
    w.se(-3)
    w.se(4)
    w.se(0)
    r = _BR(w.to_bytes())
    assert r.u(3) == 5
    assert r.ue() == 0
    assert r.ue() == 7
    assert r.se() == -3
    assert r.se() == 4
    assert r.se() == 0
    assert r.u(1) == 1  # RBSP stop bit


def test_bitreader_past_end_and_bad_golomb():
    r = _BR(b"\xff")
    r.u(8)
    assert r.remaining() == 0
    with pytest.raises(IndexError):
        r.u(1)
    with pytest.raises(ValueError):
        _BR(bytes(8)).ue()


# ── SPS VUI rewrite ──────────────────────────────────────────────────────────


def _make_sps(
    profile=66,
    poc_type=2,
    crop=False,
    frame_mbs_only=True,
    max_ref=1,
    high_extras=False,
) -> bytes:
    w = _BW()
    w.u(8, profile)
    w.u(8, 0xC0)
    w.u(8, 30)
    w.ue(0)  # sps id
    if profile in vpmod._HIGH_PROFILES:
        w.ue(3 if high_extras else 1)  # chroma_format_idc
        if high_extras:
            w.u(1, 0)  # separate_colour_plane
        w.ue(0)
        w.ue(0)
        w.u(1, 0)
        w.u(1, 1 if high_extras else 0)  # seq_scaling_matrix_present
        if high_extras:
            for i in range(12):
                if i == 0:
                    w.u(1, 1)
                    for _ in range(16):
                        w.se(0)  # delta 0 → next stays 8... keep simple
                else:
                    w.u(1, 0)
    w.ue(0)  # log2_max_frame_num_minus4
    w.ue(poc_type)
    if poc_type == 0:
        w.ue(2)
    elif poc_type == 1:
        w.u(1, 0)
        w.se(-1)
        w.se(2)
        w.ue(2)
        w.se(1)
        w.se(-1)
    w.ue(max_ref)
    w.u(1, 0)
    w.ue(3)
    w.ue(3)
    w.u(1, 1 if frame_mbs_only else 0)
    if not frame_mbs_only:
        w.u(1, 0)
    w.u(1, 1)
    w.u(1, 1 if crop else 0)
    if crop:
        for v in (0, 1, 0, 2):
            w.ue(v)
    w.u(1, 0)  # vui_parameters_present_flag = 0
    return b"\x67" + _ep_add(w.to_bytes())


def _read_restriction_from_minimal(out: bytes, sps_kw: dict) -> tuple[int, int]:
    """Walk a rewritten minimal SPS and return (max_num_reorder, max_dec_buf)."""
    r = _BR(_ep_remove(out[1:]))
    profile = r.u(8)
    r.u(8)
    r.u(8)
    r.ue()
    if profile in vpmod._HIGH_PROFILES:
        cf = r.ue()
        if cf == 3:
            r.u(1)
        r.ue()
        r.ue()
        r.u(1)
        if r.u(1):
            for i in range(12 if cf == 3 else 8):
                if r.u(1):
                    vpmod._copy_scaling_list(r, _BW(), 16 if i < 6 else 64)
    r.ue()
    poc = r.ue()
    if poc == 0:
        r.ue()
    elif poc == 1:
        r.u(1)
        r.se()
        r.se()
        for _ in range(r.ue()):
            r.se()
    r.ue()
    r.u(1)
    r.ue()
    r.ue()
    if not r.u(1):
        r.u(1)
    r.u(1)
    if r.u(1):
        for _ in range(4):
            r.ue()
    assert r.u(1) == 1  # vui present (forced)
    assert r.u(2) == 0 and r.u(1) == 0 and r.u(5) == 0
    assert r.u(1) == 1  # bitstream_restriction_flag
    assert r.u(1) == 1
    assert (r.ue(), r.ue(), r.ue(), r.ue()) == (2, 1, 16, 16)
    return r.ue(), r.ue()


@pytest.mark.parametrize(
    "kw",
    [
        {},
        {"poc_type": 0},
        {"poc_type": 1, "crop": True},
        {"frame_mbs_only": False, "max_ref": 3},
        {"profile": 100},
        {"profile": 100, "high_extras": True},
    ],
)
def test_rewrite_sps_adds_bitstream_restriction(kw):
    sps = _make_sps(**kw)
    out = rewrite_sps_vui(sps)
    assert out != sps
    assert out[0] == sps[0]
    reorder, dec_buf = _read_restriction_from_minimal(out, kw)
    assert reorder == 0
    assert dec_buf == kw.get("max_ref", 1)
    # Rewriting is idempotent (the VUI-present branch preserves our output)
    assert rewrite_sps_vui(out) == out


def test_rewrite_real_x264_high_sps_is_idempotent_and_changes_reorder():
    # SPS captured from libx264 (High profile, yuv420p, with VUI + restriction)
    sps = bytes.fromhex("6764000aacd94426c044000003000400000300083c489658")
    out = rewrite_sps_vui(sps)
    assert out != sps
    assert rewrite_sps_vui(out) == out


def test_rewrite_real_x264_444_sps():
    sps = bytes.fromhex("67f4000a919b2884d80880000003008000000301078912cb")
    out = rewrite_sps_vui(sps)
    assert rewrite_sps_vui(out) == out


def test_rewrite_sps_passthrough_cases():
    assert rewrite_sps_vui(b"") == b""
    pps = b"\x68\xeb\xe3\xc4"
    assert rewrite_sps_vui(pps) is pps
    truncated = b"\x67\x42"
    assert rewrite_sps_vui(truncated) == truncated  # parse failure → original


# ── encoder detection ────────────────────────────────────────────────────────


class _Run:
    def __init__(self, rc=0, stdout="", stderr=b""):
        self.returncode = rc
        self.stdout = stdout
        self.stderr = stderr


def test_test_encoder_success_and_failure(monkeypatch):
    calls = []

    def fake_run(cmd, **kw):
        calls.append(cmd)
        return _Run(rc=0 if "good" in cmd else 1, stderr=b"a\nb\nno device\n")

    monkeypatch.setattr(vpmod.subprocess, "run", fake_run)
    assert vpmod._test_encoder("good", [], vf="hwupload") is True
    assert "-vf" in calls[-1] and "hwupload" in calls[-1]
    assert vpmod._test_encoder("bad", ["-x"]) is False
    assert "-vf" not in calls[-1] and "-x" in calls[-1]


def test_test_encoder_exception(monkeypatch):
    def boom(*a, **k):
        raise subprocess.TimeoutExpired("ffmpeg", 10)

    monkeypatch.setattr(vpmod.subprocess, "run", boom)
    assert vpmod._test_encoder("x", []) is False


@pytest.mark.parametrize(
    "available,working,expected",
    [
        ("h264_nvenc h264_vaapi libx264", {"h264_nvenc"}, "h264_nvenc"),
        ("h264_nvenc h264_vaapi libx264", {"h264_vaapi"}, "h264_vaapi"),
        ("h264_nvenc h264_vaapi libx264", {"libx264"}, "libx264"),
        ("h264_vaapi libopenh264", {"libopenh264"}, "libopenh264"),
        ("h264_nvenc libx264 libopenh264", set(), None),
        ("", {"libx264"}, None),
    ],
)
def test_detect_encoder_preference(monkeypatch, available, working, expected):
    monkeypatch.setattr(vpmod.subprocess, "run", lambda *a, **k: _Run(stdout=available))
    probed = []

    def fake_probe(name, pre, vf=""):
        probed.append(name)
        return name in working

    monkeypatch.setattr(vpmod, "_test_encoder", fake_probe)
    enc = vpmod._detect_encoder()
    assert (enc.name if enc else None) == expected
    if expected == "h264_vaapi":
        assert enc.pre_input == ["-vaapi_device", "/dev/dri/renderD128"]
        assert enc.vf.startswith("format=nv12,hwupload,scale_vaapi=")
    if expected:
        assert "-b:v" in enc.post_codec


def test_detect_encoder_no_ffmpeg(monkeypatch):
    def nf(*a, **k):
        raise FileNotFoundError

    monkeypatch.setattr(vpmod.subprocess, "run", nf)
    assert vpmod._detect_encoder() is None


def test_libopenh264_config_uses_profile_env(monkeypatch):
    monkeypatch.setenv("STREAM_QUALITY", "720p")
    cfg = vpmod._libopenh264_config()
    assert cfg.vf == vpmod._fit_scale("1280:720")
    assert cfg.post_codec[cfg.post_codec.index("-b:v") + 1] == "10000k"


# ── NAL / RTP utilities ──────────────────────────────────────────────────────


def test_nal_type():
    assert _nal_type(b"") == 0
    assert _nal_type(b"\x65") == 5
    assert _nal_type(b"\x09\xf0") == 9


def test_rtp_header_fields():
    hdr = _rtp_header(0x1_0002, 0x1_0000_0003, 0x1_0000_0004, marker=True)
    assert len(hdr) == 12
    assert hdr[0] == 0x80
    assert hdr[1] == 0x80 | 101
    assert struct.unpack(">HII", bytes(hdr[2:])) == (2, 3, 4)
    assert _rtp_header(1, 1, 1)[1] == 101


def test_packetize_small_and_empty():
    assert _packetize_nal(b"") == []
    nal = b"\x65" + b"x" * (_MTU - 13)
    assert _packetize_nal(nal) == [nal]


def test_packetize_fu_a_fragmentation():
    body = bytes(range(256)) * 12  # 3072 bytes
    nal = bytes([0x65]) + body  # NRI=3, type=5
    pkts = _packetize_nal(nal)
    assert len(pkts) == 3
    assert all(len(p) <= _MTU - 12 for p in pkts)
    for i, p in enumerate(pkts):
        assert p[0] == 0x60 | 28  # FU indicator keeps NRI, type 28
        start, end = bool(p[1] & 0x80), bool(p[1] & 0x40)
        assert start == (i == 0)
        assert end == (i == len(pkts) - 1)
        assert p[1] & 0x1F == 5
    assert b"".join(p[2:] for p in pkts) == body


# ── encryption ───────────────────────────────────────────────────────────────


def test_encrypt_aead_rtpsize():
    hdr = bytes(_rtp_header(1, 2, 3))
    nonce = [7]
    pkt = _encrypt(hdr, b"payload", "aead_xchacha20_poly1305_rtpsize", _KEY, nonce)
    assert nonce == [8]
    assert pkt[:12] == hdr and pkt[-4:] == struct.pack(">I", 7)
    plain = nacl.secret.Aead(bytes(_KEY)).decrypt(pkt[12:-4], hdr, pkt[-4:] + bytes(20))
    assert plain == b"payload"


def test_encrypt_xsalsa20_header_nonce():
    hdr = bytes(_rtp_header(1, 2, 3))
    nonce = [0]
    pkt = _encrypt(hdr, b"hello", "xsalsa20_poly1305", _KEY, nonce)
    assert nonce == [0]
    plain = nacl.secret.SecretBox(bytes(_KEY)).decrypt(pkt[12:], hdr + bytes(12))
    assert plain == b"hello"


def test_encrypt_xsalsa20_suffix():
    hdr = bytes(_rtp_header(1, 2, 3))
    pkt = _encrypt(hdr, b"hello", "xsalsa20_poly1305_suffix", _KEY, [0])
    plain = nacl.secret.SecretBox(bytes(_KEY)).decrypt(pkt[12:-24], pkt[-24:])
    assert plain == b"hello"


def test_encrypt_lite_and_nonce_wrap():
    hdr = bytes(_rtp_header(1, 2, 3))
    nonce = [0xFFFF_FFFF]
    pkt = _encrypt(hdr, b"hi", "xsalsa20_poly1305_lite", _KEY, nonce)
    assert nonce == [0]
    assert _decrypt_lite(pkt) == (hdr, b"hi")


def test_encrypt_unknown_mode():
    with pytest.raises(ValueError):
        _encrypt(b"\x00" * 12, b"", "rot13", _KEY, [0])


# ── _AudioPipeSource ─────────────────────────────────────────────────────────


def test_audio_pipe_source():
    f = io.BytesIO(b"\x01" * (3840 + 100))
    src = _AudioPipeSource(f)
    assert src.is_opus() is False
    assert src.read() == b"\x01" * 3840
    assert src.read() == b""  # short read → end
    src.cleanup()
    assert f.closed

    class Bad:
        def close(self):
            raise OSError

    _AudioPipeSource(Bad()).cleanup()  # swallowed


# ── H264VideoPlayer: construction / FIFO / ffmpeg command ───────────────────


def test_player_real_fifo_lifecycle(monkeypatch):
    import os
    import stat

    monkeypatch.setattr(vpmod, "_ENCODER", _SW)
    p = H264VideoPlayer("http://h/x", _FakeVC(_FakeConn(), ssrc=987_650), fps=30)
    try:
        assert p.audio_fifo == "/tmp/slopsoil_987651_audio.fifo"
        assert stat.S_ISFIFO(os.stat(p.audio_fifo).st_mode)
        # re-creating over an existing FIFO works
        p._ensure_fifo()
        assert stat.S_ISFIFO(os.stat(p.audio_fifo).st_mode)
    finally:
        p._cleanup_fifo()
    assert not os.path.exists(p.audio_fifo)
    p._cleanup_fifo()  # missing file is fine


def test_player_init_state(make_player):
    p, _ = make_player(fps=30.0)
    assert p._ssrc == 1001
    assert p._ts_inc == 3000
    assert p._nonce == [_VIDEO_NONCE_BASE]
    assert p._enc is _SW
    assert p.daemon


def _opt(cmd, flag):
    return cmd[cmd.index(flag) + 1]


def test_ffmpeg_cmd_url_live(make_player):
    p, _ = make_player(url="https://h/live.m3u8", live=True, probe_size=123)
    p._enc = _HW
    cmd = p._ffmpeg_cmd()
    assert cmd[0] == "ffmpeg"
    assert "-re" not in cmd
    assert _opt(cmd, "-reconnect") == "1"
    assert _opt(cmd, "-probesize") == "123"
    assert _opt(cmd, "-analyzeduration") == "123"
    assert _opt(cmd, "-vaapi_device") == "/dev/x"
    assert cmd.index("-vaapi_device") < cmd.index("-i")
    assert _opt(cmd, "-i") == "https://h/live.m3u8"
    assert _opt(cmd, "-c:v") == "h264_vaapi"
    assert _opt(cmd, "-vf") == "hwupload"
    assert _opt(cmd, "-r") == "25" and _opt(cmd, "-g") == "25"
    assert "pipe:1" in cmd
    assert "0:a:0" in cmd and "anullsrc=r=48000:cl=stereo" not in cmd
    assert cmd[-1] == p.audio_fifo


def test_ffmpeg_cmd_local_vod_uses_re_and_no_reconnect(make_player):
    p, _ = make_player(url="/media/movie.mkv", live=False)
    cmd = p._ffmpeg_cmd()
    assert "-re" in cmd
    assert cmd.index("-re") < cmd.index("-i")
    assert "-reconnect" not in cmd


def test_ffmpeg_cmd_url_vod_has_no_re(make_player):
    p, _ = make_player(url="rtsp://cam/stream", live=False)
    cmd = p._ffmpeg_cmd()
    assert "-re" not in cmd and "-reconnect" in cmd


def test_ffmpeg_cmd_local_live_none(make_player):
    p, _ = make_player(url="/dev/video0", live=None)
    assert "-re" in p._ffmpeg_cmd()


def test_ffmpeg_cmd_no_audio_injects_silence(make_player):
    p, _ = make_player(audio=False)
    cmd = p._ffmpeg_cmd()
    assert "anullsrc=r=48000:cl=stereo" in cmd
    assert "1:a:0" in cmd and "0:a:0" not in cmd
    # silence input comes after the video output, before audio map
    assert cmd.index("anullsrc=r=48000:cl=stereo") < cmd.index("1:a:0")


def test_ffmpeg_cmd_requires_encoder(make_player):
    p, _ = make_player()
    p._enc = None
    with pytest.raises(AssertionError):
        p._ffmpeg_cmd()


# ── stop / kill / is_source_active ───────────────────────────────────────────


def test_stop_terminates_running_proc(make_player):
    p, _ = make_player()
    proc = _FakeProc(running=True)
    p._proc = proc
    p.stop()
    assert p._end.is_set() and proc.terminated == 1
    done = _FakeProc(running=False)
    p._proc = done
    p.stop()
    assert done.terminated == 0


def test_stop_without_proc(make_player):
    p, _ = make_player()
    p.stop()
    assert p._end.is_set()


def test_kill_proc_paths():
    exited = _FakeProc(running=False)
    H264VideoPlayer._kill_proc(exited)
    assert exited.terminated == 0
    graceful = _FakeProc(running=True)
    H264VideoPlayer._kill_proc(graceful)
    assert graceful.terminated == 1 and graceful.killed == 0
    stubborn = _FakeProc(running=True, wait_timeout=True)
    H264VideoPlayer._kill_proc(stubborn)
    assert stubborn.terminated == 1 and stubborn.killed == 1


def test_is_source_active(make_player):
    p, _ = make_player()
    assert p.is_source_active() is False  # thread not started
    p.is_alive = lambda: True
    assert p.is_source_active() is True
    p._end.set()
    assert p.is_source_active() is False


def test_drain_stderr_demotes_startup_noise(make_player, caplog):
    p, _ = make_player()
    p._proc = _FakeProc(stderr=b"DecodeFrame failed\nreal problem here\n")
    with caplog.at_level("DEBUG", logger="streaming.video_player"):
        p._drain_stderr()
    levels = {r.getMessage(): r.levelname for r in caplog.records}
    assert levels["ffmpeg (startup): DecodeFrame failed"] == "DEBUG"
    assert levels["ffmpeg video: real problem here"] == "WARNING"


# ── _send_frame ──────────────────────────────────────────────────────────────


def test_send_frame_marker_seq_ts_and_encryption(make_player):
    p, conn = make_player()
    big = b"\x65" + b"\xaa" * 2500
    sps = b"\x67\x42"
    p._send_frame([sps, big])
    assert len(conn.sent) == 1 + 3
    seqs, markers = [], []
    for pkt in conn.sent:
        hdr, _ = _decrypt_lite(pkt)
        seqs.append(struct.unpack(">H", hdr[2:4])[0])
        markers.append(bool(hdr[1] & 0x80))
        assert struct.unpack(">I", hdr[4:8])[0] == 0
        assert struct.unpack(">I", hdr[8:12])[0] == 1001
    assert seqs == [0, 1, 2, 3]
    assert markers == [False, False, False, True]
    assert _decrypt_lite(conn.sent[0])[1] == sps
    assert p._packets_sent == 4
    assert p._nonce == [_VIDEO_NONCE_BASE + 4]
    assert p._ts == 3600
    p._send_frame([b"\x41\x01"])
    hdr, _ = _decrypt_lite(conn.sent[-1])
    assert struct.unpack(">I", hdr[4:8])[0] == 3600
    assert hdr[1] & 0x80


def test_send_frame_empty_is_noop(make_player):
    p, conn = make_player()
    p._send_frame([])
    p._send_frame([b""])  # NAL with no payloads
    assert conn.sent == [] and p._ts == 0


def test_send_frame_drops_on_oserror(make_player):
    p, conn = make_player(conn=_FakeConn(fail=True))
    p._send_frame([b"\x65\x01", b"\x65\x02"])
    assert p._packets_sent == 0
    assert p._seq == 2  # sequence still advances


def test_send_frame_seq_wraps(make_player):
    p, conn = make_player()
    p._seq = 0xFFFF
    p._send_frame([b"\x65\x01", b"\x65\x02"])
    assert p._seq == 1


def test_send_frame_dave_encrypts_and_appends_trailer(make_player):
    class Dave:
        def __init__(self):
            self.calls = []

        def encrypt_h264(self, ssrc, annex_b):
            self.calls.append((ssrc, annex_b))
            return bytes(b ^ 0xFF for b in annex_b) + b"TRAILER\xfa\xfa"

    dave = Dave()
    p, conn = make_player(conn=_FakeConn(dave=dave))
    nals = [b"\x67\x01\x02", b"\x65\x03\x04\x05"]
    p._send_frame(nals)
    assert dave.calls == [
        (1001, b"\x00\x00\x00\x01\x67\x01\x02\x00\x00\x00\x01\x65\x03\x04\x05")
    ]
    payloads = [_decrypt_lite(pkt)[1] for pkt in conn.sent]
    assert payloads[0] == bytes(b ^ 0xFF for b in nals[0])
    assert payloads[1] == bytes(b ^ 0xFF for b in nals[1]) + b"TRAILER\xfa\xfa"


def test_send_frame_dave_passthrough_when_not_ready(make_player):
    class Dave:
        def encrypt_h264(self, ssrc, annex_b):
            return annex_b  # same object → passthrough

    p, conn = make_player(conn=_FakeConn(dave=Dave()))
    p._send_frame([b"\x65\x01"])
    assert _decrypt_lite(conn.sent[0])[1] == b"\x65\x01"


def test_send_frame_dave_without_h264_support_is_ignored(make_player):
    p, conn = make_player(conn=_FakeConn(dave=object()))
    p._send_frame([b"\x65\x01"])
    assert _decrypt_lite(conn.sent[0])[1] == b"\x65\x01"


def test_send_frame_pacing_waits_between_packets(make_player, monkeypatch):
    monkeypatch.setenv("STREAM_PACKET_PACE", "0.5")
    p, conn = make_player()
    p._send_frame([b"\x65\x01", b"\x65\x02", b"\x65\x03", b"\x65\x04"])
    assert len(conn.sent) == 4
    assert len(p._end.waits) >= 1
    assert all(0 < w <= 0.5 / 25 for w in p._end.waits)


def test_send_frame_pacing_stops_mid_frame(make_player, monkeypatch):
    monkeypatch.setenv("STREAM_PACKET_PACE", "0.9")
    p, conn = make_player()
    p._end.set()
    p._send_frame([b"\x65\x01", b"\x65\x02", b"\x65\x03"])
    assert len(conn.sent) == 1  # broke out after the first paced wait
    assert p._ts == p._ts_inc


# ── _stream ──────────────────────────────────────────────────────────────────

_SC = b"\x00\x00\x00\x01"
_AUD = b"\x09\xf0"
_SPS = _make_sps()
_PPS = b"\x68\xce\x38\x80"
_SEI = b"\x06\x05\x01\x80"
_IDR = b"\x65\x88\x84\x00\x10"
_P1 = b"\x41\x9a\x02\x03"
_P2 = b"\x41\x9a\x04\x05"
_FILLER = b"\x0c\xff\xff"


def _annexb(*nals: bytes) -> bytes:
    return b"".join(_SC + n for n in nals)


def _frames(conn) -> list[list[bytes]]:
    """Group sent packets into frames by marker bit; return payloads."""
    frames, cur = [], []
    for pkt in conn.sent:
        hdr, payload = _decrypt_lite(pkt)
        cur.append(payload)
        if hdr[1] & 0x80:
            frames.append(cur)
            cur = []
    assert cur == []
    return frames


@pytest.fixture
def popen(monkeypatch):
    holder = {}

    def install(proc):
        def fake_popen(cmd, **kw):
            holder["cmd"] = cmd
            holder["kw"] = kw
            return proc

        monkeypatch.setattr(vpmod.subprocess, "Popen", fake_popen)
        return holder

    return install


@pytest.mark.parametrize("step", [65_536, 3])
def test_stream_groups_nals_into_frames(make_player, popen, step):
    data = (
        _annexb(_AUD, _SPS, _PPS, _SEI, _IDR, _FILLER, _AUD, _P1, _AUD, _P2, _AUD) + _SC
    )
    proc = _FakeProc(stdout=_ChunkReader(data, step))
    h = popen(proc)
    p, conn = make_player()
    p._stream()
    assert h["cmd"][0] == "ffmpeg"
    assert h["kw"]["stdout"] == subprocess.PIPE
    frames = _frames(conn)
    assert frames == [[rewrite_sps_vui(_SPS), _PPS, _SEI, _IDR], [_P1], [_P2]]
    assert p._frames_emitted == 3
    assert p.first_frame_sent.is_set()
    # pacing used the (non-blocking) stop event, never time.sleep
    assert p._end.waits


def test_stream_slice_without_aud_starts_new_frame(make_player, popen):
    data = _annexb(_IDR, _P1, _P2)
    popen(_FakeProc(stdout=data))
    p, conn = make_player()
    p._stream()
    # Trailing NAL left in the buffer is flushed into the *current* frame
    # (no slice-boundary check on the tail), so P1+P2 share one timestamp.
    assert _frames(conn) == [[_IDR], [_P1, _P2]]


def test_stream_trailing_sps_is_rewritten(make_player, popen):
    popen(_FakeProc(stdout=_annexb(_AUD, _SPS)))
    p, conn = make_player()
    p._stream()
    assert _frames(conn) == [[rewrite_sps_vui(_SPS)]]


def test_stream_empty_output_emits_nothing(make_player, popen):
    popen(_FakeProc(stdout=b""))
    p, conn = make_player()
    p._stream()
    assert conn.sent == [] and p._frames_emitted == 0
    assert not p.first_frame_sent.is_set()


def test_stream_stop_requested_before_popen_returns(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD), running=True)
    popen(proc)
    p, conn = make_player()
    p._end.set()
    p._stream()
    assert proc.terminated == 1
    assert conn.sent == []


def test_stream_stop_mid_stream_sends_nothing_more(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD, _P2, _AUD), running=True)
    popen(proc)
    p, conn = make_player()
    conn.on_send = lambda pkt: p._end.set()
    p._stream()
    assert len(conn.sent) == 1


def test_stream_stop_mid_stream_kills_proc(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD, _P2, _AUD), running=True)
    popen(proc)
    p, conn = make_player()
    conn.on_send = lambda pkt: p._end.set()
    p._stream()
    assert _frames(conn)[0] == [_IDR]
    assert proc.terminated == 1


def test_stream_stop_on_slice_boundary(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _P1, _P2), running=True)
    popen(proc)
    p, conn = make_player()
    conn.on_send = lambda pkt: p._end.set()
    p._stream()
    assert _frames(conn)[0] == [_IDR]
    assert proc.terminated == 1


def test_stream_kills_proc_that_wont_exit(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR), wait_timeout=True)
    popen(proc)
    p, _ = make_player()
    p._stream()
    assert proc.killed == 1


def test_stream_registers_video_ssrc_with_dave(make_player, popen):
    class Dave:
        ready = True

        def __init__(self):
            self.ssrcs = []

        def register_video_ssrc(self, ssrc):
            self.ssrcs.append(ssrc)

    dave = Dave()
    popen(_FakeProc(stdout=b""))
    p, _ = make_player(conn=_FakeConn(dave=dave))
    p._stream()
    assert dave.ssrcs == [1001]


def test_stream_dave_register_failure_is_tolerated(make_player, popen):
    class Dave:
        def register_video_ssrc(self, ssrc):
            raise RuntimeError("nope")

    popen(_FakeProc(stdout=_annexb(_IDR)))
    p, conn = make_player(conn=_FakeConn(dave=Dave()))
    p._stream()
    assert len(conn.sent) == 1


def test_stream_logs_redacted_url(make_player, popen, caplog):
    popen(_FakeProc(stdout=b""))
    p, _ = make_player(url="http://user:secret@host:8080/live.ts")
    with caplog.at_level("INFO", logger="streaming.video_player"):
        p._stream()
    text = caplog.text
    assert "secret" not in text
    assert "http://host:8080/live.ts" in text


def test_stream_flushes_stats_periodically(make_player, popen, monkeypatch, caplog):
    monkeypatch.setattr(vpmod, "_STATS_INTERVAL", 0.0)
    popen(_FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD)))
    p, _ = make_player(fps=1e6)  # every frame is "late"
    with caplog.at_level("INFO", logger="streaming.video_player"):
        p._stream()
    assert "video stats:" in caplog.text


# ── run(): software fallback ─────────────────────────────────────────────────


@pytest.fixture
def hw_player(no_fifo, monkeypatch):
    monkeypatch.setattr(vpmod, "_ENCODER", _HW)
    monkeypatch.setattr(vpmod, "_SW_ENCODER", _SW)
    p = H264VideoPlayer("http://h/x", _FakeVC(_FakeConn()), fps=25)
    p._end = _FastEvent()
    return p


def test_run_falls_back_to_software_when_hw_emits_nothing(hw_player, monkeypatch):
    p = hw_player
    used = []

    def fake_stream():
        used.append(p._enc)
        p._proc = _FakeProc()
        p._frames_emitted = 0 if len(used) == 1 else 5

    p._stream = fake_stream
    p.run()
    assert used == [_HW, _SW]
    assert p._enc is _SW


def test_run_no_fallback_when_frames_emitted(hw_player):
    p = hw_player
    used = []

    def fake_stream():
        used.append(p._enc)
        p._frames_emitted = 1

    p._stream = fake_stream
    p.run()
    assert used == [_HW]


def test_run_no_fallback_when_stopped(hw_player):
    p = hw_player
    used = []

    def fake_stream():
        used.append(p._enc)
        p._end.set()

    p._stream = fake_stream
    p.run()
    assert used == [_HW]


def test_run_no_fallback_when_already_software(no_fifo, monkeypatch):
    monkeypatch.setattr(vpmod, "_ENCODER", _SW)
    monkeypatch.setattr(vpmod, "_SW_ENCODER", _SW)
    p = H264VideoPlayer("http://h/x", _FakeVC(_FakeConn()), fps=25)
    used = []
    p._stream = lambda: used.append(p._enc)
    p.run()
    assert used == [_SW]


def test_run_swallows_errors_and_cleans_up(no_fifo, monkeypatch, caplog):
    monkeypatch.setattr(vpmod, "_ENCODER", _SW)
    cleaned = []
    monkeypatch.setattr(
        H264VideoPlayer, "_cleanup_fifo", lambda self: cleaned.append(1)
    )
    p = H264VideoPlayer("http://h/x", _FakeVC(_FakeConn()), fps=25)
    proc = _FakeProc(running=True)

    def boom():
        p._proc = proc
        raise RuntimeError("kaboom")

    p._stream = boom
    with caplog.at_level("ERROR", logger="streaming.video_player"):
        p.run()
    assert "H264VideoPlayer error" in caplog.text
    assert proc.terminated == 1
    assert cleaned == [1]


def test_thread_start_join_end_to_end(make_player, popen):
    popen(_FakeProc(stdout=_annexb(_AUD, _SPS, _PPS, _IDR, _AUD, _P1, _AUD)))
    p, conn = make_player()
    p.start()
    p.join(timeout=5)
    assert not p.is_alive()
    assert len(_frames(conn)) == 2


# ── start offset / pause / seek ──────────────────────────────────────────────


class _Gate(threading.Event):
    """Pause gate whose wait() never blocks; it re-opens after ``opens_after``
    waits and runs ``on_wait`` (if any) on each wait."""

    def __init__(self, opens_after: int = 2, on_wait=None) -> None:
        super().__init__()
        self.waits = 0
        self.opens_after = opens_after
        self.on_wait = on_wait

    def wait(self, timeout=None):
        self.waits += 1
        if self.on_wait:
            self.on_wait()
        if self.waits >= self.opens_after:
            self.set()
        return self.is_set()


def test_ffmpeg_cmd_start_offset_seeks_before_input(make_player):
    p, _ = make_player(url="https://h/x.m3u8", start_offset=90.5)
    cmd = p._ffmpeg_cmd()
    assert _opt(cmd, "-ss") == "90.500"
    assert cmd.index("-ss") < cmd.index("-i")


@pytest.mark.parametrize("offset", [0.0, -5.0])
def test_ffmpeg_cmd_no_seek_at_start(make_player, offset):
    p, _ = make_player(start_offset=offset)
    assert "-ss" not in p._ffmpeg_cmd()
    assert p.position == 0.0


def test_pause_resume_and_position(make_player):
    p, _ = make_player(fps=25.0, start_offset=60.0)
    assert not p.paused and p.pause_gate.is_set()
    p.pause()
    assert p.paused and not p.pause_gate.is_set()
    p.resume()
    assert not p.paused
    p._frames_emitted = 50
    assert p.position == pytest.approx(62.0)


def test_stop_requested_reflects_stop(make_player):
    p, _ = make_player()
    assert not p.stop_requested
    p.stop()
    assert p.stop_requested


def test_seek_terminates_running_proc_and_clamps(make_player):
    p, _ = make_player()
    p.seek(-3)
    assert p._seek_to == 0.0  # no proc yet: nothing to terminate
    proc = _FakeProc(running=True)
    p._proc = proc
    p.seek(42.0)
    assert p._seek_to == 42.0 and proc.terminated == 1
    done = _FakeProc(running=False)
    p._proc = done
    p.seek(1.0)
    assert done.terminated == 0


def test_run_restarts_stream_on_seek(hw_player):
    p = hw_player
    offsets = []
    first_proc = _FakeProc(running=True)

    def fake_stream():
        offsets.append(p._offset)
        if len(offsets) == 1:
            p._proc = first_proc
            p._frames_emitted = 10
            p._seek_to = 30.0
        else:
            assert p._proc is None and p._seek_to is None
            p._frames_emitted = 3

    p._stream = fake_stream
    p.run()
    assert offsets == [0.0, 30.0]
    assert first_proc.terminated == 1  # killed before the restart
    assert p._enc is _HW  # a seek restart is not an encoder fallback
    assert p.position == pytest.approx(30.0 + 3 / 25)


def test_run_seek_restart_without_proc(hw_player):
    p = hw_player
    calls = []

    def fake_stream():
        calls.append(p._offset)
        if len(calls) == 1:
            p._proc = None
            p._seek_to = 5.0
        p._frames_emitted = 1

    p._stream = fake_stream
    p.run()
    assert calls == [0.0, 5.0]


def test_stream_pause_mid_stream_holds_then_resumes(make_player, popen):
    popen(_FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD, _P2, _AUD) + _SC))
    p, conn = make_player()
    gate = _Gate(opens_after=3)
    gate.set()
    p.pause_gate = gate
    sent_at_pause = []

    def on_send(pkt):
        if len(conn.sent) == 1:
            p.pause()  # pause after the first frame → _t0 already set
            sent_at_pause.append(len(conn.sent))

    conn.on_send = on_send
    p._stream()
    assert sent_at_pause == [1]
    assert gate.waits == 3  # held until the gate re-opened
    assert _frames(conn) == [[_IDR], [_P1], [_P2]]


def test_stream_paused_before_first_frame(make_player, popen):
    popen(_FakeProc(stdout=_annexb(_IDR, _AUD) + _SC))
    p, conn = make_player()
    p.pause_gate = _Gate(opens_after=2)  # starts cleared (paused)
    p._stream()
    assert _frames(conn) == [[_IDR]]
    assert p.pause_gate.waits == 2


def test_stream_stop_while_paused(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD), running=True)
    popen(proc)
    p, conn = make_player()
    p.pause_gate = _Gate(opens_after=99, on_wait=p._end.set)
    p._stream()
    assert conn.sent == []
    assert proc.terminated == 1


def test_stream_seek_while_paused_bails_out(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD), running=True)
    popen(proc)
    p, conn = make_player()

    def request_seek():
        p._seek_to = 12.0

    p.pause_gate = _Gate(opens_after=99, on_wait=request_seek)
    p._stream()
    assert conn.sent == []
    assert proc.terminated == 1  # FFmpeg reaped so run() can restart it
    assert p._seek_to == 12.0  # left for run() to consume


def test_stream_seek_pending_skips_frames_and_tail_flush(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD, _P2), running=True)
    popen(proc)
    p, conn = make_player()

    def on_send(pkt):
        p._seek_to = 7.0

    conn.on_send = on_send
    p._stream()
    assert _frames(conn) == [[_IDR]]  # P1 and the trailing P2 never sent
    assert proc.terminated == 1


def test_position_reports_pending_seek_target(make_player):
    p, _ = make_player(fps=25.0, start_offset=10.0)
    p._frames_emitted = 250
    assert p.position == pytest.approx(20.0)
    p.seek(300.0)  # restart not processed by run() yet
    assert p.position == 300.0


# ── seek / natural-end races ─────────────────────────────────────────────────


def test_seek_ignored_once_finished(make_player):
    p, _ = make_player()
    proc = _FakeProc(running=True)
    p._proc = proc
    p._finished = True
    p.seek(30.0)
    assert p._seek_to is None and proc.terminated == 0


def test_run_marks_finished_so_later_seeks_are_ignored(hw_player):
    p = hw_player

    def fake_stream():
        p._frames_emitted = 1

    p._stream = fake_stream
    p.run()
    assert p._finished and p._seek_to is None
    p.seek(10.0)
    assert p._seek_to is None


def test_run_honours_seek_that_lands_at_natural_end(hw_player):
    p = hw_player
    offsets = []

    def fake_stream():
        offsets.append(p._offset)
        p._frames_emitted = 1

    real_apply = p._apply_pending_seek
    calls = []

    def apply_then_race():
        applied = real_apply()
        calls.append(applied)
        if len(calls) == 1:
            p.seek(20.0)  # arrives just after the pending-seek check
        return applied

    p._stream = fake_stream
    p._apply_pending_seek = apply_then_race
    p.run()
    assert offsets == [0.0, 20.0]
    assert p._finished and p._seek_to is None


def test_run_finally_clears_pending_seek_on_error(hw_player, caplog):
    p = hw_player

    def boom():
        p._seek_to = 5.0
        raise RuntimeError("kaboom")

    p._stream = boom
    with caplog.at_level("ERROR", logger="streaming.video_player"):
        p.run()
    assert p._finished and p._seek_to is None


def test_stream_seek_pending_at_popen_kills_fresh_ffmpeg(make_player, popen):
    proc = _FakeProc(stdout=_annexb(_IDR, _AUD, _P1, _AUD), running=True)
    popen(proc)
    p, conn = make_player()
    p._seek_to = 40.0
    p._stream()
    assert proc.terminated == 1
    assert conn.sent == []
    assert p._seek_to == 40.0  # left for run() to apply


def test_redact_url_strips_jellyfin_apikey_variants():
    url = (
        "http://jellyfin:8096/videos/x/main.m3u8?DeviceId=d&ApiKey=SECRET1"
        "&PlaySessionId=p&api_key=SECRET1&X-Emby-Token=SECRET2"
    )
    out = vpmod.redact_url(url)
    assert "SECRET" not in out
    assert "DeviceId=d" in out and "PlaySessionId=p" in out


@pytest.mark.parametrize("name", ["h264_vaapi", "h264_nvenc"])
def test_hardware_encoders_disable_b_frames(monkeypatch, name):
    """B-frames are sent in decode order with evenly spaced RTP timestamps,
    which makes receivers judder — hardware encoders must emit none."""
    monkeypatch.setattr(vpmod.subprocess, "run", lambda *a, **k: _Run(stdout=name))
    monkeypatch.setattr(vpmod, "_test_encoder", lambda n, pre, vf="": n == name)
    enc = vpmod._detect_encoder()
    assert enc is not None and enc.name == name
    i = enc.post_codec.index("-bf")
    assert enc.post_codec[i + 1] == "0"



def test_fit_scale_uses_requested_scaler():
    vf = vpmod._fit_scale("960:540", "scale_vaapi")
    assert vf.startswith("scale_vaapi=w='if(gt(dar,960/540),960,")
    assert vf.endswith(",setsar=1")


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
@pytest.mark.parametrize(
    "src,sar,expected",
    [
        ("1920x1080", "1", (960, 540)),  # 16:9 fills the box
        ("1440x1080", "1", (720, 540)),  # 4:3 → pillarbox-sized, not stretched
        ("1920x800", "1", (960, 400)),  # 2.4:1 → letterbox-sized
        ("720x480", "32/27", (960, 540)),  # anamorphic 16:9 DVD
    ],
)
def test_fit_scale_preserves_aspect_ratio_with_real_ffmpeg(
    tmp_path, src, sar, expected
):
    out = tmp_path / "f.png"
    subprocess.run(
        [
            "ffmpeg", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", f"testsrc=size={src}:duration=0.1,setsar={sar}",
            "-vf", vpmod._fit_scale("960:540"), "-frames:v", "1", "-y", str(out),
        ],
        check=True,
    )
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "stream=width,height",
         "-of", "csv=p=0", str(out)],
        check=True, capture_output=True, text=True,
    )
    assert tuple(int(x) for x in probe.stdout.strip().split(",")) == expected



def test_with_aspect_none_keeps_source_ratio():
    vf = vpmod._fit_scale("960:540")
    assert vpmod._with_aspect(vf, None) == vf


def test_with_aspect_substitutes_forced_ratio():
    vf = vpmod._with_aspect(vpmod._fit_scale("960:540", "scale_vaapi"), 4 / 3)
    assert "dar" not in vf and "1.3333333333" in vf
    assert vf.startswith("scale_vaapi=")


def test_ffmpeg_cmd_uses_forced_aspect(make_player):
    import dataclasses

    p, _ = make_player(aspect=16 / 9)
    p._enc = dataclasses.replace(p._enc, vf=vpmod._fit_scale("960:540"))
    cmd = p._ffmpeg_cmd()
    vf = cmd[cmd.index("-vf") + 1]
    assert "1.7777777778" in vf and "dar" not in vf


def test_set_aspect_restarts_in_place_at_current_position(make_player):
    p, _ = make_player()
    assert p.aspect is None
    p._frames_emitted = 250  # 10 s at 25 fps
    p.set_aspect(2.39)
    assert p.aspect == 2.39
    assert p._seek_to == pytest.approx(10.0)


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
@pytest.mark.parametrize(
    "src,aspect,expected",
    [
        ("1440x1080", 16 / 9, (960, 540)),  # 4:3 stretched to fill 16:9
        ("1920x1080", 4 / 3, (720, 540)),  # 16:9 squeezed to 4:3
        ("1920x1080", 2.39, (960, 402)),  # stretched to scope
    ],
)
def test_forced_aspect_with_real_ffmpeg(tmp_path, src, aspect, expected):
    out = tmp_path / "f.png"
    vf = vpmod._with_aspect(vpmod._fit_scale("960:540"), aspect)
    subprocess.run(
        [
            "ffmpeg", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", f"testsrc=size={src}:duration=0.1",
            "-vf", vf, "-frames:v", "1", "-y", str(out),
        ],
        check=True,
    )
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "stream=width,height",
         "-of", "csv=p=0", str(out)],
        check=True, capture_output=True, text=True,
    )
    assert tuple(int(x) for x in probe.stdout.strip().split(",")) == expected


def test_restart_seq_and_segment_offset_track_seeks(make_player):
    p, _ = make_player(start_offset=5.0)
    assert (p.restart_seq, p.segment_offset) == (0, 5.0)
    p._seek_to = 42.0
    assert p._apply_pending_seek() is True
    assert (p.restart_seq, p.segment_offset) == (1, 42.0)
    assert p._apply_pending_seek() is False
    assert p.restart_seq == 1
