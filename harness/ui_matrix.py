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
import lib

ROOT = lib.ROOT
args = sys.argv[1:]
def opt(k):
    return args[args.index(k) + 1] if k in args else None

rid = "uimatrix_" + time.strftime("%Y%m%d_%H%M%S")   # a folder per run: the builder wrapper refuses delete commands
for d in ("_tmp\\uitest\\src\\res", f"runs\\{rid}", "_tmp\\uitest\\run"):
    lib.b_run(f"if not exist {d} mkdir {d}")
for f in sorted((ROOT / "tools/gui").glob("*.cs")) + [ROOT / "tools/gui/app.manifest"]:
    lib.b_put(f, f"_tmp/uitest/src/{f.name}")
for f in ("visionalvr.ico", "logo_640.png", "chroma_passthrough.png"):
    lib.b_put(ROOT / "tools/gui/res" / f, f"_tmp/uitest/src/res/{f}")
lib.b_put(ROOT / "harness/fixtures/ui_matrix/config", "_tmp/uitest/run/")
lib.b_put(ROOT / "tools/gui/app.exe.config", "_tmp/uitest/run/uitest.exe.config")   # native per-monitor DPI mode, like the shipped exes
rc, home = lib.b_run("echo %USERPROFILE%")
base = f"{home.strip().splitlines()[-1].strip()}\\{lib.ROOTNAME}"
src = f"{base}\\_tmp\\uitest\\src"
files = " ".join(f'"{src}\\{n}"' for n in "Common.cs Configure.cs SettingsPanel.cs Reference.cs TestRunner.cs Wizard.cs Profiles.cs Verdict.cs FindHeadset.cs VisionALVR.cs Launcher.cs Performance.cs UiTest.cs".split())
fw = "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319"
refs = " ".join(f"/r:{fw}\\{r}.dll" for r in ("System", "System.Core", "System.Drawing", "System.Windows.Forms", "System.Web.Extensions", "System.IO.Compression", "System.IO.Compression.FileSystem"))
csc = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe"
bat = ("@echo off\r\n"
       f"\"{csc}\" /nologo /noconfig /target:winexe /platform:x64 /langversion:latest /nowarn:1701,1702 /main:VisionALVR.UiTest {refs} "
       f"\"/win32manifest:{src}\\app.manifest\" \"/resource:{src}\\res\\logo_640.png,logo_640.png\" \"/resource:{src}\\res\\chroma_passthrough.png,chroma_passthrough.png\" "
       f"\"/out:{base}\\_tmp\\uitest\\run\\uitest.exe\" {files}\r\n")
lib.CACHE.mkdir(parents=True, exist_ok=True)
(lib.CACHE / "uitest_build.bat").write_text(bat)
lib.b_put(lib.CACHE / "uitest_build.bat", "_tmp/uitest/build.bat")
rc, out = lib.b_run("cmd /c _tmp\\uitest\\build.bat", timeout=300)
errs = [l for l in out.splitlines() if "error" in l.lower()]
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
