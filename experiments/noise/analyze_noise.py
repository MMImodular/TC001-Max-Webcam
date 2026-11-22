"""Analysis of a noise_capture.py run: temporal noise, noise spectrum and per-pixel drift.

Not shipped. Origin: written for this project (2026-11-21). Methods (see
docs/NOISE_AND_CALIBRATION.md for the references): the temporal / row / column split follows the
"3D noise" model (D'Agostino & Webb, SPIE 1488, 1991); the Allan deviation is Allan's (Proc. IEEE
54(2), 1966); the PSD is Welch's averaged periodogram written with numpy. No code was copied.

The scene is static but not uniform, so drift is measured per pixel against that pixel's own
start, never against its neighbours:

  D_k(p) = M_k(p) - M_0(p)       M_k = the 10-s mean frame k, M_0 = the first after the shutter
       = g_k                      the global offset (median over the frame)
       + s_k(p)                   a smooth field: the scene really cooling or warming, unevenly
       + r_k(p)                   per-pixel drift: what the shutter would have to remove

s_k is a smoothed copy of D_k - g_k (two box blurs, --smooth-px wide, numpy only). r_k is the
fixed-pattern growth. The end-of-night manual shutter checks it independently: the shutter is a
uniform reference, so the jump J(p) = mean(after) - mean(before), minus its median and its
smooth part, is the per-pixel offset error the camera had built up; it should match -r_final.

Section 5 (runs with --ffc-every-min, added 2026-11-22): every shutter's jump is minus the drift
since the previous one. Per-pixel linear models of that drift (in elapsed time, in the change of
the camera's temperature word 65, or both), the idea of the shutterless-NUC papers (Nugent et
al. 2013, Tempelhahn et al. 2016), are fitted on the odd-numbered shutters and scored on the
even-numbered ones.

usage: analyze_noise.py <run_dir> [--smooth-px 15]
Writes report.md, analysis.json and *.png into the run directory.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

H, W = 192, 256
NPIX = H * W
MK = 1000 / 64          # raw count -> mK

SERIES_DT = np.dtype([("t", "<f8"), ("wall", "<f8"), ("seq", "<u4"), ("present", "u1"),
                      ("median_c", "<f4"), ("mean_c", "<f4"), ("std_c", "<f4"),
                      ("px", "<u2", (64,))])
WIN_DT = np.dtype([("t0", "<f8"), ("t1", "<f8"), ("n", "<u4"), ("present", "u1")])
IDX_DT = np.dtype([("t", "<f8"), ("wall", "<f8"), ("seq", "<u4"), ("present", "u1")])


def box(img: np.ndarray, k: int) -> np.ndarray:
    """Mean over a k x k box with edge padding (separable, cumulative sums)."""
    r = k // 2
    p = np.pad(img, r, mode="edge")
    c = np.cumsum(np.pad(p, ((1, 0), (0, 0))), axis=0)
    p = (c[k:] - c[:-k]) / k
    c = np.cumsum(np.pad(p, ((0, 0), (1, 0))), axis=1)
    return (c[:, k:] - c[:, :-k]) / k


def smooth(img: np.ndarray, k: int) -> np.ndarray:
    return box(box(img, k), k)


def welch(x: np.ndarray, fs: float, nper: int) -> tuple[np.ndarray, np.ndarray]:
    """One-sided PSD (units^2/Hz), Hann window, 50 % overlap."""
    x = np.asarray(x, np.float64)
    nper = min(nper, len(x))
    step = nper // 2
    win = np.hanning(nper)
    scale = fs * (win ** 2).sum()
    segs = [x[i:i + nper] for i in range(0, len(x) - nper + 1, step)]
    ps = np.mean([np.abs(np.fft.rfft((s - s.mean()) * win)) ** 2 for s in segs], axis=0) / scale
    ps[1:-1] *= 2
    return np.fft.rfftfreq(nper, 1 / fs), ps


def adev(x: np.ndarray, dt: float, max_frac: float = 0.25) -> tuple[np.ndarray, np.ndarray]:
    """Overlapping Allan deviation at octave-spaced tau."""
    x = np.asarray(x, np.float64)
    taus, devs = [], []
    m = 1
    while m <= len(x) * max_frac:
        c = np.cumsum(np.concatenate([[0], x]))
        avg = (c[m:] - c[:-m]) / m
        d = avg[m:] - avg[:-m]
        if len(d) < 2:
            break
        taus.append(m * dt)
        devs.append(np.sqrt(0.5 * np.mean(d ** 2)))
        m *= 2
    return np.array(taus), np.array(devs)


def load(run: Path, name: str, dtype, shape=None) -> np.ndarray | None:
    p = run / name
    if not p.exists() or p.stat().st_size == 0:
        return None
    a = np.fromfile(p, dtype=dtype)
    return a.reshape(-1, *shape) if shape else a


def burst_noise(frames: np.ndarray) -> dict:
    """3D-noise split of a full-rate burst (raw counts in, mK out), each pixel detrended."""
    f = frames.astype(np.float64).reshape(len(frames), H, W)
    n = len(f)
    tt = np.arange(n) - (n - 1) / 2
    slope = (f * tt[:, None, None]).sum(0) / (tt ** 2).sum()
    f = f - f.mean(0) - tt[:, None, None] * slope          # each pixel: remove mean and trend
    frame_mean = f.mean(axis=(1, 2))                       # sigma_t: whole-frame flicker
    rows = f.mean(axis=2) - frame_mean[:, None]            # sigma_tv: rows flickering
    cols = f.mean(axis=1) - frame_mean[:, None]            # sigma_th: columns flickering
    rest = f - frame_mean[:, None, None] - rows[:, :, None] - cols[:, None, :]
    per_px = f.std(axis=0, ddof=1)
    return {"frames": n, "per_pixel_std_mk_median": float(np.median(per_px) * MK),
            "per_pixel_std_mk_p95": float(np.percentile(per_px, 95) * MK),
            "sigma_t_mk": float(frame_mean.std() * MK), "sigma_tv_mk": float(rows.std() * MK),
            "sigma_th_mk": float(cols.std() * MK), "sigma_tvh_mk": float(rest.std() * MK),
            "_map": per_px * MK}


def periodic_pairs(run: Path, n_avg: int = 250) -> list[dict]:
    """Every shutter with frames saved on both sides: the periodic ones (ffcNN_pre / ffcNN_post,
    from --ffc-every-min) and the end one (pre_ffc / post_ffc). Each: time t (s since the start
    shutter), the jump (after minus before, raw counts, n_avg-frame means) and the times of its
    before / after frames. Empty when no periodic shutter ran."""
    names = sorted(p.name[len("burst_"):-len("_pre_index.bin")] for p in run.glob("burst_ffc*_pre_index.bin"))
    out = []
    for name in names + ["end"]:
        pre_n, post_n = (f"{name}_pre", f"{name}_post") if name != "end" else ("pre_ffc", "post_ffc")
        pre = load(run, f"burst_{pre_n}.u16", "<u2", (NPIX,))
        post = load(run, f"burst_{post_n}.u16", "<u2", (NPIX,))
        pi = load(run, f"burst_{pre_n}_index.bin", IDX_DT)
        qi = load(run, f"burst_{post_n}_index.bin", IDX_DT)
        if pre is None or post is None or pi is None or qi is None or len(pre) < 25 or len(post) < 25:
            continue
        jump = post[:n_avg].astype(np.float64).mean(0) - pre[-n_avg:].astype(np.float64).mean(0)
        out.append({"name": name, "t_pre": float(pi["t"][min(len(pi), len(pre)) - 1]),
                    "t_post": float(qi["t"][0]), "t": float((pi["t"][-1] + qi["t"][0]) / 2),
                    "jump": jump.reshape(H, W)})
    return out if len(out) > 1 else []


def surface_basis(k: int) -> np.ndarray:
    """Level, tilts and curvatures in x and y over the inner region (the shading model)."""
    yy, xx = np.mgrid[0:H, 0:W]
    x = (xx[k:-k, k:-k].ravel() - W / 2) / W
    y = (yy[k:-k, k:-k].ravel() - H / 2) / H
    return np.column_stack([np.ones_like(x), x, y, x * x, y * y])


def drift_models(run: Path, pairs: list[dict], k: int, out: dict, plt) -> list[str]:
    """Section 5. Each shutter's jump is minus the drift built up since the shutter before it,
    over a known time dt and a known change dT of the camera temperature (metadata word 65).
    Per-pixel models are fitted on the odd-numbered shutters and tested on the even-numbered
    ones: how much of each held-out jump would the model have removed without the shutter?"""
    meta = load(run, "meta_1hz.u16", "<u2", (256,))
    mi = load(run, "frames_1hz_index.bin", IDX_DT)
    n = min(len(meta), len(mi))
    w65, tm = meta[:n, 65].astype(np.float64), mi["t"][:n]

    def temp_at(t0: float, t1: float) -> float:
        s = (tm >= t0) & (tm <= t1)
        return float(w65[s].mean()) if s.any() else float(np.interp((t0 + t1) / 2, tm, w65))

    inner = (slice(k, -k), slice(k, -k))
    prev_t, prev_T = 0.0, temp_at(0.0, 10.0)
    rows = []
    for p in pairs:
        T_before = temp_at(p["t_pre"] - 10, p["t_pre"])
        J = p["jump"] * MK
        J = J - np.median(J)
        rows.append({"name": p["name"], "t": p["t"], "dt_h": (p["t"] - prev_t) / 3600, "dT": T_before - prev_T,
                     "drift": -J[inner].ravel()})
        prev_t, prev_T = p["t_post"], temp_at(p["t_post"], p["t_post"] + 10)
    D = np.array([r["drift"] for r in rows])                 # shutters x pixels, mK
    dt_h = np.array([r["dt_h"] for r in rows])
    dT = np.array([r["dT"] for r in rows])
    train = np.arange(len(rows)) % 2 == 0                    # shutters 1, 3, 5... (1-based)
    test = ~train
    B = surface_basis(k)
    proj = B @ np.linalg.pinv(B)                             # pixels -> shading surface
    D_shade = D @ proj.T
    D_fine = D - D_shade

    def fit_predict(X_tr: np.ndarray, X_te: np.ndarray, Y: np.ndarray) -> np.ndarray:
        """Per-pixel least squares through the origin: Y_train = X_train @ C, predict X_test @ C."""
        C, *_ = np.linalg.lstsq(X_tr, Y[train], rcond=None)
        return X_te @ C

    models = {
        "time only (rate x dt)": dt_h[:, None],
        "camera temperature (coef x dT)": dT[:, None],
        "temperature and time": np.column_stack([dT, dt_h]),
    }
    res: dict = {"shutters": [{kk: (round(v, 4) if isinstance(v, float) else v) for kk, v in r.items() if kk != "drift"}
                              for r in rows],
                 "train": int(train.sum()), "test": int(test.sum())}
    lines = ["## 5. Predicting the drift between shutters", "",
             f"{len(rows)} shutters with frames on both sides; each jump is minus the drift since the "
             f"shutter before it, over dt hours and dT counts of metadata word 65 (the camera's "
             f"temperature). Models are fitted per pixel on shutters 1, 3, 5... ({int(train.sum())}) "
             f"and tested on 2, 4, 6... ({int(test.sum())}). Shading = the 5-term surface (level, "
             f"tilts, curvatures); fine = the rest. Borders of {k} px left out.", "",
             "| shutter | at (h) | dt (h) | dT (counts) | drift rms (mK) | shading rms | fine rms |",
             "|---|---|---|---|---|---|---|"]
    for i, r in enumerate(rows):
        lines.append(f"| {r['name']}{'' if train[i] else ' (test)'} | {r['t'] / 3600:.2f} | {r['dt_h']:.2f} | "
                     f"{r['dT']:+.1f} | {np.sqrt((D[i] ** 2).mean()):.1f} | {np.sqrt((D_shade[i] ** 2).mean()):.1f} | "
                     f"{np.sqrt((D_fine[i] ** 2).mean()):.1f} |")
    lines += [""]
    if train.sum() < 2 or test.sum() < 1:
        lines += ["Too few shutters to fit and test a model.", ""]
        out["drift_models"] = res
        return lines
    lines += ["| model | held-out drift rms left (mK) | of which shading | fine | share removed |",
              "|---|---|---|---|---|"]
    base = np.sqrt((D[test] ** 2).mean())
    lines.append(f"| no correction | {base:.1f} | {np.sqrt((D_shade[test] ** 2).mean()):.1f} | "
                 f"{np.sqrt((D_fine[test] ** 2).mean()):.1f} | 0 % |")
    res["models"] = {"none": {"rms_mk": float(base)}}
    left_by_model = {}
    for name, X in models.items():
        if X.shape[1] >= train.sum():
            continue
        pred = fit_predict(X[train], X[test], D)
        left = D[test] - pred
        rms = float(np.sqrt((left ** 2).mean()))
        rms_s = float(np.sqrt(((left @ proj.T) ** 2).mean()))
        rms_f = float(np.sqrt(((left - left @ proj.T) ** 2).mean()))
        share = 1 - rms ** 2 / base ** 2
        res["models"][name] = {"rms_mk": rms, "shading_rms_mk": rms_s, "fine_rms_mk": rms_f, "share_removed": share}
        left_by_model[name] = np.sqrt((left ** 2).mean(1))
        lines.append(f"| {name} | {rms:.1f} | {rms_s:.1f} | {rms_f:.1f} | {100 * share:.0f} % |")
    lines += ["", "Share removed = 1 - (rms left / rms with no correction)^2 over the held-out shutters. "
              "The shutter's own noise sets a floor no model can beat; the fine part includes it.", ""]
    out["drift_models"] = res
    # figure: drift per held-out shutter with and without each model; shading vs dT
    fig, ax = plt.subplots(1, 2, figsize=(11, 3.8))
    ti = np.flatnonzero(test)
    xs = np.arange(len(ti))
    wbar = 0.8 / (1 + len(left_by_model))
    ax[0].bar(xs, np.sqrt((D[test] ** 2).mean(1)), wbar, label="no correction")
    for j, (name, v) in enumerate(left_by_model.items(), start=1):
        ax[0].bar(xs + j * wbar, v, wbar, label=name)
    ax[0].set_xticks(xs + wbar * len(left_by_model) / 2, [rows[i]["name"] for i in ti], rotation=45)
    ax[0].set_ylabel("drift rms left (mK)")
    ax[0].set_title("held-out shutters")
    ax[0].legend(fontsize=8)
    amp = (np.linalg.pinv(B) @ D.T)[2]                       # the y-tilt term of each shutter's shading
    ax[1].plot(dT[train], amp[train], "o", label="fit shutters")
    ax[1].plot(dT[test], amp[test], "s", label="test shutters")
    ax[1].axhline(0, color="0.7", lw=0.8)
    ax[1].set_xlabel("dT since the previous shutter (word 65 counts)")
    ax[1].set_ylabel("top-to-bottom tilt (mK per image height)")
    ax[1].set_title("shading vs camera temperature")
    ax[1].legend(fontsize=8)
    fig.tight_layout()
    fig.savefig(run / "drift_models.png", dpi=110)
    plt.close(fig)
    lines += ["![drift models](drift_models.png)", ""]
    return lines


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("run", type=Path)
    ap.add_argument("--smooth-px", type=int, default=15)
    a = ap.parse_args()
    run = a.run
    import matplotlib  # noqa: PLC0415
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt  # noqa: PLC0415

    out: dict = {"run": run.name}
    lines = [f"# Noise study {run.name}", ""]
    events = [json.loads(l) for l in (run / "events.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
    summary = json.loads((run / "summary.json").read_text(encoding="utf-8")) if (run / "summary.json").exists() else {}
    kinds = {}
    for e in events:
        kinds[e["kind"]] = kinds.get(e["kind"], 0) + 1
    fw_ffc = [e for e in events if e["kind"] == "debuglog" and ("shutter" in e["text"] or "ffc" in e["text"])]
    out["events"] = kinds
    out["capture"] = summary

    series = load(run, "series.bin", SERIES_DT)
    wmean = load(run, "win_mean.f4", "<f4", (NPIX,))
    widx = load(run, "win_index.bin", WIN_DT)
    if series is None or wmean is None:
        lines.append("No recording in this run (see run.log and events.jsonl).")
        (run / "report.md").write_text("\n".join(lines), encoding="utf-8")
        return 1
    nwin = min(len(wmean), len(widx))
    wmean, widx = wmean[:nwin], widx[:nwin]
    wvar = load(run, "win_var.f4", "<f4", (NPIX,))[:nwin]
    hours = series["t"][-1] / 3600
    lines += ["## The run", "",
              f"- Recorded {hours:.2f} h, {len(series)} frames "
              f"({len(series) / max(series['t'][-1], 1):.2f} fps), {nwin} windows of 10 s.",
              f"- Capture counters: {summary.get('stats', {})}.",
              f"- Firmware log lines about the shutter during the run: {len(fw_ffc)} "
              f"(the two manual shutters are expected; more would mean the auto shutter ran).",
              f"- Stalls {kinds.get('stall', 0)}, gaps {kinds.get('gap', 0)}, reopen {kinds.get('reopen', 0)}.",
              f"- Seconds with a person seen: {int(series['present'].sum() / 25)} (those windows are left out below).",
              ""]

    # ---- 1. temporal noise ------------------------------------------------------------------
    lines += ["## 1. Temporal noise (per pixel, 60-s bursts at 25 fps, each pixel detrended)", "",
              "| burst | at (h) | per-pixel std median (mK) | p95 | frame flicker σt | row σtv | column σth | random σtvh |",
              "|---|---|---|---|---|---|---|---|"]
    bursts = {}
    for p in sorted(run.glob("burst_*_index.bin"), key=lambda q: float(np.fromfile(q, IDX_DT, count=1)["t"][0])):
        name = p.name[len("burst_"):-len("_index.bin")]
        fr = load(run, f"burst_{name}.u16", "<u2", (NPIX,))
        idx = load(run, p.name, IDX_DT)
        if fr is None or len(fr) < 50:
            continue
        n = min(len(fr), len(idx))
        b = burst_noise(fr[:n])
        b["t_h"] = float(idx["t"][0] / 3600)
        bursts[name] = b
        lines.append(f"| {name} | {b['t_h']:.2f} | {b['per_pixel_std_mk_median']:.1f} | {b['per_pixel_std_mk_p95']:.1f} | "
                     f"{b['sigma_t_mk']:.1f} | {b['sigma_tv_mk']:.1f} | {b['sigma_th_mk']:.1f} | {b['sigma_tvh_mk']:.1f} |")
    out["bursts"] = {k: {kk: vv for kk, vv in v.items() if not kk.startswith("_")} for k, v in bursts.items()}
    good = widx["present"] == 0
    wstd_med = np.median(np.sqrt(wvar), axis=1) * MK
    lines += ["", f"Per-pixel std within each 10-s window, median over pixels: "
              f"{np.median(wstd_med[good]):.1f} mK (min {wstd_med[good].min():.1f}, max {wstd_med[good].max():.1f}).", ""]
    if "start" in bursts:
        fig, ax = plt.subplots(1, 2, figsize=(11, 3.6))
        im = ax[0].imshow(bursts["start"]["_map"], cmap="viridis",
                          vmax=np.percentile(bursts["start"]["_map"], 99))
        fig.colorbar(im, ax=ax[0], label="mK")
        ax[0].set_title("temporal noise per pixel (start burst)")
        ax[1].plot(widx["t0"] / 3600, wstd_med)
        ax[1].set_xlabel("hours since shutter")
        ax[1].set_ylabel("mK")
        ax[1].set_title("median per-pixel std in 10-s windows")
        fig.tight_layout()
        fig.savefig(run / "temporal_noise.png", dpi=110)
        plt.close(fig)
        lines += ["![temporal noise](temporal_noise.png)", ""]

    # ---- 2. spectrum and Allan deviation ----------------------------------------------------
    fs = 25.0
    t = series["t"]
    px = series["px"].astype(np.float64)
    common = np.median(px, axis=1)
    rel = px - common[:, None]           # each sample pixel against the frame's common mode
    lines += ["## 2. Spectrum and Allan deviation (64 sample pixels at 25 fps, the whole night)", ""]
    fig, ax = plt.subplots(1, 2, figsize=(11, 3.8))
    f_, p_mean = welch(series["mean_c"] * 1000, fs, 2 ** 14)
    ax[0].loglog(f_[1:], p_mean[1:], label="frame mean")
    ps = np.mean([welch(rel[:, i] * MK, fs, 2 ** 14)[1] for i in range(rel.shape[1])], axis=0)
    ax[0].loglog(f_[1:], ps[1:], label="pixel − frame (mean of 64)")
    ax[0].set_xlabel("Hz")
    ax[0].set_ylabel("mK²/Hz")
    ax[0].legend()
    ax[0].set_title("PSD (Welch)")
    tau_m, ad_m = adev(series["mean_c"] * 1000, 1 / fs)
    ads = [adev(rel[:, i] * MK, 1 / fs) for i in range(rel.shape[1])]
    tau_p = ads[0][0]
    ad_p = np.median([d for _, d in ads], axis=0)
    ax[1].loglog(tau_m, ad_m, "o-", label="frame mean")
    ax[1].loglog(tau_p, ad_p, "o-", label="pixel − frame (median of 64)")
    ax[1].set_xlabel("averaging time τ (s)")
    ax[1].set_ylabel("Allan deviation (mK)")
    ax[1].legend()
    ax[1].set_title("Allan deviation")
    fig.tight_layout()
    fig.savefig(run / "spectrum_allan.png", dpi=110)
    plt.close(fig)
    best = int(np.argmin(ad_p))
    out["allan"] = {"pixel_rel_tau_s": tau_p.tolist(), "pixel_rel_mk": ad_p.tolist(),
                    "frame_mean_tau_s": tau_m.tolist(), "frame_mean_mk": ad_m.tolist()}
    lines += [f"- A single pixel (against the frame) averages down to {ad_p[best]:.1f} mK at τ = {tau_p[best]:.0f} s; "
              f"beyond that drift wins (Allan deviation rises again if τ_min is not the longest τ).",
              f"- Allan deviation at 0.04 s {ad_p[0]:.1f} mK, at 1 s {np.interp(1, tau_p, ad_p):.1f} mK.", "",
              "![spectrum and Allan deviation](spectrum_allan.png)", ""]

    # ---- 3. per-pixel drift -----------------------------------------------------------------
    k = a.smooth_px
    pairs = periodic_pairs(run)
    if pairs and (good & (widx["t1"] <= pairs[0]["t"])).sum() >= 6:
        # periodic shutters reset the drift: section 3 covers the stretch before the first
        good = good & (widx["t1"] <= pairs[0]["t"])
    use = np.flatnonzero(good)
    m0 = wmean[use[0]].reshape(H, W).astype(np.float64)
    g, s_std, r_std, r_list = [], [], [], []
    for i in use:
        d = wmean[i].reshape(H, W) - m0
        gk = float(np.median(d))
        sk = smooth(d - gk, k)
        rk = d - gk - sk
        g.append(gk * MK)
        s_std.append(float(np.std(sk)) * MK)
        r_std.append(float(np.std(rk[k:-k, k:-k])) * MK)
        r_list.append(rk.astype(np.float32))   # ~1260 windows a night: keep it to ~250 MB
    th = widx["t0"][use] / 3600
    r_arr = np.array(r_list)                     # windows x H x W
    r_final = r_arr[-min(6, len(r_arr)):].mean(0)  # the last minute
    tt = th - th.mean()
    slope = np.tensordot(tt, r_arr, axes=(0, 0)) / max((tt ** 2).sum(), 1e-12) * MK   # mK per hour
    inner = (slice(k, -k), slice(k, -k))
    scene_hp = (m0 - smooth(m0, k))[inner].ravel()
    corr_scene = float(np.corrcoef(scene_hp, r_final[inner].ravel())[0, 1]) if scene_hp.std() > 0 else float("nan")
    out["drift"] = {"hours": float(th[-1]), "global_offset_mk_end": g[-1],
                    "smooth_field_std_mk_end": s_std[-1], "per_pixel_drift_std_mk_end": r_std[-1],
                    "per_pixel_rate_mk_per_h_median_abs": float(np.median(np.abs(slope[inner]))),
                    "per_pixel_rate_mk_per_h_p99_abs": float(np.percentile(np.abs(slope[inner]), 99)),
                    "corr_with_scene_detail": corr_scene}
    lines += ["## 3. Per-pixel drift since the start shutter (static, non-uniform scene)", ""]
    if pairs:
        lines += [f"Periodic shutters ran during this run, so this section covers only the "
                  f"{th[-1]:.2f} h before the first one; section 5 covers the rest.", ""]
    lines += [
              f"Each 10-s mean frame minus the first, split into a global offset, a smooth field "
              f"({k}-px double box blur) and the per-pixel remainder (borders of {k} px left out).", "",
              f"- Global offset after {th[-1]:.2f} h: {g[-1]:+.0f} mK.",
              f"- Smooth field (the scene changing unevenly, or large-scale drift) std: {s_std[-1]:.0f} mK.",
              f"- **Per-pixel drift std: {r_std[-1]:.1f} mK** (temporal noise of a 10-s mean is about "
              f"{np.median(wstd_med[good]) / np.sqrt(250):.1f} mK, so this is drift, not noise, if it is much bigger).",
              f"- Per-pixel drift rate (linear fit): median |rate| {out['drift']['per_pixel_rate_mk_per_h_median_abs']:.1f} mK/h, "
              f"99th percentile {out['drift']['per_pixel_rate_mk_per_h_p99_abs']:.1f} mK/h.",
              f"- Correlation of the final per-pixel drift with the scene's own fine detail: {corr_scene:+.2f} "
              f"(near 0: offset drift; strongly ±: a gain / responsivity change or the scene itself moved).", ""]
    meta = load(run, "meta_1hz.u16", "<u2", (256,))
    if meta is not None and len(meta) > 10:
        mi = load(run, "frames_1hz_index.bin", IDX_DT)
        n = min(len(meta), len(mi))
        var_words = [i for i in range(256) if i not in (64, 180) and np.ptp(meta[:n, i]) > 0]
        gi = np.interp(mi["t"][:n] / 3600, th, g)
        cors = sorted(((abs(np.corrcoef(meta[:n, i], gi)[0, 1]), i) for i in var_words
                       if np.std(meta[:n, i]) > 0), reverse=True)[:5]
        out["meta_words_tracking_global_offset"] = [(i, round(c, 3)) for c, i in cors]
        lines += [f"- Metadata words that change during the run: {var_words[:30]}"
                  f"{' ...' if len(var_words) > 30 else ''}. Best correlated with the global offset "
                  f"(a candidate FPA / housing temperature): {[(i, round(c, 2)) for c, i in cors]}.", ""]
    fig, ax = plt.subplots(2, 2, figsize=(11, 7.5))
    ax[0, 0].plot(th, g, label="global offset g")
    ax[0, 0].plot(th, s_std, label="smooth field std")
    ax[0, 0].plot(th, r_std, label="per-pixel drift std")
    ax[0, 0].set_xlabel("hours since shutter")
    ax[0, 0].set_ylabel("mK")
    ax[0, 0].legend()
    ax[0, 0].set_title("drift components")
    lim = np.percentile(np.abs(r_final[inner]), 99) * MK
    im = ax[0, 1].imshow(r_final * MK, cmap="RdBu_r", vmin=-lim, vmax=lim)
    fig.colorbar(im, ax=ax[0, 1], label="mK")
    ax[0, 1].set_title("per-pixel drift at the end")
    lim = np.percentile(np.abs(slope[inner]), 99)
    im = ax[1, 0].imshow(slope, cmap="RdBu_r", vmin=-lim, vmax=lim)
    fig.colorbar(im, ax=ax[1, 0], label="mK/h")
    ax[1, 0].set_title("per-pixel drift rate")
    ax[1, 1].hist(slope[inner].ravel(), bins=200)
    ax[1, 1].set_xlabel("mK/h")
    ax[1, 1].set_title("drift rate histogram")
    fig.tight_layout()
    fig.savefig(run / "drift.png", dpi=110)
    plt.close(fig)
    lines += ["![drift](drift.png)", ""]

    # ---- 4. the end shutter as an independent check ----------------------------------------------
    pre = load(run, "burst_pre_ffc.u16", "<u2", (NPIX,))
    post = load(run, "burst_post_ffc.u16", "<u2", (NPIX,))
    if pre is not None and post is not None and len(pre) > 25 and len(post) > 25:
        j = (post[:250].astype(np.float64).mean(0) - pre[-250:].astype(np.float64).mean(0)).reshape(H, W)
        jg = float(np.median(j))
        jr = j - jg - smooth(j - jg, k)
        # with periodic shutters the end jump covers only the time since the last one, not section 3
        c = float("nan") if pairs else float(np.corrcoef(jr[inner].ravel(), r_final[inner].ravel())[0, 1])
        out["end_shutter"] = {"global_jump_mk": jg * MK, "per_pixel_jump_std_mk": float(jr[inner].std() * MK),
                              "corr_with_minus_drift": -c}
        lines += ["## 4. The end shutter (independent check)", "",
                  f"The manual shutter at the end resets the offsets against a uniform reference. Jump "
                  f"after minus before (10-s means): global {jg * MK:+.0f} mK, per-pixel std "
                  f"{jr[inner].std() * MK:.1f} mK."
                  + ("" if pairs else f" Correlation of the per-pixel jump with minus the measured drift: "
                     f"{-c:+.2f} (near +1: the drift measured above is what the shutter removes)."), ""]
        fig, ax = plt.subplots(1, 2, figsize=(11, 3.8))
        lim = np.percentile(np.abs(jr[inner]), 99) * MK
        im = ax[0].imshow(jr * MK, cmap="RdBu_r", vmin=-lim, vmax=lim)
        fig.colorbar(im, ax=ax[0], label="mK")
        ax[0].set_title("per-pixel jump at the end shutter")
        sub = np.random.default_rng(0).choice(jr[inner].size, 5000, replace=False)
        ax[1].plot(-r_final[inner].ravel()[sub] * MK, jr[inner].ravel()[sub] * MK, ".", ms=2)
        ax[1].set_xlabel("− drift measured over the night (mK)")
        ax[1].set_ylabel("shutter jump (mK)")
        fig.tight_layout()
        fig.savefig(run / "end_shutter.png", dpi=110)
        plt.close(fig)
        lines += ["![end shutter](end_shutter.png)", ""]

    # ---- 5. periodic shutters: can the drift be predicted from the camera's temperature? --------
    if pairs:
        lines += drift_models(run, pairs, k, out, plt)

    lines += ["## Scene", "", "![scene at the start](scene_at_start.png)", "",
              "Numbers in `analysis.json`; method in `docs/NOISE_AND_CALIBRATION.md`."]
    (run / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (run / "analysis.json").write_text(json.dumps(out, indent=1, default=float), encoding="utf-8")
    print(f"wrote {run / 'report.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
