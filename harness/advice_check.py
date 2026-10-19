#!/usr/bin/env python3
"""The Diagnose suggestions (tools/gui/Advice.cs, from docs/PERFORMANCE_KNOBS.md): every rule against synthetic wizard metrics and fixture debug logs / game
records, and a clean setup that must stay quiet. Run python3 harness/ui_matrix.py once first (it builds uitest.exe on the builder).

    python3 harness/advice_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

rid = "advice_" + time.strftime("%Y%m%d_%H%M%S")
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --advice-selftest --out runs\\{rid}", timeout=120)
loc = Path(tempfile.mkdtemp(prefix="advice_"))
lib.b_get(f"runs/{rid}/advice.txt", loc / "advice.txt")
f = loc / "advice.txt"
if not f.exists():
    print("BLOCKED: no output (build uitest.exe with harness/ui_matrix.py first)"); sys.exit(2)
text = f.read_text(errors="replace")
print(text.rstrip())
fails = sum(1 for l in text.splitlines() if l.startswith("CHECK FAIL"))
print("advice_check:", "OK" if fails == 0 else f"{fails} FAILED")
sys.exit(1 if fails else 0)
