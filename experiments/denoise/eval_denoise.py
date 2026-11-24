"""Measure the denoiser on the flat-plate recordings (2026-11-23, shutter every 30 s, 184 shutters).

Written by Claude (Anthropic), 2026-11-23.  python -I eval_denoise.py [run_dir] [--onnx]

Truth on the plate is a constant per pixel: the fine part of the mean of all post-shutter pictures
(this keeps the ~12 mK static floor, which no stage can see). Errors are rms over the inner region
(15 px border left out), per frame, in mK. Motion is checked on synthetic objects added to real
plate frames, since the plate recordings have none.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from denoise import H, W, Params, TableAverager, build_onnx, fine, step_numpy  # noqa: E402

RUN = Path(next((a for a in sys.argv[1:] if not a.startswith("--")),
                str(HERE.parent / "noise" / "runs" / "raw" / "20261123-125327")))
USE_ONNX = "--onnx" in sys.argv
MK = 1000 / 64
NPIX = H * W
K = 15
IN = (slice(K, -K), slice(K, -K))
NJ = int(next((a.split("=")[1] for a in sys.argv if a.startswith("--nj=")), 25))   # frames either side of a shutter used to measure its jump (25 = 1 s)
WARM = 50        # frames the filter gets to settle before it is scored
EVERY = 4        # score every 4th shutter's burst (46 bursts)


def load(name: str) -> np.ndarray:
    return np.fromfile(RUN / name, "<u2").reshape(-1, NPIX)


names = sorted((p.name[len("burst_"):-len("_pre_index.bin")] for p in RUN.glob("burst_ffc*_pre_index.bin")),
               key=lambda s: int(s[3:]))
# ---- pass 1: shutter jumps (1-s means) -> table corrections; post means -> truth
avg = TableAverager(4)
corr, post_means = [], []
for n in names:
    pre = load(f"burst_{n}_pre.u16")
    post = load(f"burst_{n}_post.u16")
    b = pre[-NJ:].astype(np.float32).mean(0).reshape(H, W) * MK
    a = post[:NJ].astype(np.float32).mean(0).reshape(H, W) * MK
    corr.append(avg.shutter(b, a))
    post_means.append(post[NJ:250].astype(np.float32).mean(0).reshape(H, W) * MK)
truth = np.mean([fine(p) for p in post_means], axis=0)[IN]
res: dict = {"run": RUN.name, "shutters": len(names), "jump_frames": NJ, "params": {k: v for k, v in vars(Params).items() if not k.startswith("_")}}
res["static_floor_mk"] = float(truth.std())

sess = None
if USE_ONNX:
    sys.path.insert(0, str(Path.home() / ".tc001max-ort"))   # ONNX Runtime (DirectML), installed there 2026-11-23
    import onnxruntime as ort
    model = build_onnx()
    (HERE / "tnr.onnx").write_bytes(model)
    sess = ort.InferenceSession(model, providers=["DmlExecutionProvider", "CPUExecutionProvider"])
    res["onnx_providers"] = sess.get_providers()
    # the graph must match the NumPy reference
    _p = load(f"burst_{names[0]}_post.u16")[:2].astype(np.float32).reshape(-1, H, W) * MK
    _y4, _cm = sess.run(None, {"x": _p[1][None, None], "y_prev": _p[0][None, None], "corr": corr[0][None, None]})
    _yn, _cmn = step_numpy(_p[1], _p[0], corr[0])
    res["onnx_vs_numpy_max_abs_mk"] = float(np.abs(_y4[0, 0] - _yn).max())
    res["onnx_vs_numpy_cm_diff_mk"] = float(abs(float(_cm.ravel()[0]) - _cmn))


def run_filter(frames: np.ndarray, c: np.ndarray, use_corr: bool = True) -> tuple[list[np.ndarray], list[float]]:
    cc = c if use_corr else np.zeros_like(c)
    y = frames[0] - cc
    ys, cms = [], []
    for x in frames:
        if sess is not None:
            y4, cm = sess.run(None, {"x": x[None, None], "y_prev": y[None, None], "corr": cc[None, None]})
            y, cm = y4[0, 0], float(cm.ravel()[0])
        else:
            y, cm = step_numpy(x, y, cc)
        ys.append(y)
        cms.append(cm)
    return ys, cms


def rms_err(img: np.ndarray) -> float:
    return float(np.sqrt(((fine(img)[IN] - truth) ** 2).mean()))


# ---- pass 2: score the stages on real plate frames
E = {"raw": [], "table": [], "table+flicker+filter": [], "flicker+filter (no table)": [], "table+flicker": []}
tstd = {"raw": [], "flicker removed": [], "filtered": []}
frame_mean_std = {"raw": [], "flicker removed": []}
for i in range(0, len(names), EVERY):
    post = load(f"burst_{names[i]}_post.u16")[NJ:250].astype(np.float32).reshape(-1, H, W) * MK
    c = corr[i]
    ys, cms = run_filter(post, c)
    ys_nc, _ = run_filter(post, c, use_corr=False)
    cms = np.array(cms)
    sc = slice(WARM, len(post))
    for t in range(WARM, len(post), 5):
        E["raw"].append(rms_err(post[t]))
        E["table"].append(rms_err(post[t] - c))
        E["table+flicker"].append(rms_err(post[t] - c - cms[t]))
        E["table+flicker+filter"].append(rms_err(ys[t]))
        E["flicker+filter (no table)"].append(rms_err(ys_nc[t]))
    x = post[sc][:, K:-K, K:-K]
    xf = x - cms[sc][:, None, None]
    y = np.array(ys)[sc][:, K:-K, K:-K]
    tstd["raw"].append(float(np.median(x.std(0))))
    tstd["flicker removed"].append(float(np.median(xf.std(0))))
    tstd["filtered"].append(float(np.median(y.std(0))))
    fm = x.mean(axis=(1, 2))
    frame_mean_std["raw"].append(float(fm.std()))
    frame_mean_std["flicker removed"].append(float(xf.mean(axis=(1, 2)).std()))
res["fine_error_vs_truth_mk"] = {k: round(float(np.mean(v)), 2) for k, v in E.items()}
res["per_pixel_temporal_std_mk"] = {k: round(float(np.mean(v)), 2) for k, v in tstd.items()}
res["frame_mean_std_mk"] = {k: round(float(np.mean(v)), 2) for k, v in frame_mean_std.items()}

# ---- motion: a warm disc (+2 K, radius 8 px) crossing real plate frames at 3 px/frame
post = load(f"burst_{names[10]}_post.u16")[:250].astype(np.float32).reshape(-1, H, W) * MK
yy, xx = np.mgrid[0:H, 0:W]
clean = np.zeros_like(post)
for t in range(len(post)):
    cx = 20 + 3 * (t - 60)
    clean[t] = np.where((yy - 96) ** 2 + (xx - cx) ** 2 <= 64, 2000.0, 0.0) if 60 <= t else 0
noisy = post + clean
ys, _ = run_filter(noisy, corr[10])
ys0, _ = run_filter(post, corr[10])          # the same frames without the disc
lag, ghost = [], []
for t in range(80, 120):
    cx = 20 + 3 * (t - 60)
    inside = (yy - 96) ** 2 + (xx - cx) ** 2 <= 36                       # disc interior
    trail = ((yy - 96) ** 2 + (xx - (cx - 30)) ** 2 <= 36)                 # where it was 10 frames ago
    dy = ys[t] - ys0[t]
    lag.append(float(2000 - dy[inside].mean()))
    ghost.append(float(dy[trail].mean()))
res["moving_disc_2K_3px_per_frame"] = {"mean_shortfall_inside_mk": round(float(np.mean(lag)), 1),
                                       "mean_ghost_10_frames_behind_mk": round(float(np.mean(ghost)), 1)}
# a 500 mK step appearing in a 40x40 patch: frames to 90 %
step = post.copy()
step[100:, 76:116, 108:148] += 500
ys, _ = run_filter(step, corr[10])
ys0 = np.array(ys0)
resp = [float((ys[t] - ys0[t])[86:106, 118:138].mean()) for t in range(100, 140)]
res["step_500mK_frames_to_90pct"] = next((i + 1 for i, v in enumerate(resp) if v >= 450), None)
res["step_500mK_response_first_8"] = [round(v, 0) for v in resp[:8]]

# ---- latency per frame
x0 = post[0]
yv = post[0].copy()
c0 = corr[10]
times = []
for t in range(300):
    x = post[t % len(post)]
    t0 = time.perf_counter()
    if sess is not None:
        y4, _ = sess.run(None, {"x": x[None, None], "y_prev": yv[None, None], "corr": c0[None, None]})
        yv = y4[0, 0]
    else:
        yv, _ = step_numpy(x, yv, c0)
    times.append(time.perf_counter() - t0)
times = np.array(times[20:]) * 1000
res["latency_ms" + ("_onnx" if sess is not None else "_numpy_cpu")] = {"median": round(float(np.median(times)), 3),
                                                                        "p99": round(float(np.percentile(times, 99)), 3)}
out = HERE / (("results_onnx" if sess is not None else "results_numpy") + f"_nj{NJ}.json")
out.write_text(json.dumps(res, indent=1))
print(json.dumps(res, indent=1))
