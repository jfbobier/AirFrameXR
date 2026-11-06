#!/usr/bin/env python3
"""The picture-edges wiring of the foveation (tools/gui/PipelineModel.cs PictureEdgesFoveation; docs/PICTURE_EDGES_DESIGN.md sections 5b and 9, owner 2026-11-06): the Vision Pro card,
the wizard step and the streamer's start compute the stream's foveation from the picture edges V and Apple's rasterization map, over the stream size in session.json. Builds uitest.exe on
the builder and runs --picture-edges-selftest (nothing is saved), then compares what the wiring wrote with the Python oracle (fov_stream_geometry.py zero_loss_geometry):

  - the whole panel (Apple's default), a fit, a per-eye profile (the per-edge union), a view wider than the panel (cut to it); the stream size follows the bitrate and the rate (300 Mbps
    at 90 Hz, 200, 150, 100 Hz), and stays when the bitrate is adaptive or the stream is fitted to the window
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
    bpp = json.loads(Path(geo.AVP_PROFILE).read_text())["stream_budget"]["reference_bits_per_pixel"]
    # name -> (wrote, view, eye or None = from the bitrate budget, manual_after, (mbps, fps) or None for a size that stays as given, given size)
    want = {
        "whole_3552x3200": (1, P, "budget", 0, (300, 90)),
        "fit_3552x3200": (1, fit, "budget", 0, (300, 90)),
        "fit_200mbps": (1, fit, "budget", 0, (200, 90)),
        "whole_150mbps": (1, P, "budget", 0, (150, 90)),
        "whole_300mbps_100hz": (1, P, "budget", 0, (300, 100)),
        "adaptive_bitrate_keeps_the_size": (1, fit, (3552, 3200), 0, None),
        "fit_to_window_keeps_the_size": (1, fit, (3200, 2880), 0, None),
        "per_eye_union": (1, union, "budget", 0, (300, 90)),
        "wider_than_panel": (1, wider, "budget", 0, (300, 90)),
        "quest_writes_nothing": (0, None, None, 0, None),
        "manual_passive_leaves_it": (0, None, None, 1, None),
        "manual_explicit_clears_it": (1, fit, "budget", 0, (300, 90)),
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
    for name, (wrote, view, eye, manual, budget) in want.items():
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
                have_eye = tuple(int(x) for x in g["eye"].split("x"))
                if eye == "budget":
                    want_eye = geo.choose_eye(tuple(view), rm, budget[0] * 1e6 / (budget[1] * bpp))
                    if any(abs(a - b) > 32 for a, b in zip(have_eye, want_eye)):
                        problems.append(f"stream size C# {have_eye} oracle {want_eye} (budget {budget[0]} Mbps at {budget[1]} Hz)")
                elif have_eye != tuple(eye):
                    problems.append(f"stream size {have_eye} should have stayed {eye}")
                eye = have_eye   # the geometry is judged at the size the wiring chose
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
