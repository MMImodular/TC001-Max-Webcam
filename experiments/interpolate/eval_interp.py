"""Measure the interpolator: 25 -> 50 fps on synthetic motion over a real thermal room scene.

Written by Claude (Anthropic), 2026-11-23.  python -I eval_interp.py [--onnx]

The background is a real TC001 Max picture (the room, 10-s mean from the 2026-11-22 night run), so
it has the camera's real texture. Motion is synthetic because no 25 fps recording of a moving scene
exists yet: pans at 1-10 px per 25 fps frame, and a warm person-sized ellipse (+8 K) crossing the
room (occlusion edges). Every frame gets 7 mK of white noise, about what the denoiser leaves.
Truth at the in-between instants is rendered noise-free; each method is scored against it:
  repeat = show frame A again (what 50 fps from a 25 fps source does without interpolation),
  blend  = (A + B) / 2,
  flow   = this interpolator.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from graph import _gridsample  # noqa: E402
from interp import H, W, P, build  # noqa: E402

MK = 1000 / 64
B = 16
IN = (slice(B, -B), slice(B, -B))
rng = np.random.default_rng(7)
NOISE = 7.0
run = HERE.parent / "noise" / "runs" / "raw" / "20261122-030000"
bg = np.fromfile(run / "win_mean.f4", "<f4", count=H * W).reshape(H, W).astype(np.float32) * MK
bg -= np.median(bg)
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)

for a_ in sys.argv[1:]:
    if a_.startswith("--set="):
        k_, v_ = a_[6:].split("=")
        setattr(P, k_, type(getattr(P, k_))(float(v_)))
OUT_TAG = "".join("_" + a_[6:] for a_ in sys.argv[1:] if a_.startswith("--set="))
g = build()
sess = None
if "--onnx" in sys.argv:
    sys.path.insert(0, str(Path.home() / ".tc001max-ort"))   # ONNX Runtime (DirectML), installed there 2026-11-23
    import onnxruntime as ort
    model = g.to_onnx()
    (HERE / "interp.onnx").write_bytes(model)
    sess = ort.InferenceSession(model, providers=["DmlExecutionProvider", "CPUExecutionProvider"])


def interp(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    f = {"a": a[None, None], "b": b[None, None]}
    if sess is not None:
        return sess.run(["mid"], f)[0][0, 0]
    return g.run(f)[0][0, 0]


def shifted(img: np.ndarray, dx: float) -> np.ndarray:
    grid = np.stack([(2 * (xx - dx) + 1) / W - 1, (2 * yy + 1) / H - 1], -1)[None]
    return _gridsample(img[None, None], grid)[0, 0]


def person(cx: float) -> np.ndarray:
    d = ((xx - cx) / 14) ** 2 + ((yy - 100) / 30) ** 2
    return (8000 * np.clip((1.15 - d) / 0.3, 0, 1)).astype(np.float32)   # soft 1-2 px edge


def score(name: str, frames_truth) -> dict:
    """frames_truth(k) -> noise-free frame at 50 fps instant k (even k = real 25 fps frames)."""
    out = {"repeat": [], "blend": [], "flow": []}
    band = {"repeat": [], "blend": [], "flow": []}
    worst = {"repeat": 0.0, "blend": 0.0, "flow": 0.0}
    for k in range(0, 16, 2):
        a = frames_truth(k) + rng.normal(0, NOISE, (H, W)).astype(np.float32)
        b = frames_truth(k + 2) + rng.normal(0, NOISE, (H, W)).astype(np.float32)
        t = frames_truth(k + 1)
        mask = None
        if name.startswith("person"):
            mask = (np.abs(person_at(k + 1) - 4000) < 3999)    # the person's edges and a little around
            mask = np.maximum.reduce([np.roll(np.roll(mask, i, 0), j, 1) for i in (-6, 0, 6) for j in (-6, 0, 6)])
        for m, est in (("repeat", a), ("blend", (a + b) / 2), ("flow", interp(a, b))):
            e = (est - t)[IN]
            out[m].append(float(np.sqrt((e ** 2).mean())))
            worst[m] = max(worst[m], float(np.percentile(np.abs(e), 99.9)))
            if mask is not None:
                band[m].append(float(np.sqrt(((est - t)[mask] ** 2).mean())))
    r = {m: round(float(np.mean(v)), 1) for m, v in out.items()}
    r["p99.9_abs_err"] = {m: round(v, 0) for m, v in worst.items()}
    if band["flow"]:
        r["rms_near_person_edges"] = {m: round(float(np.mean(v)), 0) for m, v in band.items()}
    return r


res: dict = {"noise_mk": NOISE, "params": {k: v for k, v in vars(P).items() if not k.startswith("_")}}
if sess is not None:   # the graph must match the NumPy reference
    _a = bg + rng.normal(0, NOISE, (H, W)).astype(np.float32)
    _b = shifted(bg, 4) + rng.normal(0, NOISE, (H, W)).astype(np.float32)
    _o = sess.run(None, {"a": _a[None, None], "b": _b[None, None]})
    _n = g.run({"a": _a[None, None], "b": _b[None, None]})
    res["onnx_check"] = {"providers": sess.get_providers(), "mid_max_abs_mk": float(np.abs(_o[0] - _n[0]).max()),
                         "mid_p99_abs_mk": float(np.percentile(np.abs(_o[0] - _n[0]), 99)),
                         "flow_u_max_abs_px": float(np.abs(_o[1] - _n[1]).max())}
res["static"] = score("static", lambda k: bg)
for d in (1, 3, 6, 10):
    res[f"pan_{d}px_per_frame"] = score("pan", lambda k, d=d: shifted(bg, d * k / 2))
for d in (4, 10):
    person_at = lambda k, d=d: person(60 + d * k / 2)  # noqa: E731
    res[f"person_{d}px_per_frame"] = score("person", lambda k, d=d: bg * 0 + np.maximum(bg, person(60 + d * k / 2) + bg))

a = bg + rng.normal(0, NOISE, (H, W)).astype(np.float32)
b = shifted(bg, 3) + rng.normal(0, NOISE, (H, W)).astype(np.float32)
ts = []
for i in range(30 if sess is None else 300):
    t0 = time.perf_counter()
    interp(a, b)
    ts.append(time.perf_counter() - t0)
ts = np.array(ts[3:]) * 1000
res["latency_ms_" + ("onnx" if sess is not None else "numpy_cpu")] = {"median": round(float(np.median(ts)), 2),
                                                                       "p99": round(float(np.percentile(ts, 99)), 2)}
res["graph_nodes"] = len(g.nodes)
(HERE / f"results_{'onnx' if sess is not None else 'numpy'}{OUT_TAG}.json").write_text(json.dumps(res, indent=1))
print(json.dumps(res, indent=1))
