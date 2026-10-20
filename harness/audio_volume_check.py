#!/usr/bin/env python3
"""U10 (docs/PLAN_0.5.md): the host holds the playback device's volume at 100 %. On the builder: the default device is set to 40 % before the host starts, the host
runs (VISIONALVR_FORCE_VOLUME_DEVICE=* makes the lab's device a target whatever its name), the volume must be back at 100 % within 6 s with an `audio_volume_forced`
event; a second drop to 40 % while the host runs is undone within 6 s too. The device's original volume is restored at the end. Exit 0 = pass."""
import json, subprocess, sys, threading, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
lib.b_run("if not exist runs\\audio mkdir runs\\audio")
lib.b_put(ROOT / "harness/audio_volume.ps1", "runs/audio/audio_volume.ps1")
_n = [0]
_run = time.strftime("%H%M%S")   # file names unique per run: a finished task is recognised by its rc file, a stale one would end the wait at once
def ps(args, timeout=60):
    """The Core Audio endpoint is only reachable from the interactive desktop session (the SSH session gets a COM error): run the helper as a limited task there."""
    _n[0] += 1
    n = _n[0]
    body = f"powershell -NoProfile -ExecutionPolicy Bypass -File runs\\audio\\audio_volume.ps1 {args} > runs\\audio\\out{_run}_{n}.txt 2>&1\r\n"
    proc = lib.start_limited("audio", f"va_audio{_n[0]}", f"audio{_run}_{n}.bat", body, f"rc{_run}_{n}.txt")
    proc.wait(timeout)
    dst = ROOT / "harness/cache" / f"audio_out{_run}_{n}.txt"
    dst.unlink(missing_ok=True)
    lib.b_get(f"runs/audio/out{_run}_{n}.txt", dst)
    return dst.read_text(errors="replace").strip() if dst.exists() else ""

original = ps("get").splitlines()[-1].strip()
assert original.replace(".", "").isdigit(), f"cannot read the device volume: {original}"
print("device volume before the test:", original)
fails = 0
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))

try:
    check("the device volume can be set to 40 %", ps("set 0.4").splitlines()[-1] == "0.40", "set failed")
    sc = json.load(open(ROOT / "harness/scenarios/e2e_green_idle.json"))
    sc["name"] = "e2e_audio_volume"
    sc["host"]["seconds"] = 30
    sc["client"]["seconds"] = 28
    sc["host"]["env"] = {"VISIONALVR_FORCE_VOLUME_DEVICE": "*"}
    watch = {}
    def second_drop():   # while the host runs: someone turns the PC volume down again
        time.sleep(14)
        ps("set 0.4")
        watch["lines"] = ps("watch 8", timeout=90).splitlines()
    t = threading.Thread(target=second_drop); t.start()
    outdir = lib.RESULTS / (time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "_e2e_audio_volume")
    lib.run_scenario(sc, outdir, log=print)
    t.join()
    hs = (outdir / "host_stdout.txt").read_text(errors="replace")
    forced = [json.loads(l)["data"] for l in hs.splitlines() if '"event":"audio_volume_forced"' in l]
    check("audio_volume_forced reported at start (was 40 %)", any(abs(f.get("was", 0) - 0.4) < 0.02 and f.get("set_ok") for f in forced), forced)
    check("audio_volume_forced reported again after the second drop", len(forced) >= 2, len(forced))
    lines = watch.get("lines", [])
    rows = [l.split() for l in lines if len(l.split()) == 2 and l.split()[0].replace(".", "").isdigit()]
    back = next((float(r[0]) for r in rows if float(r[1]) >= 0.999), None)
    check("volume back at 100 % within 6 s of the second drop", back is not None and back <= 6.0, lines[-3:])
finally:
    ps(f"set {original}")
    print("device volume restored to", original)
sys.exit(1 if fails else 0)
