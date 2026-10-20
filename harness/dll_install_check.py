#!/usr/bin/env python3
"""U2/U6 (docs/PLAN_0.5.md): the motion-vector DLL's install / update / remove in a temporary game folder (beside a foreign dxgi.dll too), the dismissal, and the
decision table of the vectors line. `uitest.exe --dll-install-selftest` writes CHECK lines. The elevated-copy fallback (a folder that needs administrator rights) cannot run
headless: it needs a person at the UAC prompt (checklist item). Exit 0 = pass."""
import sys, time, subprocess
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

rid = "dll_check_" + time.strftime("%Y%m%d_%H%M%S")
errs = uibuild.build(rid)
if errs:
    print("uitest.exe does not build:\n" + "\n".join(errs[:10])); sys.exit(2)
lib.b_run(f"if not exist runs\\{rid}\\out mkdir runs\\{rid}\\out")
proc = lib.start_limited(rid, "va_dll_check", "dll.bat", f"_tmp\\uitest\\run\\uitest.exe --dll-install-selftest --out runs\\{rid}\\out > runs\\{rid}\\uitest.txt 2>&1\r\n", "rc.txt")
try:
    proc.wait(120)
except subprocess.TimeoutExpired:
    print("BLOCKED: uitest.exe did not finish"); sys.exit(2)
dest = lib.ROOT / "harness/results/dll_install_check" / time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) / "dll_install.txt"
lib.b_get(f"runs/{rid}/out/dll_install.txt", dest)
txt = dest.read_text(errors="replace") if dest.exists() else ""
print(txt.strip() or "CHECK FAIL: no output")
sys.exit(0 if txt and "CHECK FAIL" not in txt else 1)
