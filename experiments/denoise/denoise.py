"""TC001 Max denoiser prototype (experiment only; the shipped webcam path does not use it).

Written by Claude (Anthropic) for this repository, 2026-11-23. Own code; the methods are textbook:
a motion-adaptive recursive temporal filter (as in video temporal noise reduction: Dubois and Sabri,
IEEE Trans. Communications 32(7), 1984; Brailean et al., Proc. IEEE 83(9), 1995), a robust
common-mode (whole-frame flicker) estimate, and averaging of the camera's shutter offset tables
through the jumps they cause (both from this project's noise study, docs/NOISE_AND_CALIBRATION.md).
The ONNX bytes are written from the ONNX format and operator specification (github.com/onnx/onnx,
onnx.proto, Apache-2.0) and the protobuf wire-format documentation; no ONNX code is included.

Three stages, all in mK on the 192 x 256 temperature plane:

1. ``TableAverager`` (once per shutter, host side). Each shutter writes a new per-pixel offset table
   with ~30 mK of fresh random error (measured 2026-11-23 on the flat plate). The jump a shutter
   causes in a still scene is (new table - old table) in its fine part, so the running sum of the
   fine jumps is each table relative to the first; subtracting each table's difference from the mean
   of the last M tables (M = 4 measured best) averages the table errors.
2. Flicker removal (per frame): the whole frame moves together by ~18 mK per frame; its estimate is
   the mean of (frame - previous output) with each pixel's difference clipped to +-3 sigma, so moving
   objects barely pull it.
3. Motion-adaptive recursive filter (per frame): y += alpha * (x - y), alpha = a_min where the 3x3
   mean of |x - y| looks like noise, rising to 1 where it is clearly motion.

Stages 2 and 3 are one ONNX graph (``build_onnx``), written straight to protobuf bytes so no extra
package (onnx, PyTorch) is needed; ``step_numpy`` is the same maths in NumPy for checking it.
"""
from __future__ import annotations

import struct

import numpy as np

H, W = 192, 256


# ---------------------------------------------------------------------------------- parameters
class Params:
    sigma_mk = 32.0      # per-pixel temporal noise per frame (median, plate 2026-11-23)
    clip_sigma = 3.0     # flicker estimate: clip each pixel's difference at this many sigma
    a_min = 1 / 16       # blend on still pixels (time constant ~16 frames = 0.64 s)
    t0_sigma = 1.2       # 3x3 mean |x - y| below this many sigma = still
    t1_sigma = 3.0       # ... above this = moving (alpha = 1)


# ---------------------------------------------------------------------------------- stage 1
def fine(img: np.ndarray, k: int = 15) -> np.ndarray:
    """Remove the smooth part (double box blur of half-width k, edge-normalised)."""
    def box(a: np.ndarray, r: int, axis: int) -> np.ndarray:
        c = np.cumsum(np.pad(a, [(r + 1, r) if ax == axis else (0, 0) for ax in range(a.ndim)], mode="edge"), axis=axis)
        n = a.shape[axis]
        hi = np.take(c, np.arange(2 * r + 1, 2 * r + 1 + n), axis=axis)
        lo = np.take(c, np.arange(0, n), axis=axis)
        return (hi - lo) / (2 * r + 1)
    s = img - np.median(img)
    sm = s
    for _ in range(2):
        sm = box(box(sm, k, 0), k, 1)
    return s - sm


class TableAverager:
    """Keeps the running sum of fine shutter jumps and returns the correction to subtract."""

    def __init__(self, m: int = 4) -> None:
        self.m = m
        self.s = np.zeros((H, W), np.float32)
        self.hist: list[np.ndarray] = [self.s.copy()]

    def shutter(self, before_mean: np.ndarray, after_mean: np.ndarray) -> np.ndarray:
        """Means of a few frames just before and just after a shutter (mK). Returns the correction."""
        self.s = self.s + fine(after_mean - before_mean).astype(np.float32)
        self.hist = (self.hist + [self.s.copy()])[-self.m:]
        return self.s - np.mean(self.hist, axis=0)


# ---------------------------------------------------------------------------------- stages 2-3, NumPy
def avgpool3(a: np.ndarray) -> np.ndarray:
    p = np.pad(a, 1)
    ones = np.pad(np.ones_like(a), 1)
    s = sum(p[i:i + a.shape[0], j:j + a.shape[1]] for i in range(3) for j in range(3))
    n = sum(ones[i:i + a.shape[0], j:j + a.shape[1]] for i in range(3) for j in range(3))
    return s / n


def step_numpy(x: np.ndarray, y_prev: np.ndarray, corr: np.ndarray, p: type[Params] = Params) -> tuple[np.ndarray, float]:
    x1 = x - corr
    c = p.clip_sigma * p.sigma_mk
    cm = float(np.clip(x1 - y_prev, -c, c).mean())
    d2 = x1 - cm - y_prev
    m = avgpool3(np.abs(d2))
    a = np.clip((m - p.t0_sigma * p.sigma_mk) / ((p.t1_sigma - p.t0_sigma) * p.sigma_mk), 0, 1)
    alpha = p.a_min + (1 - p.a_min) * a
    return (y_prev + alpha * d2).astype(np.float32), cm


