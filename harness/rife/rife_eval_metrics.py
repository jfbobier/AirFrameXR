"""The frame generation judge's metrics (tools/host/src/fg_judge.rs score), numpy; and the judge dump's PPM reader."""
import math
from pathlib import Path
import numpy as np


def ppm(p):
    b = p.read_bytes()
    parts = b.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return np.frombuffer(parts[3], np.uint8).reshape(h, w, 3)


# ---- the judge's metrics (fg_judge.rs score), numpy
def luma(a):
    a = a.astype(np.float32)
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def dilate(m, r):
    out = m
    for ax in (1, 0):
        p = np.pad(out, [(r, r) if i == ax else (0, 0) for i in range(2)], constant_values=False)
        out = np.lib.stride_tricks.sliding_window_view(p, 2 * r + 1, axis=ax).any(-1)
    return out


def gradient(l):
    g = np.zeros_like(l)
    g[:-1, :-1] = np.abs(l[:-1, 1:] - l[:-1, :-1]) + np.abs(l[1:, :-1] - l[:-1, :-1])
    return g


def psnr(a, g, m=None):
    d = (a - g) if m is None else (a - g)[m]
    if d.size == 0:
        return None
    mse = float(np.mean(d.astype(np.float64) ** 2))
    return 99.0 if mse <= 1e-9 else 10 * math.log10(255 * 255 / mse)


def truth_of(A, B, G):
    la, lb, lg = luma(A), luma(B), luma(G)
    raw = np.abs(la - lb) > 6
    moving = dilate(raw, 6)
    inner = ~dilate(~raw, 6)
    band = moving & ~inner
    gg = gradient(lg)
    return dict(lg=lg, moving=moving, band=band, gg=gg, gband=max(float(gg[band].sum()), 1e-9))


def score(T, out):
    l = luma(out)
    d = gradient(l)[T["band"]] - T["gg"][T["band"]]
    return {"psnr": psnr(l, T["lg"]), "psnr_moving": psnr(l, T["lg"], T["moving"]), "psnr_band": psnr(l, T["lg"], T["band"]),
            "edge_excess": float(d[d > 0].sum()) / T["gband"], "edge_deficit": float(-d[d < 0].sum()) / T["gband"]}


