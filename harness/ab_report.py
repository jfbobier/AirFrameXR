#!/usr/bin/env python3
"""A/B pairs from harness results (docs/PLAN_1_4_FOUNDATION.md: the fork lab's R6 / R8, D1): one table per scenario, a row per run, then per arm the mean and
the spread (max - min), and the rule of docs/PLAN_UNIVERSAL_OPENXR.md section 6: an arm changes a metric only when the difference of the means is larger
than 3 x the larger spread and at least `--min` (2 units by default: ms, fps or percentage points), and in every pair the same way.

    python3 harness/ab_report.py manifest.csv        # lines "arm,scenario,results_dir" (the batch writes one line per run, in run order)
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
]


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
    return r.get("status", "?"), vals


def main(argv):
    if not argv:
        print(__doc__)
        return 3
    min_delta = float(argv[argv.index("--min") + 1]) if "--min" in argv else 2.0
    rows = [row for row in csv.reader(open(argv[0])) if len(row) >= 3]
    by_scen = {}
    for arm, scen, run in rows:
        by_scen.setdefault(scen, []).append((arm, run) + load(run))
    for scen, runs in by_scen.items():
        arms = list(dict.fromkeys(a for a, *_ in runs))
        print(f"\n== {scen} ({', '.join(f'{a}: {sum(1 for x in runs if x[0] == a)} runs' for a in arms)})")
        names = [m[0] for m in METRICS if any(m[0] in x[3] for x in runs)]
        print("  ".join(["arm".ljust(10), "status".ljust(7)] + [n.rjust(11) for n in names] + ["  run"]))
        for arm, run, st, vals in runs:
            print("  ".join([arm.ljust(10), st.ljust(7)] + [(f"{vals[n]:.2f}" if n in vals else "-").rjust(11) for n in names] + ["  " + Path(run).name]))
        if len(arms) != 2:
            continue
        a, b = arms
        print(f"  verdict {b} against {a} (difference > 3 x the larger spread and >= {min_delta:g}, the same sign in every pair):")
        for n in names:
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
