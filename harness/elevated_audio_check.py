#!/usr/bin/env python3
"""Does process loopback hear an ELEVATED process (UEVR and mods need elevation)? On the builder a tone player is started elevated through the UAC prompt (someone has to
accept it, the script waits 90 s) and proc_loopback.exe runs with a NORMAL token, as the host does: exclude mode (everything but an unrelated process) and include mode
(only the elevated player) must both hear it, an unrelated process must not. Run `python3 harness/process_loopback_probe.py` once first (it builds the probes and the tone).
Measured 2026-10-21: both modes hear the elevated player. Exit 0 = pass."""
import re, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
home = lib.b_run("echo %USERPROFILE%")[1].strip().splitlines()[-1].strip(); base = f"{home}\\{lib.ROOTNAME}\\runs\\audio"
for f in ("elevated_audio.ps1", "elevated_player_inner.ps1"):
    lib.b_put(ROOT / "harness/probes" / f, f"runs/audio/{f}")
lib.b_put(ROOT / "harness/audio_route.ps1", "runs/audio/audio_route.ps1")
print("accept the UAC prompt on the builder now")
k = time.strftime("%H%M%S")
lib.start_limited("audio", "va_ea", "ea.bat", f'powershell -NoProfile -ExecutionPolicy Bypass -File runs\\audio\\elevated_audio.ps1 "{base}" > runs\\audio\\ea_{k}.txt 2>&1\r\n', f"rcea{k}.txt").wait(180)
dst = ROOT / "harness/cache/ea.txt"; dst.unlink(missing_ok=True)
lib.b_get(f"runs/audio/ea_{k}.txt", dst)
out = dst.read_text(errors="replace")
print(out)
def peak(key):
    m = re.search(key + r": frames=\d+ peak=([\d.]+)", out)
    return float(m.group(1)) if m else None
fails = 0
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))
check("the player is elevated and the capturing shell is not", "HIGH integrity" in out.split("this_shell")[0] and "MEDIUM integrity" in out, out[:300])
check("include mode hears the elevated player", (peak("include_player") or 0) > 0.2, peak("include_player"))
check("exclude mode hears the elevated player", (peak("exclude_other") or 0) > 0.2, peak("exclude_other"))
check("an unrelated process is silent", peak("include_other") is not None and peak("include_other") < 0.01, peak("include_other"))
sys.exit(1 if fails else 0)
