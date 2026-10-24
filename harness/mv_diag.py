#!/usr/bin/env python3
"""Game motion vectors against the optical flow, from the host's snapshots (VISIONALVR_FG_MV_DIAG=<dir>, frame generation with
`frame_gen_vector_candidate` on so the flow runs next to the vectors; nvenc/hostlib/framegen.cpp diagDump).

    python3 harness/mv_diag.py <dir with mvdiag_<k>.json/.bin> [--k N]

Each snapshot holds the flow inputs (the previous frame aligned into the current camera, the current frame, at the flow size), the pair's filtered
flow and, per eye, the game's raw vectors and the residual our shader made of them. Every hypothesis about how the vectors should be read is
turned into a backward vector per flow block (full-res px, the flow's convention) and scored by the images themselves: the mean luma difference of
the block's texels between the current frame and the aligned previous frame at the vector (what a correct vector makes small). Zero motion and
the flow are the references. The block set: textured blocks (luma spread inside the block); --moving keeps only those that moved.

Hypotheses: resid (our shader's residual, as used), conv (this script's re-implementation of the shader's conversion: must equal resid),
scale k (raw vectors times a constant, best k searched), output (vectors read at the output size instead of the render size), norot (no
head-rotation compensation), flipx / flipy / neg (sign conventions), swap (the other eye's vectors), zero, flow.
Rotation share: the fraction of the pair's head rotation the raw vectors contain (best fit from -1 to 3): 1.0 = the vectors belong to this
frame pair; anything else, or both eyes' DLSS evaluate counters differing, = vectors from another frame (Valor Mortis 2026-10-25: the shim
attached the newest evaluate, not the composed frame's, once the game pipelined)."""
import json
import math
import sys
from pathlib import Path

import numpy as np


def half(a):
    return a.view(np.float16).astype(np.float32)


def load(base):
    meta = json.loads(Path(str(base) + ".json").read_text())
    raw = np.fromfile(str(base) + ".bin", dtype=np.uint8)
    tex = {}
    for t in meta["textures"]:
        b = raw[t["offset"]: t["offset"] + t["w"] * t["h"] * t["bpp"]]
        f = t["format"]
        if f in (87, 88, 91):          # B8G8R8A8: luma of the gamma-encoded colour, as the shader's lumaOf
            px = b.reshape(t["h"], t["w"], 4).astype(np.float32) / 255.0
            tex[t["name"]] = px[..., 2] * 0.2126 + px[..., 1] * 0.7152 + px[..., 0] * 0.0722
        elif f == 10:                  # R16G16B16A16_FLOAT
            tex[t["name"]] = half(b).reshape(t["h"], t["w"], 4)
        elif f == 34:                  # R16G16_FLOAT
            tex[t["name"]] = half(b).reshape(t["h"], t["w"], 2)
        elif f == 16:                  # R32G32_FLOAT
            tex[t["name"]] = b.view(np.float32).reshape(t["h"], t["w"], 2)
        else:
            print(f"  texture {t['name']}: format {f} not handled")
    return meta, tex


def qmat(q):
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def bilinear(img, x, y):
    h, w = img.shape[:2]
    x = np.clip(x - 0.5, 0, w - 1.001)
    y = np.clip(y - 0.5, 0, h - 1.001)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    a = img[y0, x0] * (1 - fx) + img[y0, x0 + 1] * fx
    b = img[y0 + 1, x0] * (1 - fx) + img[y0 + 1, x0 + 1] * fx
    return a * (1 - fy) + b * fy


class Eye:
    """The shader's geometry for one eye (framegen.cpp dirOf / projectTo / uvToPx)."""

    def __init__(self, meta, e):
        self.e = e
        self.W, self.H = meta["frame"]
        self.l, self.r, self.u, self.d = meta["fov8"][e * 4: e * 4 + 4]
        qa, qb = meta["q_prev"][e * 4: e * 4 + 4], meta["q_cur"][e * 4: e * 4 + 4]
        self.M = qmat(qa).T @ qmat(qb)          # direction in the current view -> the previous view (relRot)
        self.rot_deg = math.degrees(math.acos(max(-1.0, min(1.0, (np.trace(self.M) - 1) / 2))))

    def dir_of(self, u, v):
        return np.stack([-self.l + (self.r + self.l) * u, self.u - (self.u + self.d) * v, -np.ones_like(u)], -1)

    def project(self, d):
        ok = d[..., 2] < -1e-4
        tx, ty = d[..., 0] / -d[..., 2], d[..., 1] / -d[..., 2]
        u = (tx + self.l) / (self.l + self.r)
        v = 1 - (ty + self.d) / (self.u + self.d)
        return u, v, ok

    def to_px(self, u, v):
        return np.stack([(self.e * 0.5 + u * 0.5) * self.W, v * self.H], -1)


