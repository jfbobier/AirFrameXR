#!/usr/bin/env python3
"""A/B of the foveal QP delta map with the quality benchmark as the judge: the same sweep (docs/BENCHMARK_DESIGN.md) with the map off and on, then per
configuration the PSNR against the native render (centre, periphery, foveal-weighted) and the encode time of the two runs side by side. The map only acts on
the foveated configurations (center -3, edge +6). The sweep needs the GPU to itself (no other harness run). The picture is judged by PSNR here, not by eye.

    python3 harness/qpmap_ab.py [quick|full]
"""
import json, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
plan = sys.argv[1] if len(sys.argv) > 1 else "full"
runs = {}
for qp in ("off", "on"):
    p = subprocess.run([sys.executable, str(ROOT / "harness/run.py"), "bench-quality", plan, "--qp", qp], capture_output=True, text=True)
    print(f"qp {qp}: bench-quality exit {p.returncode}")
    d = sorted((ROOT / "harness/results").glob(f"*_bench_quality_{plan}_qp{qp}"))[-1]
    events = [json.loads(l) for l in (d / "stdout.jsonl").read_text(errors="replace").splitlines() if l.startswith("{")]
    runs[qp] = {e["data"]["label"]: e["data"] for e in events if e.get("event") == "bq_result" and e["data"].get("ok")}
    print("  ", d.name, len(runs[qp]), "configurations")
common = [l for l in runs["off"] if l in runs["on"] and runs["on"][l].get("qp_map")]
print(f"\n{len(common)} configurations where the map acts (foveated):")
print(f"{'configuration':58s} {'weighted':>16s} {'centre':>16s} {'periphery':>16s} {'encode p50 ms':>16s}   (off -> on, dB / ms)")
sums = {k: 0.0 for k in ("weighted", "center", "periphery")}
for l in common:
    a, b = runs["off"][l], runs["on"][l]
    row = []
    for k in ("weighted", "center", "periphery"):
        d = b["psnr"][k] - a["psnr"][k]; sums[k] += d
        row.append(f"{a['psnr'][k]:5.2f}>{b['psnr'][k]:5.2f} {d:+.2f}")
    e0, e1 = a["encode_ms"]["p50"], b["encode_ms"]["p50"]
    print(f"{l[:58]:58s} {row[0]:>16s} {row[1]:>16s} {row[2]:>16s} {e0:5.1f}>{e1:5.1f} {e1 - e0:+.1f}")
if common:
    n = len(common)
    print(f"\nmean change with the map on: weighted {sums['weighted'] / n:+.2f} dB, centre {sums['center'] / n:+.2f} dB, periphery {sums['periphery'] / n:+.2f} dB")
