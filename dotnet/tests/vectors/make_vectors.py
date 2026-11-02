"""Generate byte-exact test vectors for the .NET port from the Python bot.

Run from the repository root:  python dotnet/tests/vectors/make_vectors.py
Writes dotnet/tests/Slopsoil.Tests/Vectors/vectors.json. Re-run (and commit the
result) whenever the Python reference behaviour changes on purpose.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile

sys.path.insert(0, os.getcwd())

from streaming import video_player as vp  # noqa: E402

OUT = "dotnet/tests/Slopsoil.Tests/Vectors/vectors.json"
_START = re.compile(rb"\x00\x00\x00?\x01")


def _sps_from_ffmpeg(args: list[str]) -> bytes:
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, "o.h264")
        subprocess.run(
            ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
             "-i", "testsrc=size=320x240:rate=30:duration=0.2", *args,
             "-frames:v", "2", "-f", "h264", "-y", out],
            check=True,
        )
        data = open(out, "rb").read()
    for nal in _START.split(data):
        nal = nal.rstrip(b"\x00")
        if nal and nal[0] & 0x1F == 7:
            return nal
    raise RuntimeError(f"no SPS for {args}")


def _synthetic_sps() -> list[dict]:
    """Hand-built SPS for branches x264 never emits (POC type 1, no VUI)."""
    out = []
    for name, poc, vui in [("synthetic_poc1_no_vui", 1, False), ("synthetic_poc0_no_vui", 0, False)]:
        w = vp._BW()
        w.u(8, 66)   # baseline profile
        w.u(8, 0)
        w.u(8, 30)
        w.ue(0)      # sps id
        w.ue(0)      # log2_max_frame_num_minus4
        w.ue(poc)
        if poc == 0:
            w.ue(2)
        else:
            w.u(1, 0)
            w.se(-3)
            w.se(2)
            w.ue(2)
            w.se(1)
            w.se(-1)
        w.ue(1)      # max_num_ref_frames
        w.u(1, 0)
        w.ue(19)     # width mbs - 1
        w.ue(14)     # height map units - 1
        w.u(1, 1)    # frame_mbs_only
        w.u(1, 1)    # direct_8x8
        w.u(1, 0)    # no cropping
        w.u(1, 1 if vui else 0)
        nal = bytes([0x67]) + vp._ep_add(w.to_bytes())
        out.append({"name": name, "in": nal.hex(), "out": vp.rewrite_sps_vui(nal).hex()})

    # High profile with an SPS scaling matrix and a VUI carrying video signal
    # type + colour description, VCL HRD and no bitstream restriction.
    w = vp._BW()
    w.u(8, 100)
    w.u(8, 0)
    w.u(8, 40)
    w.ue(0)
    w.ue(1)      # chroma_format_idc 4:2:0
    w.ue(0)
    w.ue(0)
    w.u(1, 0)
    w.u(1, 1)    # seq_scaling_matrix_present
    for i in range(8):
        present = 1 if i in (0, 6) else 0
        w.u(1, present)
        if present:
            for _ in range(16 if i < 6 else 64):
                w.se(1)
    w.ue(0)
    w.ue(2)      # poc type 2
    w.ue(3)
    w.u(1, 0)
    w.ue(19)
    w.ue(14)
    w.u(1, 1)
    w.u(1, 1)
    w.u(1, 0)
    w.u(1, 1)    # vui present
    w.u(1, 0)    # aspect
    w.u(1, 0)    # overscan
    w.u(1, 1)    # video_signal_type_present
    w.u(3, 5)
    w.u(1, 1)
    w.u(1, 1)    # colour description
    w.u(8, 1)
    w.u(8, 1)
    w.u(8, 1)
    w.u(1, 0)    # chroma loc
    w.u(1, 0)    # timing
    w.u(1, 0)    # nal hrd
    w.u(1, 1)    # vcl hrd
    w.ue(0)
    w.u(4, 1)
    w.u(4, 2)
    w.ue(100)
    w.ue(200)
    w.u(1, 1)
    for _ in range(4):
        w.u(5, 23)
    w.u(1, 0)    # low_delay
    w.u(1, 0)    # pic_struct
    w.u(1, 0)    # no bitstream restriction
    nal = bytes([0x67]) + vp._ep_add(w.to_bytes())
    out.append({"name": "synthetic_full_vui", "in": nal.hex(), "out": vp.rewrite_sps_vui(nal).hex()})

    # Exp-Golomb with >31 leading zeros: Python passes the SPS through unchanged.
    bad = bytes([0x67, 0x42, 0x00, 0x1E, 0x00, 0x00, 0x00, 0x00, 0x01])
    out.append({"name": "synthetic_bad_golomb", "in": bad.hex(), "out": vp.rewrite_sps_vui(bad).hex()})
    return out


def main() -> None:
    yuv = ["-pix_fmt", "yuv420p"]
    sps_cases = {
        "x264_high": ["-c:v", "libx264", "-profile:v", "high", *yuv],
        "x264_baseline": ["-c:v", "libx264", "-profile:v", "baseline", *yuv],
        "x264_main_bframes": ["-c:v", "libx264", "-profile:v", "main", "-bf", "2", *yuv],
        "x264_hrd_fullrange": ["-c:v", "libx264", "-x264-params",
                               "nal-hrd=cbr:force-cfr=1", "-b:v", "500k",
                               "-maxrate", "500k", "-bufsize", "500k",
                               "-color_range", "pc", *yuv],
        "x264_444": ["-c:v", "libx264", "-profile:v", "high444", "-pix_fmt", "yuv444p"],
        "x264_sar_overscan_chromaloc": ["-c:v", "libx264", "-x264-params",
                                        "sar=4/3:overscan=show:chromaloc=1", *yuv],
        "x264_extended_sar": ["-c:v", "libx264", "-x264-params", "sar=13/7", *yuv],
        "x264_interlaced_cropped": ["-c:v", "libx264", "-x264-params", "interlaced=1",
                                    "-vf", "scale=330:244", *yuv],
        "x264_cqm_jvt": ["-c:v", "libx264", "-x264-params", "cqm=jvt", *yuv],
    }
    sps = []
    for name, args in sps_cases.items():
        src = _sps_from_ffmpeg(args)
        sps.append({"name": name, "in": src.hex(), "out": vp.rewrite_sps_vui(src).hex()})

    sps.extend(_synthetic_sps())

    rtp = [
        {"seq": s, "ts": t, "ssrc": c, "marker": m,
         "out": bytes(vp._rtp_header(s, t, c, m)).hex()}
        for s, t, c, m in [(0, 0, 1, False), (65535, 0xFFFFFFFF, 0xDEADBEEF, True),
                           (70000, 2**33 + 5, 2**32 + 7, False)]
    ]

    nals = {
        "small": bytes([0x65]) + bytes(range(256)) * 2,
        "exact": bytes([0x41]) + bytes(1187),
        "large": bytes([0x65]) + bytes(i % 251 for i in range(5000)),
    }
    fua = [{"name": k, "in": v.hex(), "out": [p.hex() for p in vp._packetize_nal(v)]}
           for k, v in nals.items()]

    key = list(range(32))
    hdr = bytes(vp._rtp_header(7, 3000, 0x1234, True))
    payload = b"hello discord video payload"
    crypto = []
    for mode in ("aead_xchacha20_poly1305_rtpsize", "xsalsa20_poly1305_lite",
                 "xsalsa20_poly1305"):
        for start in (0, vp._VIDEO_NONCE_BASE):
            counter = [start]
            out = vp._encrypt(hdr, payload, mode, key, counter)
            crypto.append({"mode": mode, "nonce": start, "header": hdr.hex(),
                           "payload": payload.hex(), "key": bytes(key).hex(),
                           "out": out.hex(), "next_nonce": counter[0]})

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump({"sps": sps, "rtp": rtp, "fua": fua, "crypto": crypto}, f, indent=1)
    print(f"wrote {OUT}: {len(sps)} sps, {len(rtp)} rtp, {len(fua)} fua, {len(crypto)} crypto")


if __name__ == "__main__":
    main()
