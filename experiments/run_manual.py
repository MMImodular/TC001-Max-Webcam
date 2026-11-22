"""Manual shutter while the auto shutter is off (C4 in LOG.md), then restore the default.

usage: run_manual.py <seconds_before> <seconds_after_on> <out.jsonl>
  stream with the auto shutter as found, send the P3 manual shutter command once, keep streaming
  <seconds_before>; then SetAutoFFCStatus(1) (the default), read it back and stream
  <seconds_after_on> to see the automatic shutter return. Always tries to restore on exit.
"""

import sys
import time

from cam import COMMANDS, Cam, log, monitor, vdcmd

COMMANDS["shutter"] = (vdcmd((0x43, 0x36, 0x01)), 0, "P3_PROTOCOL.md 'shutter'")
assert COMMANDS["shutter"][0].hex() == "01364300000000000000000000000000cd0b"

before, after, out = float(sys.argv[1]), float(sys.argv[2]), sys.argv[3]

cam = Cam()
restored = False
try:
    log(f"get_auto_ffc_status = {cam.send('get_auto_ffc_status').hex()}")
    cam.start()
    time.sleep(0.2)
    monitor(cam, 10, out, "pre_manual")
    cam.send("shutter")
    monitor(cam, before, out, "after_manual")
    cam.send("set_auto_ffc_on")
    st = cam.send("get_auto_ffc_status")
    restored = st == b"\x01"
    log(f"restored: {restored}")
    monitor(cam, after, out, "on")
    log(f"get_auto_ffc_status = {cam.send('get_auto_ffc_status').hex()}")
finally:
    if not restored:
        try:
            cam.send("set_auto_ffc_on")
            log(f"restore in finally: {cam.send('get_auto_ffc_status').hex()}")
        except Exception as exc:  # noqa: BLE001
            log(f"RESTORE FAILED: {exc}")
    cam.close()
