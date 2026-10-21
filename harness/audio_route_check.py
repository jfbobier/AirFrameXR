#!/usr/bin/env python3
"""The streamer routes the game audio like startvisionalvr.bat did (tools/gui/AudioRoute.cs). On the builder, from a copy of the package (run.py build gui package first):
the PC's own output is set to 55 % and the cable to 40 %, streamer.exe is started in the interactive session and, after 12 s, the system must show the VB-Audio cable as
the default output for all three roles, at 100 % and unmuted, the PC's output muted with its volume still 55 %; after the streamer's window is closed (and it exited
promptly) the defaults are back on the PC's output, which is unmuted at 55 %. The volumes are restored at the end. Exit 0 = pass."""
import re, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
lib.b_run("if not exist runs\\audio mkdir runs\\audio")
lib.b_put(ROOT / "harness/audio_route.ps1", "runs/audio/audio_route.ps1")
_run = time.strftime("%H%M%S")
_n = [0]
def ps(args, timeout=120):
    """Runs audio_route.ps1 in the interactive session; returns its output lines."""
    _n[0] += 1
    n = _n[0]
    body = f"powershell -NoProfile -ExecutionPolicy Bypass -File runs\\audio\\audio_route.ps1 {args} > runs\\audio\\r{_run}_{n}.txt 2>&1\r\n"
    lib.start_limited("audio", f"va_ar{n}", f"ar{_run}_{n}.bat", body, f"rc{_run}_{n}.txt").wait(timeout)
    dst = ROOT / "harness/cache" / f"audio_route{_run}_{n}.txt"
    dst.unlink(missing_ok=True)
    lib.b_get(f"runs/audio/r{_run}_{n}.txt", dst)
    return dst.read_text(errors="replace").splitlines() if dst.exists() else []

def parse(lines):
    d = {"default": {}, "device": {}}
    for l in lines:
        m = re.match(r"default (\w+) (.*)", l)
        if m: d["default"][m.group(1)] = m.group(2).strip()
        m = re.match(r"device (.*) \| vol=([\d.]+) mute=(\d)", l)
        if m: d["device"][m.group(1).strip()] = (float(m.group(2)), m.group(3) == "1")
    return d

fails = 0
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))

before = parse(ps("snapshot"))
cable = next((n for n in before["device"] if "cable input" in n.lower()), None)
pc = before["default"].get("console")
assert cable, f"no VB-Audio 'CABLE Input' device on the builder: {list(before['device'])}"
assert pc and "cable" not in pc.lower(), f"the default output must be the PC's own device to start with: {pc}"
orig_pc, orig_cable = before["device"][pc][0], before["device"][cable][0]
print(f"before: default {pc}, PC volume {orig_pc}, cable volume {orig_cable}")

rid = "audio_route"
try:
    ps(f'set "{pc}" 0.55')
    ps(f'set "{cable}" 0.40')
    lib.b_run(f"if exist runs\\{rid}\\VisionALVR rmdir /s /q runs\\{rid}\\VisionALVR")
    lib.b_run(f"robocopy out\\dist\\VisionALVR runs\\{rid}\\VisionALVR /E /NFL /NDL /NJH /NJS /NP")
    rc, home = lib.b_run("echo %USERPROFILE%")
    exe = f"{home.strip().splitlines()[-1].strip()}\\{lib.ROOTNAME}\\runs\\{rid}\\VisionALVR\\bin\\streamer.exe"
    out = ps(f'run "{exe}" 12', timeout=180)
    i, j = out.index("== during"), out.index("== after")
    during, after = parse(out[i:j]), parse(out[j:])
    ex = [l for l in out if l.startswith("exit")]
    for role in ("console", "multimedia", "communications"):
        check(f"{role} default is the VB-Audio cable while the streamer runs", cable in during["default"].get(role, ""), during["default"].get(role))
    check("the cable is at 100 %, unmuted", during["device"][cable] == (1.0, False), during["device"][cable])
    check("the PC output is muted, its volume unchanged (55 %)", during["device"][pc] == (0.55, True), during["device"][pc])
    check("the streamer exited after its window was closed", ex and "TIMEOUT" not in ex[0], ex)
    for role in ("console", "multimedia", "communications"):
        check(f"{role} default is back on the PC output", after["default"].get(role) == pc, after["default"].get(role))
    check("the PC output is unmuted at 55 % again", after["device"][pc] == (0.55, False), after["device"][pc])
finally:
    ps(f'set "{pc}" {orig_pc}')
    ps(f'set "{cable}" {orig_cable}')
    print("volumes restored")
sys.exit(1 if fails else 0)
