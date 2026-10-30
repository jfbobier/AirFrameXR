#!/usr/bin/env python3
"""Regenerates the screenshots of the README (docs/images/ui_*.png) from the UI harness: the windows at 1080p / 125 %, with the harness's demo data (fixture game
records and logs, a demo headset), never a real person's data. Run it after a GUI change, look at the images, commit them.

    python3 harness/readme_screens.py
"""
import re, shutil, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
SHOTS = {   # harness case -> README image
    "launcher-ready": "ui_launcher", "streamer-clean": "ui_streamer", "performance": "ui_performance", "wizard-page3": "ui_wizard",
    "suggestions": "ui_suggestions", "firewall-rules": "ui_firewall",
    "rec-recording": "ui_blackbox_recorder", "analyze-problem": "ui_blackbox_analyzer",   # the Black Box (docs/BENCH_UI.md)
}
p = subprocess.run([sys.executable, str(ROOT / "harness/ui_matrix.py"), "--matrix", "1920x1080@125"], capture_output=True, text=True)
m = re.search(r"PNGs in (\S+)", p.stdout)
if not m:
    print(p.stdout[-600:]); sys.exit(2)
src = Path(m.group(1)); out = ROOT / "docs/images"; out.mkdir(exist_ok=True)
for case, name in SHOTS.items():
    f = src / f"{case}_1920x1080@125.png"
    shutil.copy(f, out / f"{name}.png")
    print("docs/images/%s.png  <-  %s" % (name, f.name))
print("ui_matrix exit code", p.returncode, "(0 = no layout failure)")
