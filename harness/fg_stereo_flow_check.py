#!/usr/bin/env python3
"""Offline check of the stereo idea (fg-testing): do the two eyes' temporal flows agree, and does their disagreement say more than the engine's cost?

Input: the camera-turn dumps (AIRFRAMEXR_FG_CAM_TURN_DUMP, one pair's unpacked vectors of the side-by-side frame, left half = left eye).
The same surface moves nearly the same way in both eyes, shifted by the disparity d: flow_R(x) ~ flow_L(x - d). We have no disparity here (the dumps
hold vectors and cost only), so the right eye is compared with the left at a global block shift s (the shift with the best agreement is a crude
disparity of the dominant depth). What it can answer: how often the eyes agree at all, and among blocks the engine's cost calls good (cost < 0.02),
how many still disagree with the other eye (information the cross-eye test would add over cost).

    python3 harness/fg_stereo_flow_check.py <dump dir> [--limit N]
"""
import sys
from pathlib import Path
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cam_turn_dump as ctd


def fields(d):
    a = np.array(d["raw"], dtype=np.uint16).reshape(d["bh"], d["bw"], 4).view(np.float16).astype(np.float32)
    h = d["bw"] // 2
    return a[:, :h], a[:, h:]


def analyse(d, smax=24):
    L, R = fields(d)
    h = L.shape[1]
    out = {}
    valid = lambda f: (f[..., 3] >= 0.5) & (f[..., 2] < 0.08) & np.isfinite(f[..., 0]) & np.isfinite(f[..., 1])
    vl, vr = valid(L), valid(R)
    best = (-1.0, 0)
    for s in range(-smax, smax + 1):
        lo, hi = max(0, -s), min(h, h - s)               # L column x pairs with R column x + s
        if hi - lo < 20:
            continue
        a, b = L[:, lo:hi], R[:, lo + s:hi + s]
        m = vl[:, lo:hi] & vr[:, lo + s:hi + s]
        if m.sum() < 500:
            continue
        diff = np.hypot(a[..., 0] - b[..., 0], a[..., 1] - b[..., 1])
        agree = float((diff[m] < 1.0).mean())
        if agree > best[0]:
            best = (agree, s, m.sum())
    out["best_agree"], out["best_shift"] = best[0], best[1]
    s = best[1]
    lo, hi = max(0, -s), min(h, h - s)
    a, b = L[:, lo:hi], R[:, lo + s:hi + s]
    m = vl[:, lo:hi] & vr[:, lo + s:hi + s]
    diff = np.hypot(a[..., 0] - b[..., 0], a[..., 1] - b[..., 1])
    conf = m & (a[..., 2] < 0.02) & (b[..., 2] < 0.02)
    moving = conf & (np.hypot(a[..., 0], a[..., 1]) > 1.0)
    out["conf_blocks"] = int(conf.sum())
    out["conf_disagree_2px"] = float((diff[conf] > 2.0).mean()) if conf.any() else float("nan")
    out["moving_disagree_2px"] = float((diff[moving] > 2.0).mean()) if moving.any() else float("nan")
    out["valid_share"] = float(m.mean())
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    files = sorted(Path(sys.argv[1]).glob("turn_*.bin"))
    if "--limit" in sys.argv:
        files = files[:int(sys.argv[sys.argv.index("--limit") + 1])]
    rows = []
    for f in files:
        r = analyse(ctd.load(f))
        rows.append(r)
        print(f"{f.name}  best shift {r['best_shift']:+3d} blocks  agree<1px {r['best_agree']*100:5.1f} %  valid {r['valid_share']*100:4.1f} %  "
              f"cost-good blocks {r['conf_blocks']:6d}: disagree>2px {r['conf_disagree_2px']*100:5.1f} %  (moving ones {r['moving_disagree_2px']*100:5.1f} %)")
    if rows:
        med = lambda k: float(np.nanmedian([r[k] for r in rows]))
        print(f"\nmedian over {len(rows)} dumps: agree<1px {med('best_agree')*100:.1f} %, cost-good but disagree>2px {med('conf_disagree_2px')*100:.1f} %, "
              f"moving cost-good disagree>2px {med('moving_disagree_2px')*100:.1f} %, best shift {med('best_shift'):+.0f} blocks")


if __name__ == "__main__":
    main()
