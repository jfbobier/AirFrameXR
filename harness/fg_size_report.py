#!/usr/bin/env python3
"""Frame generation by picture preset: the e2e_fg_size_* runs (harness/scenarios/optional) side by side, one row per run: the game, the encoder and the
generator, so a slower generation can be told apart (1.4: users saw generation slow down with the bigger presets).

    python3 harness/fg_size_report.py                 the latest run of each e2e_fg_size_* scenario in harness/results
    python3 harness/fg_size_report.py DIR [DIR...]    these result folders

Columns: eye = the stream eye (the game renders it too), frame = the side-by-side frame the generator works on (Mpx), encoded = the frame after the
foveation (what the encoder gets), app / stream = fps (medians of the host's 10 s windows), enc p50 / p95 = ms per frame (with generation on the encoder
encodes every display slot: one engine has 1000 / display-rate ms), flow = the motion search per game frame, synth = one output pass, gen ms/s = the GPU time
the generator's passes span per second (flow x pairs/s + synth x outputs/s), busy0 = the builder GPU's load before the run (another program's load spoils it).
"""
import json, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = Path(__file__).resolve().parent.parent
ORDER = ["e2e_fg_size_13", "e2e_fg_size_performance", "e2e_fg_size_balanced", "e2e_fg_size_sharp"]


def row(d):
    d = Path(d)
    a = json.loads((d / "analysis.json").read_text()) if (d / "analysis.json").exists() else {}
    r = json.loads((d / "result.json").read_text()) if (d / "result.json").exists() else {}
    f, e = a.get("fg"), ((a.get("host") or {}).get("encoder") or {}).get("encode_ms") or {}
    if not f and (d / "host_stdout.txt").exists():   # runs analysed before analyse_fg existed
        f = lib.analyse_fg((d / "host_stdout.txt").read_text(errors="replace"), 90)
    f = f or {}
    gr = (a.get("game_runs") or [{}])[-1]
    pipe = gr.get("pipeline") or {}
    neg = ((a.get("client") or {}).get("negotiated_config") or {}).get("view_resolution")
    try:
        first = next(l for l in (d / "gpu.csv").read_text(errors="replace").splitlines() if l[:4].isdigit())
        busy0 = float(first.split(",")[5].strip().split()[0])
    except Exception:
        busy0 = None
    enc = pipe.get("encoded")
    fails = [c["name"] for c in r.get("checks", []) if not c.get("ok")]
    return {"scenario": r.get("scenario", d.name.split("_", 1)[-1]), "status": r.get("status", "?"), "eye": neg or pipe.get("stream_eye"),
            "frame_mpx": f.get("frame_mpx"), "encoded": enc, "encoded_mpx": round(enc[0] * enc[1] / 1e6, 2) if enc else None,
            "app": f.get("app_fps_p50"), "stream": f.get("stream_fps_p50"), "enc_p50": e.get("p50"), "enc_p95": e.get("p95"),
            "flow": f.get("flow_ms_avg"), "synth": f.get("synth_ms_avg"), "gen": f.get("gpu_ms_per_s"), "busy0": busy0, "fails": fails, "dir": d.name}


def main(argv):
    dirs = [Path(x) for x in argv]
    if not dirs:
        res = ROOT / "harness" / "results"
        for s in ORDER:
            got = sorted(res.glob(f"*_{s}"))
            if got:
                dirs.append(got[-1])
    if not dirs:
        print("no e2e_fg_size_* results (python3 harness/run.py run e2e_fg_size_13 e2e_fg_size_performance e2e_fg_size_balanced e2e_fg_size_sharp)")
        return 3
    fmt = lambda v, p=1: "-" if v is None else (f"{v:.{p}f}" if isinstance(v, float) else (f"{v[0]}x{v[1]}" if isinstance(v, list) else str(v)))
    hdr = ["scenario", "status", "eye", "frame Mpx", "encoded", "enc Mpx", "app", "stream", "enc p50", "enc p95", "flow ms", "synth ms", "gen ms/s", "busy0"]
    rows = []
    for d in dirs:
        x = row(d)
        rows.append([x["scenario"].replace("e2e_fg_size_", ""), x["status"], fmt(x["eye"]), fmt(x["frame_mpx"], 2), fmt(x["encoded"]), fmt(x["encoded_mpx"], 2),
                     fmt(x["app"]), fmt(x["stream"]), fmt(x["enc_p50"]), fmt(x["enc_p95"]), fmt(x["flow"], 2), fmt(x["synth"], 2), fmt(x["gen"], 0), fmt(x["busy0"], 0)])
        if x["fails"]:
            rows[-1].append("fails: " + ", ".join(x["fails"]))
    w = [max(len(str(r[i])) for r in [hdr] + rows) for i in range(len(hdr))]
    print("  ".join(h.ljust(w[i]) for i, h in enumerate(hdr)))
    for r in rows:
        print("  ".join(str(c).ljust(w[i]) for i, c in enumerate(r[:len(hdr)])) + ("  " + r[len(hdr)] if len(r) > len(hdr) else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