def frac_rot(M, s):
    """The rotation M scaled to s times its angle (same axis)."""
    ang = math.acos(max(-1.0, min(1.0, (np.trace(M) - 1) / 2)))
    if ang < 1e-9:
        return np.eye(3)
    ax = np.array([M[2, 1] - M[1, 2], M[0, 2] - M[2, 0], M[1, 0] - M[0, 1]]) / (2 * math.sin(ang))
    K = np.array([[0, -ax[2], ax[1]], [ax[2], 0, -ax[0]], [-ax[1], ax[0], 0]])
    return np.eye(3) + math.sin(s * ang) * K + (1 - math.cos(s * ang)) * K @ K


def convert(eye, uvB, mv, rect, rotate=True):
    """Raw vector (texture units, already scaled) -> backward residual flow in full-res px: the shader's vecFlowAt."""
    uA = uvB[..., 0] + mv[..., 0] / rect[2]
    vA = uvB[..., 1] + mv[..., 1] / rect[3]
    if rotate:
        dA = eye.dir_of(uA, vA)
        dB = dA @ eye.M                          # M^T dA, row vectors
        uA, vA, ok = eye.project(dB)
    else:
        ok = np.ones_like(uA, bool)
    return eye.to_px(uA, vA) - eye.to_px(uvB[..., 0], uvB[..., 1]), ok


