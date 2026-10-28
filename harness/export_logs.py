#!/usr/bin/env python3
"""U1 (docs/permanent_record/PLAN_0.5.md): the log export writes its zip to a path with no dialog and leaves no window open. `uitest.exe --export-selftest <zip> --game <stem>` runs
LogExport.Write against the fixture's logs and game records; the checks are on the zip's entries and on Application.OpenForms. Exit 0 = pass."""
import sys, time, subprocess
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

rid = "export_logs_" + time.strftime("%Y%m%d_%H%M%S")
errs = uibuild.build(rid)
if errs:
    print("uitest.exe does not build:\n" + "\n".join(errs[:10])); sys.exit(2)
lib.b_run(f"if not exist runs\\{rid}\\out mkdir runs\\{rid}\\out")
proc = lib.start_limited(rid, "va_export_logs", "export.bat", f"_tmp\\uitest\\run\\uitest.exe --export-selftest runs\\{rid}\\export.zip --game Cyberpunk2077 --out runs\\{rid}\\out > runs\\{rid}\\uitest.txt 2>&1\r\n", "rc.txt")
try:
    proc.wait(120)
except subprocess.TimeoutExpired:
    print("BLOCKED: uitest.exe did not finish"); sys.exit(2)
dest = lib.ROOT / "harness/results/export_logs" / time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
lib.b_get(f"runs/{rid}/out", dest)
f = dest / "out" / "export.txt"
if not f.exists():
    f = dest / "export.txt"   # scp puts the folder inside only when the target exists
if not f.exists():
    print("CHECK FAIL: no export.txt"); sys.exit(1)
names = f.read_text().split()
fails = 0
def check(name, ok):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}")
check("game record in the zip", "game/Cyberpunk2077.json" in names)
check("general log in the zip", "AirFrameXR.log" in names)
check("newest debug run in the zip", any(n.startswith("debug/") for n in names))
check("info.txt in the zip", "info.txt" in names)
check("no window left open", "open_forms=0" in names)
sys.exit(1 if fails else 0)
