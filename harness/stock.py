"""The stock chain (ALVR v20.14.1 + SteamVR) driven exactly like the lab drives our own chain, for 1:1 comparisons (harness/compare.py).

Same scenario file, same session.json (lib.derive_session writes it for both chains, every run), same mock headset client (alvr_client_headless: display
emulation, head trace replay), same OpenXR app (xr_probe, Littlest Tokyo) -- only the middle differs: here the app runs on SteamVR's OpenXR runtime and
ALVR's own SteamVR driver streams it; there it runs on VDXR + OVRShim and our host streams it.

Needs on the PC (tools/pc.local): stock ALVR (VISIONALVR_STOCK_ALVR, default C:\\Games\\ALVR) with SteamVR installed at the default path, the mock client and
the probe built (python3 harness/run.py build client probe), the ALVR app on the headset closed. The stock dashboard's session.json is replaced for the run
(the original is kept as session.json.bak-stockcmp and restored).
"""
import json, os, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import live, lib

ALVR = os.environ.get("VISIONALVR_STOCK_ALVR", r"C:\Games\ALVR")
HOME = rf"C:\Users\{live.PC_USER}\{lib.ROOTNAME}"          # the builder's working folder (clone)
CLIENT = HOME + "\\" + lib.CLIENT_EXE
PROBE = HOME + r"\build\probe\xr_probe.exe"
STEAMXR = r"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json"
MOCK_IP = "127.0.0.2"
log = live.log


def _fwd(p):
    return p.replace("\\", "/")


def kill_all():
    live.ps("""
Stop-Process -Name xr_probe,alvr_client_headless,alvr_host,VisionALVR -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'ALVR Dashboard',vrserver,vrmonitor,vrcompositor,vrdashboard,vrwebhelper,vrstartup -Force -ErrorAction SilentlyContinue
foreach ($t in 'stock_dash','stock_client','stock_probe') { schtasks /delete /tn $t /f 2>$null | Out-Null }
""")
    time.sleep(3)


def task(name, lines, rd):
    """cmd.exe lines through a LIMITED scheduled task in the interactive session (a .cmd copied over: paths with spaces survive)."""
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    f = live.SCRATCH / f"{name}.cmd"
    f.write_bytes(("\r\n".join(["@echo off"] + lines) + "\r\n").encode())
    live.put(f, f"{_fwd(rd)}/{name}.cmd")
    live.ps(f"""
schtasks /delete /tn {name} /f 2>$null | Out-Null
schtasks /create /tn {name} /tr 'cmd /c {rd}\\{name}.cmd' /sc once /st 00:00 /rl LIMITED /it /f | Out-Null
schtasks /run /tn {name} | Out-Null
""")


def install_session(sc, hostname):
    """The lab's session for this scenario (the very file our chain gets) as the stock dashboard's session.json, the mock client pre-trusted."""
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    local = live.SCRATCH / "stock_session.json"
    lib.derive_session(sc, local, pin=(hostname, MOCK_IP))
    j = json.loads(local.read_text())
    j["session_settings"]["extra"]["logging"]["log_tracking"] = True
    local.write_text(json.dumps(j, indent=1))
    live.ps(f"if (-not (Test-Path '{ALVR}\\session.json.bak-stockcmp') -and (Test-Path '{ALVR}\\session.json')) {{ Copy-Item '{ALVR}\\session.json' '{ALVR}\\session.json.bak-stockcmp' }}")
    live.put(local, f"{_fwd(ALVR)}/session.json")
    return local


def restore_session():
    live.ps(f"if (Test-Path '{ALVR}\\session.json.bak-stockcmp') {{ Copy-Item '{ALVR}\\session.json.bak-stockcmp' '{ALVR}\\session.json' -Force }} else {{ Remove-Item '{ALVR}\\session.json' -ErrorAction SilentlyContinue }}")


