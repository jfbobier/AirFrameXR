#!/usr/bin/env python3
"""A/B of the foveal QP delta map with the quality benchmark as the judge: the same sweep (docs/BENCHMARK_DESIGN.md) with the map off and on, at one or
several bitrates, and per configuration the PSNR against the native render (centre, periphery, foveal-weighted) and the encode time side by side. The map only
acts on the strongly foveated configurations (center -3, edge +6). A QP shift matters most when rate control is tight, hence the bitrate list. The sweep needs
the GPU to itself (no other harness run). The picture is judged by PSNR here, not by eye.

    python3 harness/qpmap_ab.py [quick|full] [WxH] [mbps,mbps,...]
        WxH = the base eye size (default: the session's, Vision Pro 3552x3200; 2064x2208 = Meta Quest 3)   mbps default 250 (the benchmark's own)
"""
import json, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
plan = sys.argv[1] if len(sys.argv) > 1 else "full"
eye = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "-" else None
rates = [int(x) for x in sys.argv[3].split(",")] if len(sys.argv) > 3 else [0]
for mbps in rates:
    runs = {}
    for qp in ("off", "on"):
        cmd = [sys.executable, str(ROOT / "harness/run.py"), "bench-quality", plan, "--qp", qp] + (["--eye", eye] if eye else []) + (["--mbps", str(mbps)] if mbps else [])
        subprocess.run(cmd, capture_output=True, text=True)   # exit 1 = its own sanity checks (they assume the default sweep), the results are what is read here
        d = sorted((ROOT / "harness/results").glob(f"*_bench_quality_{plan}_qp{qp}" + (f"_{eye}" if eye else "") + (f"_{mbps}mbps" if mbps else "")))[-1]
        events = [json.loads(l) for l in (d / "stdout.jsonl").read_text(errors="replace").splitlines() if l.startswith("{")]
        runs[qp] = {e["data"]["label"]: e["data"] for e in events if e.get("event") == "bq_result" and e["data"].get("ok")}
    common = [l for l in runs["off"] if l in runs["on"] and runs["on"][l].get("qp_map")]
    print(f"\n=== {mbps or 250} Mbps, eye {eye or 'session'}: {len(common)} configurations where the map acts (foveated)")
    print(f"{'configuration':44s} {'weighted':>15s} {'centre':>15s} {'periphery':>15s} {'encode p50':>12s}   off>on  dB / ms   produced Mbps off>on")
    sums = {k: 0.0 for k in ("weighted", "center", "periphery")}
    for l in common:
        a, b = runs["off"][l], runs["on"][l]
        row = []
        for k in ("weighted", "center", "periphery"):
            d = b["psnr"][k] - a["psnr"][k]; sums[k] += d
            row.append(f"{a['psnr'][k]:5.2f}>{b['psnr'][k]:5.2f}{d:+.2f}")
        e0, e1 = a["encode_ms"]["p50"], b["encode_ms"]["p50"]
        print(f"{l[:44]:44s} {row[0]:>15s} {row[1]:>15s} {row[2]:>15s} {e0:4.1f}>{e1:4.1f}{e1 - e0:+.1f}   {a['produced_mbps']:5.1f}>{b['produced_mbps']:5.1f}")
    if common:
        n = len(common)
        print(f"mean change with the map on: weighted {sums['weighted'] / n:+.2f} dB, centre {sums['center'] / n:+.2f} dB, periphery {sums['periphery'] / n:+.2f} dB")
