#!/usr/bin/env python3
"""Windows that start centred are centred on their screen's work area once their content has sized them (tools/gui/Common.cs Ui.AutoFitWindow).
Builds uitest.exe on the builder and runs it in the interactive session (the window positions are the real screen's).

    python3 harness/center_check.py
"""
import subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

rid = "center_" + time.strftime("%Y%m%d_%H%M%S")
errs = uibuild.build(rid)
if errs:
    print("uitest.exe does not build:\n" + "\n".join(errs[:10])); sys.exit(2)
body = f"_tmp\\uitest\\run\\uitest.exe --center-selftest --out runs\\{rid} > runs\\{rid}\\uitest.txt 2>&1\r\n"
proc = lib.start_limited(rid, "va_center", "center.bat", body, "rc.txt")
try:
    proc.wait(120)
except subprocess.TimeoutExpired:
    print("BLOCKED: uitest.exe did not finish in 2 minutes"); sys.exit(2)
loc = Path(tempfile.mkdtemp(prefix="center_"))
lib.b_get(f"runs/{rid}/center.txt", loc / "center.txt")
f = loc / "center.txt"
if not f.exists():
    print("BLOCKED: no output"); sys.exit(2)
text = f.read_text(errors="replace")
print(text.rstrip())
fails = sum(1 for l in text.splitlines() if l.startswith("CHECK FAIL"))
print("center_check:", "OK" if fails == 0 else f"{fails} FAILED")
sys.exit(1 if fails else 0)
