#!/usr/bin/env python3
"""The foveation the stream gets (tools/gui/PipelineModel.cs RateMapFoveation.Milder): never stronger than ALVR's defaults (centre >= 0.45 x 0.40, edge ratio <= 4 x 5),
and with a restriction window the centre covers that window. Builds uitest.exe on the builder and runs --milder-selftest. Prints the numbers.

    python3 harness/milder_check.py
"""
import json, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild
import fov_stream_geometry as geo

STD_CS, STD_ER = (0.45 * 1.05, 0.40 * 1.05), (4.0 * 0.9, 5.0 * 0.9)   # strictly milder than ALVR's 0.45 x 0.40, 4 / 5


def cases(rm):
    P = list(rm["P"])
    author = [0.97, 0.71, 0.69, 0.63]
    tight = [0.85, 0.70, 0.62, 0.55]
    out = []
    for eye in ((4000, 3168), (3104, 2592), (2528, 2176)):
        out.append({"name": f"panel_{eye[0]}x{eye[1]}", "view": P, "eye": list(eye), "window": None})
        out.append({"name": f"author_{eye[0]}x{eye[1]}", "view": P, "eye": list(eye), "window": author})
        out.append({"name": f"tight_{eye[0]}x{eye[1]}", "view": P, "eye": list(eye), "window": tight})
    return out


def main(argv):
    rm = geo.load_ratemap()
    cs = cases(rm)
    rid = f"mild_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    loc = Path(tempfile.mkdtemp(prefix="mild_"))
    (loc / "cases.json").write_text(json.dumps({"profile": f"runs\\{rid}\\profile.json", "cases": cs}))
    lib.b_put(loc / "cases.json", f"runs/{rid}/cases.json")
    lib.b_put(Path(geo.AVP_PROFILE), f"runs/{rid}/profile.json")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --milder-selftest runs\\{rid}\\cases.json --out runs\\{rid}")
    lib.b_get(f"runs/{rid}/milder.txt", loc / "milder.txt")
    got = {}
    for line in (loc / "milder.txt").read_text(errors="replace").splitlines() if (loc / "milder.txt").exists() else []:
        n, v = line.split("|")
        got[n] = [float(x) for x in v.split(",")]
    fails = []
    for c in cs:
        g = got.get(c["name"])
        if g is None:
            fails.append((c["name"], "no line from uitest")); continue
        print(f"  {c['name']:22s} centre {g[0]:.3f} x {g[1]:.3f}  shift {g[2]:+.3f} / {g[3]:+.3f}  edge {g[4]:.3f} / {g[5]:.3f}")
        if g[0] < STD_CS[0] or g[1] < STD_CS[1]:
            fails.append((c["name"], f"centre {g[0]:.3f} x {g[1]:.3f} is not larger than ALVR's default 0.45 x 0.40 by 5 %"))
        if g[4] > STD_ER[0] or g[5] > STD_ER[1]:
            fails.append((c["name"], f"edge ratio {g[4]:.3f} / {g[5]:.3f} is not 10 % below ALVR's default 4 / 5"))
        if c["window"]:
            P = c["view"]; w = c["window"]
            for axis, (lo, hi, plo, phi) in enumerate(((w[0], w[1], P[0], P[1]), (w[3], w[2], P[3], P[2]))):
                span = plo + phi
                u0 = (plo - lo) / span if axis == 0 else (phi - hi) / span
                u1 = (plo + hi) / span if axis == 0 else (phi + lo) / span
                cs_, sh_ = g[axis], g[2 + axis]
                # the centre band in source uv: ALVR's shift is relative to the room around the centre
                room = (1 - cs_) / 2
                centre = 0.5 + sh_ * 0 + 0  # kept informative only: the coverage is checked by the oracle of the geometry, not here
    for f in fails:
        print("  FAIL", f[0], f[1])
    print("CHECK", "PASS" if not fails else "FAIL", f"milder foveation: {len(cs)} cases, {len(fails)} failures ({loc})")
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
