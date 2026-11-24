"""Record a short 25 fps clip of a moving scene for the denoiser / interpolator tests.

Written by Claude (Anthropic), 2026-11-23. Reuses the camera access, claim file and log from
experiments/noise/noise_capture.py. Waits (up to --wait-min) until the plate is off (scene spread
p99 - p1 above 3 K), then records --seconds of temperature frames. Sends no camera commands: the
auto shutter stays as it is (off since 2026-11-21).

python -I record_motion.py [--seconds 60] [--wait-min 10]
-> runs/motion-<stamp>/frames.u16 (uint16 [n, 192*256], 1/64 K), times.f8 (monotonic s), events.jsonl
"""
from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "noise"))
import noise_capture as nc  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=60.0)
    ap.add_argument("--wait-min", type=float, default=10.0)
    a = ap.parse_args()
    out = HERE / "runs" / time.strftime("motion-%Y%m%d-%H%M%S")
    out.mkdir(parents=True)
    log = nc.Log(out)
    claim = nc.ClaimFile(enabled=True)
    src = nc.RealSource(log)
    try:
        if claim.labbench() is not None:
            log.ev("labbench_busy")
            return 2
        claim.beat(holding=False)
        t_wait = time.monotonic()
        while claim.lower_holding() is not None and time.monotonic() - t_wait < 20:
            claim.beat(holding=False)
            time.sleep(0.5)
        src.open()
        src.start()
        claim.beat(holding=True)
        log.ev("camera_open")
        # wait for the plate to come off
        deadline = time.monotonic() + a.wait_min * 60
        beat = 0.0
        while True:
            temps, _ = src.frame()
            if time.monotonic() > beat:
                beat = time.monotonic() + 1
                claim.beat(holding=True)
                c = temps.astype(np.float32) / 64 - 273.15
                lo, hi = np.percentile(c, [1, 99])
                log.ev("scene", p1_c=round(float(lo), 2), p99_c=round(float(hi), 2))
                if hi - lo > 3:
                    break
                if time.monotonic() > deadline:
                    log.ev("gave_up", why="plate still on")
                    return 3
        log.ev("recording", seconds=a.seconds)
        frames, times = [], []
        t_end = time.monotonic() + a.seconds
        prev = None
        repeats = 0
        while time.monotonic() < t_end:
            temps, _ = src.frame()
            now = time.monotonic()
            if now > beat:
                beat = now + 1
                claim.beat(holding=True)
            if prev is not None and np.array_equal(temps, prev):
                repeats += 1
                continue
            prev = temps
            frames.append(temps)
            times.append(now)
        np.asarray(frames, "<u2").tofile(out / "frames.u16")
        np.asarray(times, "<f8").tofile(out / "times.f8")
        log.ev("done", frames=len(frames), repeats_skipped=repeats,
               fps=round(len(frames) / (times[-1] - times[0]), 2) if len(times) > 1 else 0)
        return 0
    finally:
        try:
            src.close()
        finally:
            claim.remove()


if __name__ == "__main__":
    sys.exit(main())
