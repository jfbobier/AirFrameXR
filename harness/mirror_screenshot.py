#!/usr/bin/env python3
"""Regenerates the README's desktop-mirror screenshot (docs/images/ui_mirror.png): the mirror renders the demo desktop (harness/fixtures/mirror_demo_desktop.png, drawn by
mirror_demo_desktop.py) through the whole chain (scenario e2e_mirror_screenshot, mock headset on the builder) and a frame of the decoded stream is cropped around the
screen: what the left eye sees, with the controller's beam. Run it after a mirror change, look at the image, commit it.

    python3 harness/mirror_screenshot.py
"""
import subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
p = subprocess.run([sys.executable, str(ROOT / "harness/run.py"), "run", "e2e_mirror_screenshot"], capture_output=True, text=True)
if p.returncode != 0:
    print(p.stdout[-800:]); sys.exit(p.returncode)
run = sorted((ROOT / "harness/results").glob("*_e2e_mirror_screenshot"))[-1]
out = ROOT / "docs/images/ui_mirror.png"
# the left eye is 1760 x 1600 of the 3520 x 1600 stream; the screen and the beam sit about 500..1330 x 640..1030 in it
r = subprocess.run(["ffmpeg", "-hide_banner", "-v", "error", "-y", "-i", str(run / "client.hevc"), "-vf", "select=eq(n\\,1000),crop=830:390:500:640,scale=1660:-1:flags=lanczos,format=rgb24",
                    "-frames:v", "1", str(out)], capture_output=True, text=True)
if not out.exists():
    print(r.stderr[-500:]); sys.exit(2)
print(out.relative_to(ROOT), "<-", run.name)
