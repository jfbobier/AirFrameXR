#!/usr/bin/env python3
"""The Bench recorder's pieces that need no headset (tools/gui/BenchRecorder.cs): the engine files are cut to the recording, the hotkeys register (F8 registered, F12 polled), the
recorder's own settings file round-trips. Run python3 harness/ui_matrix.py once first (it builds uitest.exe on the builder).

    python3 harness/recorder_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

rid = "recorder_" + time.strftime("%Y%m%d_%H%M%S")
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --recorder-selftest --out runs\\{rid}", timeout=120)
loc = Path(tempfile.mkdtemp(prefix="recorder_"))
lib.b_get(f"runs/{rid}/recorder.txt", loc / "recorder.txt")
f = loc / "recorder.txt"
if not f.exists():
    print("BLOCKED: no output (build uitest.exe with harness/ui_matrix.py first)"); sys.exit(2)
text = f.read_text(errors="replace")
print(text.rstrip())
fails = sum(1 for l in text.splitlines() if l.startswith("CHECK FAIL"))
skips = [l for l in text.splitlines() if l.startswith("CHECK SKIP")]
print("recorder_check:", "OK" if fails == 0 else f"{fails} FAILED", f"({len(skips)} skipped, not tested: {skips[0][11:] if skips else ""})" if skips else "")
sys.exit(1 if fails else 0)
