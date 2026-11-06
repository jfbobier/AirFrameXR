#!/usr/bin/env python3
"""The picture-edges wiring of the foveation (tools/gui/PipelineModel.cs PictureEdgesFoveation; docs/PICTURE_EDGES_DESIGN.md sections 5b and 9, owner 2026-11-06): the Vision Pro card,
the wizard step and the streamer's start compute the stream's foveation from the picture edges V and Apple's rasterization map, over the stream size in session.json. Builds uitest.exe on
the builder and runs --picture-edges-selftest (nothing is saved), then compares what the wiring wrote with the Python oracle (fov_stream_geometry.py zero_loss_geometry):

  - the whole panel (Apple's default), a fit, a fit at another stream size, a per-eye profile (the per-edge union), a view wider than the panel (cut to it)
  - a Quest: nothing written (the map is Apple's)
  - hand-edited numbers: a passive recompute leaves them, a picture-edges choice computes them again and clears the flag
  - both places the host reads (session_settings...content.* and openvr_config.foveation_*) get the same six numbers

    python3 harness/picture_edges_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild
import fov_stream_geometry as geo

KS = ["center_size_x", "center_size_y", "center_shift_x", "center_shift_y", "edge_ratio_x", "edge_ratio_y"]


def main(argv):
    rm = geo.load_ratemap()
    P = tuple(rm["P"])
    union = (max(1.0, 0.9), max(0.7, 0.8), max(0.7, 0.69), max(0.65, 0.6))
    wider = tuple(min(a, b) for a, b in zip((2.0, 1.5, 1.5, 1.5), P))
    fit = geo.AUTHOR_FIT
    # name -> (wrote, view, eye, manual_after)
    want = {
        "whole_3552x3200": (1, P, (3552, 3200), 0),
        "fit_3552x3200": (1, fit, (3552, 3200), 0),
        "fit_2848x2560": (1, fit, (2848, 2560), 0),
        "per_eye_union": (1, union, (3552, 3200), 0),
        "wider_than_panel": (1, wider, (3552, 3200), 0),
        "quest_writes_nothing": (0, None, None, 0),
        "manual_passive_leaves_it": (0, None, None, 1),
        "manual_explicit_clears_it": (1, fit, (3552, 3200), 0),
    }
    rid = f"pe_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    loc = Path(tempfile.mkdtemp(prefix="pe_"))
    lib.b_put(Path(geo.AVP_PROFILE), f"runs/{rid}/profile.json")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --picture-edges-selftest runs\\{rid}\\profile.json --out runs\\{rid}")
    lib.b_get(f"runs/{rid}/picture_edges.txt", loc / "picture_edges.txt")
    got = {}
    for line in (loc / "picture_edges.txt").read_text(errors="replace").splitlines() if (loc / "picture_edges.txt").exists() else []:
        f = line.split("|")
        got[f[0]] = {k: v for k, v in (x.split("=", 1) for x in f[1:])}
    bad = 0
    for name, (wrote, view, eye, manual) in want.items():
        g = got.get(name)
        problems = []
        if g is None:
            problems.append("no line from uitest")
        else:
            if int(g["wrote"]) != wrote:
                problems.append(f"wrote={g['wrote']} expected {wrote}")
            if int(g["manual_after"]) != manual:
                problems.append(f"manual flag after = {g['manual_after']} expected {manual}")
            sess = [float(x) for x in g["session"].split(",")]
            cache = [float(x) for x in g["cache"].split(",")]
            if wrote:
                zg, _ = geo.zero_loss_geometry(tuple(view), tuple(eye), rm, 1.0)
                bd = [(KS[i], a, round(b, 3)) for i, (a, b) in enumerate(zip(sess, zg)) if abs(a - b) > 0.0015]
                if bd:
                    problems.append(f"C# {sess} oracle {[round(x, 3) for x in zg]}")
                if sess != cache:
                    problems.append(f"the two places differ: session {sess} cache {cache}")
            elif any(v != -1 for v in sess + cache):
                problems.append(f"something was written: {sess}")
        bad += bool(problems)
        print("CHECK", "FAIL" if problems else "PASS", f"{name}" + ("" if not problems else ": " + "; ".join(problems)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
