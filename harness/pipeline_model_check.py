#!/usr/bin/env python3
"""The launcher's pipeline model (tools/gui/PipelineModel.cs: what the frame pipeline will do with the current settings, and the warnings the
Advanced window shows) against the Python oracle (harness/fov_stream_geometry.py `model()`), over a grid of the parameter space of
docs/FOV_PIPELINE_OPTIM.md section 2b: the rectangle (100 / 70 / 50 % of the declared box), the render quality (0 / 0.5 / 1), foveation (off /
strong / mild), the stream size (2848x2560 / 3552x3200 / 4256x3840), the stream fit (off / on with the Vision Pro reference), the split mode
(auto / forced 3 / off), the preset and tuning (P1 / P3 / P5 low latency, P2 / P3 high quality) and the GPU's engine count (1 / 2 / 3, unknown).
Builds uitest.exe on the builder (harness/uibuild.py) and runs its --pipeline-model-selftest. Sizes and strips must be equal, bands within
0.1 degree, warning and info counts equal.

    python3 harness/pipeline_model_check.py [--all]        # --all: the whole product instead of the deterministic sample of about 150 cases
"""
import itertools, json, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild
import fov_stream_geometry as geo

DECLARED = [0.94, 0.93, 0.90, 0.76]
RECT_R = [0.96, 0.71, 0.69, 0.61]


def cases(all_cases):
    out = []
    grid = list(itertools.product(
        (("decl", DECLARED, None), ("r_box", RECT_R, None), ("pct70", DECLARED, 70), ("pct50", DECLARED, 50)),
        (0.0, 0.5, 1.0),
        (("fov_off", False, None), ("fov_strong", True, (0.45, 0.4, 0.4, 0.1, 4.0, 5.0)), ("fov_mild", True, (0.45, 0.4, 0.4, 0.1, 2.5, 3.0))),
        ((2848, 2560), (3552, 3200), (4256, 3840)),
        (False, True),
        ((0, 3, "ll"), (0, 1, "ll"), (0, 5, "ll"), (0, 2, "hq"), (0, 3, "hq"), (3, 3, "ll"), (15, 3, "ll")),
        (1, 2, 3, -1),
    ))
    for n, (box, q, fov, eye, fit, split, eng) in enumerate(grid):
        if not all_cases and (n * 7919) % 41 != 0:   # a deterministic sample through the product
            continue
        name, declared, pct = box
        rect = None if pct is None else [round(v * (pct / 100.0) ** 0.5, 3) for v in declared]
        out.append({"name": f"{name}_q{q}_{fov[0]}_{eye[0]}x{eye[1]}_{'fit' if fit else 'nofit'}_s{split[0]}p{split[1]}{split[2]}_e{eng}", "declared": declared, "rect": rect, "eye": list(eye),
                    "reference_eye": [3552, 3200] if fit else None, "reference_fov": DECLARED if fit else None, "fit": fit, "quality": q,
                    "foveated": fov[1], "geometry": list(fov[2]) if fov[2] else None, "split_mode": split[0], "preset": split[1], "tuning": split[2], "engines": eng})
    return out


def oracle(c):
    return geo.model(c["declared"], c["rect"], tuple(c["eye"]), reference_eye=tuple(c["reference_eye"]) if c["reference_eye"] else None,
                     reference_fov=tuple(c["reference_fov"]) if c["reference_fov"] else None, fit=c["fit"], quality=c["quality"], foveated=c["foveated"],
                     geometry=tuple(c["geometry"]) if c["geometry"] else (0.45, 0.4, 0.4, 0.1, 4.0, 5.0), split_mode=c["split_mode"], preset=c["preset"],
                     tuning=c["tuning"], engines=c["engines"])


def main(argv):
    cs = cases("--all" in argv)
    rid = f"pm_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    loc = Path(tempfile.mkdtemp(prefix="pm_"))
    (loc / "cases.json").write_text(json.dumps({"cases": cs}))
    lib.b_put(loc / "cases.json", f"runs/{rid}/cases.json")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --pipeline-model-selftest runs\\{rid}\\cases.json --out runs\\{rid}")
    lib.b_get(f"runs/{rid}/pipeline_model.txt", loc / "pipeline_model.txt")
    got = {}
    for line in (loc / "pipeline_model.txt").read_text(errors="replace").splitlines() if (loc / "pipeline_model.txt").exists() else []:
        f = line.split("|")
        kv = dict(x.split("=", 1) for x in f[1:9])
        got[f[0]] = kv
    fails, n = [], 0
    for c in cs:
        o = oracle(c)
        g = got.get(c["name"])
        if g is None:
            fails.append((c["name"], "no line from uitest"))
            continue
        n += 1
        want = {"fit_eye": "%dx%d" % tuple(o["fit_eye"]), "game": "%dx%d" % tuple(o["game"]), "encoded": "%dx%d" % tuple(o["encoded"]),
                "strips": str(o["strips"]), "warnings": str(len(o["warnings"])), "info": str(len(o["info"]))}
        for k, v in want.items():
            if g.get(k) != v:
                fails.append((c["name"], f"{k}: C# {g.get(k)} oracle {v}"))
        for k in ("band_x", "band_y"):
            ob = o[k]
            if ob is None:
                if g.get(k) != "-":
                    fails.append((c["name"], f"{k}: C# {g.get(k)} oracle none"))
                continue
            try:
                a, b = (float(x) for x in g[k].split(".."))
                if abs(a - ob[0]) > 0.1 or abs(b - ob[1]) > 0.1:
                    fails.append((c["name"], f"{k}: C# {g[k]} oracle {ob}"))
            except (ValueError, KeyError):
                fails.append((c["name"], f"{k}: C# {g.get(k)} oracle {ob}"))
    for f in fails[:40]:
        print("  MISMATCH", f[0], f[1])
    print("CHECK", "PASS" if not fails and n == len(cs) else "FAIL", f"pipeline model: {n} of {len(cs)} cases compared, {len(fails)} mismatches ({loc})")
    return 0 if not fails and n == len(cs) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
