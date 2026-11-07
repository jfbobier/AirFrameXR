#!/usr/bin/env python3
"""The game's render choice (tools/gui/PipelineModel.cs RenderPlan): the C# against an independent Python calculation over the user's own numbers (a Vision Pro, 3552 x 3200 declared,
AVP optimized, the stream at 275 Mbps) and a few others, and the figures of the owner's own session (the floor is the 1628 x 1264 the shim logged; pixel for pixel about 2124 x 1886 = 4.0 Mpx, a saving of 65 %).
Builds uitest.exe on the builder and runs --render-plan-selftest.

    python3 harness/render_plan_check.py
"""
import json, math, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild


def even64(v):
    return max(64, (int(math.floor(v + 0.5)) + 1) // 2 * 2)


def size(floor, ceil, q):
    q = max(0.0, min(1.0, q))
    return [even64(floor[0] + (ceil[0] - floor[0]) * q), even64(floor[1] + (ceil[1] - floor[1]) * q)]


def oracle(c):
    ref, P, G, st, pct, above = c["reference"], c["declared"], c["box"], c["stream"], c["pct"], c["above"]
    pw, ph = P[0] + P[1], P[2] + P[3]
    gw, gh = min(G[0], P[0]) + min(G[1], P[1]), min(G[2], P[2]) + min(G[3], P[3])
    wr, hr = gw / pw, gh / ph
    fb, cd = [st[0] * wr, st[1] * hr], [float(st[0]), float(st[1])]
    p4p = [even64(ref[0] * wr), even64(ref[1] * hr)]
    floor, ceil = size(fb, cd, 0), size(fb, cd, 1)
    a1 = p4p[0] * p4p[1]
    fp, cp = 100.0 * floor[0] * floor[1] / a1, 100.0 * ceil[0] * ceil[1] / a1
    mp = cp if above else min(100.0, cp)
    q = 0.0
    if pct > 0:
        want = max(fp, min(mp, pct)) / 100 * a1
        lo, hi = 0.0, 1.0
        for _ in range(40):
            mid = (lo + hi) / 2
            w, h = fb[0] + (cd[0] - fb[0]) * mid, fb[1] + (cd[1] - fb[1]) * mid
            if w * h < want:
                lo = mid
            else:
                hi = mid
        q = (lo + hi) / 2
    q = round(max(0.0, min(1.0, q)), 3)
    ch = size(fb, cd, q)
    return dict(q=q, chosen=ch, floor=floor, ceiling=ceil, p4p=p4p, floorpct=fp, maxpct=mp, chosenpct=100.0 * ch[0] * ch[1] / a1, saving=100.0 * (1 - ch[0] * ch[1] / (ref[0] * ref[1])))


P = [1.7879, 1.0207, 1.2196, 1.0207]
G = [0.97, 0.71, 0.69, 0.63]   # the profile's box (AVP optimized), what the panel uses; the game is told the same box times the headset's own tangents
CASES = []
for name, ref, box, st, pct, above in (
    ("owner_floor", [3552, 3200], G, [2720, 2144], 0, False),
    ("owner_100", [3552, 3200], G, [2720, 2144], 100, False),
    ("owner_150_blocked", [3552, 3200], G, [2720, 2144], 150, False),
    ("owner_150_allowed", [3552, 3200], G, [2720, 2144], 150, True),
    ("owner_75", [3552, 3200], G, [2720, 2144], 75, False),
    ("no_restriction", [3552, 3200], P, [2720, 2144], 100, False),
    ("4k_user", [4096, 3584], G, [3040, 2432], 100, False),
    ("tiny_stream", [3552, 3200], G, [1024, 1024], 100, True),
    ("quest", [2064, 2208], [1.2, 0.9, 1.0, 1.1], [2064, 2208], 90, False)):
    CASES.append({"name": name, "reference": ref, "declared": P if name != "quest" else [1.25, 1.0, 1.1, 1.2], "box": box, "stream": st, "pct": pct, "above": above})


def main(argv):
    # the figures the owner was shown
    o = oracle(CASES[1])
    print("oracle, owner at 100 %:", o["p4p"], round(o["chosenpct"], 1), "% ; saving", round(o["saving"], 1), "%;", "floor", oracle(CASES[0])["floor"], "saving", round(oracle(CASES[0])["saving"], 1), "%")
    rid = f"plan_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    loc = Path(tempfile.mkdtemp(prefix="plan_"))
    (loc / "cases.json").write_text(json.dumps({"cases": CASES}))
    lib.b_put(loc / "cases.json", f"runs/{rid}/cases.json")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --render-plan-selftest runs\\{rid}\\cases.json --out runs\\{rid}")
    lib.b_get(f"runs/{rid}/render_plan.txt", loc / "render_plan.txt")
    got = {}
    for line in (loc / "render_plan.txt").read_text(errors="replace").splitlines():
        n, v = line.split("|", 1); got[n] = v
    fails = []
    for c in CASES:
        want = oracle(c)
        v = got.get(c["name"])
        if v is None or v == "null":
            fails.append((c["name"], "no result")); continue
        have = dict(x.split("=") for x in v.split(";"))
        sz = lambda t: [int(x) for x in t.split("x")]
        checks = [("q", abs(float(have["q"]) - want["q"]) <= 0.0015), ("chosen", all(abs(a - b) <= 2 for a, b in zip(sz(have["chosen"]), want["chosen"]))),
                  ("floor", sz(have["floor"]) == want["floor"]), ("ceiling", sz(have["ceiling"]) == want["ceiling"]), ("p4p", sz(have["p4p"]) == want["p4p"]),
                  ("floorpct", abs(float(have["floorpct"]) - want["floorpct"]) <= 0.02), ("maxpct", abs(float(have["maxpct"]) - want["maxpct"]) <= 0.02),
                  ("saving", abs(float(have["saving"]) - want["saving"]) <= 0.1)]
        for k, ok in checks:
            if not ok:
                fails.append((c["name"], f"{k}: C# {have} oracle {want}"))
        print(f"  {c['name']:20s} q={have['q']:>6s} chosen {have['chosen']:>10s} ({float(have['chosenpct']):6.1f} % of pixel for pixel, saves {float(have['saving']):5.1f} %)  range {float(have['floorpct']):.0f} to {float(have['maxpct']):.0f} %")
    # the owner's figures
    own = got.get("owner_100", ""); fl = got.get("owner_floor", "")
    import re
    m = re.search(r"p4p=(\d+)x(\d+)", own)
    if not m or abs(int(m.group(1)) - 2124) > 3 or abs(int(m.group(2)) - 1886) > 3:
        fails.append(("owner_100", "pixel for pixel is not about 2124 x 1886: " + own))
    m = re.search(r"floor=(\d+)x(\d+)", fl)
    if not m or abs(int(m.group(1)) - 1628) > 3 or abs(int(m.group(2)) - 1264) > 3:
        fails.append(("owner_floor", "the floor is not about 1628 x 1264 (what the shim logged): " + fl))
    for f in fails[:20]:
        print("  MISMATCH", f[0], f[1])
    print("CHECK", "PASS" if not fails else "FAIL", f"render plan: {len(CASES)} cases, {len(fails)} mismatches ({loc})")
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
