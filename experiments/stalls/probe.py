"""Stall probe for the TC001 Max EPIPE investigation (2026-11-20). Not shipped. Origin: written for
this project; the camera access is ../cam.py's (see its sources), the async reads call libusb's
documented API through ctypes.

Streams the camera with one of several read strategies and records every EPIPE stall with the
read timing just before it. Uses the start sequence and commands of ``../cam.py`` only (no new
command is sent); the device is always released at the end (``Cam.close``).

Modes:
  sync   PyUSB ``dev.read`` of ``--size`` bytes, one transfer outstanding (labbench today:
         16384).
  async  libusb's asynchronous API through ctypes on the libusb-1.0.dll that libusb-package
         ships (no new dependency): ``--n`` transfers of ``--size`` bytes always queued; a
         callback copies the data out and resubmits at once; parsing runs in another thread.
         ``--raw-io`` also sets the WinUSB RAW_IO pipe policy (libusb_endpoint_set_raw_io).

Options:
  --load K       K extra Python threads doing pure-Python work (GIL contention, like the run
                 host's monitor / analysis threads).
  --inject LIST  every --inject-every seconds, hold the I/O thread for D ms (cycling through
                 the list) - the reader thread sleeps (sync) or the libusb event thread sleeps
                 (async) - and log whether a stall follows within 1 s.
  --decode       decode every block like ../cam.py does (median etc.) in the parsing thread.

Output: JSON lines appended to --out (one header, one line per stall / injection, a summary).
"""

from __future__ import annotations

import argparse
import array
import collections
import ctypes as C
import errno
import json
import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import libusb_package  # noqa: E402
import numpy as np  # noqa: E402
import usb.core  # noqa: E402

from cam import EP_IN, Cam, decode, log  # noqa: E402
from labbench.drivers.topdon.tc001max_protocol import BlockParser  # noqa: E402

# ---------------------------------------------------------------------------------------------
# libusb async API (libusb.h 1.0.30: struct libusb_transfer, libusb_submit_transfer, ...)


class Transfer(C.Structure):
    pass


CB = C.CFUNCTYPE(None, C.POINTER(Transfer))
Transfer._fields_ = [("dev_handle", C.c_void_p), ("flags", C.c_uint8), ("endpoint", C.c_uint8),
                     ("type", C.c_uint8), ("timeout", C.c_uint), ("status", C.c_int),
                     ("length", C.c_int), ("actual_length", C.c_int), ("callback", CB),
                     ("user_data", C.c_void_p), ("buffer", C.c_void_p),
                     ("num_iso_packets", C.c_int)]


class Timeval(C.Structure):
    _fields_ = [("tv_sec", C.c_long), ("tv_usec", C.c_long)]


LIBUSB_TRANSFER_TYPE_BULK = 2
ST_COMPLETED, ST_ERROR, ST_TIMED_OUT, ST_CANCELLED, ST_STALL, ST_NO_DEVICE, ST_OVERFLOW = range(7)
ST_NAMES = ["completed", "error", "timed_out", "cancelled", "stall", "no_device", "overflow"]


def load_lib() -> C.CDLL:
    lib = C.CDLL(libusb_package.get_library_path())
    lib.libusb_alloc_transfer.argtypes = [C.c_int]
    lib.libusb_alloc_transfer.restype = C.POINTER(Transfer)
    lib.libusb_free_transfer.argtypes = [C.POINTER(Transfer)]
    lib.libusb_submit_transfer.argtypes = [C.POINTER(Transfer)]
    lib.libusb_cancel_transfer.argtypes = [C.POINTER(Transfer)]
    lib.libusb_handle_events_timeout_completed.argtypes = [C.c_void_p, C.POINTER(Timeval),
                                                           C.c_void_p]
    lib.libusb_clear_halt.argtypes = [C.c_void_p, C.c_ubyte]
    lib.libusb_endpoint_set_raw_io.argtypes = [C.c_void_p, C.c_uint8, C.c_int]
    lib.libusb_get_max_raw_io_transfer_size.argtypes = [C.c_void_p, C.c_uint8]
    lib.libusb_error_name.argtypes = [C.c_int]
    lib.libusb_error_name.restype = C.c_char_p
    return lib


# ---------------------------------------------------------------------------------------------


