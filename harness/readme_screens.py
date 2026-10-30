#!/usr/bin/env python3
"""Regenerates the screenshots of the README (docs/images/ui_*.png) from the UI harness: the windows at 1080p / 125 %, with the harness's demo data (fixture game
records and logs, a demo headset), never a real person's data. Run it after a GUI change, look at the images, commit them.

    python3 harness/readme_screens.py
"""
import re, shutil, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
SHOTS = {   # harness case -> README image (the size the picture is taken at is the second item when it is not 1080p / 125 %)
    "launcher-ready": "ui_launcher", "streamer-clean": "ui_streamer", "performance": "ui_performance", "wizard-page3": "ui_wizard",
    "suggestions": "ui_suggestions", "firewall-rules": "ui_firewall",
    "rec-recording": "ui_blackbox_recorder",                              # the Black Box (docs/BENCH_UI.md)
    "analyze-problem-tall": ("ui_blackbox_analyzer", "2560x1440@100"),    # as tall as the screen allows: the resource table and the charts together
}
sizes = {}
for case, v in SHOTS.items():
    name, size = (v, "1920x1080@125") if isinstance(v, str) else v
    sizes.setdefault(size, []).append((case, name))
out = ROOT / "docs/images"; out.mkdir(exist_ok=True)
code = 0
for size, items in sizes.items():
    p = subprocess.run([sys.executable, str(ROOT / "harness/ui_matrix.py"), "--matrix", size], capture_output=True, text=True)
    m = re.search(r"PNGs in (\S+)", p.stdout)
    if not m:
        print(p.stdout[-600:]); sys.exit(2)
    src = Path(m.group(1))
    for case, name in items:
        f = src / f"{case}_{size}.png"
        shutil.copy(f, out / f"{name}.png")
        print("docs/images/%s.png  <-  %s" % (name, f.name))
    code = code or p.returncode
print("ui_matrix exit code", code, "(0 = no layout failure)")
