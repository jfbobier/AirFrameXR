#!/usr/bin/env python3
"""Regenerates the README's desktop-mirror screenshot (docs/images/ui_mirror.png): the mirror renders the demo desktop (harness/fixtures/mirror_demo_desktop.png, drawn by
mirror_demo_desktop.py) through the whole chain (scenario e2e_mirror_screenshot, mock headset on the builder) and a frame of the decoded stream is cropped around the
screen: what the left eye sees, with the controller's beam. Run it after a mirror change, look at the image, commit it.

    python3 harness/mirror_screenshot.py            # the demo desktop: docs/images/ui_mirror.png (the README)
    python3 harness/mirror_screenshot.py --real     # the builder's real desktop (every window minimised, harness/desktop_grab.ps1), right and left controller:
                                                    # docs/images/ui_mirror_real.png, ui_mirror_left.png (the documentation). Check them for anything private.
"""
import subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent

def stream_frame(run, out, crop, n=1000, width=1440):
    r = subprocess.run(["ffmpeg", "-hide_banner", "-v", "error", "-y", "-i", str(run / "client.hevc"), "-vf", f"select=eq(n\\,{n}),crop={crop},scale={width}:-1:flags=lanczos,format=rgb24",
                        "-frames:v", "1", str(out)], capture_output=True, text=True)
    if not out.exists():
        print(r.stderr[-500:]); sys.exit(2)
    print(out.relative_to(ROOT), "<-", run.name)

if "--real" in sys.argv:
    sys.path.insert(0, str(ROOT / "harness"))
    import lib
    lib.b_run("if not exist runs\\grab mkdir runs\\grab")
    lib.b_put(ROOT / "harness/desktop_grab.ps1", "runs/grab/desktop_grab.ps1")
    lib.b_run("del runs\\grab\\grab.rc")
    body = "powershell -NoProfile -ExecutionPolicy Bypass -File runs\\grab\\desktop_grab.ps1 -Out runs\\grab\\desktop.png > runs\\grab\\out.txt 2>&1\r\n"
    lib.start_limited("grab", "va_grab", "grab.bat", body, "grab.rc").wait(90)
    png = ROOT / "harness/cache/real_desktop.png"
    png.unlink(missing_ok=True)
    lib.b_get("runs/grab/desktop.png", png)
    if not png.exists():
        print("no desktop picture"); sys.exit(2)
    for name in ("e2e_mirror_screenshot_real", "e2e_mirror_screenshot_left"):
        p = subprocess.run([sys.executable, str(ROOT / "harness/run.py"), "run", name], capture_output=True, text=True)
        if p.returncode != 0:
            print(p.stdout[-800:]); sys.exit(p.returncode)
    runs = lambda n: sorted((ROOT / "harness/results").glob(f"*_{n}"))[-1]
    stream_frame(runs("e2e_mirror_screenshot_real"), ROOT / "docs/images/ui_mirror_real.png", "720:420:520:620")
    stream_frame(runs("e2e_mirror_screenshot_left"), ROOT / "docs/images/ui_mirror_left.png", "720:420:520:620")
    sys.exit(0)

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