class Stats:
    def __init__(self, out, label: str) -> None:
        self.f = out
        self.label = label
        self.t0 = time.perf_counter()
        self.reads = collections.deque(maxlen=200)    # (t_submit|t_prev_done, t_done, n)
        self.stalls: list[float] = []
        self.inject: list[dict] = []
        self.pending_inject: dict | None = None
        self.bytes = 0
        self.nreads = 0
        self.short = 0
        self.gaps = array.array("d")                   # host gap before each read (sync)
        self.lock = threading.Lock()

    def t(self) -> float:
        return time.perf_counter() - self.t0

    def ev(self, kind: str, **kw) -> None:
        e = {"t": round(self.t(), 4), "wall": time.strftime("%H:%M:%S"), "kind": kind, **kw}
        with self.lock:
            self.f.write(json.dumps(e) + "\n")
            self.f.flush()
        if kind != "inject":
            log(f"[{self.label}] +{e['t']:.1f}s {kind} "
                f"{ {k: v for k, v in kw.items() if k != 'recent'} }")

    def stall(self, **kw) -> None:
        t = self.t()
        self.stalls.append(t)
        recent = list(self.reads)[-40:]
        base = recent[-1][1] if recent else 0.0
        rec = [[round((a - base) * 1000, 3), round((b - base) * 1000, 3), n] for a, b, n in recent]
        pi = self.pending_inject
        if pi is not None and t - pi["t"] < 1.0:
            pi["stall_after_s"] = round(t - pi["t"], 4)
        self.ev("stall", recent_ms=rec, **kw)


def busy(stop: threading.Event) -> None:
    x = 0
    while not stop.is_set():
        for i in range(2000):
            x += i * i
        x &= 0xFFFF


def parse_thread(q: collections.deque, cv: threading.Condition, stop: threading.Event,
                 st: Stats, do_decode: bool, res: dict) -> None:
    parser = BlockParser(skip_first=True)
    while not stop.is_set() or q:
        with cv:
            while not q and not stop.is_set():
                cv.wait(0.2)
            items = []
            while q:
                items.append(q.popleft())
        for it in items:
            if it is None:            # stall marker: the stream is discontinuous
                parser = BlockParser(stats=parser.stats, skip_first=True)
                parser.stats.last_sequence = None
                continue
            for blk in parser.feed(it):
                res["frames"] += 1
                if do_decode or res["frames"] % 25 == 0:
                    d = decode(blk)
                    res["medians"].append(d["median_c"])
                    res["seq_last"] = d["seq"]
    res["rejected"] = parser.stats.rejected
    res["lost"] = parser.stats.lost


def ep_status(cam: Cam) -> str:
    """Standard GET_STATUS (USB 2.0 spec 9.4.5) of endpoint 0x81: bit 0 = Halt."""
    try:
        return bytes(cam.dev.ctrl_transfer(0x82, 0x00, 0, EP_IN, 2, 500)).hex()
    except usb.core.USBError as exc:
        return f"error {exc}"


def run_sync(cam: Cam, a, st: Stats, q, cv, stop) -> None:
    buf = array.array("B", bytes(a.size))
    inject = [float(x) for x in a.inject.split(",")] if a.inject else []
    next_inject = a.inject_every
    k = 0
    t_prev = time.perf_counter()
    while st.t() < a.seconds:
        if inject and st.t() >= next_inject:
            d = inject[k % len(inject)]
            k += 1
            next_inject += a.inject_every
            st.pending_inject = {"t": st.t(), "ms": d}
            st.inject.append(st.pending_inject)
            time.sleep(d / 1000)
        t_sub = time.perf_counter()
        st.gaps.append(t_sub - t_prev)
        try:
            n = cam.dev.read(EP_IN, buf, 1000)
        except usb.core.USBTimeoutError:
            st.ev("timeout")
            t_prev = time.perf_counter()
            continue
        except usb.core.USBError as exc:
            if exc.errno == errno.EPIPE or getattr(exc, "backend_error_code", None) == -9:
                st.reads.append((t_sub, time.perf_counter(), -1))
                st.stall(gap_before_ms=round((t_sub - t_prev) * 1000, 3),
                         read_ms=round((time.perf_counter() - t_sub) * 1000, 3))
                halted = ep_status(cam)
                # does the pipe work again without ClearFeature (WinUSB AUTO_CLEAR_STALL)?
                retry = "?"
                if a.retry_first:
                    try:
                        n2 = cam.dev.read(EP_IN, buf, a.retry_ms)
                        retry = f"ok {n2}"
                    except usb.core.USBError as exc2:
                        retry = f"error {exc2}"
                halted2 = ep_status(cam)
                t1 = time.perf_counter()
                cam.dev.clear_halt(EP_IN)
                st.ev("cleared", clear_ms=round((time.perf_counter() - t1) * 1000, 2),
                      ep_status_after_stall=halted, retry_without_clear=retry,
                      ep_status_after_retry=halted2, ep_status_after_clear=ep_status(cam))
                with cv:
                    q.append(None)
                    cv.notify()
                t_prev = time.perf_counter()
                continue
            raise
        t_done = time.perf_counter()
        st.reads.append((t_sub, t_done, n))
        st.nreads += 1
        st.bytes += n
        if n < a.size:
            st.short += 1
        chunk = bytes(memoryview(buf)[:n])
        with cv:
            q.append(chunk)
            cv.notify()
        t_prev = time.perf_counter()


