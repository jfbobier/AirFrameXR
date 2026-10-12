#!/usr/bin/env python3
"""Runs the simrig (host built with the cargo feature `simrig`, see docs/DEVELOPMENT.md) on the PC and judges the result.

    python3 harness/simrig/run_sim.py build                 # builds the simrig host on the PC (python3 harness/live.py build host --simrig does the same)
    python3 harness/simrig/run_sim.py run <scenario>[,<scenario>...] [--seconds 30] [--show 0] [--real-encode 1] [--seed 7] [--eye-w 1760]
    python3 harness/simrig/run_sim.py list

The probe (XR_PROBE_SCENE=dots, black sphere, white rectangles every 10 deg on the horizon) runs through the installed OpenXR runtime and the
real shim; the host runs the real pipeline against a synthetic client (constant rotation or still, timing from a recorded profile) and the
viewer writes dots.csv + meta.json; judge.py gives the verdict. Results: harness/results/<UTC>_simrig_<scenario>/.
The installed streamer must not be running; the PC's interactive session is used (windows appear there). Needs the PC of tools/pc.local."""
import json, os, subprocess, sys, time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import live

ROOT = live.ROOT
PROFILES = ROOT / "harness" / "simrig" / "profiles"
SIMEXE = "alvr_host_simrig.exe"

# knobs are VISIONALVR_SIM_* variables (tools/host/src/simrig.rs); a scenario is a set of them
SCENARIOS = {
    "ideal":       {"note": "no timing imperfection at all, no prediction noise: the null case (must be clean, or the rig itself is wrong)"},
    "games":       {"PROFILE": "real_games_pred0.json", "note": "90 deg/s, timing of the real game sessions (prediction 0)"},
    "probe100":    {"PROFILE": "real_probe_pred100.json", "PREDICT_MS": 100, "note": "90 deg/s, timing and prediction noise of the real probe session (prediction 100)"},
    "still100":    {"PROFILE": "real_probe_pred100.json", "PREDICT_MS": 100, "MODE": "still", "note": "head still, prediction noise of pred 100 (the user's still-pose trembling)"},
    "still0":      {"PROFILE": "real_games_pred0.json", "MODE": "still", "note": "head still, no prediction"},
    "noreproject": {"PROFILE": "real_games_pred0.json", "REPROJECT": 0, "note": "the frames as they come: what the stream alone does"},
    "inject":      {"PROFILE": "real_games_pred0.json", "STAMP_ERROR": 0.2, "note": "20% of the frames shown with a neighbouring sample's pose: the judge must say TREMBLE"},
}


def host_cmd(rd, env, seconds):
    lines = ["@echo off"] + [f"set VISIONALVR_SIM_{k}={v}" for k, v in env.items()] + [
        f'"{live.INSTALL}\\{SIMEXE}" --install-dir "{live.INSTALL}" --shim --simrig {rd}\\out --seconds {seconds + 90} --debug --connect-timeout 30 > {rd}\\host_stdout.txt 2>&1']
    return lines


def task(name, lines, rd, elevated=False):
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    f = live.SCRATCH / f"{name}.cmd"
    f.write_bytes(("\r\n".join(lines) + "\r\n").encode())
    live.put(f, f"{rd.replace(chr(92), '/')}/{name}.cmd")
    live.ps(f"""
schtasks /delete /tn {name} /f 2>$null | Out-Null
schtasks /create /tn {name} /tr 'cmd /c {rd}\\{name}.cmd' /sc once /st 00:00 /rl {'HIGHEST' if elevated else 'LIMITED'} /it /f | Out-Null
schtasks /run /tn {name} | Out-Null
""")


def kill():
    live.ps(f"Stop-Process -Name xr_probe,{Path(SIMEXE).stem},alvr_host,VisionALVR -Force -ErrorAction SilentlyContinue; "
            "schtasks /delete /tn simrig_host /f 2>$null | Out-Null; schtasks /delete /tn simrig_probe /f 2>$null | Out-Null")
    time.sleep(2)


