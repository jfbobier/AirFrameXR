#!/usr/bin/env python3
"""The headset's address is not fixed: the streamer (bin\\streamer.exe) against the mock headset (alvr_client_headless at the PC's own LAN address).
    python3 harness/find_headset_e2e.py [auto] [button] [unpaired] [--seconds 90]
Scenarios (each from a fresh copy of the package folder out\\dist\\VisionALVR on the PC, default VISIONALVR_PROFILE = tools/pc.local):
  auto      session pairs the mock headset at a WRONG address: the streamer must still connect (ALVR re-finds it by name from its announcements)
  button    same wrong address, streamer.exe --find-auto: the 'Find headset' dialog lists the headset at its new address, pairs it, restarts the streamer
  unpaired  nothing paired: the dialog finds the headset, pairs it, the streamer connects
A scenario passes when the mock headset reports streaming_started (and, for button/unpaired, the session holds the right address and the log says so)."""
import json, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import live, lib

U = live.PC_USER
wd = rf"C:\Users\{U}\find_e2e"
wdf = wd.replace("\\", "/")
src = rf"{live.CLONE}\out\dist\VisionALVR"
limit = int(sys.argv[sys.argv.index("--seconds") + 1]) if "--seconds" in sys.argv else 90
names = [a for a in sys.argv[1:] if a in ("auto", "button", "unpaired")] or ["auto", "button", "unpaired"]
host = lib.builder_client_hostname()
# The mock headset lives at 127.0.0.2 (loopback, ALVR_BIND_IP). Its own announcements (UDP broadcast) do not reach the streamer from there, so
# announce.ps1 sends the same datagram a headset broadcasts, unicast from 127.0.0.2 to the streamer's port. The wrong address the session starts with
# must not be loopback (all of 127.0.0.0/8 answers on the control port): an unroutable one.
HMD_IP = "127.0.0.2"
WRONG_IP = "10.254.254.9"
live.log(f"mock headset hostname {host}")
PROTO = json.loads([l for l in live.ps(rf"& '{live.CLONE}\\out\\dist\\VisionALVR\\bin\\alvr_host.exe' --system-check").splitlines() if l.startswith("{")][0])["data"]["alvr_protocol_id"]
default_session = json.loads((live.ROOT / "tools/install/session.default.json").read_text())


def session_for(manual_ip):
    s = json.loads(json.dumps(default_session))
    if manual_ip:
        s["client_connections"] = {host: {"display_name": "Mock headset", "current_ip": manual_ip, "manual_ips": [manual_ip], "trusted": True, "connection_state": "Disconnected"}}
    else:
        s["client_connections"] = {}
    return s


ANNOUNCE = r"""param($hostname, $ip, $proto)
# what a headset's ALVR app broadcasts every second (alvr_client_core AnnouncerSocket): "ALVR", zeros, protocol id (u64 LE) at 16, hostname at 24; sent as a
# unicast datagram to the streamer on this PC, because a broadcast from a loopback address is not delivered
$pk = New-Object byte[] 56
[Text.Encoding]::ASCII.GetBytes('ALVR').CopyTo($pk, 0)
[BitConverter]::GetBytes([uint64]$proto).CopyTo($pk, 16)
[Text.Encoding]::ASCII.GetBytes($hostname).CopyTo($pk, 24)
$u = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($ip)), 0)
while ($true) { $u.Send($pk, 56, "127.0.0.1", 9943) | Out-Null; Start-Sleep -Milliseconds 1000 }
"""


def task(name, lines):
    f = live.SCRATCH / f"{name}.cmd"
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    f.write_bytes(("\r\n".join(lines) + "\r\n").encode())
    live.put(f, f"{wdf}/{name}.cmd")
    live.ps(f"schtasks /create /tn {name} /tr 'cmd /c {wd}\\{name}.cmd' /sc once /st 00:00 /rl LIMITED /it /f | Out-Null")


