"""Overnight noise and per-pixel drift capture for the TC001 Max with the automatic shutter off.

Not shipped. Origin: written for this project (2026-11-21). It drives the camera with the
experiment helpers in ``experiments/cam.py`` (see that file's header for the origin of each
command: jvdillon/p3-ir-camera P3_PROTOCOL.md, Apache-2.0; xaionaro-go/thermalmaster, CC0-1.0)
and decodes blocks with the author's labbench ``BlockParser`` (adapted from WendyOS PR #2621
parser.go, Apache-2.0, Copyright Wendy Labs). The camera-claim files follow labbench's
``labbench.thermal.ownership`` format, as ``src/TC001MaxWebcam.Source/Core/CameraClaims.cs`` does.
The only commands it sends are ones already used in ``experiments/LOG.md``: start_stream,
GetAutoFFCStatus, SetAutoFFCStatus 0 / restore, and the P3 manual shutter (C4).

What it does (the owner's plan, 2026-11-21: start about 03:00, stop by sunrise at 06:30, hold
off while a warm person is in the picture until they have been gone for 10 minutes):

1. Waits for ``--start-at``. Never touches the camera while a labbench claim (run host or GUI) is
   fresh; it waits instead. Takes the camera from the webcam with a claim of priority 25 (above
   the webcam's 10 and labbench's GUI 20, below a labbench run host 30, to which it yields).
2. **hold**: streams and watches for a person (1-s mean frames: a moving region, or a large
   human-temperature region that is not a static object). Starts recording only after
   ``--absent-min`` minutes with nobody seen. Gives up if too little time is left.
3. **record**: auto shutter off (read back), one manual shutter (the start reference), then
   records until the end. A person seen during the recording is flagged per second, not stopped.
4. **end**: the last ``--ffc-tail-s`` seconds before a second manual shutter and the same after
   it are saved at full rate. Because the shutter is a uniform reference and the scene does not
   change in those few seconds, after-minus-before is the per-pixel offset drift the camera
   built up during the night.
5. Restores the auto-shutter state it found, releases the camera (the webcam takes it back by
   itself), then runs ``analyze_noise.py`` on the run.

Output: ``experiments/noise/runs/raw/<YYYYmmdd-HHMMSS>/`` (git-ignored). Layout in
``manifest.json``; all temperatures are raw uint16 at 1/64 K, row-major 192 x 256.

usage (Windows Task Scheduler runs it with pythonw.exe so no window opens):
  python noise_capture.py --start-at 2026-11-22T03:00 --end-at 2026-11-22T06:30
  python noise_capture.py --fake --end-at +6m --absent-min 0.5 --burst-every-min 2   (dry run)
"""

from __future__ import annotations

import argparse
import collections
import ctypes
import datetime as dt
import errno
import json
import os
import socket
import subprocess
import sys
import time
import traceback
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
H, W = 192, 256
NPIX = H * W
LEAK_RAW = int((200 + 273.15) * 64)       # a temperature word above 200 degC = picture leak
CLAIM_DIR = Path(os.environ.get("PROGRAMDATA", r"C:\ProgramData")) / "TC001MaxWebcam" / "claims"
ROLE, PRIORITY, EXPIRES_S = "noise_study", 25, 5.0
LABBENCH_ROLES = {"run_host", "gui"}

# presence detector (on 1-s mean frames, degC)
WARM_ABS_C, WARM_REL_C, WARM_MAX_C = 30.0, 4.0, 38.0   # human skin/clothing band in this camera
WARM_PX = 1500           # ~3 % of the frame: a person within a few metres
MOTION_DT_S = 5          # compare with the 1-s mean frame this many seconds earlier
MOTION_C, MOTION_PX = 1.5, 150
STATIC_WARM_S = 10 * 60  # a warm region that has not moved or changed for this long is an object

SAMPLE_ROWS = np.linspace(8, H - 9, 8).astype(int)
SAMPLE_COLS = np.linspace(8, W - 9, 8).astype(int)
SAMPLE_IDX = (SAMPLE_ROWS[:, None] * W + SAMPLE_COLS[None, :]).ravel()   # 64 pixels at full rate

SERIES_DT = np.dtype([("t", "<f8"), ("wall", "<f8"), ("seq", "<u4"), ("present", "u1"),
                      ("median_c", "<f4"), ("mean_c", "<f4"), ("std_c", "<f4"),
                      ("px", "<u2", (64,))])