def build():
    live.build("host", simrig=True)
    live.build("shim")
    live.ps(f"""
$src = '{live.CLONE}\\src\\ALVR-v20.14.1\\target\\release\\alvr_host.exe'
Copy-Item $src '{live.INSTALL}\\{SIMEXE}' -Force
Copy-Item '{live.CLONE}\\src\\VDXR\\bin-shim\\x64\\Release\\LibOVRRT64_1.dll' '{live.INSTALL}\\LibOVRRT64_1.dll' -Force
Get-Item '{live.INSTALL}\\{SIMEXE}' | Select-Object Name, Length, LastWriteTime | Out-String
""")


def run_one(name, seconds, extra):
    sc = dict(SCENARIOS[name])
    note = sc.pop("note", "")
    env = {"SECONDS": seconds, "SEED": 1, "SHOW": 1}
    env.update(sc)
    env.update(extra)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    rd = rf"{live.INSTALL}\simrig\{stamp}_{name}"
    outdir = ROOT / "harness" / "results" / f"{stamp}_simrig_{name}"
    outdir.mkdir(parents=True, exist_ok=True)
    live.pc(f'if not exist "{rd}\\out" mkdir "{rd}\\out"')
    if "PROFILE" in env:
        live.put(PROFILES / env["PROFILE"], f"{rd.replace(chr(92), '/')}/profile.json")
        env["PROFILE"] = f"{rd}\\profile.json"
    live.log(f"simrig {name}: {note}")
    kill()
    ok = False
    try:
        task("simrig_host", host_cmd(rd, env, seconds), rd, elevated=True)
        time.sleep(8)
        probe = ["@echo off", f'set XR_RUNTIME_JSON={live.INSTALL}\\virtualdesktop-openxr.json', "set XR_PROBE_SCENE=dots", "set XR_PROBE_CPU_MS=0", "set XR_PROBE_LOAD=steady",
                 f"cd /d {live.INSTALL}", f'"C:\\Users\\{live.PC_USER}\\openxr\\build\\probe\\xr_probe.exe" {rd}\\probe.log 200000 {seconds + 60} - - > {rd}\\probe_stdout.txt 2>&1']
        task("simrig_probe", probe, rd)
        t0 = time.time()
        while time.time() - t0 < seconds + 90:
            time.sleep(5)
            if live.ps(f"Test-Path '{rd}\\out\\viewer_summary.json'").strip() == "True":
                ok = True
                break
    finally:
        kill()
    live.get_dir(rd.replace("\\", "/"), outdir)
    if not ok:
        live.log("the viewer did not finish (see host_stdout.txt / probe.log in the results)")
        return 2
    r = subprocess.run([sys.executable, str(ROOT / "harness/simrig/judge.py"), str(outdir / "out")], capture_output=True, text=True)
    print(r.stdout + r.stderr)
    (outdir / "verdict.txt").write_text(r.stdout)
    return r.returncode


def main(a):
    if not a or a[0] == "list":
        for k, v in SCENARIOS.items():
            print(f"{k:12s} {v['note']}")
        return 0
    if a[0] == "build":
        build()
        return 0
    if a[0] == "run":
        names = a[1].split(",")
        seconds = int(a[a.index("--seconds") + 1]) if "--seconds" in a else 30
        extra = {}
        for flag, key in (("--show", "SHOW"), ("--real-encode", "REAL_ENCODE"), ("--seed", "SEED"), ("--eye-w", "EYE_W"), ("--omega", "OMEGA_DPS"),
                          ("--predict-ms", "PREDICT_MS"), ("--dump-every", "DUMP_EVERY")):
            if flag in a:
                extra[key] = a[a.index(flag) + 1]
        rc = 0
        for n in names:
            rc = max(rc, run_one(n, seconds, extra))
        return rc
    print(__doc__)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
