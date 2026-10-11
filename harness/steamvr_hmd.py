#!/usr/bin/env python3
"""Real-headset run on STOCK ALVR + SteamVR with our probe (Littlest Tokyo) as the game, to compare by eye with our own pipeline on the same
app (harness/live.py run ... --only probe_light_pred0/100). The probe logs the pose it gets (app_markers.csv, unix time): SteamVR passes the
headset's samples through unchanged (see steamvr_baseline.py), so that file is also a recording of the real head trace.

    python3 harness/steamvr_hmd.py [--seconds 90] [--max-pred 100] [--name stock_pred100]

Open the ALVR app on the headset after the script says SteamVR is up; it waits for the connection (up to 6 minutes), then starts the probe."""
import sys, time
from datetime import datetime, timezone
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import live
import steamvr_baseline as sb

log = live.log


def main():
    a = sys.argv[1:]
    opt = lambda k, d=None: a[a.index(k) + 1] if k in a else d
    seconds, max_pred, name = int(opt("--seconds", 90)), int(opt("--max-pred", 100)), opt("--name", f"stock_pred{opt('--max-pred', 100)}")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    outdir = sb.ROOT / "harness" / "results" / f"{stamp}_steamvr_hmd_{name}"
    outdir.mkdir(parents=True, exist_ok=True)
    rd = f"{sb.HOME}\\runs\\shmd_{stamp}"
    live.pc(f"if not exist {rd} mkdir {rd}")
    sb.kill_all()
    try:
        sb.edit_stock_session(max_pred, hmd=True)
        log("starting the ALVR dashboard (it starts SteamVR)")
        sb.task("sbase_dash", [f'cd /d "{sb.ALVR}"', 'start "" "ALVR Dashboard.exe"'], rd)
        t0 = time.time()
        while time.time() - t0 < 120:
            out = live.ps("(Get-Process vrserver -ErrorAction SilentlyContinue | Measure-Object).Count; (netstat -ano | Select-String ':9943').Count")
            n = [x.strip() for x in out.split() if x.strip().isdigit()]
            if len(n) >= 2 and int(n[0]) > 0 and int(n[1]) > 0:
                break
            time.sleep(4)
        log(">>> SteamVR + ALVR are up: OPEN THE ALVR APP ON THE HEADSET NOW (waiting up to 6 minutes for the connection)")
        base = live.ps(f"(Get-Content '{sb.ALVR}\\\\session_log.txt' | Measure-Object -Line).Lines").strip()
        n0 = int(base) if base.isdigit() else 0
        t0 = time.time()
        connected = False
        while time.time() - t0 < 360:
            out = live.ps(f"Get-Content '{sb.ALVR}\\\\session_log.txt' | Select-Object -Skip {n0} | Select-String 'Client connected|Streaming started|STATS' | Select-Object -First 3 | ForEach-Object {{ $_.Line }}")
            if out.strip():
                connected = True
                break
            time.sleep(4)
        if not connected:
            log("no headset connection: giving up")
            return
        log("headset connected; waiting 15 s for the stream to settle, then starting the probe")
        time.sleep(15)
        sb.task("sbase_probe", [f'set "XR_RUNTIME_JSON={sb.STEAMXR}"', "set XR_PROBE_SCENE=bench\\littlest_tokyo.vab", "set XR_PROBE_CPU_MS=0", "set XR_PROBE_LOAD=steady",
              f"set XR_PROBE_MARKERS_CSV={rd}\\app_markers.csv", f"set XR_PROBE_TIMING_CSV={rd}\\app_timing.csv", f"cd /d {sb.HOME}",
              f"{sb.PROBE} {rd}\\app.log 200000 {seconds + 30} {rd}\\app_pose.csv - > {rd}\\app_stdout.txt 2>&1"], rd)
        log(f">>> the probe is running for {seconds} s: look at the room, still for 20 s, then move your head as before")
        time.sleep(seconds)
    finally:
        live.ps(f"Stop-Process -Name xr_probe -Force -ErrorAction SilentlyContinue; Copy-Item '{sb.ALVR}\\\\session_log.txt' '{rd}\\\\alvr_session_log.txt' -Force -ErrorAction SilentlyContinue")
        sb.kill_all()
        sb.restore_stock_session()
    live.get_dir(rd.replace("\\", "/"), outdir)
    log(f"results in {outdir}")


if __name__ == "__main__":
    main()
