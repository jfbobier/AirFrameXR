#!/usr/bin/env python3
"""One command for a machine's baseline: ours vs stock ALVR + SteamVR on the comparison scenarios, repeated, summarised in one file.

    AIRFRAMEXR_PROFILE=5090 python3 harness/baseline.py [--update] [--repeat 3] [--scenarios cmp_tokyo_4k100,cmp_tokyo_4k100_gpu]

The machine comes from tools/pc.<profile>.local and tools/builder.<profile>.local (no profile: tools/pc.local, tools/builder.local).
--update: fetch the 0.3 branch into the machine's clone and rebuild the lab binaries first (host, client, shim, probe; debug flavour).
Results: harness/results/baseline_<profile>_<UTC>.md and the run folders (harness/results/*_{ours,stock}_<scenario>/)."""
import json, os, subprocess, sys, time
from datetime import datetime, timezone
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import live, lib, metrics, run, compare

a = sys.argv[1:]
repeat = int(a[a.index("--repeat") + 1]) if "--repeat" in a else 3
scen = (a[a.index("--scenarios") + 1] if "--scenarios" in a else "cmp_tokyo_4k100,cmp_tokyo_4k100_gpu").split(",")
prof = os.environ.get("AIRFRAMEXR_PROFILE", "default")
live.log(f"baseline on {live.PC} (profile {prof}), clone {live.CLONE}")
if "--update" in a:
    live.log("updating the clone to the 0.3 branch")
    print(live.pc(f'cd /d {live.CLONE} && git fetch origin 0.3 2>&1 && git checkout -f 0.3 2>&1 && git reset --hard origin/0.3 2>&1 && git log --oneline -1', timeout=300)[1])
    rc = run.build(["client", "host", "shim", "probe"])
    if rc:
        sys.exit("build failed")
lines = []
stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
for sc_name in scen:
    sc = run.load(sc_name)
    for i in range(repeat):
        for ch in ("ours", "stock"):
            t0 = time.time()
            d = (compare.run_ours if ch == "ours" else compare.run_stock)(json.loads(json.dumps(sc)), sc_name)
            try:
                m = metrics.metrics(d)
                line = f"{sc_name:24s} {metrics.line(f'{ch} #{i + 1}', m)}"
            except Exception as e:
                line = f"{sc_name:24s} {ch} #{i + 1}: no metrics ({e!r})"
            print(line, flush=True)
            lines.append(line)
out = lib.RESULTS / f"baseline_{prof}_{stamp}.md"
out.write_text(f"# baseline {prof} {stamp} ({live.PC})\n\n```\n" + "\n".join(lines) + "\n```\n")
print("summary:", out)
