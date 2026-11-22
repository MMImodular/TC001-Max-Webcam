"""Experiment helpers for the TC001 Max auto-shutter investigation (2026-11-20).

Not shipped. Origin: written for this project; the command bytes are from jvdillon/p3-ir-camera
P3_PROTOCOL.md (Apache-2.0, Copyright jvdillon and contributors), ``crc16_xmodem`` / ``vdcmd`` and
the auto-FFC command ids are adapted from xaionaro-go/thermalmaster command.go / command_ffc.go
(CC0-1.0), and it imports the author's labbench BlockParser (not public; adapted from WendyOS
PR #2621 parser.go, Apache-2.0, Copyright Wendy Labs). Each command's source is in ``COMMANDS``.

Talks to the camera with PyUSB + libusb (WinUSB bound with Zadig),
the same way as the author's labbench driver: claim interfaces 0 and 1, 18-byte commands over
ep0 (0x41/0x20), busy status 0xC1/0x22, response 0xC1/0x21 (docs/PROTOCOL.md "Commands").

Every command this module can send is listed in ``COMMANDS`` with its source; see
experiments/LOG.md before adding one. ``Cam.close`` always releases the device
(set interface 1 alt 0, release interfaces, dispose_resources).
"""

from __future__ import annotations

import errno
import json
import struct
import sys
import time

import libusb_package
import numpy as np
# The author's proven block finder (pure code, read-only use of the labbench package installed
# in the venv): labbench.drivers.topdon.tc001max_protocol.BlockParser.
from labbench.drivers.topdon.tc001max_protocol import BlockParser
import usb.core
import usb.util

VID, PID = 0x3474, 0x4962
EP_IN = 0x81
READ_BYTES = 16 * 1024

ROW = 512
BLOCK = 386 * ROW
END_ROW = b"\xff\x00" * 256


def crc16_xmodem(data: bytes) -> int:
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) if crc & 0x8000 else (crc << 1)
            crc &= 0xFFFF
    return crc


def vdcmd(cmd_id: tuple[int, int, int], register: int = 0, resp_len: int = 0) -> bytes:
    """18-byte command from a 3-byte VDCMD id (xaionaro-go/thermalmaster command.go BuildVDCMD):
    wire bytes 0-1 = cmd_id[2], cmd_id[1]; bytes 2-3 = param cmd_id[0] (LE); 4-5 register (LE);
    12-13 response length (LE); 16-17 CRC-16/XMODEM of bytes 0-15 (LE)."""
    body = bytes([cmd_id[2], cmd_id[1], cmd_id[0], 0]) + struct.pack("<H", register) + bytes(6)
    body += struct.pack("<H", resp_len) + bytes(2)
    return body + struct.pack("<H", crc16_xmodem(body))


# name -> (bytes, response length, source). Reads first; the only writes are the shutter
# trigger (P3_PROTOCOL.md, not used in this study) and SetAutoFFCStatus 0/1 (LOG.md, candidate W1).
COMMANDS: dict[str, tuple[bytes, int, str]] = {
    "read_model": (bytes.fromhex("0101810001000000000000001e0000004f90"), 30, "P3_PROTOCOL.md"),
    "read_fw": (bytes.fromhex("0101810002000000000000000c0000001f63"), 12, "P3_PROTOCOL.md"),
    "read_part": (bytes.fromhex("01018100060000000000000040000000654f"), 64, "P3_PROTOCOL.md"),
    "read_hw": (bytes.fromhex("010181000a00000000000000400000001959"), 64, "P3_PROTOCOL.md"),
    "read_model_long": (bytes.fromhex("010181000f0000000000000040000000b857"), 64,
                        "P3_PROTOCOL.md"),
    "start_stream": (bytes.fromhex("012f81000000000000000000010000004930"), 1, "P3_PROTOCOL.md"),
    # thermalmaster pkg/thermalmaster/command_ffc.go (VDCMD category 0x0210, AC020 command lib)
    "get_auto_ffc_status": (vdcmd((0x81, 0x02, 0x10), 1, 1), 1, "thermalmaster command_ffc.go"),
    "get_auto_ffc_params": (vdcmd((0x82, 0x02, 0x10), 1, 1), 1, "thermalmaster command_ffc.go"),
    "get_shutter_status": (vdcmd((0x83, 0x02, 0x10), 1, 1), 1, "thermalmaster command_ffc.go"),
    "set_auto_ffc_off": (vdcmd((0x41, 0x02, 0x10), 0, 0), 0, "thermalmaster command_ffc.go + shutter.go"),
    "set_auto_ffc_on": (vdcmd((0x41, 0x02, 0x10), 1, 0), 0, "thermalmaster command_ffc.go + shutter.go"),
}

