#!/usr/bin/env python3
"""The streamer routes the game audio like startvisionalvr.bat did (tools/gui/AudioRoute.cs, Core Audio from .NET). On the builder, from a copy of the package
(run.py build gui package first), with the PC's own output at 55 % and the VB-Audio cable at 40 %; streamer.exe is started in the interactive session:
  plain    the cable is the default output for all three roles, at 100 % and unmuted, the PC output muted with its volume still 55 %; ALVR's mute_when_streaming is
           off in session.json; after the window is closed the streamer exits promptly, the defaults are back on the PC output, unmuted at 55 %
  nomute   audio.mute_pc = false in visionalvr.json: the PC output is not muted
  disturb  while it runs the default is moved back to the PC, the cable is muted and set to 30 % (Windows / a game / ALVR's own mute): all healed within 7 s
  named    the audio settings name the 16ch cable device: it is the default while the streamer runs
  format   the cable's Default format is 44.1 kHz before the start: set to plain 2 ch / 48000 Hz PCM
  crash    the streamer is killed (defaults stay on the cable, PC muted); the next start restores them first, and its close leaves the PC as it was
The volumes are restored at the end. Exit 0 = pass."""
import json, re, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
lib.b_run("if not exist runs\\audio mkdir runs\\audio")
lib.b_put(ROOT / "harness/audio_route.ps1", "runs/audio/audio_route.ps1")
_run = time.strftime("%H%M%S")
_n = [0]
def ps(args, timeout=180):
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
        m = re.match(r"device (.*) \| vol=([\d.]+) mute=(\d) fmt=(\S+)", l)
        if m: d["device"][m.group(1).strip()] = (float(m.group(2)), m.group(3) == "1"); d.setdefault("fmt", {})[m.group(1).strip()] = m.group(4)
    return d

def section(lines, name):
    out, on = [], False
    for l in lines:
        if l.startswith("== "): on = l[3:].strip() == name; continue
        if on: out.append(l)
    return parse(out)

fails = 0
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))

ROLES = ("console", "multimedia", "communications")
before = parse(ps("snapshot"))
cable = next((n for n in before["device"] if "cable input" in n.lower()), None)
pc = before["default"].get("console")
assert cable, f"no VB-Audio 'CABLE Input' device on the builder: {list(before['device'])}"
assert pc and "cable" not in pc.lower(), f"the default output must be the PC's own device to start with: {pc}"
orig_rate = int(before.get('fmt', {}).get(cable, '2ch/48000/16bit').split('/')[1])
orig_pc, orig_cable = before["device"][pc][0], before["device"][cable][0]
print(f"before: default {pc}, PC volume {orig_pc}, cable volume {orig_cable}")
base = lib.b_run("echo %USERPROFILE%")[1].strip().splitlines()[-1].strip() + f"\\{lib.ROOTNAME}\\runs\\audio_route\\VisionALVR"

def fresh_copy(session_edit=None, settings=None):
    # /MIR makes the copy equal to the package, so the last phase's session.json, logs and state file are gone (the builder's guard refuses rmdir / del)
    lib.b_run("robocopy out\\dist\\VisionALVR runs\\audio_route\\VisionALVR /MIR /NFL /NDL /NJH /NJS /NP")
    if session_edit:
        for name in ("session.default.json", "session.json"):   # the package may carry both: the streamer reads session.json
            sess = ROOT / "harness/cache/audio_route_session.json"; sess.unlink(missing_ok=True)
            lib.b_get(f"runs/audio_route/VisionALVR/config/{name}", sess)
            if not sess.exists(): continue
            d = json.loads(sess.read_text(encoding="utf-8-sig")); session_edit(d); sess.write_text(json.dumps(d))
            lib.b_put(sess, f"runs/audio_route/VisionALVR/config/{name}")
    if settings is not None:
        f = ROOT / "harness/cache/audio_route_settings.json"; f.write_text(json.dumps(settings))
        lib.b_put(f, "runs/audio_route/VisionALVR/config/visionalvr.json")

def reset(cable_vol=0.40):
    ps(f'setdefault "{pc}"')
    ps(f'mute "{pc}" 0'); ps(f'mute "CABLE Input" 0')
    ps(f'set "{pc}" 0.55'); ps(f'set "CABLE Input" {cable_vol}')

def run(mode="plain", seconds=12):
    return ps(f'run "{base}\\bin\\streamer.exe" {seconds} {mode} Realtek')

def session_text():
    dst = ROOT / "harness/cache/audio_route_session_after.json"; dst.unlink(missing_ok=True)
    lib.b_get("runs/audio_route/VisionALVR/config/session.json", dst)
    return dst.read_text(encoding="utf-8-sig", errors="replace") if dst.exists() else ""