# ---------------------------------------------------------------------------------- stages 2-3, ONNX
def _varint(n: int) -> bytes:
    out = bytearray()
    n &= (1 << 64) - 1
    while True:
        b = n & 0x7F
        n >>= 7
        out.append(b | (0x80 if n else 0))
        if not n:
            return bytes(out)


def _key(field: int, wire: int) -> bytes:
    return _varint(field << 3 | wire)


def _vi(field: int, v: int) -> bytes:
    return _key(field, 0) + _varint(v)


def _ld(field: int, payload: bytes) -> bytes:
    return _key(field, 2) + _varint(len(payload)) + payload


def _str(field: int, s: str) -> bytes:
    return _ld(field, s.encode())


def _f32(field: int, v: float) -> bytes:
    return _key(field, 5) + struct.pack("<f", v)


FLOAT, INT64 = 1, 7
A_FLOAT, A_INT, A_INTS = 1, 2, 7


def _attr_i(name: str, v: int) -> bytes:
    return _ld(5, _str(1, name) + _vi(3, v) + _vi(20, A_INT))


def _attr_ints(name: str, vs: list[int]) -> bytes:
    return _ld(5, _str(1, name) + b"".join(_vi(8, v) for v in vs) + _vi(20, A_INTS))


def _node(op: str, ins: list[str], outs: list[str], name: str, attrs: bytes = b"") -> bytes:
    return _ld(1, b"".join(_str(1, i) for i in ins) + b"".join(_str(2, o) for o in outs) + _str(3, name) + _str(4, op) + attrs)


def _const(name: str, arr: np.ndarray) -> bytes:
    arr = np.ascontiguousarray(arr)
    dt = FLOAT if arr.dtype == np.float32 else INT64
    body = b"".join(_vi(1, d) for d in arr.shape) + _vi(2, dt) + _str(8, name) + _ld(9, arr.tobytes())
    return _ld(5, body)


def _value(field: int, name: str, shape: list[int]) -> bytes:
    dims = b"".join(_ld(1, _vi(1, d)) for d in shape)
    tensor = _vi(1, FLOAT) + _ld(2, dims)
    return _ld(field, _str(1, name) + _ld(2, _ld(1, tensor)))


def build_onnx(p: type[Params] = Params, h: int = H, w: int = W) -> bytes:
    """Inputs x, y_prev, corr ([1,1,h,w] float32, mK). Outputs y ([1,1,h,w]) and cm ([1,1,1,1])."""
    c = p.clip_sigma * p.sigma_mk
    f = lambda v: np.array(v, np.float32)  # noqa: E731
    inits = [_const("neg_c", f(-c)), _const("pos_c", f(c)), _const("t0", f(p.t0_sigma * p.sigma_mk)),
             _const("inv_t", f(1 / ((p.t1_sigma - p.t0_sigma) * p.sigma_mk))), _const("zero", f(0)),
             _const("one", f(1)), _const("one_m_amin", f(1 - p.a_min)), _const("amin", f(p.a_min))]
    nodes = [
        _node("Sub", ["x", "corr"], ["x1"], "n1"),
        _node("Sub", ["x1", "y_prev"], ["d"], "n2"),
        _node("Clip", ["d", "neg_c", "pos_c"], ["dc"], "n3"),
        _node("ReduceMean", ["dc"], ["cm"], "n4", _attr_ints("axes", [2, 3]) + _attr_i("keepdims", 1)),
        _node("Sub", ["d", "cm"], ["d2"], "n5"),
        _node("Abs", ["d2"], ["ad"], "n6"),
        _node("AveragePool", ["ad"], ["m"], "n7", _attr_ints("kernel_shape", [3, 3]) + _attr_ints("pads", [1, 1, 1, 1])
              + _attr_ints("strides", [1, 1]) + _attr_i("count_include_pad", 0)),
        _node("Sub", ["m", "t0"], ["mt"], "n8"),
        _node("Mul", ["mt", "inv_t"], ["ms"], "n9"),
        _node("Clip", ["ms", "zero", "one"], ["a"], "n10"),
        _node("Mul", ["a", "one_m_amin"], ["a2"], "n11"),
        _node("Add", ["a2", "amin"], ["alpha"], "n12"),
        _node("Mul", ["alpha", "d2"], ["upd"], "n13"),
        _node("Add", ["y_prev", "upd"], ["y"], "n14"),
    ]
    graph = (b"".join(nodes) + _str(2, "tc001max_tnr") + b"".join(inits)
             + _value(11, "x", [1, 1, h, w]) + _value(11, "y_prev", [1, 1, h, w]) + _value(11, "corr", [1, 1, h, w])
             + _value(12, "y", [1, 1, h, w]) + _value(12, "cm", [1, 1, 1, 1]))
    opset = _str(1, "") + _vi(2, 17)
    return _vi(1, 8) + _str(2, "tc001max-denoise") + _ld(7, graph) + _ld(8, opset)
