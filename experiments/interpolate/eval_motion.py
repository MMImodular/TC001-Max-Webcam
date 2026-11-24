"""Score the denoiser and interpolator on a real 25 fps recording of a moving scene.

Written by Claude (Anthropic), 2026-11-23.  python -I eval_motion.py <runs/motion-...>

No 50 fps truth exists, so the interpolator is scored by holding out: every odd frame is rebuilt
from its two neighbours (a 12.5 -> 25 fps test, so twice the motion of the real 25 -> 50 case) and
compared with the real (denoised) frame. The denoiser has no truth on a moving scene either: its
noise is measured on still background pixels, and its lag on moving pixels as how far the output
trails the input.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path.home() / ".tc001max-ort"))
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "denoise"))
import onnxruntime as ort  # noqa: E402
from denoise import H, W, build_onnx  # noqa: E402
from interp import build  # noqa: E402

RUN = Path(sys.argv[1])
MK = 1000 / 64
B = 16
IN = (slice(B, -B), slice(B, -B))
f = np.fromfile(RUN / "frames.u16", "<u2").reshape(-1, H, W).astype(np.float32) * MK
t = np.fromfile(RUN / "times.f8", "<f8")
n = len(f)
prov = ["DmlExecutionProvider", "CPUExecutionProvider"]
dn = ort.InferenceSession(build_onnx(), providers=prov)
ip = ort.InferenceSession(build().to_onnx(), providers=prov)
zero = np.zeros((1, 1, H, W), np.float32)

# ---- denoise the whole clip
den = np.empty_like(f)
y = f[0][None, None]
for i in range(n):
    y, _ = dn.run(None, {"x": f[i][None, None], "y_prev": y, "corr": zero})
    den[i] = y[0, 0]
res: dict = {"run": RUN.name, "frames": n, "fps": round(float((n - 1) / (t[-1] - t[0])), 2)}
# still vs moving pixels, from the raw clip: frame-to-frame change of 1-s means
k = 25
m1 = np.array([f[i:i + k].mean(0) for i in range(0, n - k, k)])
change = np.abs(np.diff(m1, axis=0)).max(0)
still = change < 150                      # never moved more than 150 mK between 1-s means
res["still_px_frac"] = round(float(still[IN].mean()), 3)


def hp_std(a, mask):
    """Per-pixel std over time after removing a 2-s running mean, median over the mask."""
    c = np.cumsum(np.concatenate([np.zeros((1, H, W), np.float32), a]), axis=0)
    lo, hi = np.clip(np.arange(len(a)) - 25, 0, len(a)), np.clip(np.arange(len(a)) + 26, 0, len(a))
    hp = a - (c[hi] - c[lo]) / (hi - lo)[:, None, None]
    return float(np.median(hp[50:-50].std(0)[mask]))


inner = np.zeros((H, W), bool)
inner[IN] = True
res["denoiser"] = {"still_px_temporal_std_mk": {"raw": round(hp_std(f, still & inner), 1), "denoised": round(hp_std(den, still & inner), 1)}}
mov = (~still) & inner
if mov.any():
    d = (den - f)[:, mov]
    big = np.abs(f[1:] - f[:-1])[:, mov] > 1000       # pixels where the scene changed > 1 K this frame
    lagd = np.abs(d[1:])[big]
    res["denoiser"]["moving_px_abs_output_minus_input_mk"] = {"median": round(float(np.median(np.abs(d))), 1),
                                                             "where_scene_jumped_over_1K_median": round(float(np.median(lagd)), 1) if lagd.size else None,
                                                             "where_scene_jumped_over_1K_p99": round(float(np.percentile(lagd, 99)), 1) if lagd.size else None}

# ---- interpolator, held-out odd frames
err = {"repeat": [], "blend": [], "flow": []}
err_mov = {"repeat": [], "blend": [], "flow": []}
p999 = {"repeat": [], "blend": [], "flow": []}
flow_mag = []
for i in range(1, n - 1, 2):
    a, b, truth = den[i - 1], den[i + 1], den[i]
    mid, fu, fv = ip.run(None, {"a": a[None, None], "b": b[None, None]})
    flow_mag.append(float(np.percentile(np.hypot(fu, fv)[0, 0][IN], 99)))
    motion = np.abs(b - a) > 300
    for name, est in (("repeat", a), ("blend", (a + b) / 2), ("flow", mid[0, 0])):
        e = (est - truth)
        err[name].append(float(np.sqrt((e[IN] ** 2).mean())))
        p999[name].append(float(np.percentile(np.abs(e[IN]), 99.9)))
        mm = motion & inner
        if mm.sum() > 200:
            err_mov[name].append(float(np.sqrt((e[mm] ** 2).mean())))
res["interp_holdout_12p5_to_25fps"] = {
    "rms_mk": {k2: round(float(np.mean(v)), 1) for k2, v in err.items()},
    "rms_on_moving_px_mk": {k2: round(float(np.mean(v)), 1) for k2, v in err_mov.items()},
    "moving_frames_scored": len(err_mov["flow"]),
    "p99.9_abs_mk_median_over_frames": {k2: round(float(np.median(v)), 0) for k2, v in p999.items()},
    "flow_p99_px_per_2_frames": {"median": round(float(np.median(flow_mag)), 1), "max": round(float(np.max(flow_mag)), 1)},
}
# worst frame for a picture later
w = int(np.argmax(err_mov["flow"])) if err_mov["flow"] else 0
res["interp_worst_moving_rms_mk"] = round(float(max(err_mov["flow"])) if err_mov["flow"] else 0, 1)
(RUN / "eval_motion.json").write_text(json.dumps(res, indent=1))
print(json.dumps(res, indent=1))
