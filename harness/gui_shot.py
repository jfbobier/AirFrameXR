#!/usr/bin/env python3
"""Screenshots of configure.exe pages on the PC (the wizard's --demo mode needs no headset): python3 harness/gui_shot.py <page 0-5> <out.png> [extra args]
Runs the freshly built out\\gui\\configure.exe from a scratch copy of the package (C:\\Users\\<u>\\wizard_test, so no real config is touched) in the
interactive session and fetches the PNG."""
import subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import live

import os
exe = os.environ.get("GUI_EXE", "configure.exe")   # streamer.exe for the runtime window (no --demo/--page there)
page, out = sys.argv[1], Path(sys.argv[2])
extra = " ".join(sys.argv[3:])
wt = rf"C:\Users\{live.PC_USER}\wizard_test"
live.ps(f"""
New-Item -ItemType Directory -Force '{wt}\\bin' | Out-Null
Stop-Process -Name AirFrameXR,streamer,configure,alvr_host -Force -ErrorAction SilentlyContinue
if (-not (Test-Path '{wt}\\bin\\alvr_host.exe')) {{ Copy-Item '{live.CLONE}\\out\\dist\\AirFrameXR\\*' '{wt}' -Recurse -Force -ErrorAction SilentlyContinue }}
Copy-Item '{live.CLONE}\\src\\ALVR-v20.14.1\\target\\release\\alvr_host.exe' '{wt}\\bin\\alvr_host.exe' -Force
Copy-Item '{live.CLONE}\\out\\gui\\{exe}' '{wt}\\bin\\{exe}' -Force
Remove-Item '{wt}\\shot_{page}.png' -ErrorAction SilentlyContinue
""")
cmd = f'"{wt}\\bin\\{exe}" ' + ('--demo --page ' + page + ' ' if exe == 'configure.exe' else '') + f'--screenshot {wt}\\shot_{page}.png {extra}'
live.SCRATCH.mkdir(parents=True, exist_ok=True)
f = live.SCRATCH / "shot.cmd"
f.write_bytes(("@echo off\r\n" + cmd + "\r\n").encode())
live.put(f, f"{wt.replace(chr(92), '/')}/shot.cmd")
live.ps(f"schtasks /delete /tn va_shot /f 2>$null | Out-Null; schtasks /create /tn va_shot /tr 'cmd /c {wt}\\shot.cmd' /sc once /st 00:00 /rl LIMITED /it /f | Out-Null; schtasks /run /tn va_shot | Out-Null")
for _ in range(40):
    time.sleep(2)
    if live.ps(f"Test-Path '{wt}\\shot_{page}.png'").strip() == "True":
        break
time.sleep(1)
out.parent.mkdir(parents=True, exist_ok=True)
live._retry(["scp", "-o", "BatchMode=yes", *live.MUX, "-q", f"{live.PC}:{wt.replace(chr(92), '/')}/shot_{page}.png", str(out)])
print(out, out.exists())
