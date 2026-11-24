"""TC001 Max frame interpolator prototype: one in-between frame from two denoised frames.

Written by Claude (Anthropic), 2026-11-23. Own code; the method is textbook coarse-to-fine
Lucas-Kanade optical flow (Lucas & Kanade 1981; Bouguet's pyramidal form), solved on the midpoint
grid so that A(x - u/2) ~ B(x + u/2), then each frame is warped halfway and the two are blended.

No learned model: RIFE-style networks need a downloaded model (not approved), and a 192 x 256
thermal frame is small enough for a few classical iterations. The whole thing is one ONNX graph
(GridSample, opset 17) built with graph.py, so NumPy runs the same graph the GPU would.

Inputs a, b: [1,1,192,256] float32 in mK (denoised). Output mid: [1,1,192,256].
"""
from __future__ import annotations

import numpy as np

from graph import Graph

H, W = 192, 256


class P:
    levels = 4            # 192x256, 96x128, 48x64, 24x32 (4th added 2026-11-23: real motion reached 40 px)
    iters = 3             # Lucas-Kanade iterations per level
    window = 7            # Lucas-Kanade window (box), pixels at each level
    lam_mk2 = 400.0       # Tikhonov term in (mK/px)^2: flat noisy areas get no flow
    max_step_px = 2.0     # clip each update
    blend_k_mk = 1e9      # mismatch fallback toward (a+b)/2; 150 mK made person edges worse (58 -> 216 mK), so off


def build(p: type[P] = P, h: int = H, w: int = W) -> Graph:
    g = Graph("tc001max_interp")
    a = g.inp("a", [1, 1, h, w])
    b = g.inp("b", [1, 1, h, w])
    # pyramids
    pa, pb = [a], [b]
    for _ in range(p.levels - 1):
        pa.append(g.op("AveragePool", pa[-1], kernel_shape=[2, 2], strides=[2, 2]))
        pb.append(g.op("AveragePool", pb[-1], kernel_shape=[2, 2], strides=[2, 2]))
    half = g.const(0.5)
    zero_k = g.const(0.0)
    lam = g.const(p.lam_mk2)
    lo, hi = g.const(-p.max_step_px), g.const(p.max_step_px)
    kx = g.const(np.array([-0.5, 0, 0.5], np.float32).reshape(1, 1, 1, 3))
    ky = g.const(np.array([-0.5, 0, 0.5], np.float32).reshape(1, 1, 3, 1))
    padx = g.const([0, 0, 0, 1, 0, 0, 0, 1], np.int64)
    pady = g.const([0, 0, 1, 0, 0, 0, 1, 0], np.int64)
    r = p.window // 2
    u = v = None
    for lev in range(p.levels - 1, -1, -1):
        lh, lw = h >> lev, w >> lev
        A, B = pa[lev], pb[lev]
        ys, xs = np.mgrid[0:lh, 0:lw].astype(np.float32)
        gx0 = g.const(((2 * xs + 1) / lw - 1).reshape(1, lh, lw, 1))
        gy0 = g.const(((2 * ys + 1) / lh - 1).reshape(1, lh, lw, 1))
        sx, sy = g.const(1.0 / lw), g.const(1.0 / lh)      # half a pixel of flow in grid units: u/2 * 2/lw
        shp = g.const([1, lh, lw, 1], np.int64)
        if u is None:
            u = g.op("Mul", A, zero_k)
            v = g.op("Mul", A, zero_k)
        else:
            sc = g.const([1, 1, 2, 2])
            two = g.const(2.0)
            u = g.op("AveragePool", g.op("Mul", g.op("Resize", u, "", sc, mode="nearest"), two),
                     kernel_shape=[3, 3], pads=[1, 1, 1, 1], count_include_pad=0)
            v = g.op("AveragePool", g.op("Mul", g.op("Resize", v, "", sc, mode="nearest"), two),
                     kernel_shape=[3, 3], pads=[1, 1, 1, 1], count_include_pad=0)

        def warps(u, v):
            ur = g.op("Reshape", u, shp)
            vr = g.op("Reshape", v, shp)
            ga = g.op("Concat", g.op("Sub", gx0, g.op("Mul", ur, sx)), g.op("Sub", gy0, g.op("Mul", vr, sy)), axis=3)
            gb = g.op("Concat", g.op("Add", gx0, g.op("Mul", ur, sx)), g.op("Add", gy0, g.op("Mul", vr, sy)), axis=3)
            aw = g.op("GridSample", A, ga, mode="bilinear", padding_mode="border", align_corners=0)
            bw = g.op("GridSample", B, gb, mode="bilinear", padding_mode="border", align_corners=0)
            return aw, bw

        for _ in range(p.iters):
            aw, bw = warps(u, v)
            m = g.op("Mul", g.op("Add", aw, bw), half)
            it = g.op("Sub", bw, aw)
            gx = g.op("Conv", g.op("Pad", m, padx, mode="edge"), kx)
            gy = g.op("Conv", g.op("Pad", m, pady, mode="edge"), ky)

            def box(t):
                return g.op("AveragePool", t, kernel_shape=[p.window, p.window], pads=[r, r, r, r], count_include_pad=0)
            sxx = g.op("Add", box(g.op("Mul", gx, gx)), lam)
            syy = g.op("Add", box(g.op("Mul", gy, gy)), lam)
            sxy = box(g.op("Mul", gx, gy))
            sxt = box(g.op("Mul", gx, it))
            syt = box(g.op("Mul", gy, it))
            det = g.op("Sub", g.op("Mul", sxx, syy), g.op("Mul", sxy, sxy))
            du = g.op("Div", g.op("Sub", g.op("Mul", sxy, syt), g.op("Mul", syy, sxt)), det)
            dv = g.op("Div", g.op("Sub", g.op("Mul", sxy, sxt), g.op("Mul", sxx, syt)), det)
            # the linearisation is in units of the full flow u (each frame moves u/2: d it / d u = gx)
            u = g.op("Add", u, g.op("Clip", du, lo, hi))
            v = g.op("Add", v, g.op("Clip", dv, lo, hi))
    aw, bw = warps(u, v)
    # blend: plain average where the two warps agree; where they disagree (occlusion, failed flow)
    # lean toward the un-warped average of the inputs, which at least never invents a structure
    mis = g.op("Abs", g.op("Sub", aw, bw))
    wgt = g.op("Clip", g.op("Div", mis, g.const(p.blend_k_mk)), g.const(0.0), g.const(1.0))
    warped = g.op("Mul", g.op("Add", aw, bw), half)
    plain = g.op("Mul", g.op("Add", a, b), half)
    mid = g.op("Add", warped, g.op("Mul", wgt, g.op("Sub", plain, warped)), name="mid")
    g.out("mid", [1, 1, h, w])
    g.op("Mul", u, g.const(1.0), name="flow_u")
    g.op("Mul", v, g.const(1.0), name="flow_v")
    g.out("flow_u", [1, 1, h, w])
    g.out("flow_v", [1, 1, h, w])
    return g