WIN_DT = np.dtype([("t0", "<f8"), ("t1", "<f8"), ("n", "<u4"), ("present", "u1")])
IDX_DT = np.dtype([("t", "<f8"), ("wall", "<f8"), ("seq", "<u4"), ("present", "u1")])


def to_c(raw: np.ndarray) -> np.ndarray:
    return raw.astype(np.float32) / 64.0 - 273.15


# -- logging -----------------------------------------------------------------------------------
class Log:
    def __init__(self, run_dir: Path) -> None:
        self.events = open(run_dir / "events.jsonl", "a", encoding="utf-8")
        self.t0: float | None = None

    def msg(self, text: str) -> None:
        print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {text}", flush=True)

    def ev(self, kind: str, **kw) -> None:
        e = {"wall": round(time.time(), 3), "time": time.strftime("%H:%M:%S"), "kind": kind}
        if self.t0 is not None:
            e["t"] = round(time.monotonic() - self.t0, 3)
        e.update(kw)
        self.events.write(json.dumps(e, default=lambda o: o.item() if hasattr(o, "item") else str(o)) + "\n")
        self.events.flush()
        self.msg(f"{kind} {kw}")


# -- camera claims (labbench.thermal.ownership format) -------------------------------------------
def pid_alive(pid: int) -> bool:
    if os.name != "nt":
        return True
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return k32.GetLastError() == 5        # access denied: it exists
    try:
        code = ctypes.c_ulong()
        k32.GetExitCodeProcess(h, ctypes.byref(code))
        return code.value == 259              # STILL_ACTIVE
    finally:
        k32.CloseHandle(h)


class ClaimFile:
    def __init__(self, enabled: bool) -> None:
        self.enabled = enabled
        self.pid = os.getpid()
        self.host = socket.gethostname()
        self.created = time.time()
        self.path = CLAIM_DIR / f"claim-{ROLE}-{self.pid}.json"

    def beat(self, holding: bool) -> None:
        if not self.enabled:
            return
        data = {"version": 1, "role": ROLE, "priority": PRIORITY, "pid": self.pid,
                "host": self.host, "created": self.created, "t_wall": time.time(),
                "expires_s": EXPIRES_S, "state": "holding" if holding else "wanted",
                "source": "TC001 Max noise study (experiments/noise)"}
        tmp = self.path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(data), encoding="utf-8")
        for _ in range(4):
            try:
                os.replace(tmp, self.path)
                return
            except OSError:
                time.sleep(0.01)

    def others(self) -> list[dict]:
        if not self.enabled:
            return []
        out, now = [], time.time()
        for f in CLAIM_DIR.glob("claim-*.json"):
            try:
                c = json.loads(f.read_text(encoding="utf-8"))
                pid, t_wall = int(c["pid"]), float(c["t_wall"])
            except (OSError, ValueError, KeyError, TypeError):
                continue
            if pid == self.pid:
                continue
            if now - t_wall > float(c.get("expires_s", EXPIRES_S)):
                continue
            host = str(c.get("host", ""))
            if (not host or host.lower() == self.host.lower()) and not pid_alive(pid):
                continue
            out.append(c)
        return out

    def labbench(self) -> dict | None:
        return next((c for c in self.others() if c.get("role") in LABBENCH_ROLES), None)

    def higher(self) -> dict | None:
        return next((c for c in self.others() if int(c.get("priority", 0)) > PRIORITY), None)

    def lower_holding(self) -> dict | None:
        return next((c for c in self.others() if int(c.get("priority", 0)) < PRIORITY
                     and c.get("state") == "holding"), None)

    def remove(self) -> None:
        if self.enabled:
            for p in (self.path, self.path.with_suffix(".json.tmp")):
                try:
                    p.unlink()
                except OSError:
                    pass


# -- frame sources ---------------------------------------------------------------------------------
class SourceError(Exception):
    pass


