#!/usr/bin/env python3
"""1:1 comparison of our chain with stock ALVR + SteamVR on one scenario of the replay lab (same session.json, same head trace, same mock headset,
same OpenXR app), on the machine of tools/builder.local / tools/pc.local.

    python3 harness/compare.py <scenario> [--chains ours,stock] [--repeat N] [--seconds S]
    python3 harness/compare.py --metrics <dir> [<dir> ...]       # only summarise existing result dirs

The scenario is a harness/scenarios/**/<name>.json (resolution, fps, bitrate, foveation, display emulation, head trace: `session_overrides`, `host`,
`client`, `app`). Results: harness/results/<UTC>_{ours,stock}_<scenario>/ plus a one-line summary per run from harness/metrics.py (latency from head
motion to the picture on the display, repeated display frames, rotation the client must supply)."""
import json, sys, time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import lib, metrics, run, stock


def run_ours(sc, name):
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out = lib.RESULTS / f"{stamp}_ours_{name}"
    lib.run_scenario(sc, out, log=print)
    return out


def run_stock(sc, name):
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out = lib.RESULTS / f"{stamp}_stock_{name}"
    stock.run_stock(sc, out)
    return out


def main(a):
    if "--metrics" in a:
        for d in a[a.index("--metrics") + 1:]:
            print(metrics.line(Path(d).name[:34], metrics.metrics(d)))
        return 0
    name = a[0]
    chains = (a[a.index("--chains") + 1] if "--chains" in a else "ours,stock").split(",")
    repeat = int(a[a.index("--repeat") + 1]) if "--repeat" in a else 1
    sc = run.load(name)
    if "--seconds" in a:
        s = int(a[a.index("--seconds") + 1])
        sc["host"]["seconds"] = s
        sc["client"]["seconds"] = s + 4
        sc.setdefault("app", {})["timeout_s"] = s + 11
        sc["app"].pop("frames", None)
    rows = []
    for i in range(repeat):
        for ch in chains:
            t0 = time.time()
            d = (run_ours if ch == "ours" else run_stock)(json.loads(json.dumps(sc)), name)
            try:
                rows.append((f"{ch} #{i + 1}", d, metrics.metrics(d)))
                print(metrics.line(f"{ch} #{i + 1}", rows[-1][2]), f"  [{d.name}, {time.time() - t0:.0f} s]")
            except Exception as e:
                print(f"{ch} #{i + 1}: no metrics ({e!r}) in {d}")
    print("\n== " + name)
    for n, d, m in rows:
        print(metrics.line(n, m))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
