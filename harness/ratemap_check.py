#!/usr/bin/env python3
"""Step D (docs/PICTURE_EDGES_DESIGN.md 5b): the launcher's C# rate-map foveation (tools/gui/PipelineModel.cs RateMapFoveation: rule geometry, zero-loss
geometry, density eye) against the Python oracle (fov_stream_geometry.py), over Apple's frustum, 0.8 x, 0.6 x, the author's fit and an asymmetric box, at three
eye sizes. Builds uitest.exe on the builder and runs --ratemap-selftest. Geometry within 0.002 (the bisection stops at 22 steps), density eye equal.

    python3 harness/ratemap_check.py
"""
import json, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild
import fov_stream_geometry as geo


def cases():
    rm = geo.load_ratemap()
    P = rm["P"]
    views = [("panel", P), ("p80", tuple(round(0.8 * x, 4) for x in P)), ("p60", tuple(round(0.6 * x, 4) for x in P)), ("fit", geo.AUTHOR_FIT), ("asym", (1.2, 0.8, 0.5, 0.9))]
    out = []
    for n, v in views:
        for eye in ((3552, 3200), (4096, 3584), (2848, 2560)):
            for m in (1.0, 0.8):
                out.append({"name": f"{n}_{eye[0]}x{eye[1]}_m{m}", "view": list(v), "eye": list(eye), "m": m})
    return rm, out


def main(argv):
    rm, cs = cases()
    rid = f"rm_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    loc = Path(tempfile.mkdtemp(prefix="rm_"))
    prof = Path(geo.AVP_PROFILE)
    (loc / "cases.json").write_text(json.dumps({"profile": f"runs\\{rid}\\profile.json", "cases": cs}))
    lib.b_put(loc / "cases.json", f"runs/{rid}/cases.json")
    lib.b_put(prof, f"runs/{rid}/profile.json")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --ratemap-selftest runs\\{rid}\\cases.json --out runs\\{rid}")
    lib.b_get(f"runs/{rid}/ratemap.txt", loc / "ratemap.txt")
    got = {}
    for line in (loc / "ratemap.txt").read_text(errors="replace").splitlines() if (loc / "ratemap.txt").exists() else []:
        f = line.split("|")
        got[f[0]] = {k: v for k, v in (x.split("=", 1) for x in f[1:])}
    fails, n = [], 0
    for c in cs:
        g = got.get(c["name"])
        if g is None:
            fails.append((c["name"], "no line from uitest")); continue
        n += 1
        view, eye = tuple(c["view"]), tuple(c["eye"])
        rg, _ = geo.rule_geometry(view, rm)
        zg, _ = geo.zero_loss_geometry(view, eye, rm, c["m"])
        de = geo.density_eye(view, rm)
        for key, want in (("rule", rg), ("zero", zg)):
            have = [float(x) for x in g[key].split(",")]
            bad = [(i, a, b) for i, (a, b) in enumerate(zip(have, want)) if abs(a - b) > 0.002]
            if bad:
                fails.append((c["name"], f"{key}: C# {have} oracle {[round(x, 4) for x in want]}"))
        if g["density"] != "%dx%d" % tuple(de):
            fails.append((c["name"], f"density: C# {g['density']} oracle {de}"))
    for f in fails[:30]:
        print("  MISMATCH", f[0], f[1])
    print("CHECK", "PASS" if not fails and n == len(cs) else "FAIL", f"rate-map foveation: {n} of {len(cs)} cases compared, {len(fails)} mismatches ({loc})")
    return 0 if not fails and n == len(cs) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