class RealSource:
    """The camera through experiments/cam.py (PyUSB + libusb on the WinUSB binding)."""

    def __init__(self, log: Log) -> None:
        sys.path.insert(0, str(HERE.parent))
        import cam  # noqa: PLC0415  (experiments/cam.py; needs the labbench venv)
        import usb.core  # noqa: PLC0415
        self.cammod, self.usbcore, self.log = cam, usb.core, log
        cam.COMMANDS["shutter"] = (cam.vdcmd((0x43, 0x36, 0x01)), 0, "P3_PROTOCOL.md 'shutter'")
        assert cam.COMMANDS["shutter"][0].hex() == "01364300000000000000000000000000cd0b"
        self.cam = None
        self.pending: collections.deque[bytes] = collections.deque()

    def open(self) -> None:
        self.cam = self.cammod.Cam()
        self.pending.clear()

    def start(self) -> None:
        self.cam.start()

    def send(self, name: str) -> bytes:
        return self.cam.send(name)

    def debug_line(self) -> str:
        try:
            dl = self.cam.debug_log(128)
            return dl[64:].split(b"\x00")[0].decode("ascii", "replace")
        except self.usbcore.USBError as exc:
            return f"<debug log error {exc}>"

    def frame(self, timeout_s: float = 10.0) -> tuple[np.ndarray, np.ndarray]:
        """Next block as (temperatures uint16 [H*W], metadata row uint16 [256])."""
        deadline = time.monotonic() + timeout_s
        while not self.pending:
            if time.monotonic() > deadline:
                raise SourceError(f"no frame for {timeout_s} s")
            try:
                chunk = bytes(self.cam.dev.read(self.cammod.EP_IN, self.cammod.READ_BYTES, 1000))
            except self.usbcore.USBTimeoutError:
                self.log.ev("timeout")
                continue
            except self.usbcore.USBError as exc:
                if exc.errno == errno.EPIPE or getattr(exc, "backend_error_code", None) == -9:
                    self.cam.dev.clear_halt(self.cammod.EP_IN)
                    self.cam.parser = self.cammod.BlockParser(skip_first=True)
                    self.log.ev("stall")
                    continue
                raise SourceError(str(exc)) from exc
            self.pending.extend(self.cam.blocks(chunk))
        blk = self.pending.popleft()
        temps = np.frombuffer(blk, dtype="<u2", count=NPIX, offset=192 * 512).copy()
        meta = np.frombuffer(blk, dtype="<u2", count=256, offset=384 * 512).copy()
        return temps, meta

    def close(self) -> None:
        if self.cam is not None:
            self.cam.close()
            self.cam = None


class FakeSource:
    """A simulated camera for dry runs: a static but non-uniform scene (gradient, warm and cold
    objects), a slowly cooling room, per-pixel offset drift (random walk) that a shutter resets,
    50 mK white noise, column noise, and a person who walks through for the first ``person_s``."""

    def __init__(self, log: Log, person_s: float = 40.0, speed: float = 60.0) -> None:
        self.log, self.person_s, self.speed = log, person_s, speed
        rng = np.random.default_rng(1)
        self.rng = rng
        y, x = np.mgrid[0:H, 0:W]
        scene = 24.0 + 2.0 * x / W + 1.0 * y / H
        scene[40:80, 30:90] += 8.0          # a warm appliance
        scene[120:170, 180:240] -= 3.0      # a cold window
        self.scene, self.y, self.x = scene, y, x
        self.drift_rate = rng.normal(0, 0.05, NPIX).astype(np.float32)  # degC per simulated hour
        self.offset = np.zeros(NPIX, np.float32)
        self.t_open = time.monotonic()
        self.t_last = self.t_open
        self.seq = 0
        self.auto_ffc = b"\x01"

    def open(self) -> None:
        self.t_open = self.t_last = time.monotonic()

    def start(self) -> None:
        pass

    def send(self, name: str) -> bytes:
        self.log.msg(f"fake cmd {name}")
        if name == "shutter":
            self.offset[:] = 0
        elif name == "set_auto_ffc_off":
            self.auto_ffc = b"\x00"
        elif name == "set_auto_ffc_on":
            self.auto_ffc = b"\x01"
        elif name == "get_auto_ffc_status":
            return self.auto_ffc
        return b""

    def debug_line(self) -> str:
        return "I/fake: ok"

    def frame(self, timeout_s: float = 10.0) -> tuple[np.ndarray, np.ndarray]:
        nxt = self.t_last + 0.04
        delay = nxt - time.monotonic()
        if delay > 0:
            time.sleep(delay)
        now = time.monotonic()
        dt_h = (now - self.t_last) * self.speed / 3600
        self.t_last = now
        t = now - self.t_open
        hours = t * self.speed / 3600
        self.offset += self.drift_rate * dt_h + self.rng.normal(0, 0.002, NPIX).astype(np.float32) * np.sqrt(dt_h)
        img = self.scene.ravel() - 0.8 * hours + self.offset
        img = img + self.rng.normal(0, 0.05, NPIX) + np.tile(self.rng.normal(0, 0.02, W), H)
        if t < self.person_s:   # a person walking left to right
            cx = 40 + (t / self.person_s) * 180
            body = ((self.x - cx) / 25) ** 2 + ((self.y - 110) / 70) ** 2 < 1
            img = np.where(body.ravel(), 33.0 + self.rng.normal(0, 0.3, NPIX), img)
        if self.rng.random() < 0.001:
            img[:6] = 241.0   # an occasional picture leak
        self.seq += 1
        meta = np.zeros(256, np.uint16)
        meta[64] = self.seq & 0xFFFF
        return np.round((img + 273.15) * 64).astype(np.uint16), meta

    def close(self) -> None:
        pass


