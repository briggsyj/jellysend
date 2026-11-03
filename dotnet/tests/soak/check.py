#!/usr/bin/env python3
"""Check a soak CSV (SLOPSOIL_SOAK=1) against SC-008 and SC-009. Usage: check.py slopsoil-soak.csv"""

import csv
import sys

rows = list(csv.DictReader(open(sys.argv[1], newline="")))
steady = rows[1:]  # skip the first window (start-up / settling)
if not steady:
    sys.exit("not enough rows (need > 1 window)")

frames = sum(int(r["frames"]) for r in steady)
late = sum(int(r["late_frames"]) for r in steady)
on_time = 100.0 * (frames - late) / frames if frames else 0.0
worst_late = max(float(r["max_lateness_ms"]) for r in steady)
worst_stall = max(float(r["read_stall_ms"]) for r in steady)
minutes = sum(float(r["window_s"]) for r in steady) / 60.0
corrections = sum(int(r["silence_steps"]) + int(r["skip_steps"]) for r in steady)
drifts = [abs(float(r["drift_ms"])) for r in steady if r["drift_ms"]]
worst_drift = max(drifts) if drifts else 0.0

checks = [
    ("SC-008 frames on time >= 99.5%", on_time >= 99.5, f"{on_time:.2f}% ({late} late of {frames})"),
    ("SC-008 no freeze > 500 ms", worst_late <= 500 and worst_stall <= 500, f"max lateness {worst_late:.0f} ms, max read stall {worst_stall:.0f} ms"),
    ("SC-009 corrections <= 1/min", corrections <= minutes, f"{corrections} in {minutes:.1f} min"),
    ("SC-009 |drift| <= 40 ms", worst_drift <= 40, f"max |drift| {worst_drift:.0f} ms"),
]
for name, ok, detail in checks:
    print(f"{'PASS' if ok else 'FAIL'}  {name}: {detail}")
sys.exit(0 if all(ok for _, ok, _ in checks) else 1)