def analyse(base, args):
    meta, tex = load(base)
    fw, fh = meta["flow_in"]
    grid = meta["grid"]
    bw, bh = meta["blocks"]
    W, H = meta["frame"]
    sx, sy = fw / W, fh / H                       # full-res px -> flow-input px
    cur, prev, flow = tex["cur"], tex["prev_aligned"], tex["flow"]
    print(f"{Path(base).name}: frame {W}x{H}, flow input {fw}x{fh}, grid {grid}, t {meta['t']}")
    gy, gx = np.mgrid[0:grid, 0:grid]
    out = {}
    for e in (0, 1):
        em = meta["eyes"][e]
        if not em["vectors"] or f"mv_raw{e}" not in tex:
            continue
        eye = Eye(meta, e)
        low = (em["flags"] & 2) != 0
        size = em["render"] if low else em["output"]
        rect = [em["mv_base"][0], em["mv_base"][1], size[0], size[1]]
        raw, resid = tex[f"mv_raw{e}"], tex[f"mv_resid{e}"]
        mh, mw = raw.shape[:2]
        # the eye's flow blocks
        b0, b1 = e * bw // 2, (e + 1) * bw // 2
        by, bx = np.mgrid[0:bh, b0:b1]
        cx, cy = (bx * grid + grid / 2.0), (by * grid + grid / 2.0)      # block centre, flow-input px (SBS)
        uvB = np.stack([(cx - e * fw / 2) / (fw / 2), cy / fh], -1)
        tx = np.clip((rect[0] + uvB[..., 0] * rect[2]).astype(int), rect[0], rect[0] + rect[2] - 1)
        ty = np.clip((rect[1] + uvB[..., 1] * rect[3]).astype(int), rect[1], rect[1] + rect[3] - 1)
        mv = raw[ty, tx] * np.array(em["scale"], np.float32)
        qx = (bx * grid)[..., None, None] + gx + 0.5                    # the block's texels
        qy = (by * grid)[..., None, None] + gy + 0.5
        lc = bilinear(cur, qx, qy)
        x_lo, x_hi = e * fw / 2, (e + 1) * fw / 2

        def score(v):
            """mean |L_cur(q) - L_prev(q + v)| over each block; v: backward full-res px per block"""
            px = qx + (v[..., 0] * sx)[..., None, None]
            py = qy + (v[..., 1] * sy)[..., None, None]
            inside = (px >= x_lo) & (px < x_hi) & (py >= 0) & (py < fh)
            err = np.where(inside, np.abs(lc - bilinear(prev, np.clip(px, x_lo + 0.5, x_hi - 0.5), py)), 0.25)
            return err.mean(axis=(-1, -2))

        hyp = {"zero": np.zeros(bx.shape + (2,), np.float32), "flow": flow[by, bx, :2]}
        hyp["resid"] = np.where(resid[ty, tx, 2:3] > 0.5, resid[ty, tx, :2], 0)
        hyp["conv"], _ = convert(eye, uvB, mv, rect)
        hyp["norot"], _ = convert(eye, uvB, mv, rect, rotate=False)
        hyp["flipx"], _ = convert(eye, uvB, mv * np.array([-1, 1], np.float32), rect)
        hyp["flipy"], _ = convert(eye, uvB, mv * np.array([1, -1], np.float32), rect)
        hyp["neg"], _ = convert(eye, uvB, -mv, rect)
        osz = em["output"]
        hyp["output"], _ = convert(eye, uvB, mv * np.array([osz[0] / max(1, size[0]), osz[1] / max(1, size[1])], np.float32), rect)
        o = 1 - e
        if f"mv_raw{o}" in tex:
            hyp["swap"], _ = convert(eye, uvB, tex[f"mv_raw{o}"][ty, tx] * np.array(meta["eyes"][o]["scale"], np.float32), rect)
        sc = {k: score(v) for k, v in hyp.items()}
        # the blocks the images can judge: textured (luma spread inside the block), all of them by default (static content tests the
        # head-rotation part of the conversion); --moving: only those that moved (the flow beats zero clearly; the rig's probe vectors carry
        # camera motion only, not its marker's)
        sel = lc.std(axis=(-1, -2)) > 0.03
        if "--moving" in args:
            sel &= (sc["zero"] > sc["flow"] + 0.01)
        n = int(sel.sum())
        # a constant scale on the raw vectors: coarse search, then refine
        best_k, best_err = 1.0, None
        for k in list(np.geomspace(0.05, 20, 41)) + [-1.0, -0.5, -2.0]:
            v, _ = convert(eye, uvB, mv * k, rect)
            err = float(score(v)[sel].mean()) if n else 0
            if best_err is None or err < best_err:
                best_k, best_err = float(k), err
        for k in np.linspace(best_k * 0.8, best_k * 1.25, 19):
            v, _ = convert(eye, uvB, mv * k, rect)
            err = float(score(v)[sel].mean()) if n else 0
            if err < best_err:
                best_k, best_err = float(k), err
        # the share of this pair's head rotation the vectors contain (1.0 = they belong to this pair)
        M0, share, share_err = eye.M.copy(), 1.0, None
        for sh in np.linspace(-1, 3, 41):
            eye.M = frac_rot(M0, sh)
            v, _ = convert(eye, uvB, mv, rect)
            err = float(score(v)[sel].mean()) if n else 0
            if share_err is None or err < share_err:
                share, share_err = float(sh), err
        eye.M = M0
        # least-squares scale between the residual and the flow on the selected blocks (what the flow says about the vectors' size)
        f, r = hyp["flow"][sel], hyp["conv"][sel]
        ls = float((f * r).sum() / max(1e-9, (r * r).sum())) if n else 0
        mag = lambda a: float(np.median(np.linalg.norm(a[sel], axis=-1))) if n else 0
        print(f"  eye {e}: vectors {mw}x{mh} rect {rect} scale {em['scale']} flags {em['flags']} jitter {em['jitter']}, head rotation in the pair {eye.rot_deg:.3f} deg")
        print(f"    {'moving ' if '--moving' in args else ''}textured blocks {n} of {sel.size}; median |flow| {mag(hyp['flow']):.2f} px, |resid| {mag(hyp['resid']):.2f} px, |raw| {float(np.median(np.linalg.norm(mv[sel], axis=-1))) if n else 0:.2f} vector px")
        print(f"    conv vs resid (re-implementation check): median diff {float(np.median(np.linalg.norm((hyp['conv'] - hyp['resid'])[sel], axis=-1))) if n else 0:.3f} px")
        rows = sorted(((float(sc[k][sel].mean()) if n else 0, k) for k in sc))
        for err, k in rows:
            wins = float((sc[k][sel] < sc["flow"][sel] - 0.002).mean() * 100) if n else 0
            print(f"    {k:7s} error {err:.4f}   beats the flow on {wins:5.1f} % of the blocks")
        print(f"    best constant scale on the raw vectors: k = {best_k:.3f} (error {best_err:.4f}); least-squares flow/resid scale {ls:.3f}")
        print(f"    rotation share {share:+.1f} (error {share_err:.4f}); DLSS evaluate counter {em['frame']} (other eye {meta['eyes'][1 - e]['frame']})")
        out[e] = {"n": n, "rows": rows, "k": best_k, "ls": ls, "rot": eye.rot_deg, "share": share, "sync": em["frame"] == meta["eyes"][1 - e]["frame"]}
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    d = Path(sys.argv[1])
    bases = sorted(p.with_suffix("") for p in d.glob("mvdiag_*.json"))
    if "--k" in sys.argv:
        k = int(sys.argv[sys.argv.index("--k") + 1])
        bases = [b for b in bases if b.name == f"mvdiag_{k}"]
    if not bases:
        print(f"no mvdiag_*.json in {d}")
        return 1
    res = [analyse(b, sys.argv) for b in bases]
    # summary: which reading wins most often, and the scale it needs
    tally = {}
    ks = []
    for r in res:
        for e, v in r.items():
            if v["n"]:
                tally[v["rows"][0][1]] = tally.get(v["rows"][0][1], 0) + 1
                ks.append(v["k"])
    print(f"\nbest reading per snapshot and eye: {tally}; constant scale k median {float(np.median(ks)) if ks else 0:.3f} (range {min(ks) if ks else 0:.3f}..{max(ks) if ks else 0:.3f})")
    ok = [v for r in res for v in r.values() if v["n"]]
    matched = sum(1 for v in ok if abs(v["share"] - 1) < 0.15 and v["sync"])
    print(f"vectors that belong to their frame pair (rotation share 1.0 +- 0.15 and both eyes on the same evaluate): {matched} of {len(ok)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
