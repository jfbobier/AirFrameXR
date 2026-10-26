#!/usr/bin/env python3
"""Frame generation judge (docs/FRAME_GENERATION_JUDGE.md): classic, edge and edge-blend scored against a rendered ground truth on the benchmark scene.
The builder renders Littlest Tokyo at times A and B (the pair a game at half the display rate delivers) and at the midpoint G, generates the midpoint with
each synthesis and scores it against G (PSNR / SSIM over the frame, in the moving region and on its boundary band, edge energy in excess = double images or in
deficit = blur); holding B and the 50/50 blend are the references. Scenarios: scene (head still), head (scene frozen), both. This script runs it, then prints
per scenario and rate the means, and the paired differences between the syntheses over all cases with their standard errors, and builds picture montages.

    python3 harness/fg_judge.py [--eye WxH] [--starts N] [--gaps 22.2,33.3,44.4] [--speeds 1,8,24]
    python3 harness/fg_judge.py --report <results dir>      # re-evaluate saved results
"""
import json, math, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

KINDS = ["hold", "blend", "classic", "edge", "edge_blend"]
METRICS = [("psnr_band", "PSNR on the boundary band (dB)", +1), ("psnr_moving", "PSNR in the moving region (dB)", +1), ("psnr", "PSNR whole frame (dB)", +1),
           ("ssim_moving", "SSIM in the moving region", +1), ("edge_excess", "edge excess on the band (double images)", -1), ("edge_deficit", "edge deficit on the band (blur)", -1)]


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def run_on_builder():
    eye, starts, gaps, speeds = arg("--eye", "1032x1104"), arg("--starts", "6"), arg("--gaps", "22.2,33.3,44.4"), arg("--speeds", "1,8,24")
    rid = "fgj_" + datetime.now(timezone.utc).strftime("%H%M%S")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    outdir = lib.RESULTS / f"{stamp}_fg_judge"
    outdir.mkdir(parents=True, exist_ok=True)
    lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
    lib.b_put(lib.ROOT / "harness/fixtures/session.user.json", f"runs/{rid}/session.json")
    r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage22_fg_judge.ps1", "-Label", rid, "-Eye", eye, "-Starts", starts, "-Gaps", gaps.replace(",", "+"), "-Speeds", speeds.replace(",", "+")],
                       cwd=lib.ROOT, capture_output=True, timeout=1800)
    o = r.stdout.decode(errors="replace").replace("\x00", "")
    print(o.strip().splitlines()[-2:] if o.strip() else "no output")
    lib.b_get(f"runs/{rid}/stdout.jsonl", outdir / "stdout.jsonl")
    lib.b_get(f"runs/{rid}/stderr.txt", outdir / "stderr.txt")
    lib.b_get(f"runs/{rid}/out", outdir / "out")
    return outdir


def load_cases(d):
    cases = []
    for l in (d / "stdout.jsonl").read_text(errors="replace").splitlines():
        if l.startswith("{"):
            try:
                e = json.loads(l)
            except ValueError:
                continue
            if e.get("event") == "fgj_case":
                cases.append(e["data"])
            elif e.get("event") == "fgj_error":
                print("ERROR:", e["data"])
    return cases


def mean(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else None


def fmt(x, nd=2):
    return "   -  " if x is None else f"{x:6.{nd}f}"


def montage(d):
    """truth | hold | classic | edge | edge-blend crops, and the 4x difference images, per scenario and rate (first start time)."""
    out = d / "out"
    for truth in sorted(out.glob("*_truth.ppm")):
        stem = truth.name[:-len("_truth.ppm")]
        for label, names in (("pictures", ["truth", "hold", "classic", "edge", "edge_blend"]), ("difference_x4", ["classic_diff", "edge_diff", "edge_blend_diff"])):
            files = [out / f"{stem}_{n}.ppm" for n in names]
            if not all(f.exists() for f in files):
                continue
            cmd = ["ffmpeg", "-hide_banner", "-v", "error", "-y"]
            for f in files:
                cmd += ["-i", str(f)]
            cmd += ["-filter_complex", f"hstack=inputs={len(files)}", "-frames:v", "1", str(d / f"{stem}_{label}.png")]
            subprocess.run(cmd, capture_output=True)


def report(d):
    cases = load_cases(d)
    if not cases:
        print("no cases"); return 2
    gaps = sorted({c["gap_ms"] for c in cases})
    for c in cases:
        c["label"] = c["scenario"] if c["scenario"] == "head" else f'{c["scenario"]} x{c["speed"]:g}'
    scen = sorted({c["label"] for c in cases}, key=lambda x: (x.split()[0] != "head", x))
    print(f"{len(cases)} cases ({len(gaps)} rates of the rendered pair x {len(scen)} scenarios x {len({c['start'] for c in cases})} start times)")
    for s in scen:
        print(f"moving region, {s:10s}: {100 * mean([c['moving_frac'] for c in cases if c['label'] == s]):5.1f} % of the frame on average")
    print()
    for key, title, sign in METRICS:
        print(f"== {title}  (mean over start times; better = {'higher' if sign > 0 else 'lower'})")
        print(f"{'scenario':10s} {'pair gap':>9s} " + " ".join(f"{k:>10s}" for k in KINDS))
        for s in scen:
            for g in gaps:
                sel = [c for c in cases if c["label"] == s and c["gap_ms"] == g]
                if sel:
                    print(f"{s:10s} {g:7.1f}ms " + " ".join(f"{fmt(mean([c[k][key] for c in sel])):>10s}" for k in KINDS))
        print()
    print("== paired differences over every case (mean, standard error of the mean, wins of the first)")
    pairs = [("edge", "classic"), ("edge", "edge_blend"), ("edge_blend", "classic")]
    for key, title, sign in METRICS:
        for a, b in pairs:
            d_ = [(c[a][key] - c[b][key]) * sign for c in cases if c["moving_frac"] > 0.0005 and c[a][key] is not None and c[b][key] is not None]
            if len(d_) < 2:
                continue
            m = sum(d_) / len(d_)
            se = math.sqrt(sum((x - m) ** 2 for x in d_) / (len(d_) - 1) / len(d_))
            wins = sum(1 for x in d_ if x > 0)
            verdict = "better" if m > 2 * se and m > 0 else ("worse" if m < -2 * se else "tie")
            print(f"  {title[:46]:46s} {a:>10s} - {b:<10s} {m:+8.4f} +- {se:6.4f}  wins {wins:3d}/{len(d_):3d}  {verdict}")
        print()
    print("timing (GPU, last case): flow", cases[-1]["flow_ms"], "ms; synthesis", cases[-1]["synth_ms"])
    montage(d)
    print("pictures:", d)
    return 0


if __name__ == "__main__":
    if "--report" in sys.argv:
        sys.exit(report(Path(arg("--report", ""))))
    d = run_on_builder()
    sys.exit(report(d))