def run_stock(sc, outdir: Path):
    """Runs the scenario's replay on the stock chain; artifacts (poses.csv, frames.csv, display.csv, app_markers.csv, client.hevc, ...) land in outdir."""
    outdir.mkdir(parents=True, exist_ok=True)
    c, app, h = sc["client"], sc.get("app", {}), sc.get("host", {})
    rd = f"{HOME}\\runs\\stock_{outdir.name}"
    live.pc(f'if not exist "{rd}" mkdir "{rd}"')
    seconds = int(h.get("seconds", c.get("seconds", 60)))
    trace = lib.ROOT / c["replay"]
    live.put(trace, f"{_fwd(rd)}/replay.csv")
    rc, _ = 0, None
    hostname = lib.builder_client_hostname()
    kill_all()
    steps = {"alvr": ALVR}
    try:
        install_session(sc, hostname)
        log("starting the ALVR dashboard (it starts SteamVR)")
        task("stock_dash", [f'cd /d "{ALVR}"', 'start "" "ALVR Dashboard.exe"'], rd)
        t0 = time.time()
        up = False
        while time.time() - t0 < 150:
            out = live.ps("(Get-Process vrserver -ErrorAction SilentlyContinue | Measure-Object).Count; (netstat -ano | Select-String ':9943').Count")
            n = [x.strip() for x in out.split() if x.strip().isdigit()]
            if len(n) >= 2 and int(n[0]) > 0 and int(n[1]) > 0:
                up = True
                break
            time.sleep(4)
        steps["steamvr_up"] = up
        log("SteamVR + ALVR driver up" if up else "SteamVR/ALVR did not come up in 150 s (continuing anyway)")
        time.sleep(8)
        flags = lib.argline({**c, "seconds": int(c.get("seconds", seconds)) + 140, "connect_timeout": 120}, lib.CLIENT_FLAGS)
        cmd = (f"{CLIENT} --report {rd}\\client_report.json --frames-csv {rd}\\frames.csv --out {rd}\\client.hevc {flags} "
               f"--replay {rd}\\replay.csv --replay-start-file {rd}\\replay.go --stop-file {rd}\\client.stop "
               f"--display-csv {rd}\\display.csv --poses-csv {rd}\\poses.csv > {rd}\\client_stdout.txt 2>&1")
        log("starting the mock client (replaying the head trace)")
        task("stock_client", [f"set ALVR_BIND_IP={MOCK_IP}", *[f"set {k}={v}" for k, v in sc.get("client_env", {}).items()], cmd], rd)
        t0 = time.time()
        connected = False
        while time.time() - t0 < 120:
            out = live.ps(f"if (Test-Path '{rd}\\client_stdout.txt') {{ Get-Content '{rd}\\client_stdout.txt' -Raw }}")
            if "decoder_config" in out:
                connected = True
                break
            time.sleep(3)
        steps["connected"] = connected
        log("mock client streaming" if connected else "mock client did not connect in 120 s")
        if connected:
            # the first connection with a new refresh rate / resolution makes ALVR's driver restart SteamVR; wait for the second decoder_config
            t0 = time.time()
            while time.time() - t0 < 25:
                out = live.ps(f"if (Test-Path '{rd}\\client_stdout.txt') {{ (Select-String -Path '{rd}\\client_stdout.txt' -Pattern 'decoder_config' -SimpleMatch | Measure-Object).Count }}")
                if out.strip().isdigit() and int(out.strip()) >= 2:
                    log("SteamVR restarted and the client reconnected")
                    time.sleep(12)
                    break
                time.sleep(4)
            env = {"XR_RUNTIME_JSON": STEAMXR, "XR_PROBE_MARKERS_CSV": f"{rd}\\app_markers.csv", "XR_PROBE_TIMING_CSV": f"{rd}\\app_timing.csv", **app.get("env", {})}
            env = {k: v.replace("\\\\", "\\") for k, v in env.items()}
            task("stock_probe", [*[f"set {k}={v}" for k, v in env.items()], f"cd /d {HOME}",
                                 f"{PROBE} {rd}\\app.log {int(app.get('frames', 100000))} {seconds + 40} {rd}\\app_pose.csv - > {rd}\\app_stdout.txt 2>&1"], rd)
            for _ in range(30):                         # the app must be running (session FOCUSED) before the trace starts
                time.sleep(2)
                if "FOCUSED" in live.ps(f"if (Test-Path '{rd}\\app.log') {{ Get-Content '{rd}\\app.log' -Raw }}"):
                    break
            time.sleep(2)
            live.ps(f"Set-Content -Path '{rd}\\replay.go' -Value go")
            log(f"replay started ({seconds} s)")
            time.sleep(seconds)
    finally:
        log("stopping the mock client (it writes its reports) and everything else")
        live.ps(f"Set-Content -Path '{rd}\\client.stop' -Value stop; Stop-Process -Name xr_probe -Force -ErrorAction SilentlyContinue")
        for _ in range(20):
            time.sleep(2)
            if live.ps(f"Test-Path '{rd}\\poses.csv'").strip() == "True":
                break
        live.ps(f"Copy-Item '{ALVR}\\session_log.txt' '{rd}\\alvr_session_log.txt' -Force -ErrorAction SilentlyContinue")
        kill_all()
        restore_session()
    live.get_dir(_fwd(rd), outdir)
    (outdir / "stock_steps.json").write_text(json.dumps(steps))
    return steps
