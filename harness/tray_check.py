#!/usr/bin/env python3
"""The tray (tools/gui/Tray.cs, bin\\tray.exe) on the PC: it must watch while nothing streams and go fully idle while a streamer runs.
    AIRFRAMEXR_PROFILE=5090 python3 harness/tray_check.py [--no-build]
A throwaway folder (C:\\Users\\<user>\\tray_e2e) holds a copy of the install's bin\\ (real alvr_host.exe), the freshly built tray.exe and a stand-in
streamer.exe that just sleeps; nothing in the real install is touched or stopped. tray_check.ps1 samples every 100 ms (stand-in streamers running,
`alvr_host --discover` children running) while it plays the field: a headset announcing itself (UDP 9943 datagram like find_headset_e2e), another
headset, a streamer started by someone else, the tray killed. Verdicts:
  idle_watches                    one discovery child while nothing streams
  other_headset_ignored           an unpaired headset starts nothing
  paired_starts_streamer          the paired headset starts the streamer
  start_is_single                 announces that keep coming never start a second streamer
  idle_while_streamer_runs        no discovery child once the streamer runs (the streamer's host needs UDP 9943)
  cooldown_no_restart             after the streamer closes, announces start nothing and discovery stays off for the cooldown (5 s here, 60 s shipped)
  resumes_after_cooldown          then it watches again
  external_start_stops_discovery  a streamer started by the launcher: the child is gone in under 1.5 s (the host binds UDP 9943 seconds later)
  killed_tray_leaves_no_child     the tray killed hard leaves no discovery child holding UDP 9943"""
import json, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import live

U = live.PC_USER
wd = rf"C:\Users\{U}\tray_e2e"
wdf = wd.replace("\\", "/")
HOST = "tray-mock-hmd"
COOLDOWN = 5
STUB = r"""
Add-Type -OutputType ConsoleApplication -OutputAssembly '%WD%\bin\streamer.exe' -TypeDefinition 'public static class S { public static void Main() { System.Threading.Thread.Sleep(600000); } }'
"""

if "--no-build" not in sys.argv:
    live.build("gui", release=True)

busy = live.ps("[bool](Get-Process alvr_host -ErrorAction SilentlyContinue)").strip()
if busy == "True":
    sys.exit("an alvr_host.exe is running on the PC (UDP 9943 is taken): stop the streamer first")

live.ps(f"""
Get-Process tray -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{wd}\\*' }} | Stop-Process -Force
Start-Sleep 1
if (Test-Path '{wd}') {{ Remove-Item '{wd}' -Recurse -Force }}
New-Item -ItemType Directory -Force '{wd}\\bin','{wd}\\config' | Out-Null
robocopy '{live.INSTALL}\\bin' '{wd}\\bin' /E /NFL /NDL /NJH /NJS | Out-Null
Copy-Item '{live.CLONE}\\out\\gui\\tray.exe' '{wd}\\bin\\tray.exe' -Force
Copy-Item '{live.CLONE}\\out\\gui\\app.exe.config' '{wd}\\bin\\tray.exe.config' -Force -ErrorAction SilentlyContinue
Copy-Item '{live.CLONE}\\tools\\gui\\app.exe.config' '{wd}\\bin\\tray.exe.config' -Force -ErrorAction SilentlyContinue
Remove-Item '{wd}\\bin\\streamer.exe' -Force -ErrorAction SilentlyContinue
""" + STUB.replace("%WD%", wd))

proto = json.loads([l for l in live.ps(rf"& '{wd}\bin\alvr_host.exe' --system-check").splitlines() if l.startswith("{")][0])["data"]["alvr_protocol_id"]
sess = json.loads((live.ROOT / "tools/install/session.default.json").read_text())
sess["client_connections"] = {HOST: {"display_name": "Tray mock", "current_ip": "127.0.0.2", "manual_ips": ["127.0.0.2"], "trusted": True, "connection_state": "Disconnected"}}
live.SCRATCH.mkdir(parents=True, exist_ok=True)
f = live.SCRATCH / "tray_session.json"
f.write_text(json.dumps(sess, indent=2))
live.put(f, f"{wdf}/config/session.base.json")
live.put(live.ROOT / "harness/tray_check.ps1", f"{wdf}/tray_check.ps1")

# the tray needs a desktop (a notification icon): an interactive task, like the GUIs in find_headset_e2e
cmd = live.SCRATCH / "va_tray.cmd"
cmd.write_bytes(f'@echo off\r\nset AIRFRAMEXR_TRAY_COOLDOWN={COOLDOWN}\r\n"{wd}\\bin\\tray.exe"\r\n'.encode())
live.put(cmd, f"{wdf}/va_tray.cmd")
live.ps(f"schtasks /create /tn va_tray /tr 'cmd /c {wd}\\va_tray.cmd' /sc once /st 00:00 /rl LIMITED /it /f | Out-Null; schtasks /run /tn va_tray | Out-Null")

out = live.ps(f"& '{wd}\\tray_check.ps1' -wd '{wd}' -hostname '{HOST}' -proto {proto} -cooldown {COOLDOWN}", timeout=400)
print(out.strip()[-600:])

res = live.ROOT / "harness" / "results" / f"tray_{time.strftime('%Y%m%dT%H%M%S')}"
res.mkdir(parents=True, exist_ok=True)
for name in ("verdicts.json", "samples.csv", "logs/AirFrameXR.log"):
    (res / Path(name).parent).mkdir(parents=True, exist_ok=True)
    live._retry(["scp", "-o", "BatchMode=yes", *live.MUX, "-q", f"{live.PC}:{wdf}/{name}", str(res / name)])
live.ps(f"""schtasks /delete /tn va_tray /f 2>$null | Out-Null
Get-Process tray,streamer -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{wd}\\*' }} | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='alvr_host.exe'" | Where-Object {{ $_.ExecutablePath -like '{wd}\\*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}""")

try:
    v = json.loads((res / "verdicts.json").read_text(encoding="utf-8-sig"))["results"]
except Exception as e:
    sys.exit(f"[FAIL] no verdicts ({e}); see {res}")
ok = True
for r in v:
    ok &= r["ok"]
    print(f"[{'PASS' if r['ok'] else 'FAIL'}] {r['name']}: {r['detail']}")
print(f"results: {res}")
sys.exit(0 if ok else 1)
