"""A tiny graph builder: the same graph is written as ONNX bytes and run in NumPy.

Written by Claude (Anthropic), 2026-11-23, for the TC001 Max experiments. Own code. The NumPy side
implements only the ONNX ops used here, following the ONNX operator spec (opset 17), so the CPU
measurements exercise the exact graph that ONNX Runtime would run on the GPU.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "denoise"))
from denoise import _attr_i, _attr_ints, _const, _key, _ld, _node, _str, _value, _vi  # noqa: E402

import struct  # noqa: E402


def _attr_f(name: str, v: float) -> bytes:
    return _ld(5, _str(1, name) + _key(2, 5) + struct.pack("<f", v) + _vi(20, 1))


def _attr_s(name: str, v: str) -> bytes:
    return _ld(5, _str(1, name) + _ld(4, v.encode()) + _vi(20, 3))


class Graph:
    def __init__(self, name: str) -> None:
        self.name = name
        self.nodes: list[tuple] = []        # (op, inputs, output, attrs dict)
        self.consts: dict[str, np.ndarray] = {}
        self.inputs: list[tuple[str, list[int]]] = []
        self.outputs: list[tuple[str, list[int]]] = []
        self._n = 0

    # ---- building
    def inp(self, name: str, shape: list[int]) -> str:
        self.inputs.append((name, shape))
        return name

    def out(self, name: str, shape: list[int]) -> None:
        self.outputs.append((name, shape))

    def const(self, arr, dtype=np.float32) -> str:
        self._n += 1
        n = f"c{self._n}"
        self.consts[n] = np.asarray(arr, dtype=dtype)
        return n

    def op(self, op: str, *ins: str, name: str | None = None, **attrs) -> str:
        self._n += 1
        o = name or f"t{self._n}"
        self.nodes.append((op, list(ins), o, attrs))
        return o

    # ---- ONNX
    def to_onnx(self) -> bytes:
        body = b""
        for i, (op, ins, o, attrs) in enumerate(self.nodes):
            ab = b""
            for k, v in attrs.items():
                if isinstance(v, str):
                    ab += _attr_s(k, v)
                elif isinstance(v, float):
                    ab += _attr_f(k, v)
                elif isinstance(v, (list, tuple)):
                    ab += _attr_ints(k, list(v))
                else:
                    ab += _attr_i(k, int(v))
            body += _node(op, ins, [o], f"n{i}", ab)
        body += _str(2, self.name)
        body += b"".join(_const(n, a) for n, a in self.consts.items())
        body += b"".join(_value(11, n, s) for n, s in self.inputs)
        body += b"".join(_value(12, n, s) for n, s in self.outputs)
        return _vi(1, 8) + _str(2, "tc001max-experiments") + _ld(7, body) + _ld(8, _str(1, "") + _vi(2, 17))

    # ---- NumPy
    def run(self, feeds: dict[str, np.ndarray]) -> list[np.ndarray]:
        env = dict(self.consts)
        env.update({k: np.asarray(v, np.float32) for k, v in feeds.items()})
        env[""] = None
        for op, ins, o, attrs in self.nodes:
            env[o] = OPS[op](*[env[i] for i in ins], **attrs)
        return [env[n] for n, _ in self.outputs]


# ---------------------------------------------------------------------------------- op semantics
def _avgpool(x, kernel_shape, strides=(1, 1), pads=(0, 0, 0, 0), count_include_pad=0):
    kh, kw = kernel_shape
    sh, sw = strides
    t, l, b, r = pads[0], pads[1], pads[2], pads[3]
    xp = np.pad(x, ((0, 0), (0, 0), (t, b), (l, r)))
    ones = np.pad(np.ones_like(x), ((0, 0), (0, 0), (t, b), (l, r)))
    oh = (xp.shape[2] - kh) // sh + 1
    ow = (xp.shape[3] - kw) // sw + 1
    s = np.zeros(x.shape[:2] + (oh, ow), np.float32)
    n = np.zeros_like(s)
    for i in range(kh):
        for j in range(kw):
            s += xp[:, :, i:i + sh * oh:sh, j:j + sw * ow:sw]
            n += ones[:, :, i:i + sh * oh:sh, j:j + sw * ow:sw]
    return s / (n if not count_include_pad else kh * kw)


def _conv(x, w, pads=(0, 0, 0, 0)):
    t, l, b, r = pads
    xp = np.pad(x, ((0, 0), (0, 0), (t, b), (l, r)))
    kh, kw = w.shape[2:]
    oh, ow = xp.shape[2] - kh + 1, xp.shape[3] - kw + 1
    out = np.zeros((x.shape[0], w.shape[0], oh, ow), np.float32)
    for i in range(kh):
        for j in range(kw):
            out += w[None, :, 0, i, j, None, None] * xp[:, 0:1, i:i + oh, j:j + ow]
    return out


def _pad(x, pads, value=None, mode="constant"):
    p = np.asarray(pads, int)
    n = x.ndim
    return np.pad(x, [(p[i], p[i + n]) for i in range(n)], mode="edge" if mode == "edge" else "constant")


def _gridsample(x, grid, mode="bilinear", padding_mode="border", align_corners=0):
    n, c, h, w = x.shape
    gx = ((grid[..., 0] + 1) * w - 1) / 2
    gy = ((grid[..., 1] + 1) * h - 1) / 2
    gx = np.clip(gx, 0, w - 1)
    gy = np.clip(gy, 0, h - 1)
    x0 = np.floor(gx).astype(int)
    y0 = np.floor(gy).astype(int)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fx = (gx - x0)[:, None]
    fy = (gy - y0)[:, None]
    out = np.empty((n, c) + gx.shape[1:], np.float32)
    for b in range(n):
        im = x[b]
        out[b] = ((im[:, y0[b], x0[b]] * (1 - fx[b]) + im[:, y0[b], x1[b]] * fx[b]) * (1 - fy[b])
                  + (im[:, y1[b], x0[b]] * (1 - fx[b]) + im[:, y1[b], x1[b]] * fx[b]) * fy[b])
    return out


def _resize(x, roi, scales, mode="nearest"):
    s = np.asarray(scales).astype(int)
    return np.repeat(np.repeat(x, s[2], axis=2), s[3], axis=3)


OPS = {
    "Add": lambda a, b: a + b, "Sub": lambda a, b: a - b, "Mul": lambda a, b: a * b, "Div": lambda a, b: a / b,
    "Abs": np.abs, "Neg": np.negative,
    "Clip": lambda x, lo, hi: np.clip(x, lo, hi),
    "AveragePool": _avgpool, "Conv": _conv, "Pad": _pad, "GridSample": _gridsample, "Resize": _resize,
    "Reshape": lambda x, s: x.reshape(np.asarray(s, int)),
    "Concat": lambda *xs, axis: np.concatenate(xs, axis=axis),
    "Max": np.maximum, "Min": np.minimum,
}