def run_async(cam: Cam, a, st: Stats, q, cv, stop) -> None:
    lib = load_lib()
    be = cam.dev._ctx.backend
    ctx = be.ctx
    cam.dev._ctx.managed_open()
    handle = C.c_void_p(cam.dev._ctx.handle.handle.value)
    if a.raw_io:
        r = lib.libusb_endpoint_set_raw_io(handle, EP_IN, 1)
        mx = lib.libusb_get_max_raw_io_transfer_size(handle, EP_IN)
        st.ev("raw_io", set=r, max_transfer=mx)
        if r != 0:
            raise SystemExit(f"RAW_IO failed: {lib.libusb_error_name(r)}")
        if a.size > mx:
            raise SystemExit(f"size {a.size} > RAW_IO max {mx}")
    bufs = [(C.c_ubyte * a.size)() for _ in range(a.n)]
    xfers = []
    state = {"stalled": False, "inflight": 0, "running": True, "status": collections.Counter()}

    def on_done(tp) -> None:
        t_done = time.perf_counter()
        tr = tp.contents
        i = int(tr.user_data or 0)
        status, n = tr.status, tr.actual_length
        state["inflight"] -= 1
        state["status"][ST_NAMES[status] if status < 7 else str(status)] += 1
        if n > 0 and status in (ST_COMPLETED, ST_TIMED_OUT):
            chunk = C.string_at(bufs[i], n)
            st.reads.append((last_done[0], t_done, n))
            st.nreads += 1
            st.bytes += n
            if n < a.size:
                st.short += 1
            with cv:
                q.append(chunk)
                cv.notify()
        last_done[0] = t_done
        if status in (ST_STALL, ST_ERROR, ST_OVERFLOW):
            if not state["stalled"]:
                state["stalled"] = True
                st.reads.append((t_done, t_done, -1))
                st.stall(status=ST_NAMES[status], inflight=state["inflight"])
            return
        if status == ST_NO_DEVICE:
            state["running"] = False
            st.ev("no_device")
            return
        if state["running"] and not state["stalled"] and status != ST_CANCELLED:
            submit(i)

    cb = CB(on_done)
    last_done = [time.perf_counter()]

    def submit(i: int) -> None:
        tr = xfers[i].contents
        tr.status = 0
        tr.actual_length = 0
        r = lib.libusb_submit_transfer(xfers[i])
        if r != 0:
            st.ev("submit_error", err=lib.libusb_error_name(r).decode())
            state["stalled"] = True
            return
        state["inflight"] += 1

    for i in range(a.n):
        tp = lib.libusb_alloc_transfer(0)
        tr = tp.contents
        tr.dev_handle = handle.value
        tr.flags = 0
        tr.endpoint = EP_IN
        tr.type = LIBUSB_TRANSFER_TYPE_BULK
        tr.timeout = 1000
        tr.length = a.size
        tr.callback = cb
        tr.user_data = i
        tr.buffer = C.cast(bufs[i], C.c_void_p).value
        tr.num_iso_packets = 0
        xfers.append(tp)
    for i in range(a.n):
        submit(i)

    inject = [float(x) for x in a.inject.split(",")] if a.inject else []
    next_inject = a.inject_every
    k = 0
    tv = Timeval(0, 50_000)
    try:
        while st.t() < a.seconds and state["running"]:
            if inject and st.t() >= next_inject:
                d = inject[k % len(inject)]
                k += 1
                next_inject += a.inject_every
                st.pending_inject = {"t": st.t(), "ms": d}
                st.inject.append(st.pending_inject)
                time.sleep(d / 1000)
            lib.libusb_handle_events_timeout_completed(ctx, C.byref(tv), None)
            if state["stalled"]:
                # cancel what is still queued, wait for every callback, clear, resubmit all
                t1 = time.perf_counter()
                for tp in xfers:
                    lib.libusb_cancel_transfer(tp)
                while state["inflight"] > 0 and time.perf_counter() - t1 < 3:
                    lib.libusb_handle_events_timeout_completed(ctx, C.byref(tv), None)
                halted = ep_status(cam)
                r = lib.libusb_clear_halt(handle, EP_IN)
                st.ev("cleared", clear_ms=round((time.perf_counter() - t1) * 1000, 2), rc=r,
                      inflight_left=state["inflight"], ep_status_after_stall=halted,
                      ep_status_after_clear=ep_status(cam))
                with cv:
                    q.append(None)
                    cv.notify()
                state["stalled"] = False
                for i in range(a.n):
                    submit(i)
    finally:
        state["running"] = False
        for tp in xfers:
            lib.libusb_cancel_transfer(tp)
        t1 = time.perf_counter()
        while state["inflight"] > 0 and time.perf_counter() - t1 < 3:
            lib.libusb_handle_events_timeout_completed(ctx, C.byref(tv), None)
        for tp in xfers:
            lib.libusb_free_transfer(tp)
        if a.raw_io:
            lib.libusb_endpoint_set_raw_io(handle, EP_IN, 0)
        st.ev("async_status", counts=dict(state["status"]), inflight_left=state["inflight"])


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("label")
    ap.add_argument("--mode", choices=["sync", "async"], default="sync")
    ap.add_argument("--size", type=int, default=16384)
    ap.add_argument("--n", type=int, default=8)
    ap.add_argument("--raw-io", action="store_true")
    ap.add_argument("--seconds", type=float, default=300)
    ap.add_argument("--load", type=int, default=0)
    ap.add_argument("--inject", default="")
    ap.add_argument("--inject-every", type=float, default=10.0)
    ap.add_argument("--decode", action="store_true")
    ap.add_argument("--retry-ms", type=int, default=200)
    ap.add_argument("--retry-first", action="store_true",
                    help="sync: after a stall, read once more before clearing the halt")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "runs",
                                                  "probe.jsonl"))
    a = ap.parse_args()
    if a.size % 512:
        raise SystemExit("--size must be a multiple of 512 (max packet size)")
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    stop = threading.Event()
    q: collections.deque = collections.deque()
    cv = threading.Condition()
    res = {"frames": 0, "medians": [], "seq_last": None}
    with open(a.out, "a", encoding="utf-8") as f:
        st = Stats(f, a.label)
        f.write(json.dumps({"label": a.label, "start": time.strftime("%Y-%m-%d %H:%M:%S"),
                            "args": vars(a), "switchinterval": sys.getswitchinterval()}) + "\n")
        cam = Cam()
        loads = []
        pt = None
        try:
            fw = cam.send("read_fw")
            ffc = cam.send("get_auto_ffc_status")
            st.ev("info", fw=fw.split(b"\x00")[0].decode("ascii", "replace"), auto_ffc=ffc.hex(),
                  speed=getattr(cam.dev, "speed", None), bcd_usb=hex(cam.dev.bcdUSB))
            cam.start()
            st.t0 = time.perf_counter()
            pt = threading.Thread(target=parse_thread, args=(q, cv, stop, st, a.decode, res),
                                  daemon=True)
            pt.start()
            for _ in range(a.load):
                th = threading.Thread(target=busy, args=(stop,), daemon=True)
                th.start()
                loads.append(th)
            if a.mode == "sync":
                run_sync(cam, a, st, q, cv, stop)
            else:
                run_async(cam, a, st, q, cv, stop)
        finally:
            dur = st.t()
            stop.set()
            if pt is not None:
                pt.join(5)
            for th in loads:
                th.join(2)
            try:
                ffc_end = cam.send("get_auto_ffc_status").hex()
            except Exception as exc:  # noqa: BLE001
                ffc_end = f"error {exc}"
            cam.close()
        g = np.frombuffer(st.gaps, dtype=np.float64) * 1000 if len(st.gaps) else np.zeros(1)
        med = res["medians"]
        summ = {"label": a.label, "summary": True, "mode": a.mode, "size": a.size,
                "n": a.n if a.mode == "async" else 1, "raw_io": a.raw_io, "load": a.load,
                "inject": a.inject, "seconds": round(dur, 1), "stalls": len(st.stalls),
                "stall_t": [round(x, 2) for x in st.stalls],
                "stalls_per_min": round(len(st.stalls) / dur * 60, 3) if dur else None,
                "frames": res["frames"], "fps": round(res["frames"] / dur, 2) if dur else None,
                "mb_s": round(st.bytes / dur / 1e6, 2) if dur else None,
                "reads": st.nreads, "short_reads": st.short,
                "host_gap_ms_p50_p99_p999_max": [round(float(np.percentile(g, p)), 3)
                                                  for p in (50, 99, 99.9)]
                + [round(float(g.max()), 3)],
                "rejected": res.get("rejected"), "lost": res.get("lost"),
                "median_c_range": [min(med), max(med)] if med else None,
                "inject_results": st.inject, "auto_ffc_end": ffc_end}
        f.write(json.dumps(summ) + "\n")
    log(f"[{a.label}] SUMMARY {json.dumps(summ)}")


if __name__ == "__main__":
    main()
