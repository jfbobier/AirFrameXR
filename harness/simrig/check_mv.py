#!/usr/bin/env python3
"""Checks the host's motion-vector dumps of a `steady_mv` simrig run against the probe's synthetic pattern (docs/MOTION_VECTORS.md).
    python3 harness/simrig/check_mv.py harness/results/<run>/
The probe publishes, per eye, vx = s * 8 sin(2 pi y / h), vy = s * 4 cos(2 pi x / w) (s = +1 left, -1 right) and the 16 x 16 block at the
origin with vx = frame % 32; the host writes mv_<n>_eye<e>.ppm with R/G = 128 + 4 * (vx, vy) and a .txt sidecar with the parameters.
Verdict per dump: the mean absolute error of R and G against the formula (quantised the same way) and the frame block's value."""
import math, re, sys
from pathlib import Path


def read_ppm(p):
    data = p.read_bytes()
    # P6\n<w> <h>\n255\n
    m = re.match(rb"P6\s+(\d+)\s+(\d+)\s+255\s", data)
    if not m:
        raise SystemExit(f"{p}: not a P6 ppm")
    w, h = int(m.group(1)), int(m.group(2))
    px = data[m.end():]
    return w, h, px


def q(a):
    a = 128.0 + 4.0 * a
    return 0 if a < 0 else 255 if a > 255 else int(a + 0.5) if a >= 0 else int(a - 0.5)


def main(d):
    d = Path(d)
    dumps = sorted(d.glob("mv_*_eye*.ppm"))
    if not dumps:
        print("no mv_*.ppm dumps in", d)
        return 2
    rc = 0
    for p in dumps:
        eye = int(p.stem.split("eye")[1])
        s = -1.0 if eye else 1.0
        w, h, px = read_ppm(p)
        txt = p.with_suffix(".txt").read_text() if p.with_suffix(".txt").exists() else ""
        fm = re.search(r"frame (\d+)", txt)
        frame = int(fm.group(1)) if fm else None
        # sample a grid of points (outside the frame block)
        errs = []
        for yi in range(0, h, max(1, h // 48)):
            for xi in range(0, w, max(1, w // 48)):
                if xi < 16 and yi < 16:
                    continue
                o = (yi * w + xi) * 3
                r, g = px[o], px[o + 1]
                vx = s * 8.0 * math.sin(2 * math.pi * yi / h)
                vy = s * 4.0 * math.cos(2 * math.pi * xi / w)
                errs.append(abs(r - q(vx)) + abs(g - q(vy)))
        mae = sum(errs) / len(errs)
        block = px[(8 * w + 8) * 3]   # R at (8, 8)
        block_v = (block - 128) / 4.0
        ok = mae < 1.5 and (frame is None or abs(block_v - (frame % 32)) < 0.6)
        rc |= 0 if ok else 1
        print(f"{p.name}: {w}x{h} pattern MAE {mae:.2f} (R+G levels), frame block {block_v:.1f} vs published frame {frame} -> {'OK' if ok else 'MISMATCH'}")
        if txt:
            print("   ", txt.strip())
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "."))