# -- presence -----------------------------------------------------------------------------------
class Presence:
    def __init__(self, static_warm_s: float = STATIC_WARM_S) -> None:
        self.static_warm_s = static_warm_s
        self.hist: collections.deque[tuple[float, int, int]] = collections.deque()
        self.frames: collections.deque[tuple[float, np.ndarray]] = collections.deque()
        self.last_seen: float | None = None
        self.last = {}

    def update(self, t: float, frame_c: np.ndarray) -> bool:
        """``frame_c``: a 1-s mean frame in degC. Returns whether a person is seen now."""
        med = float(np.median(frame_c))
        band = (frame_c > max(WARM_ABS_C, med + WARM_REL_C)) & (frame_c < WARM_MAX_C)
        warm = int(band.sum())
        moving = 0
        while self.frames and t - self.frames[0][0] > MOTION_DT_S + 0.5:
            self.frames.popleft()
        if self.frames and t - self.frames[0][0] >= MOTION_DT_S - 0.5:
            d = frame_c - self.frames[0][1]
            d -= np.median(d)
            moving = int((np.abs(d) > MOTION_C).sum())
        self.frames.append((t, frame_c))
        self.hist.append((t, warm, moving))
        while t - self.hist[0][0] > self.static_warm_s:
            self.hist.popleft()
        static_warm = False
        if warm >= WARM_PX and t - self.hist[0][0] >= self.static_warm_s - 5:
            # no motion at all for the whole interval: an object (it may slowly warm or cool), not a person
            static_warm = all(h[2] < MOTION_PX for h in self.hist)
        present = moving >= MOTION_PX or (warm >= WARM_PX and not static_warm)
        if present:
            self.last_seen = t
        self.last = {"warm_px": warm, "moving_px": moving, "static_warm": bool(static_warm),
                     "median_c": round(med, 2), "max_c": round(float(frame_c.max()), 2)}
        return present


# -- output files ---------------------------------------------------------------------------------
class Writer:
    def __init__(self, run_dir: Path) -> None:
        self.dir = run_dir
        self.files: dict[str, object] = {}

    def append(self, name: str, arr: np.ndarray) -> None:
        f = self.files.get(name)
        if f is None:
            f = self.files[name] = open(self.dir / name, "ab")
        f.write(np.ascontiguousarray(arr).tobytes())

    def flush(self) -> None:
        for f in self.files.values():
            f.flush()

    def close(self) -> None:
        for f in self.files.values():
            f.close()
        self.files.clear()


def save_png(path: Path, frame_c: np.ndarray) -> None:
    try:
        import matplotlib  # noqa: PLC0415
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt  # noqa: PLC0415
        lo, hi = np.percentile(frame_c, [1, 99.5])
        plt.imsave(path, frame_c.reshape(H, W), cmap="inferno", vmin=lo, vmax=hi)
    except Exception as exc:  # noqa: BLE001
        print(f"png failed: {exc}", flush=True)


# -- the study ------------------------------------------------------------------------------------
class Yield(Exception):
    pass


