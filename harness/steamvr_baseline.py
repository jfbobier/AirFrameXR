#!/usr/bin/env python3
"""SteamVR baseline: replay a recorded head trace into STOCK ALVR (SteamVR + ALVR's own driver) with the mock client, run our OpenXR probe
(Littlest Tokyo) under SteamVR's OpenXR runtime, and record what the game gets. Same trace, same app as the follow-the-pose lab of our own
pipeline (`run.py pacing replay_*`): the two can be compared pose by pose to see what SteamVR does to the head pose on its way to the game.

    python3 harness/steamvr_baseline.py harness/fixtures/headtraces/real_cyberpunk_pred0.csv [--seconds 60] [--max-pred 100] [--name x]

Needs on the PC (live.py's PC): stock ALVR v20.14.1 registered with SteamVR (D:\\_VR\\alvr_launcher_windows\\installations\\v20.14.1), the mock
client and the probe built by harness/run.py (C:\\Users\\<user>\\openxr), the ALVR app on the headset CLOSED (it would connect to the dashboard).
The dashboard's session.json is edited (backup kept as session.json.bak-harness): the mock client is trusted, tracking logging is on and
max_prediction_ms is set. Results: harness/results/<UTC>_steamvr_<name>/ (probe pose logs, mock poses/frames/display, ALVR session log).
"""
import json, os, subprocess, sys, time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import live

ROOT = live.ROOT
ALVR = os.environ.get("VISIONALVR_STOCK_ALVR", r"D:\_VR\alvr_launcher_windows\installations\v20.14.1")   # stock ALVR install (tools/pc.local)
HOME = rf"C:\Users\{live.PC_USER}\openxr"
CLIENT = HOME + r"\src\ALVR-v20.14.1\target\release\alvr_client_headless.exe"
PROBE = HOME + r"\build\probe\xr_probe.exe"
STEAMXR = r"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json"
MOCK = ("7031.client", "Mock client", "127.0.0.2")
log = live.log


def kill_all():
    live.ps("""
Stop-Process -Name xr_probe,alvr_client_headless,alvr_host,VisionALVR -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'ALVR Dashboard',vrserver,vrmonitor,vrcompositor,vrdashboard,vrwebhelper,vrstartup -Force -ErrorAction SilentlyContinue
schtasks /delete /tn sbase_dash /f 2>$null | Out-Null; schtasks /delete /tn sbase_client /f 2>$null | Out-Null; schtasks /delete /tn sbase_probe /f 2>$null | Out-Null
""")
    time.sleep(3)


def edit_stock_session(max_pred, fps=100.0, hmd=False):
    """Download the dashboard's session.json, edit it with python's json (a PowerShell round trip broke a session once) and put it back."""
    scratch = live.SCRATCH
    scratch.mkdir(parents=True, exist_ok=True)
    local = scratch / "stock_session.json"
    live.ps(f"Copy-Item '{ALVR}\\session.json' '{ALVR}\\session.json.bak-harness' -Force")
    subprocess.run(["scp", "-o", "BatchMode=yes", "-q", f"{live.PC}:{ALVR.replace(chr(92), '/')}/session.json", str(local)], check=True)
    j = json.loads(local.read_text())
    if not hmd:
        j["client_connections"][MOCK[0]] = {"display_name": MOCK[1], "current_ip": None, "manual_ips": [MOCK[2]], "trusted": True, "connection_state": "Disconnected"}
    s = j["session_settings"]
    s["headset"]["max_prediction_ms"] = int(max_pred)
    if not hmd:
        s["extra"]["logging"]["log_tracking"] = True
        s["video"]["preferred_fps"] = float(fps)
        # no foveated encoding: the stamp blocks and the marker of the probe must stay readable in the decoded stream
        s["video"]["foveated_encoding"]["enabled"] = False
    out = scratch / "stock_session_edited.json"
    out.write_text(json.dumps(j, indent=2))
    live.put(out, f"{ALVR.replace(chr(92), '/')}/session.json")


def restore_stock_session():
    live.ps(f"Copy-Item '{ALVR}\\session.json.bak-harness' '{ALVR}\\session.json' -Force")


def task(name, lines, rd, limited=True):
    """Run cmd.exe lines in the interactive session through a scheduled task (a .cmd file copied over: paths with spaces and quotes survive)."""
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    f = live.SCRATCH / f"{name}.cmd"
    f.write_bytes(("\r\n".join(["@echo off"] + lines) + "\r\n").encode())
    live.put(f, f"{rd.replace(chr(92), '/')}/{name}.cmd")
    live.ps(f"""
schtasks /delete /tn {name} /f 2>$null | Out-Null
schtasks /create /tn {name} /tr 'cmd /c {rd}\\{name}.cmd' /sc once /st 00:00 /rl {'LIMITED' if limited else 'HIGHEST'} /it /f | Out-Null
schtasks /run /tn {name} | Out-Null
""")


