#!/usr/bin/env python3
"""End-to-end run of the configure wizard on the PC against the mock headset (alvr_client_headless on loopback, restarted whenever a test's host
process ends): the wizard runs unattended (`--auto`), through the handshake, every test and the proposal, and writes a report.
    python3 harness/wizard_e2e.py [--seconds 900]
Needs the lab build (python3 harness/run.py build host client gui) and the package folder C:\\Users\\<u>\\VisionALVR_0.3test as the template."""
import json, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import live, lib

U = live.PC_USER
wd = rf"C:\Users\{U}\wizard_e2e"
# --template <folder>: the unzipped package used as the base (default C:\Users\<u>\VisionALVR_0.3test)
# --local-profile <file>: a headset profile file copied to config\profiles\ so the mock headset ("Windows PC", 3552x3200, 100 Hz) matches it
tmpl = sys.argv[sys.argv.index("--template") + 1] if "--template" in sys.argv else rf"C:\Users\{U}\VisionALVR_0.3test"
local_profile = sys.argv[sys.argv.index("--local-profile") + 1] if "--local-profile" in sys.argv else None
root = live.CLONE
limit = int(sys.argv[sys.argv.index("--seconds") + 1]) if "--seconds" in sys.argv else 900
host = lib.builder_client_hostname()
live.log(f"mock headset hostname {host}")
live.ps(f"""
Stop-Process -Name configure,alvr_host,alvr_client_headless -Force -ErrorAction SilentlyContinue
schtasks /delete /tn va_mock /f 2>$null | Out-Null; schtasks /delete /tn va_wiz /f 2>$null | Out-Null
if (Test-Path '{wd}') {{ Remove-Item '{wd}' -Recurse -Force }}
New-Item -ItemType Directory -Force '{wd}' | Out-Null
robocopy '{tmpl}' '{wd}' /E /NFL /NDL /NJH /NJS | Out-Null
Remove-Item '{wd}\\config\\session.json','{wd}\\config\\visionalvr.json','{wd}\\logs' -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item '{root}\\out\\gui\\configure.exe' '{wd}\\configure.exe' -Force
Copy-Item '{root}\\src\\ALVR-v20.14.1\\target\\release\\alvr_host.exe' '{wd}\\alvr_host.exe' -Force
Copy-Item '{root}\\out\\vdxr-shim\\*' '{wd}' -Force
New-Item -ItemType Directory -Force '{wd}\\bench' | Out-Null
Copy-Item '{root}\\build\\probe\\xr_probe.exe' '{wd}\\bench\\xr_probe.exe' -Force
if (-not (Test-Path '{wd}\\bench\\littlest_tokyo.vab')) {{ Copy-Item '{root}\\bench\\littlest_tokyo.vab' '{wd}\\bench\\littlest_tokyo.vab' -Force }}
New-Item -ItemType Directory -Force '{wd}\\profiles','{wd}\\config\\profiles' | Out-Null
Copy-Item '{root}\\_tmp\\install\\profiles\\*.json' '{wd}\\profiles' -Force -ErrorAction SilentlyContinue
""")
if local_profile:
    live.put(Path(local_profile), f"{wd.replace(chr(92), '/')}/config/profiles/e2e-profile.json")
client = rf"{root}\{lib.CLIENT_EXE}"
loop = ["@echo off", "set ALVR_BIND_IP=127.0.0.2", ":loop",
        f'"{client}" --report {wd}\\mock_report.json --connect-timeout 900 --seconds 1200 --refresh 100 --real-views --poll-ms 5 --queue-max 2 --display-sim --display-hz 100 > {wd}\\mock_stdout.txt 2>&1',
        "goto loop"]
wiz = ["@echo off", f'"{wd}\\configure.exe" --auto --headset-ip 127.0.0.2 --headset-host {host} --report {wd}\\report.json']
for name, lines in (("va_mock", loop), ("va_wiz", wiz)):
    f = live.SCRATCH / f"{name}.cmd"
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    f.write_bytes(("\r\n".join(lines) + "\r\n").encode())
    live.put(f, f"{wd.replace(chr(92), '/')}/{name}.cmd")
    live.ps(f"schtasks /create /tn {name} /tr 'cmd /c {wd}\\{name}.cmd' /sc once /st 00:00 /rl LIMITED /it /f | Out-Null")
live.log("starting the mock headset, then the wizard")
live.ps("schtasks /run /tn va_mock | Out-Null")
time.sleep(4)
live.ps("schtasks /run /tn va_wiz | Out-Null")
t0 = time.time()
done = False
while time.time() - t0 < limit:
    time.sleep(10)
    if live.ps(f"Test-Path '{wd}\\report.json'").strip() == "True":
        done = True
        break
live.log("wizard finished" if done else "TIMEOUT: the wizard did not finish")
out = live.ROOT / "harness" / "results" / f"wizard_e2e_{time.strftime('%Y%m%dT%H%M%S')}"
out.mkdir(parents=True, exist_ok=True)
live.ps("Stop-Process -Name configure,alvr_host -Force -ErrorAction SilentlyContinue; schtasks /delete /tn va_mock /f 2>$null | Out-Null; Stop-Process -Name alvr_client_headless -Force -ErrorAction SilentlyContinue; Stop-Process -Name cmd -Force -ErrorAction SilentlyContinue")
time.sleep(2)
live.get_dir(wd.replace("\\", "/"), out / "folder_files") if False else None
for f in ("report.json", "mock_stdout.txt", "config/session.json", "config/visionalvr.json", "logs/VisionALVR.log"):
    (out / Path(f).parent).mkdir(parents=True, exist_ok=True)
    live._retry(["scp", "-o", "BatchMode=yes", *live.MUX, "-q", f"{live.PC}:{wd.replace(chr(92), '/')}/{f}", str(out / f)])
rep = out / "report.json"
if rep.exists():
    r = json.loads(rep.read_text())
    print(json.dumps({k: r[k] for k in ("handshake", "proposal")}, indent=1))
    for t in r["tests"]:
        print(f"  [{t['state']:8s}] {t['name']}: {t['note']}")
    print(json.dumps(r["results"], indent=1))
else:
    print("no report; see", out)
