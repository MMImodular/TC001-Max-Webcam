"""Export a recorded motion clip as H.264 MP4 (ffmpeg / libx264, the imageio-ffmpeg binary).

Written by Claude (Anthropic), 2026-11-23.  python -I export_video.py <runs/motion-...>
Writes, in the clip's folder:
  raw_25fps.mp4             the clip as recorded, inferno palette, 3x upscaled (768 x 576)
  raw_vs_processed_50fps.mp4  left: raw (each frame shown twice), right: denoised + interpolated to 50 fps
ffmpeg: the static build from the imageio-ffmpeg wheel, installed user-level 2026-11-23.
"""
from __future__ import annotations

import sys
from pathlib import Path

import cv2
import numpy as np
from matplotlib import colormaps

HERE = Path(__file__).resolve().parent
RUN = Path(sys.argv[1])
H, W = 192, 256
S = 3
f = np.fromfile(RUN / "frames.u16", "<u2").reshape(-1, H, W).astype(np.float32) / 64 - 273.15
lo, hi = np.percentile(f, [1, 99.5])
LUT = (colormaps["inferno"](np.linspace(0, 1, 256))[:, :3] * 255).astype(np.uint8)[:, ::-1]   # BGR


def color(img_c: np.ndarray) -> np.ndarray:
    i = np.clip((img_c - lo) / (hi - lo) * 255, 0, 255).astype(np.uint8)
    return cv2.resize(LUT[i], (W * S, H * S), interpolation=cv2.INTER_NEAREST)


FFMPEG = next(Path.home().glob(".tc001max-ffmpeg/imageio_ffmpeg/binaries/ffmpeg-*.exe"))


class writer:
    """Raw BGR frames into ffmpeg's stdin -> H.264 MP4 (yuv420p, CRF 18), no console window."""

    def __init__(self, path: Path, fps: float, size: tuple[int, int]) -> None:
        import subprocess
        cmd = [str(FFMPEG), "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgr24",
               "-s", f"{size[0]}x{size[1]}", "-r", str(fps), "-i", "-", "-c:v", "libx264", "-preset", "slow",
               "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(path)]
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "BELOW_NORMAL_PRIORITY_CLASS", 0)
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, creationflags=flags)

    def write(self, img: np.ndarray) -> None:
        self.p.stdin.write(np.ascontiguousarray(img).tobytes())

    def release(self) -> None:
        self.p.stdin.close()
        if self.p.wait() != 0:
            raise RuntimeError("ffmpeg failed")


def label(img: np.ndarray, text: str) -> np.ndarray:
    cv2.putText(img, text, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 2, cv2.LINE_AA)
    return img


out = []
w = writer(RUN / "raw_25fps.mp4", 25.0, (W * S, H * S))
for x in f:
    w.write(color(x))
w.release()
out.append("raw_25fps.mp4")

# processed: denoise (GPU) then one in-between frame per pair
sys.path.insert(0, str(Path.home() / ".tc001max-ort"))
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "denoise"))
import onnxruntime as ort  # noqa: E402
from denoise import build_onnx  # noqa: E402
from interp import build  # noqa: E402

prov = ["DmlExecutionProvider", "CPUExecutionProvider"]
dn = ort.InferenceSession(build_onnx(), providers=prov)
ip = ort.InferenceSession(build().to_onnx(), providers=prov)
mk = f * 1000                       # the graphs work in mK; the offset does not matter
zero = np.zeros((1, 1, H, W), np.float32)
y = mk[0][None, None]
den = []
for x in mk:
    y, _ = dn.run(None, {"x": x[None, None], "y_prev": y, "corr": zero})
    den.append(y[0, 0])
w = writer(RUN / "raw_vs_processed_50fps.mp4", 50.0, (2 * W * S, H * S))
for i in range(len(den)):
    raw = label(color(f[i]), "raw 25 fps")
    w.write(np.hstack([raw, label(color(den[i] / 1000), "denoised + interpolated 50 fps")]))
    if i + 1 < len(den):
        mid = ip.run(["mid"], {"a": den[i][None, None], "b": den[i + 1][None, None]})[0][0, 0]
        w.write(np.hstack([raw, label(color(mid / 1000), "denoised + interpolated 50 fps")]))
w.release()
out.append("raw_vs_processed_50fps.mp4")
for o in out:
    p = RUN / o
    print(o, round(p.stat().st_size / 1e6, 2), "MB")