def stop():
    live.ps("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'announce.ps1' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; "
            "Stop-Process -Name VisionALVR,streamer,configure,alvr_host,alvr_client_headless -Force -ErrorAction SilentlyContinue; "
            "schtasks /delete /tn va_mock /f 2>$null | Out-Null; schtasks /delete /tn va_gui /f 2>$null | Out-Null; Stop-Process -Name cmd -Force -ErrorAction SilentlyContinue")
    time.sleep(2)


def run(name):
    live.log(f"=== {name}")
    stop()
    live.ps(f"""
if (Test-Path '{wd}') {{ Remove-Item '{wd}' -Recurse -Force }}
robocopy '{src}' '{wd}' /E /NFL /NDL /NJH /NJS | Out-Null
Remove-Item '{wd}\\logs','{wd}\\config\\session.json','{wd}\\config\\visionalvr.json' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force '{wd}\\config' | Out-Null
""")
    sess = live.SCRATCH / "find_session.json"
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    sess.write_text(json.dumps(session_for(None if name == "unpaired" else WRONG_IP), indent=2))
    live.put(sess, f"{wdf}/config/session.json")
    client = rf"{live.CLONE}\{lib.CLIENT_EXE}"
    ann = live.SCRATCH / "announce.ps1"
    ann.write_text(ANNOUNCE)
    live.put(ann, f"{wdf}/announce.ps1")
    task("va_mock", ["@echo off", f"set ALVR_BIND_IP={HMD_IP}",
                     f'start "" /b powershell -NoProfile -ExecutionPolicy Bypass -File {wd}\\announce.ps1 {host} {HMD_IP} {PROTO}', ":loop",
                     f'"{client}" --report {wd}\\mock_report.json --connect-timeout 900 --seconds 1200 --refresh 90 --real-views > {wd}\\mock_stdout.txt 2>&1', "goto loop"])
    gui_args = "--test" + (" --find-auto" if name != "auto" else "")
    task("va_gui", ["@echo off", f'"{wd}\\bin\\streamer.exe" {gui_args}'])
    live.ps("schtasks /run /tn va_mock | Out-Null")
    time.sleep(3)
    live.ps("schtasks /run /tn va_gui | Out-Null")
    t0 = time.time()
    ok = False
    while time.time() - t0 < limit:
        time.sleep(5)
        out = live.ps(f"if (Test-Path '{wd}\\mock_stdout.txt') {{ Select-String -Path '{wd}\\mock_stdout.txt' -Pattern streaming_started -SimpleMatch | Select-Object -First 1 | ForEach-Object {{ 'YES' }} }}").strip()
        if "YES" in out:
            ok = True
            break
    took = time.time() - t0
    time.sleep(3)
    res = live.ROOT / "harness" / "results" / f"find_headset_{time.strftime('%Y%m%dT%H%M%S')}_{name}"
    res.mkdir(parents=True, exist_ok=True)
    for f in ("config/session.json", "logs/VisionALVR.log", "mock_stdout.txt"):
        (res / Path(f).parent).mkdir(parents=True, exist_ok=True)
        live._retry(["scp", "-o", "BatchMode=yes", *live.MUX, "-q", f"{live.PC}:{wdf}/{f}", str(res / f)])
    stop()
    detail = []
    try:
        s = json.loads((res / "config/session.json").read_text())
        cc = s.get("client_connections", {}).get(host, {})
        detail.append(f"session: manual_ips={cc.get('manual_ips')} trusted={cc.get('trusted')} state={cc.get('connection_state')}")
        if name in ("button", "unpaired") and HMD_IP not in (cc.get("manual_ips") or []):
            ok = False
            detail.append(f"the dialog did not save the headset's address {HMD_IP}")
    except Exception as e:
        detail.append(f"session unreadable: {e}")
    try:
        lg = (res / "logs/VisionALVR.log").read_text(errors="replace")
        detail += [l for l in lg.splitlines() if "find headset" in l]
    except Exception:
        detail.append("no VisionALVR.log")
    print(f"[{'PASS' if ok else 'FAIL'}] {name}: streaming after {took:.0f} s" if ok else f"[FAIL] {name}: no streaming within {limit} s")
    for d in detail:
        print("   ", d)
    return ok


results = {n: run(n) for n in names}
print(json.dumps(results))
sys.exit(0 if all(results.values()) else 1)
