#!/usr/bin/env python3
"""A/B pairs from harness results (docs/PLAN_1_4_FOUNDATION.md: the fork lab's R6 / R8, D1): one table per scenario, a row per run, then per arm the mean and
the spread (max - min), and the rule of docs/PLAN_UNIVERSAL_OPENXR.md section 6: an arm changes a metric only when the difference of the means is larger
than 3 x the larger spread and at least `--min` (2 units by default: ms, fps or percentage points), and in every pair the same way.

    python3 harness/ab_report.py manifest.csv        # lines "arm,scenario,results_dir" (the batch writes one line per run, in run order)
    python3 harness/ab_report.py manifest.csv --idle 10   # only the runs whose GPU was idle (<= 10 % in gpu.csv's first sample) at the host's start
"""
import csv, json, sys
from pathlib import Path

METRICS = [   # (name, path in analysis.json, unit)
    ("app_fps", ("app_timing", "fps"), "fps"),
    ("encode_p50", ("host", "encoder", "encode_ms", "p50"), "ms"),
    ("encode_p95", ("host", "encoder", "encode_ms", "p95"), "ms"),
    ("compose", ("host", "encoder", "shim_compose_ms_avg"), "ms"),
    ("ffr_gpu", ("host", "encoder", "ffr_gpu_ms_avg"), "ms"),
    ("pace_p95", ("pacing", "p95"), "ms"),
    ("pace_p99", ("pacing", "p99"), "ms"),
    ("late_pct", ("pacing", "late_frac"), "%"),
    ("latency_p50", ("client", "arrival_minus_timestamp_ms", "p50"), "ms"),
    ("client_fps", ("client", "fps_measured"), "fps"),
    ("fg_flow", ("fg", "flow_ms_avg"), "ms"),          # frame generation (analyse_fg): the motion search per game frame
    ("fg_synth", ("fg", "synth_ms_avg"), "ms"),        # one output pass
    ("fg_gpu_ms_s", ("fg", "gpu_ms_per_s"), "ms"),     # the generator's GPU time per second
    ("fg_stream_fps", ("fg", "stream_fps_p50"), "fps"),
]
SHOWN_ONLY = ["gpu_busy0"]   # columns without a verdict (gpu.csv's first utilization sample, %)


def get(d, path):
    for k in path:
        if not isinstance(d, dict) or k not in d:
            return None
        d = d[k]
    return d


def load(run):
    p = Path(run)
    a = json.loads((p / "analysis.json").read_text()) if (p / "analysis.json").exists() else {}
    r = json.loads((p / "result.json").read_text()) if (p / "result.json").exists() else {}
    vals = {}
    for name, path, unit in METRICS:
        v = get(a, path)
        if isinstance(v, (int, float)):
            vals[name] = 100.0 * v if unit == "%" else float(v)
    # the builder's GPU before our encoder ran (gpu.csv's first sample, at the host's start): another program's load there (2026-11-20, R6:
    # 80-98 % and 88 W for half an hour) slows both arms, so a pair with a busy start is not evidence either way
    try:
        first = next(l for l in (p / "gpu.csv").read_text(errors="replace").splitlines() if l[:4].isdigit())
        vals["gpu_busy0"] = float(first.split(",")[5].strip().split()[0])
    except Exception:
        pass
    return r.get("status", "?"), vals


def main(argv):
    if not argv:
        print(__doc__)
        return 3
    min_delta = float(argv[argv.index("--min") + 1]) if "--min" in argv else 2.0
    rows = [row for row in csv.reader(open(argv[0])) if len(row) >= 3]
    by_scen = {}
    idle = float(argv[argv.index("--idle") + 1]) if "--idle" in argv else None
    for arm, scen, run in rows:
        x = (arm, run) + load(run)
        if idle is not None and x[3].get("gpu_busy0", 0.0) > idle:
            print(f"  (left out, the GPU was {x[3]['gpu_busy0']:.0f} % busy at the start: {Path(run).name})")
            continue
        by_scen.setdefault(scen, []).append(x)
    for scen, runs in by_scen.items():
        arms = list(dict.fromkeys(a for a, *_ in runs))
        print(f"\n== {scen} ({', '.join(f'{a}: {sum(1 for x in runs if x[0] == a)} runs' for a in arms)})")
        names = [m[0] for m in METRICS if any(m[0] in x[3] for x in runs)] + [n for n in SHOWN_ONLY if any(n in x[3] for x in runs)]
        print("  ".join(["arm".ljust(10), "status".ljust(7)] + [n.rjust(11) for n in names] + ["  run"]))
        for arm, run, st, vals in runs:
            print("  ".join([arm.ljust(10), st.ljust(7)] + [(f"{vals[n]:.2f}" if n in vals else "-").rjust(11) for n in names] + ["  " + Path(run).name]))
        if len(arms) != 2:
            continue
        a, b = arms
        print(f"  verdict {b} against {a} (difference > 3 x the larger spread and >= {min_delta:g}, the same sign in every pair):")
        for n in names:
            if n in SHOWN_ONLY:
                continue
            va = [x[3][n] for x in runs if x[0] == a and n in x[3]]
            vb = [x[3][n] for x in runs if x[0] == b and n in x[3]]
            if not va or not vb:
                continue
            ma, mb = sum(va) / len(va), sum(vb) / len(vb)
            spread = max(max(va) - min(va), max(vb) - min(vb))
            pairs = list(zip(va, vb))
            same_sign = all((y - x) * (mb - ma) > 0 for x, y in pairs) if pairs else False
            changed = abs(mb - ma) > 3 * spread and abs(mb - ma) >= min_delta and same_sign
            print(f"    {n:12s} {a} {ma:8.2f}  {b} {mb:8.2f}  diff {mb - ma:+7.2f}  spread {spread:6.2f}  -> {'CHANGED' if changed else 'within noise'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
