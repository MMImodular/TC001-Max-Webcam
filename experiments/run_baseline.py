"""Step 1: identity reads (documented P3 register reads) + baseline stream with the default
start sequence, logging every stall / metadata change / debug-log line. See LOG.md."""

import sys
import time

from cam import Cam, log, monitor

seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 330
out = sys.argv[2] if len(sys.argv) > 2 else "runs/baseline.jsonl"

cam = Cam()
try:
    for name in ("read_model", "read_fw", "read_part", "read_hw", "read_model_long"):
        r = cam.send(name)
        log(f"{name}: {r.split(bytes(1))[0]!r}")
    dl = cam.debug_log(128)
    log(f"debug log (128 B) hex: {dl.hex()}")
    log(f"debug log text: {dl[64:].split(bytes(1))[0]!r}")
    cam.start()
    time.sleep(0.2)
    monitor(cam, seconds, out, "baseline")
finally:
    cam.close()