def main():
    a = sys.argv[1:]
    trace = Path(a[0])
    opt = lambda k, d=None: a[a.index(k) + 1] if k in a else d
    seconds = int(opt("--seconds", 60))
    max_pred = int(opt("--max-pred", 100))
    name = opt("--name", trace.stem)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    outdir = ROOT / "harness" / "results" / f"{stamp}_steamvr_{name}"
    outdir.mkdir(parents=True, exist_ok=True)
    rd = f"{HOME}\\runs\\sbase_{stamp}"
    live.pc(f"if not exist {rd} mkdir {rd}")
    live.put(trace, f"{rd.replace(chr(92), '/')}/replay.csv")
    log(f"stock ALVR + SteamVR baseline: {trace.name}, {seconds} s, max_prediction_ms {max_pred}")
    kill_all()
    try:
        edit_stock_session(max_pred)
        log("starting the ALVR dashboard (it starts SteamVR)")
        task("sbase_dash", [f'cd /d "{ALVR}"', 'start "" "ALVR Dashboard.exe"'], rd)
        t0 = time.time()
        up = False
        while time.time() - t0 < 150:
            out = live.ps("(Get-Process vrserver -ErrorAction SilentlyContinue | Measure-Object).Count; (netstat -ano | Select-String ':9943').Count")
            n = [x.strip() for x in out.split() if x.strip().isdigit()]
            if len(n) >= 2 and int(n[0]) > 0 and int(n[1]) > 0:
                up = True
                break
            time.sleep(4)
        log("SteamVR + ALVR driver up" if up else "SteamVR/ALVR did not come up in 150 s (continuing anyway)")
        time.sleep(8)
        log("starting the mock client (replaying the head trace)")
        task("sbase_client", [f"set ALVR_BIND_IP={MOCK[2]}",
                f"{CLIENT} --report {rd}\\client_report.json --frames-csv {rd}\\frames.csv --out {rd}\\client.hevc "
                f"--seconds {seconds + 140} --connect-timeout 120 --display-sim --refresh 100 --display-hz 100 --real-views --poll-ms 5 --queue-max 2 "
                f"--replay {rd}\\replay.csv --replay-start-file {rd}\\replay.go --stop-file {rd}\\client.stop --display-csv {rd}\\display.csv --poses-csv {rd}\\poses.csv > {rd}\\client_stdout.txt 2>&1"], rd)
        t0 = time.time()
        connected = False
        while time.time() - t0 < 120:
            out = live.ps(f"if (Test-Path '{rd}\\client_stdout.txt') {{ Get-Content '{rd}\\client_stdout.txt' -Raw }}")
            if "decoder_config" in out or "stream" in out.lower() and "started" in out.lower():
                connected = True
                break
            time.sleep(3)
        log("mock client streaming" if connected else "mock client did not connect in 120 s")
        if connected:
            # the first connection with a new refresh rate / resolution makes the ALVR driver restart SteamVR; the client reconnects by itself.
            # Wait for that restart (a second decoder_config) before the app starts, or SteamVR kills it a few seconds in.
            t0 = time.time()
            while time.time() - t0 < 25:
                out = live.ps(f"if (Test-Path '{rd}\\client_stdout.txt') {{ (Select-String -Path '{rd}\\client_stdout.txt' -Pattern 'decoder_config' -SimpleMatch | Measure-Object).Count }}")
                if out.strip().isdigit() and int(out.strip()) >= 2:
                    log("SteamVR restarted and the client reconnected")
                    time.sleep(12)
                    break
                time.sleep(4)
            else:
                log("no restart seen: continuing")
            log(f"starting the probe under SteamVR's OpenXR runtime for {seconds} s")
            task("sbase_probe", [f'set "XR_RUNTIME_JSON={STEAMXR}"', "set XR_PROBE_SCENE=bench\\littlest_tokyo.vab", "set XR_PROBE_CPU_MS=0", "set XR_PROBE_LOAD=steady",
                  f"set XR_PROBE_MARKERS_CSV={rd}\\app_markers.csv", f"set XR_PROBE_TIMING_CSV={rd}\\app_timing.csv", f"cd /d {HOME}",
                  f"{PROBE} {rd}\\app.log 100000 {seconds + 20} {rd}\\app_pose.csv - > {rd}\\app_stdout.txt 2>&1"], rd)
            for _ in range(30):                       # the app must be running (session FOCUSED) before the trace starts
                time.sleep(2)
                if "FOCUSED" in live.ps(f"if (Test-Path '{rd}\\app.log') {{ Get-Content '{rd}\\app.log' -Raw }}"):
                    break
            time.sleep(2)
            live.ps(f"Set-Content -Path '{rd}\\replay.go' -Value go")
            log("replay started")
            time.sleep(min(seconds, 62))
    finally:
        log("stopping the mock client (it writes its reports) and everything else")
        live.ps(f"Set-Content -Path '{rd}\\client.stop' -Value stop; Stop-Process -Name xr_probe -Force -ErrorAction SilentlyContinue")
        for _ in range(20):
            time.sleep(2)
            if live.ps(f"Test-Path '{rd}\\poses.csv'").strip() == "True":
                break
        log("restoring the dashboard's session.json")
        live.ps(f"Copy-Item '{ALVR}\\session_log.txt' '{rd}\\alvr_session_log.txt' -Force -ErrorAction SilentlyContinue")
        kill_all()
        restore_stock_session()
    live.get_dir(rd.replace("\\", "/"), outdir)
    log(f"results in {outdir}")
    for f in ("app.log", "client_stdout.txt"):
        p = outdir / f
        if p.exists():
            print(f"--- {f} (tail)")
            print("\n".join(p.read_text(errors='replace').splitlines()[-6:]))


if __name__ == "__main__":
    main()
