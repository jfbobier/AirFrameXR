#!/usr/bin/env python3
"""The GUI at 1080p, 1440p and 4K, at the Windows scales people use (docs/PLAN_0.5.md). Builds tools/gui/UiTest.cs (uitest.exe) on the builder, runs it
in the interactive session and audits every window, dialog and wizard page at each resolution x scale:
  - the window fits the work area (screen minus the taskbar); a resizable window must at least have a minimum size that fits
  - no control is clipped by its parent, no siblings overlap, no button / label / list header is cut off, lists do not need sideways scrolling
Each case is saved as a PNG next to the results (harness/results/ui_matrix/<time>/).

    python3 harness/ui_matrix.py                        # the full matrix (9 screen/scale cases x ~19 windows and pages)
    python3 harness/ui_matrix.py --matrix 1920x1080@150 # one case
    python3 harness/ui_matrix.py --only streamer        # one window

The scale is emulated (see the header of tools/gui/UiTest.cs): the real laptop's own scale is not changed. Exit 0 = no FAIL (warnings are listed).
"""
import json, re, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

ROOT = lib.ROOT
args = sys.argv[1:]
def opt(k):
    return args[args.index(k) + 1] if k in args else None

rid = "uimatrix_" + time.strftime("%Y%m%d_%H%M%S")   # a folder per run: the builder wrapper refuses delete commands
errs = uibuild.build(rid)
if errs:
    print("uitest.exe does not build:\n" + "\n".join(errs[:10])); sys.exit(2)

extra = (f" --matrix {opt('--matrix')}" if opt("--matrix") else "") + (f" --only {opt('--only')}" if opt("--only") else "")
body = f"_tmp\\uitest\\run\\uitest.exe --out runs\\{rid}\\out{extra} > runs\\{rid}\\uitest.txt 2>&1\r\n"
proc = lib.start_limited(rid, "va_uimatrix", "uimatrix.bat", body, "rc.txt")
try:
    proc.wait(900)
except subprocess.TimeoutExpired:
    print("BLOCKED: uitest.exe did not finish in 15 minutes"); sys.exit(2)

dest = ROOT / "harness/results/ui_matrix" / time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
dest.mkdir(parents=True, exist_ok=True)
lib.b_get(f"runs/{rid}/out", dest)
dest = dest / "out"   # scp -r puts the folder inside
res_file = dest / "results.json"
if not res_file.exists():
    print("BLOCKED: no results.json (see harness/results/ui_matrix)"); sys.exit(2)
res = json.loads(res_file.read_text())
fails = [(r, i) for r in res for i in r["issues"] if i.startswith("FAIL")]
warns = [(r, i) for r in res for i in r["issues"] if i.startswith("WARN")]
cases = sorted({(r["screen"], r["scale"]) for r in res}, key=lambda c: (int(c[0].split("x")[0]), c[1]))
forms = []
for r in res:
    if r["form"] not in forms:
        forms.append(r["form"])
print(f"{'':22}" + "".join(f"{s.split('x')[1]}p@{p}".rjust(9) for s, p in cases))
for f in forms:
    row = f"{f:22}"
    for s, p in cases:
        r = next((x for x in res if x["form"] == f and x["screen"] == s and x["scale"] == p), None)
        mark = "-" if r is None else ("FAIL" if any(i.startswith("FAIL") for i in r["issues"]) else ("warn" if r["issues"] else "ok"))
        row += mark.rjust(9)
    print(row)
print()
groups = {}
for r, i in fails + warns:
    key = (i[:4], r["form"], re.sub(r"\d+", "#", i[5:])[:150])
    g = groups.setdefault(key, {"example": i[5:], "cases": set()})
    g["cases"].add(f"{r['screen'].split('x')[1]}p@{r['scale']}")
def case_order(c): return (int(c.split("p@")[0]), int(c.split("@")[1]))
for (lvl, form, _), g in sorted(groups.items(), key=lambda kv: (kv[0][0] != "FAIL", kv[0][1])):
    print(f"{lvl} {form}: {g['example'][:170]}   [{', '.join(sorted(g['cases'], key=case_order))}]")
print(f"\nui_matrix: {len(res)} renders, {len(fails)} FAIL, {len(warns)} warnings; PNGs in {dest}")
sys.exit(1 if fails else 0)
