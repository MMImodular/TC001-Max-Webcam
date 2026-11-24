"""Contact sheet of a recorded motion clip (16 frames, 4 s apart) as a PNG. Written by Claude (Anthropic), 2026-11-23."""
import sys
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
RUN = Path(sys.argv[1])
f = np.fromfile(RUN / "frames.u16", "<u2").reshape(-1, 192, 256).astype(np.float32) / 64 - 273.15
lo, hi = np.percentile(f, [1, 99.5])
idx = np.linspace(0, len(f) - 1, 16).astype(int)
fig, ax = plt.subplots(4, 4, figsize=(12, 7.2))
for a, i in zip(ax.ravel(), idx):
    a.imshow(f[i], cmap="inferno", vmin=lo, vmax=hi)
    a.set_title(f"t = {i / 25:.1f} s", fontsize=8)
    a.axis("off")
fig.suptitle(f"{RUN.name}: {len(f)} frames at 25 fps, {lo:.1f}-{hi:.1f} degC", fontsize=10)
fig.tight_layout()
fig.savefig(RUN / "preview.png", dpi=90)