try:
    # -- plain
    reset(); fresh_copy()
    out = run()
    during, after = section(out, "during"), section(out, "after")
    for role in ROLES:
        check(f"plain: {role} default is the VB-Audio cable while the streamer runs", cable in during["default"].get(role, ""), during["default"].get(role))
    check("plain: the cable is at 100 %, unmuted", during["device"][cable] == (1.0, False), during["device"][cable])
    check("plain: the PC output is muted, its volume unchanged (55 %)", during["device"][pc] == (0.55, True), during["device"][pc])
    check("plain: ALVR's mute_when_streaming is off in session.json", re.search(r'"mute_when_streaming"\s*:\s*false', session_text()) is not None, "still on")
    ex = [l for l in out if l.startswith("exit")]
    check("plain: the streamer exited after its window was closed", ex and "TIMEOUT" not in ex[0], ex)
    for role in ROLES:
        check(f"plain: {role} default is back on the PC output", after["default"].get(role) == pc, after["default"].get(role))
    check("plain: the PC output is unmuted at 55 % again", after["device"][pc] == (0.55, False), after["device"][pc])

    # -- the PC output is left alone when audio.mute_pc is off
    reset(); fresh_copy(settings={"audio": {"mute_pc": False}})
    out = run()
    during, after = section(out, "during"), section(out, "after")
    check("nomute: the cable is the default and at 100 %", cable in during["default"].get("console", "") and during["device"][cable] == (1.0, False), during)
    check("nomute: the PC output is not muted", during["device"][pc] == (0.55, False), during["device"][pc])
    check("nomute: defaults back on the PC at the end", all(after["default"].get(r) == pc for r in ROLES), after["default"])

    # -- disturbed while running
    reset(); fresh_copy()
    out = run("disturb", 12)
    healed = section(out, "healed")
    check("disturb: the default is the cable again within 7 s (all roles)", all(cable in healed["default"].get(r, "") for r in ROLES), healed["default"])
    check("disturb: the cable is unmuted and back at 100 %", healed["device"][cable] == (1.0, False), healed["device"][cable])
    after = section(out, "after")
    check("disturb: restored to the PC at the end", all(after["default"].get(r) == pc for r in ROLES) and after["device"][pc] == (0.55, False), (after["default"], after["device"][pc]))

    # -- a device named in the audio settings is the one used (this driver's 16ch device mixes 2 channels, so it is capturable; the more-than-8 refusal needs a real
    #    multichannel device and is not exercised here)
    def name16(d):
        g = d["session_settings"]["audio"]["game_audio"]["content"]
        g["device"]["set"] = True; g["device"]["content"]["NameSubstring"] = "16ch"
    reset(); fresh_copy(session_edit=name16)
    out = run()
    during = section(out, "during")
    check("named: the device named in the audio settings (16ch) is the default while the streamer runs", all("16ch" in during["default"].get(r, "") for r in ROLES), during["default"])
    after = section(out, "after")
    check("named: restored to the PC at the end", all(after["default"].get(r) == pc for r in ROLES), after["default"])

    # -- the cable's format is set to plain PCM, 2 channels, 48 kHz
    reset(); ps(f'format "CABLE Input" 44100'); fresh_copy()
    check("format: the lab put the cable at 44.1 kHz first", parse(ps("snapshot")).get("fmt", {}).get(cable, "").startswith("2ch/44100"), parse(ps("snapshot")).get("fmt"))
    out = run()
    during = section(out, "during")
    check("format: while the streamer runs the cable mixes 2 channels at 48000 Hz", str(during.get("fmt", {}).get(cable)).startswith("2ch/48000/"), during.get("fmt", {}).get(cable))

    # -- crash and recovery
    reset(); fresh_copy()
    out = run("crash", 10)
    crashed = section(out, "crashed")
    check("crash: the killed streamer left the cable as default (the situation to recover from)", cable in crashed["default"].get("console", "") and crashed["device"][pc][1], crashed["default"])
    out = run("plain", 10)   # same copy: its state file is still there
    during, after = section(out, "during"), section(out, "after")
    check("crash: the next start routes again", cable in during["default"].get("console", "") and during["device"][pc] == (0.55, True), during["default"])
    check("crash: after its close the PC is the default and unmuted at 55 %", all(after["default"].get(r) == pc for r in ROLES) and after["device"][pc] == (0.55, False), (after["default"], after["device"][pc]))
finally:
    ps(f'setdefault "{pc}"'); ps(f'mute "{pc}" 0'); ps(f'mute "CABLE Input" 0')
    ps(f'set "{pc}" {orig_pc}'); ps(f'set "CABLE Input" {orig_cable}'); ps(f'format "CABLE Input" {orig_rate}')
    print("defaults and volumes restored")
sys.exit(1 if fails else 0)
