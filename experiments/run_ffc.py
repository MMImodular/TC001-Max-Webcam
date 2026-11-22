"""Auto-FFC experiment steps (see LOG.md, candidates R1-R3 and W1).

usage: run_ffc.py <step> <seconds> <out.jsonl>
  reads   : FFC status reads only (R1-R3), then stream and monitor
  off     : reads, SetAutoFFCStatus(0) (W1 off), read back, stream and monitor; leaves it off
  on      : reads, SetAutoFFCStatus(1) (W1 on, the default), read back, stream and monitor
"""

import sys
import time

from cam import Cam, log, monitor

step, seconds, out = sys.argv[1], float(sys.argv[2]), sys.argv[3]
assert step in ("reads", "off", "on")


def reads(cam: Cam) -> None:
    for name in ("get_auto_ffc_status", "get_auto_ffc_params", "get_shutter_status"):
        log(f"{name} = {cam.send(name).hex()}")


cam = Cam()
try:
    reads(cam)
    cam.start()
    time.sleep(0.2)
    if step == "off":   # while streaming, as the vendor app's toggle would
        cam.send("set_auto_ffc_off")
        reads(cam)
    elif step == "on":
        cam.send("set_auto_ffc_on")
        reads(cam)
    monitor(cam, seconds, out, step)
    reads(cam)
finally:
    cam.close()