class Study:
    def __init__(self, args, run_dir: Path, log: Log) -> None:
        self.args, self.dir, self.log = args, run_dir, log
        self.claim = ClaimFile(enabled=not args.fake)
        self.src = FakeSource(log, speed=args.fake_speed) if args.fake else RealSource(log)
        self.w = Writer(run_dir)
        self.presence = Presence(args.static_warm_min * 60)
        self.end_wall = args.end_at.timestamp()
        self.orig_ffc: bytes | None = None
        self.opened = False
        self.next_beat = 0.0
        self.last_debug = ""
        self.stats = collections.Counter()
        self.summary: dict = {"phases": []}
        self.prev_raw: np.ndarray | None = None
        self.last_frame_mono: float | None = None

    # housekeeping once a second: claim heartbeat, yield check, firmware log
    def tick(self) -> None:
        now = time.monotonic()
        if now < self.next_beat:
            return
        self.next_beat = now + 1.0
        self.claim.beat(holding=self.opened)
        hi = self.claim.higher()
        if hi is not None:
            raise Yield(f"{hi.get('source') or hi.get('role')} (pid {hi.get('pid')}, priority {hi.get('priority')})")
        line = self.src.debug_line()
        if line and line != self.last_debug:
            self.last_debug = line
            self.log.ev("debuglog", text=line)
            if "shutter" in line or "ffc" in line:
                self.stats["firmware_ffc_lines"] += 1

    def acquire(self) -> bool:
        """Wait for labbench to be idle and the webcam to let go, then open the camera."""
        while time.time() < self.end_wall:
            lb = self.claim.labbench()
            if lb is not None:
                self.log.ev("waiting_for_labbench", claim=lb)
                time.sleep(30)
                continue
            self.claim.beat(holding=False)
            t_wait = time.monotonic()
            while (h := self.claim.lower_holding()) is not None and time.monotonic() - t_wait < 20:
                self.claim.beat(holding=False)
                time.sleep(0.5)
            for attempt in range(5):
                self.claim.beat(holding=False)
                try:
                    self.src.open()
                    self.src.start()
                    self.opened = True
                    self.claim.beat(holding=True)
                    self.log.ev("camera_open", attempt=attempt, waited_s=round(time.monotonic() - t_wait, 1))
                    return True
                except Exception as exc:  # noqa: BLE001
                    self.log.ev("open_failed", attempt=attempt, err=repr(exc))
                    try:
                        self.src.close()
                    except Exception:  # noqa: BLE001
                        pass
                    time.sleep(3)
            time.sleep(30)
        return False

    def reopen(self, why: str) -> None:
        self.log.ev("reopen", why=why)
        try:
            self.src.close()
        except Exception:  # noqa: BLE001
            pass
        self.opened = False
        if not self.acquire():
            raise SourceError("could not reopen the camera before the end time")
        if self.recording:
            self.src.send("set_auto_ffc_off")

    def frames(self):
        """Good frames as (t_mono, wall, temps, meta); counts and skips leaks and repeats."""
        fails = 0
        while True:
            self.tick()
            try:
                temps, meta = self.src.frame()
                fails = 0
            except SourceError as exc:
                fails += 1
                if fails > 3:
                    raise
                self.reopen(str(exc))
                continue
            now = time.monotonic()
            if self.last_frame_mono is not None and now - self.last_frame_mono > 0.3:
                self.log.ev("gap", gap_s=round(now - self.last_frame_mono, 3), seq=int(meta[64]))
            self.last_frame_mono = now
            self.stats["frames"] += 1
            if int(temps.max()) > LEAK_RAW:
                self.stats["leak_frames"] += 1
                continue
            if self.prev_raw is not None and np.array_equal(temps, self.prev_raw):
                self.stats["repeated_frames"] += 1
                continue
            self.prev_raw = temps
            yield now, time.time(), temps, meta

    recording = False

    def hold(self) -> bool:
        """Stream until nobody has been seen for --absent-min minutes. False: not enough time."""
        self.log.ev("phase", name="hold", absent_min=self.args.absent_min)
        t_start = time.monotonic()
        self.presence.last_seen = t_start          # must see an empty room for the whole interval
        need = self.args.absent_min * 60
        sec_sum, sec_n, sec_id, last_report, was = None, 0, None, 0.0, None
        for now, wall, temps, _meta in self.frames():
            sid = int(now - t_start)
            if sec_id is None:
                sec_id, sec_sum, sec_n = sid, temps.astype(np.float64), 1
                continue
            if sid == sec_id:
                sec_sum += temps
                sec_n += 1
                continue
            frame_c = (sec_sum / sec_n / 64 - 273.15).astype(np.float32)
            present = self.presence.update(now, frame_c)
            sec_id, sec_sum, sec_n = sid, temps.astype(np.float64), 1
            if present != was:
                self.log.ev("presence", present=present, **self.presence.last)
                if present:
                    save_png(self.dir / "hold_person_seen.png", frame_c)
                was = present
            absent_s = now - self.presence.last_seen
            if now - last_report >= 60:
                last_report = now
                self.log.ev("hold_status", absent_s=round(absent_s), **self.presence.last)
            if absent_s >= need:
                save_png(self.dir / "scene_at_start.png", frame_c)
                self.log.ev("hold_done", waited_s=round(now - t_start), **self.presence.last)
                return True
            if wall > self.end_wall - self.args.min_record_min * 60:
                self.log.ev("gave_up", why="a person was seen too close to the end time",
                            **self.presence.last)
                return False
        return False

    def record(self) -> None:
        a = self.args
        self.recording = True
        st = self.src.send("set_auto_ffc_off")
        st = self.src.send("get_auto_ffc_status")
        self.log.ev("auto_ffc_off", readback=st.hex())
        if st != b"\x00":
            raise SourceError(f"auto shutter did not switch off (read back {st.hex()})")
        self.ffc("start")
        t0 = time.monotonic()
        self.log.t0 = t0
        self.summary["t0_wall"] = time.time()
        self.log.ev("phase", name="record")
        end_rec = self.end_wall - (2 * a.ffc_tail_s + 20)   # leave room for the end shutter
        bursts = [("start", 0.0)]
        k = 1
        while True:
            bt = k * a.burst_every_min * 60
            if self.summary["t0_wall"] + bt + a.burst_s > end_rec - a.ffc_tail_s:
                break
            bursts.append((f"b{k:02d}", bt))
            k += 1
        burst_i, in_burst = 0, None
        win_sum = win_sq = None
        win_n, win_t0, win_present = 0, 0.0, 0
        sec_id, sec_sum, sec_n, sec_present = None, None, 0, False
        series_buf: list[tuple] = []
        pre_ffc_t = end_rec - self.summary["t0_wall"] - a.ffc_tail_s

        def close_window(t_end: float) -> None:
            nonlocal win_sum, win_sq, win_n, win_present
            if win_n >= 2:
                mean = win_sum / win_n
                var = np.maximum(win_sq / win_n - mean * mean, 0) * win_n / (win_n - 1)
                self.w.append("win_mean.f4", mean.astype("<f4"))
                self.w.append("win_var.f4", var.astype("<f4"))
                self.w.append("win_index.bin", np.array([(win_t0, t_end, win_n, win_present)], WIN_DT))
            win_sum = win_sq = None
            win_n, win_present = 0, 0

        for now, wall, temps, meta in self.frames():
            t = now - t0
            # bursts at full rate
            if in_burst is None and burst_i < len(bursts) and t >= bursts[burst_i][1]:
                in_burst = (bursts[burst_i][0], t + a.burst_s)
                burst_i += 1
                self.log.ev("burst_start", name=in_burst[0])
            if in_burst is None and t >= pre_ffc_t and "pre_ffc" not in self.summary:
                in_burst = ("pre_ffc", t + a.ffc_tail_s)
                self.summary["pre_ffc"] = True
                self.log.ev("burst_start", name="pre_ffc")
            if in_burst is not None:
                name = in_burst[0]
                self.w.append(f"burst_{name}.u16", temps)
                self.w.append(f"burst_{name}_index.bin", np.array([(t, wall, meta[64], sec_present)], IDX_DT))
                if t >= in_burst[1]:
                    self.log.ev("burst_end", name=name)
                    in_burst = None
                    if name == "pre_ffc":
                        break
            # per-second: 1 Hz raw frame, metadata, presence
            sid = int(t)
            if sec_id != sid:
                if sec_id is not None and sec_n:
                    frame_c = (sec_sum / sec_n / 64 - 273.15).astype(np.float32)
                    p = self.presence.update(now, frame_c)
                    if p != sec_present:
                        self.log.ev("presence", present=p, **self.presence.last)
                        if p:
                            save_png(self.dir / f"person_seen_{int(t)}s.png", frame_c)
                    sec_present = p
                    if p:
                        self.stats["present_s"] += 1
                    if sid % 60 == 0:
                        self.log.ev("status", frames=self.stats["frames"], **self.presence.last)
                sec_id, sec_sum, sec_n = sid, temps.astype(np.float64), 1
                self.w.append("frames_1hz.u16", temps)
                self.w.append("frames_1hz_index.bin", np.array([(t, wall, meta[64], sec_present)], IDX_DT))
                self.w.append("meta_1hz.u16", meta)
                self.w.append("series.bin", np.array(series_buf, SERIES_DT)) if series_buf else None
                series_buf.clear()
                self.w.flush()
            else:
                sec_sum += temps
                sec_n += 1
            # 10-s windows: per-pixel mean and variance
            if win_n and t - win_t0 >= a.window_s:
                close_window(t)
            if win_n == 0:
                win_t0 = t
                win_sum = np.zeros(NPIX, np.float64)
                win_sq = np.zeros(NPIX, np.float64)
            f = temps.astype(np.float64)
            win_sum += f
            win_sq += f * f
            win_n += 1
            win_present |= int(sec_present)
            # every frame: global statistics and 64 fixed pixels
            c = to_c(temps)
            series_buf.append((t, wall, int(meta[64]), int(sec_present), float(np.median(c)),
                               float(c.mean()), float(c.std()), temps[SAMPLE_IDX]))
            self.stats["recorded_frames"] += 1
        close_window(t)
        if series_buf:
            self.w.append("series.bin", np.array(series_buf, SERIES_DT))
        self.ffc("end")
        t_post0 = time.monotonic() - t0
        self.log.ev("burst_start", name="post_ffc")
        for now, wall, temps, meta in self.frames():
            t = now - t0
            self.w.append("burst_post_ffc.u16", temps)
            self.w.append("burst_post_ffc_index.bin", np.array([(t, wall, meta[64], 0)], IDX_DT))
            if t - t_post0 >= a.ffc_tail_s:
                break
        self.log.ev("burst_end", name="post_ffc")
        self.w.flush()

    def ffc(self, label: str) -> None:
        """One manual shutter, then let the picture settle (frames read and dropped)."""
        self.src.send("shutter")
        self.log.ev("manual_ffc", label=label)
        self.summary.setdefault("ffc", []).append({"label": label, "wall": time.time()})
        t_end = time.monotonic() + self.args.ffc_settle_s
        for now, *_ in self.frames():
            if now >= t_end:
                break

    def run(self) -> int:
        a = self.args
        manifest = {
            "created": time.strftime("%Y-%m-%d %H:%M:%S"), "args": {k: str(v) for k, v in vars(a).items()},
            "shape": [H, W], "raw_units": "uint16, 1/64 K (degC = raw/64 - 273.15)",
            "files": {
                "frames_1hz.u16": "one raw temperature frame per second of the recording",
                "frames_1hz_index.bin": f"per 1-Hz frame: {IDX_DT.descr}",
                "meta_1hz.u16": "the metadata row (256 uint16) of each 1-Hz frame",
                "win_mean.f4 / win_var.f4": f"per {a.window_s}-s window: per-pixel mean and variance (raw units, float32)",
                "win_index.bin": f"per window: {WIN_DT.descr}",
                "series.bin": f"every recorded frame: {SERIES_DT.descr}; px = 64 pixels at rows {SAMPLE_ROWS.tolist()} x cols {SAMPLE_COLS.tolist()}",
                "burst_<name>.u16 + burst_<name>_index.bin": f"full-rate raw frames: 'start' and every {a.burst_every_min} min for {a.burst_s} s; 'pre_ffc' / 'post_ffc' the last {a.ffc_tail_s} s before and the first after the end shutter",
                "events.jsonl": "phases, presence, firmware log lines, stalls, gaps, errors",
            },
            "presence": {"warm_band_c": [WARM_ABS_C, f"median+{WARM_REL_C}", WARM_MAX_C], "warm_px": WARM_PX,
                         "motion": f">{MOTION_C} degC over {MOTION_DT_S} s on {MOTION_PX}+ px",
                         "static_warm_s": a.static_warm_min * 60},
            "t": "seconds since the start shutter settled (monotonic clock)",
        }
        (self.dir / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
        rc = 1
        try:
            if not self.acquire():
                self.log.ev("gave_up", why="camera not available before the end time")
                return 2
            self.orig_ffc = self.src.send("get_auto_ffc_status")
            self.log.ev("auto_ffc_found", status=self.orig_ffc.hex())
            if not self.hold():
                return 3
            self.record()
            rc = 0
        except Yield as y:
            self.log.ev("yielded", to=str(y))
            rc = 4
        except Exception as exc:  # noqa: BLE001
            self.log.ev("error", err=repr(exc), tb=traceback.format_exc())
            rc = 1
        finally:
            self.w.close()
            if self.opened:
                try:
                    want = "set_auto_ffc_off" if self.orig_ffc == b"\x00" else "set_auto_ffc_on"
                    self.src.send(want)
                    st = self.src.send("get_auto_ffc_status")
                    self.log.ev("auto_ffc_restored", sent=want, readback=st.hex())
                except Exception as exc:  # noqa: BLE001
                    self.log.ev("restore_failed", err=repr(exc))
                try:
                    self.src.close()
                except Exception as exc:  # noqa: BLE001
                    self.log.ev("close_failed", err=repr(exc))
                self.opened = False
            self.claim.remove()
            self.summary.update(rc=rc, stats=dict(self.stats), end=time.strftime("%Y-%m-%d %H:%M:%S"))
            (self.dir / "summary.json").write_text(json.dumps(self.summary, indent=1), encoding="utf-8")
            self.log.ev("done", rc=rc, stats=dict(self.stats))
        return rc


def parse_when(text: str) -> dt.datetime:
    if text.startswith("+"):
        n, unit = float(text[1:-1]), text[-1]
        return dt.datetime.now() + dt.timedelta(seconds=n * {"s": 1, "m": 60, "h": 3600}[unit])
    return dt.datetime.fromisoformat(text)


def keep_awake(on: bool) -> None:
    if os.name == "nt":   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED (the display may sleep)
        ctypes.windll.kernel32.SetThreadExecutionState(0x80000000 | (0x1 if on else 0))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--start-at", type=parse_when, default=None)
    ap.add_argument("--end-at", type=parse_when, required=True)
    ap.add_argument("--absent-min", type=float, default=10.0)
    ap.add_argument("--static-warm-min", type=float, default=STATIC_WARM_S / 60,
                    help="a warm region with no motion for this long counts as an object, not a person")
    ap.add_argument("--min-record-min", type=float, default=20.0, help="give up if less is left")
    ap.add_argument("--burst-every-min", type=float, default=30.0)
    ap.add_argument("--burst-s", type=float, default=60.0)
    ap.add_argument("--ffc-tail-s", type=float, default=60.0)
    ap.add_argument("--ffc-settle-s", type=float, default=5.0)
    ap.add_argument("--window-s", type=float, default=10.0)
    ap.add_argument("--out", type=Path, default=HERE / "runs" / "raw")
    ap.add_argument("--fake", action="store_true", help="simulated camera (dry run)")
    ap.add_argument("--fake-speed", type=float, default=60.0, help="simulated hours run this much faster")
    ap.add_argument("--no-analyze", action="store_true")
    a = ap.parse_args()

    run_dir = a.out / (time.strftime("%Y%m%d-%H%M%S") + ("-fake" if a.fake else ""))
    run_dir.mkdir(parents=True, exist_ok=True)
    out = open(run_dir / "run.log", "a", encoding="utf-8", buffering=1)
    sys.stdout = sys.stderr = out   # pythonw has no console
    log = Log(run_dir)
    log.msg(f"noise study: {run_dir}")
    try:
        import psutil  # noqa: PLC0415
        psutil.Process().nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
    except Exception:  # noqa: BLE001
        pass
    if a.start_at is not None and a.start_at > dt.datetime.now():
        log.ev("waiting_for_start", start_at=a.start_at.isoformat())
        time.sleep((a.start_at - dt.datetime.now()).total_seconds())
    if dt.datetime.now() >= a.end_at:
        log.ev("gave_up", why=f"already past the end time {a.end_at.isoformat()}")
        return 2
    keep_awake(True)
    try:
        rc = Study(a, run_dir, log).run()
    finally:
        keep_awake(False)
    if not a.no_analyze and (run_dir / "series.bin").exists():
        log.msg("running analyze_noise.py")
        r = subprocess.run([sys.executable, str(HERE / "analyze_noise.py"), str(run_dir)],
                           stdout=out, stderr=out, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)
                           | getattr(subprocess, "BELOW_NORMAL_PRIORITY_CLASS", 0))
        log.msg(f"analysis exit code {r.returncode}")
    return rc


if __name__ == "__main__":
    sys.exit(main())
