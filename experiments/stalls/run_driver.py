"""Run the labbench TC001 Max driver itself on the real camera and count stalls (2026-11-20).

Use with PYTHONPATH pointing at the labbench worktree under test, e.g.
  $env:PYTHONPATH='C:/Git/Automated_Testing/.claude/worktrees/<agent>/src'
  python run_driver.py sync 300
--transfers / --kib only work with the queued-read driver that was tried and not shipped
(labbench branch tc001-usb-stalls history); the shipped driver reads 16 KiB synchronously.
The driver sends only its documented start sequence; close() releases the camera on every path.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import time

import numpy as np

from labbench.core.clock import RealClock
from labbench.drivers.topdon.tc001max import Tc001Max
from labbench.core.errors import TransportTimeout
from labbench.interfaces.thermal import CameraCalibrating


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("label")
    ap.add_argument("seconds", type=float)
    ap.add_argument("--transfers", type=int, default=None)
    ap.add_argument("--kib", type=int, default=None)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "runs",
                                                  "driver.jsonl"))
    a = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: "
                        "%(message)s")
    opts = {}
    if a.transfers is not None:     # queued-read experiment driver only
        opts = {"usb_transfers": a.transfers, "usb_transfer_kib": a.kib or 256}
    cam = Tc001Max("thermal_camera", clock=RealClock(), **opts)
    frames, paused, medians, seqs = 0, 0, [], []
    errors: list[str] = []
    t0 = time.monotonic()
    cam.connect()
    try:
        st0 = cam.camera_status()
        print("reader:", st0.extra.get("usb_reader"), flush=True)
        t0 = time.monotonic()
        cpu0 = time.process_time()
        while time.monotonic() - t0 < a.seconds:
            try:
                f = cam.read_frame(timeout_s=3.0)
            except CameraCalibrating:
                paused += 1
                continue
            except TransportTimeout as exc:     # reconnecting: the driver retries by itself
                errors.append(f"{time.strftime('%H:%M:%S')} {exc}")
                print("ERROR", errors[-1], flush=True)
                continue
            frames += 1
            seqs.append(f.sequence)
            if frames % 25 == 0:
                medians.append(float(np.median(f.temperature_c)))
        st = cam.camera_status()
        cpu = time.process_time() - cpu0
    finally:
        cam.close()
    dur = time.monotonic() - t0
    summ = {"label": a.label, "summary": True, "start": time.strftime("%Y-%m-%d %H:%M:%S"),
            "seconds": round(dur, 1), "usb_reader": st.extra.get("usb_reader"),
            "frames_returned": frames, "fps_returned": round(frames / dur, 2),
            "camera_fps": st.fps, "mb_per_s": st.extra.get("mb_per_s"),
            "usb_stalls": st.extra.get("usb_stalls"), "stall_clears": st.extra.get("stall_clears"),
            "reconnects": st.extra.get("reconnects"), "soft_restarts": st.extra.get("soft_restarts"),
            "paused_reads": paused, "read_errors": errors, "lost": cam.parser.stats.lost,
            "cpu_percent_of_one_core": round(100 * cpu / dur, 1),
            "rejected": cam.parser.stats.rejected + cam.rejected_frames,
            "median_c_range": [round(min(medians), 2), round(max(medians), 2)] if medians else None}
    with open(a.out, "a", encoding="utf-8") as fh:
        fh.write(json.dumps(summ) + "\n")
    print("SUMMARY", json.dumps(summ), flush=True)


if __name__ == "__main__":
    main()