# self-check against the P3_PROTOCOL.md test vectors (same builder must reproduce them)
assert vdcmd((0x43, 0x36, 0x01)) == bytes.fromhex("01364300000000000000000000000000cd0b")
assert vdcmd((0x81, 0x2F, 0x01), 0, 1) == COMMANDS["start_stream"][0]
assert vdcmd((0x81, 0x01, 0x01), 1, 30) == COMMANDS["read_model"][0]


def log(msg: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


class Cam:
    def __init__(self) -> None:
        be = libusb_package.get_libusb1_backend()
        self.dev = usb.core.find(idVendor=VID, idProduct=PID, backend=be)
        if self.dev is None:
            raise SystemExit("TC001 Max not found")
        try:
            self.dev.get_active_configuration()
        except usb.core.USBError:
            self.dev.set_configuration()
        for i in (0, 1):
            usb.util.claim_interface(self.dev, i)
        self.parser = BlockParser(skip_first=True)
        self.sent: list[tuple[str, str]] = []

    # -- control ------------------------------------------------------------------------
    def status(self) -> int:
        return int(self.dev.ctrl_transfer(0xC1, 0x22, 0, 0, 1, 1000)[0])

    def wait_idle(self) -> int:
        st = 0
        for _ in range(100):
            st = self.status()
            if not st & 1:
                break
            time.sleep(0.01)
        return st

    def send(self, name: str) -> bytes:
        """Send a command from COMMANDS; return its response (b'' if none)."""
        data, resp_len, _src = COMMANDS[name]
        self.sent.append((name, data.hex()))
        self.dev.ctrl_transfer(0x41, 0x20, 0, 0, data, 1000)
        st1 = self.wait_idle()
        resp = b""
        if resp_len:
            resp = bytes(self.dev.ctrl_transfer(0xC1, 0x21, 0, 0, resp_len, 1000))
            self.wait_idle()
        log(f"cmd {name} {data.hex()} status=0x{st1:02x} resp={resp.hex()}")
        return resp

    def debug_log(self, n: int = 128) -> bytes:
        """Extended status read (P3_PROTOCOL.md "Extended Status Register (Debug Logs)")."""
        return bytes(self.dev.ctrl_transfer(0xC1, 0x22, 0, 0, n, 1000))

    # -- stream ---------------------------------------------------------------------------
    def start(self) -> None:
        """Start sequence of the labbench driver / docs/PROTOCOL.md."""
        self.send("start_stream")
        time.sleep(1.0)
        self.dev.set_interface_altsetting(interface=1, alternate_setting=1)
        self.dev.ctrl_transfer(0x40, 0xEE, 0, 1, None, 1000)
        time.sleep(2.0)
        try:
            self.dev.read(EP_IN, READ_BYTES, 100)
        except usb.core.USBError:
            pass
        self.send("start_stream")

    def blocks(self, chunk: bytes) -> list[bytes]:
        return self.parser.feed(chunk)

    def close(self) -> None:
        try:
            self.dev.set_interface_altsetting(interface=1, alternate_setting=0)
        except Exception as exc:  # noqa: BLE001
            log(f"alt 0 failed: {exc}")
        for i in (1, 0):
            try:
                usb.util.release_interface(self.dev, i)
            except Exception:  # noqa: BLE001
                pass
        usb.util.dispose_resources(self.dev)
        log("released")


def decode(blk: bytes) -> dict:
    temps = np.frombuffer(blk, dtype="<u2", count=192 * 256, offset=192 * ROW)
    meta = np.frombuffer(blk, dtype="<u2", count=256, offset=384 * ROW)
    med = float(np.median(temps)) / 64 - 273.15
    return {"seq": int(meta[64]), "median_c": round(med, 2), "temps": temps,
            "max_c": round(float(temps.max()) / 64 - 273.15, 2),
            "meta": [int(meta[i]) for i in (68, 72, 75, 180)],
            "w68_72": (int(meta[68]), int(meta[72])), "meta_full": meta}


def monitor(cam: Cam, seconds: float, out_path: str, label: str, poll_log_s: float = 0.5) -> dict:
    """Stream for ``seconds``; record every EPIPE stall, frame gap > 0.3 s, change of metadata
    words 68/72/75/180 and every new debug-log line. Writes JSON lines to ``out_path``."""
    t0 = time.monotonic()
    events: list[dict] = []
    frames = 0
    last_frame_t = None
    last_meta = None
    last_log = b""
    next_log = t0
    medians = []
    seqs = []
    after_stall = False
    last_full = None
    # frame-content indicators of a shutter: frame-to-frame mean |diff| (counts) jumps, or the
    # camera repeats the same frame (diff 0) while the shutter is closed
    prev_temps = None
    mads: list[float] = []
    frozen_run = 0
    frozen_start = 0.0

    def ev(kind: str, **kw) -> None:
        e = {"t": round(time.monotonic() - t0, 3), "wall": time.strftime("%H:%M:%S"),
             "kind": kind, **kw}
        events.append(e)
        f.write(json.dumps(e) + "\n")
        f.flush()
        log(f"[{label}] +{e['t']:.1f}s {kind} {kw}")

    with open(out_path, "a", encoding="utf-8") as f:
        f.write(json.dumps({"label": label, "start": time.strftime("%Y-%m-%d %H:%M:%S"),
                            "seconds": seconds}) + "\n")
        while time.monotonic() - t0 < seconds:
            now = time.monotonic()
            if now >= next_log:
                next_log = now + poll_log_s
                try:
                    dl = cam.debug_log(128)
                    text = dl[64:].split(b"\x00")[0]
                    if text and text != last_log:
                        last_log = text
                        ev("debuglog", text=text.decode("ascii", "replace"))
                except usb.core.USBError as exc:
                    ev("debuglog_error", err=str(exc))
            try:
                chunk = bytes(cam.dev.read(EP_IN, READ_BYTES, 1000))
            except usb.core.USBTimeoutError:
                ev("timeout")
                continue
            except usb.core.USBError as exc:
                if exc.errno == errno.EPIPE or getattr(exc, "backend_error_code", None) == -9:
                    t_stall = time.monotonic()
                    cam.dev.clear_halt(EP_IN)
                    ev("stall", clear_s=round(time.monotonic() - t_stall, 3))
                    cam.parser = BlockParser(skip_first=True)
                    after_stall = True
                    continue
                raise
            for blk in cam.blocks(chunk):
                d = decode(blk)
                frames += 1
                t = time.monotonic()
                if after_stall and last_full is not None:
                    diff = {i: [int(last_full[i]), int(d["meta_full"][i])] for i in range(256)
                            if last_full[i] != d["meta_full"][i]}
                    ev("after_stall_meta_diff", diff=diff, median_c=d["median_c"])
                after_stall = False
                last_full = d["meta_full"].copy()
                cur = d["temps"].astype(np.int32)
                if prev_temps is not None:
                    mad = float(np.mean(np.abs(cur - prev_temps)))
                    if mad == 0.0:
                        if frozen_run == 0:
                            frozen_start = time.monotonic() - t0
                        frozen_run += 1
                    else:
                        if frozen_run >= 3:
                            ev("frozen", frames=frozen_run, since=round(frozen_start, 3))
                        frozen_run = 0
                        base = float(np.median(mads[-200:])) if len(mads) >= 20 else None
                        if base is not None and mad > max(8 * base, 4.0):
                            ev("jump", mad=round(mad, 2), base=round(base, 2),
                               std=round(float(cur.std()), 1), median_c=d["median_c"])
                        mads.append(mad)
                prev_temps = cur
                if last_frame_t is not None and t - last_frame_t > 0.3:
                    ev("gap", gap_s=round(t - last_frame_t, 3), seq=d["seq"])
                last_frame_t = t
                medians.append(d["median_c"])
                seqs.append(d["seq"])
                # words 68/72 track the scene and 180 counts frames: only word 75 is a state
                if last_meta is not None and d["meta"][2] != last_meta[2]:
                    ev("meta", before=last_meta, after=d["meta"], seq=d["seq"],
                       median_c=d["median_c"])
                last_meta = d["meta"]
                if frames % 1500 == 0:
                    log(f"[{label}] {frames} frames, median {d['median_c']} C, max {d['max_c']} C")
        dur = time.monotonic() - t0
        summ = {"label": label, "summary": True, "seconds": round(dur, 1), "frames": frames,
                "fps": round(frames / dur, 2),
                "stalls": [e["t"] for e in events if e["kind"] == "stall"],
                "gaps": [e["t"] for e in events if e["kind"] == "gap"],
                "shutter_log": [e["t"] for e in events if e["kind"] == "debuglog"
                                and ("I/shutter" in e["text"] or "ffc" in e["text"])],
                "frozen": [e["t"] for e in events if e["kind"] == "frozen"],
                "jumps": [e["t"] for e in events if e["kind"] == "jump"],
                "median_c_range": [min(medians), max(medians)] if medians else None}
        f.write(json.dumps(summ) + "\n")
    log(f"[{label}] SUMMARY {json.dumps(summ)}")
    return summ


if __name__ == "__main__":
    print("helpers only; see the run_*.py scripts", file=sys.stderr)
