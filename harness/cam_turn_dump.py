#!/usr/bin/env python3
"""The frame generator's camera-turn measurement, offline (nvenc/hostlib/framegen.cpp FrameGen::harvestTurn).

AIRFRAMEXR_FG_CAM_TURN_DUMP=<dir> makes the generator write every 8th measurement (up to 120) as turn_<seq>.bin (the pair's unpacked vectors with the
newer frame's effective orientation and fov) and one line per measurement to turn_log.csv. This script re-runs the estimator on the dumps (a port of
harvestTurn, checked against the C++ line by line in turn_log.csv) and shows what the per-block azimuth differences look like: where the mass is (the turn,
standing still = the HUD / a camera-fixed light, noise), so that the estimator can be changed against a real game's data, not a guess.

    python3 harness/cam_turn_dump.py <dump dir>                one line per dump: C++ vs port, the peaks of the distribution
    python3 harness/cam_turn_dump.py <dump dir> --plot <seq>   a text histogram of one dump (+-6 deg around 0 and the peak)
"""
import csv, math, struct, sys
from pathlib import Path

DEG = 180 / math.pi
K_RANGE, K_BIN, K_BINS = 20 / DEG, 0.05 / DEG, 801


def half(h):
    return struct.unpack("<e", struct.pack("<H", h))[0]


def qmat(q):
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1
    x, y, z, w = x / n, y / n, z / n, w / n
    return [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]


def load(path):
    b = Path(path).read_bytes()
    hdr = struct.unpack_from("<8i", b, 0)
    assert hdr[0] == 0x4E525554, "not a camera-turn dump"
    _, bw, bh, grid, flw, flh, mw, mh = hdr
    o = 32
    q = struct.unpack_from("<8f", b, o); o += 32
    fov = struct.unpack_from("<8f", b, o); o += 32
    used, dt = struct.unpack_from("<2d", b, o); o += 16
    raw = struct.unpack_from(f"<{bw * bh * 4}H", b, o)
    return dict(bw=bw, bh=bh, grid=grid, flw=flw, flh=flh, mw=mw, mh=mh, q=q, fov=fov, used=used, dt=dt, raw=raw)


def samples(d, step=2, cost_max=0.08, tex_only=True):
    """the per-block azimuth differences (rad) and weights, as harvestTurn computes them"""
    M = [qmat(d["q"][0:4]), qmat(d["q"][4:8])]
    fx, fy = d["flw"] / d["mw"], d["flh"] / d["mh"]
    half_w = d["bw"] // 2
    out = []
    for by in range(0, d["bh"], step):
        for bx in range(0, d["bw"], step):
            k = (by * d["bw"] + bx) * 4
            vx, vy, cost, info = (half(d["raw"][k + i]) for i in range(4))
            if tex_only and info < 0.5:
                continue
            if not (cost < cost_max) or not math.isfinite(vx) or not math.isfinite(vy):
                continue
            e = 1 if bx >= half_w else 0
            l, r, u, dn = d["fov"][e * 4:e * 4 + 4]
            sx, sy = (bx + 0.5) * d["grid"] / fx, (by + 0.5) * d["grid"] / fy
            ub, vb = (sx / d["mw"] - (0.5 if e else 0)) / 0.5, sy / d["mh"]
            ua, va = ((sx + vx) / d["mw"] - (0.5 if e else 0)) / 0.5, (sy + vy) / d["mh"]
            if not (0 <= ua <= 1 and 0 <= va <= 1):
                continue

            def world(uu, vv):
                dd = (-l + uu * (l + r), u - vv * (u + dn), -1.0)
                m = M[e]
                return [m[i * 3] * dd[0] + m[i * 3 + 1] * dd[1] + m[i * 3 + 2] * dd[2] for i in range(3)]
            wb, wa = world(ub, vb), world(ua, va)
            hb = math.hypot(wb[0], wb[2]); nb = math.sqrt(hb * hb + wb[1] ** 2)
            if hb < 0.2 * nb:
                continue
            dd = math.atan2(wb[0], wb[2]) - math.atan2(wa[0], wa[2])
            if dd > math.pi: dd -= 2 * math.pi
            elif dd < -math.pi: dd += 2 * math.pi
            if -K_RANGE <= dd <= K_RANGE:
                out.append((dd, hb / nb, bx, by, e, math.hypot(vx, vy), cost))
    return out


def estimate(sm):
    """harvestTurn's mode: 5-bin window peak, mean within 4 bins; (miss rad, agree, n)"""
    hist = [0.0] * K_BINS
    for dd, w, *_ in sm:
        hist[int(round((dd + K_RANGE) / K_BIN))] += w
    total = sum(hist)
    best, peak = 0.0, -1
    for k in range(2, K_BINS - 2):
        w5 = sum(hist[k - 2:k + 3])
        if w5 > best:
            best, peak = w5, k
    if peak < 0:
        return 0.0, 0.0, len(sm), hist
    c = peak * K_BIN - K_RANGE
    sw = sum(w for dd, w, *_ in sm if abs(dd - c) <= 4 * K_BIN)
    sd = sum(w * dd for dd, w, *_ in sm if abs(dd - c) <= 4 * K_BIN)
    return (sd / sw if sw else c), (best / total if total else 0), len(sm), hist


