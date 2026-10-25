#!/usr/bin/env python3
"""Draws the demo desktop of the README's desktop-mirror screenshot (harness/fixtures/mirror_demo_desktop.png, 1920x1080): a made-up game launcher on a
plain wallpaper, no real person's data and no third-party artwork. Needs ffmpeg and the Segoe UI font (Windows' fonts folder, reached from WSL).

    python3 harness/mirror_demo_desktop.py
"""
import shutil, subprocess, sys, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
FONTS = Path("/mnt/c/Windows/Fonts")
tmp = Path(tempfile.mkdtemp(prefix="airframexr_demo_"))
for f in ("segoeui.ttf", "segoeuib.ttf"):
    shutil.copyfile(FONTS / f, tmp / f)   # a path without a drive colon for drawtext
reg, bold = tmp / "segoeui.ttf", tmp / "segoeuib.ttf"
tiles = [("Aurora Racing", "0x1475FF"), ("Orbital Siege", "0x7a3cff"), ("Skyline Flight", "0x00b4d8"),
         ("Deep Cavern", "0x2a9d8f"), ("Drift Kings", "0xe76f51"), ("Block Garden", "0xf4a261")]
wallpaper = "gradients=s=1920x1080:c0=0x061a38:c1=0x1a5fb8:x0=0:y0=0:x1=1920:y1=1080:d=1:n=2"
fl = []
# a launcher window: title bar, heading, 3 x 2 tiles
fl.append("drawbox=x=300:y=110:w=1320:h=780:color=0xf3f6fb:t=fill")
fl.append("drawbox=x=300:y=110:w=1320:h=46:color=0x1c2b45:t=fill")
fl.append(f"drawtext=fontfile={bold}:text='Game Library':x=324:y=120:fontsize=26:fontcolor=white")
fl.append(f"drawtext=fontfile={bold}:text='Play next':x=348:y=190:fontsize=40:fontcolor=0x1c2b45")
fl.append(f"drawtext=fontfile={reg}:text='Start a game from here, the headset takes over when it opens.':x=348:y=244:fontsize=24:fontcolor=0x56657f")
for i, (name, col) in enumerate(tiles):
    x, y = 348 + (i % 3) * 420, 300 + (i // 3) * 290
    fl.append(f"drawbox=x={x}:y={y}:w=384:h=214:color={col}:t=fill")
    fl.append(f"drawbox=x={x}:y={y + 214}:w=384:h=46:color=0x1c2b45:t=fill")
    fl.append(f"drawtext=fontfile={bold}:text='{name}':x={x + 16}:y={y + 222}:fontsize=26:fontcolor=white")
# the taskbar
fl.append("drawbox=x=0:y=1032:w=1920:h=48:color=0x0b1220@0.92:t=fill")
fl.append("drawbox=x=24:y=1044:w=24:h=24:color=0x1475ff:t=fill")
fl.append(f"drawtext=fontfile={reg}:text='12.00':x=1830:y=1044:fontsize=22:fontcolor=white")
out = ROOT / "harness/fixtures/mirror_demo_desktop.png"
r = subprocess.run(["ffmpeg", "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", wallpaper, "-vf", ",".join(fl), "-frames:v", "1", "-pix_fmt", "rgb24", str(out)], capture_output=True, text=True)
if r.returncode:
    print(r.stderr[-800:]); sys.exit(1)
print(out.relative_to(ROOT))