def peaks(hist, n=4, sep=6):
    """the n largest 5-bin windows at least `sep` bins apart: (centre deg, share)"""
    total = sum(hist) or 1
    wins = sorted(((sum(hist[k - 2:k + 3]), k) for k in range(2, K_BINS - 2)), reverse=True)
    got = []
    for w, k in wins:
        if all(abs(k - j) >= sep for _, j in got):
            got.append((w, k))
        if len(got) == n:
            break
    return [((k * K_BIN - K_RANGE) * DEG, w / total) for w, k in got]


def main():
    d = Path(sys.argv[1])
    log = {}
    if (d / "turn_log.csv").exists():
        for row in csv.DictReader(open(d / "turn_log.csv")):
            log[int(row["seq"])] = row
    if "--plot" in sys.argv:
        seq = int(sys.argv[sys.argv.index("--plot") + 1])
        dd = load(d / f"turn_{seq:06d}.bin")
        sm = samples(dd)
        miss, agree, n, hist = estimate(sm)
        print(f"seq {seq}: used {dd['used'] * DEG:.3f} deg, dt {dd['dt'] * 1000:.1f} ms, miss {miss * DEG:.3f}, agree {agree:.2f}, n {n}")
        total = sum(hist) or 1
        for k in range(0, K_BINS, 4):
            share = sum(hist[k:k + 4]) / total
            if share > 0.004:
                print(f"{(k * K_BIN - K_RANGE) * DEG:+7.2f} {'#' * int(share * 400)} {100 * share:.1f} %")
        return 0
    print(f"{'seq':>6s} {'used':>7s} {'C++ miss':>8s} {'agree':>5s} {'n':>6s} | {'port miss':>9s} {'agree':>5s} {'n':>6s} | peaks (deg: share)")
    for f in sorted(d.glob("turn_*.bin")):
        seq = int(f.stem.split("_")[1])
        dd = load(f)
        sm = samples(dd)
        miss, agree, n, hist = estimate(sm)
        row = log.get(seq, {})
        pk = "  ".join(f"{c:+6.2f}: {100 * s:4.1f} %" for c, s in peaks(hist))
        print(f"{seq:6d} {dd['used'] * DEG:7.3f} {float(row.get('miss_deg', 'nan')):8.3f} {float(row.get('agree', 'nan')):5.2f} {int(row.get('blocks', -1)):6d} | "
              f"{miss * DEG:9.3f} {agree:5.2f} {n:6d} | {pk}")
    return 0


if __name__ == "__main__":
    sys.exit(main())


def estimate_v3(sm, min_moving=0.15, min_share=0.30, min_spread=0.35, bw=None):
    """v3 (2026-11-21, from the Hellraiser dumps): blocks where standing still won (vector 0) never vote: in a dark, grainy scene they are noise
    and voted 'no turn' (hiding every turn start). Few moving blocks = the prediction was right (miss 0); else the mode of the moving blocks, kept
    only if it holds min_share of them and spreads over min_spread of the eye width (a moving object is not a camera turn).
    Returns (miss rad, ok, why, moving share, peak share, spread)."""
    still = [s for s in sm if s[5] == 0]
    moving = [s for s in sm if s[5] > 0]
    tot = sum(s[1] for s in sm) or 1
    mshare = sum(s[1] for s in moving) / tot
    if len(sm) < 200:
        return 0.0, False, "few blocks", mshare, 0, 0
    if mshare < min_moving:
        return 0.0, True, "still", mshare, 0, 0
    miss, agree, n, hist = estimate(moving)
    cl = [s for s in moving if abs(s[0] - miss) <= 4 * K_BIN]
    if not cl:
        return 0.0, False, "no peak", mshare, agree, 0
    us = sorted(((s[2] - (bw // 2 if s[4] else 0)) / (bw / 2)) for s in cl)
    spread = us[int(0.9 * (len(us) - 1))] - us[int(0.1 * (len(us) - 1))]
    ok = agree >= min_share and spread >= min_spread
    return (miss if ok else 0.0), ok, ("turn" if ok else ("weak peak" if agree < min_share else "narrow")), mshare, agree, spread


def estimate_v4(sm, still_cost=0.008, min_share=0.30, min_spread=0.35, bw=None):
    """v4 (2026-11-21): v2's mode over all blocks, but a block where standing still won votes only if that match is good (cost < still_cost):
    in a dark, grainy scene most such blocks are noise, which matches equally badly everywhere and voted 'the prediction was right' (Hellraiser:
    median still cost 0.020 against the lab's 0.002). A non-zero miss must also spread over min_spread of the eye width (not one moving object).
    Returns (miss rad, ok, why, agree, spread, n)."""
    vote = [s for s in sm if s[5] > 0 or s[6] < still_cost]
    if len(vote) < 200:
        return 0.0, False, "few blocks", 0, 0, len(vote)
    miss, agree, n, hist = estimate(vote)
    if agree < min_share:
        return 0.0, False, "weak peak", agree, 0, n
    if abs(miss) < 0.15 / DEG:
        return miss, True, "small", agree, 0, n
    cl = [s for s in vote if abs(s[0] - miss) <= 4 * K_BIN]
    us = sorted(((s[2] - (bw // 2 if s[4] else 0)) / (bw / 2)) for s in cl)
    spread = us[int(0.9 * (len(us) - 1))] - us[int(0.1 * (len(us) - 1))]
    if spread < min_spread:
        return 0.0, False, "narrow", agree, spread, n
    return miss, True, "turn", agree, spread, n
