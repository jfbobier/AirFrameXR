#!/usr/bin/env python3
"""The whole audio chain, driven by the streamer: streamer.exe (AudioRoute) starts alvr_host in the portable folder, the mock headset connects, the Littlest Tokyo scene (xr_probe,
with its music) plays to the Windows default output, and the levels are measured with WASAPI loopback (harness/probes/audio_level.cs):
  - control (no streamer): a tone to the PC speakers is heard by the speakers' loopback (so silence later means something)
  - during: the system default output is the VB-Audio cable, the PC speakers are muted, and
      cable loopback   = music (the scene reaches the cable the host captures)
      speakers loopback = silence (muted: nothing audible, the builder has no microphone, the loopback after mute is the proxy)
      sink loopback     = music (what the mock headset received over the network and played; VISIONALVR_CLIENT_AUDIO_DEVICE=CABLE In 16ch)
  - after the streamer's window is closed: defaults and the PC speakers' mute state are back.
Needs: python3 harness/run.py build client probe gui package. Exit 0 = pass."""
import json, re, sys, threading, time, wave, struct, math
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
PC, SINK = "Realtek", "CABLE In 16ch"
rid = "audio_chain"
rd = f"runs\\{rid}"
inst = f"{rd}\\VisionALVR"
sc = json.load(open(ROOT / "harness/scenarios/e2e_installed_layout.json"))
lib.b_run(f"if not exist {rd} mkdir {rd}")
lib.b_run("if not exist runs\\audio mkdir runs\\audio")
home = lib.b_run("echo %USERPROFILE%")[1].strip().splitlines()[-1].strip()
base = f"{home}\\{lib.ROOTNAME}"
_run = time.strftime("%H%M%S"); _n = [0]

def ps(script, args="", timeout=180, local="harness/audio_route.ps1"):
    _n[0] += 1; n = _n[0]
    lib.b_put(ROOT / local, f"runs/audio/{script}")
    body = f"powershell -NoProfile -ExecutionPolicy Bypass -File runs\\audio\\{script} {args} > runs\\audio\\c{_run}_{n}.txt 2>&1\r\n"
    lib.start_limited("audio", f"va_ch{n}", f"c{_run}_{n}.bat", body, f"rcc{_run}_{n}.txt").wait(timeout)
    dst = ROOT / "harness/cache" / f"chain{_run}_{n}.txt"; dst.unlink(missing_ok=True)
    lib.b_get(f"runs/audio/c{_run}_{n}.txt", dst)
    return dst.read_text(errors="replace").splitlines() if dst.exists() else []
route = lambda args: ps("audio_route.ps1", args)
chain = lambda args: ps("audio_chain.ps1", args, local="harness/probes/audio_chain.ps1")

def parse(lines):
    d = {"default": {}, "device": {}}
    for l in lines:
        m = re.match(r"default (\w+) (.*)", l)
        if m: d["default"][m.group(1)] = m.group(2).strip()
        m = re.match(r"device (.*) \| vol=([\d.]+) mute=(\d)", l)
        if m: d["device"][m.group(1).strip()] = (float(m.group(2)), m.group(3) == "1")
    return d
def level(lines, key):
    for l in lines:
        if l.startswith(key + ":"):
            m = re.search(r"peak=([\d.]+) rms=([\d.]+)", l)
            if m: return float(m.group(1)), float(m.group(2))
            if "ERROR" in l: return None
    return (0.0, 0.0) if not any(l.startswith(key + ":") and "ERROR" in l for l in lines) else None

fails = 0
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))

before = parse(route("snapshot"))
cable = next(n for n in before["device"] if "cable input" in n.lower())
pc = before["default"]["console"]
assert PC.lower() in pc.lower(), f"the default output must be the PC's {PC} to start with: {pc}"
orig = {n: v for n, v in before["device"].items()}
print("before:", pc, orig.get(pc))

# the tools on the builder
lib.b_put(ROOT / "harness/probes/audio_level.cs", "runs/audio/audio_level.cs")
csc = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe"
rc, out = lib.b_run(f'"{csc}" /nologo /platform:x64 /out:runs\\audio\\audio_level.exe runs\\audio\\audio_level.cs')
assert "error" not in out.lower(), out
level_exe = f"{base}\\runs\\audio\\audio_level.exe"
tone = ROOT / "harness/cache/tone.wav"
with wave.open(str(tone), "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(48000)
    w.writeframes(b"".join(struct.pack("<hh", v, v) for v in (int(16000 * math.sin(2 * math.pi * 440 * i / 48000)) for i in range(48000 * 5))))
lib.b_put(tone, "runs/audio/tone.wav")

try:
    route(f'setdefault "{PC}"'); route(f'mute "{PC}" 0'); route(f'set "{PC}" 0.55'); route(f'set "CABLE Input" 0.40')
    # -- control: the speakers' loopback hears a tone played to the default output
    ctl = level(chain(f'control "{level_exe}" "{base}\\runs\\audio\\tone.wav" "{PC}"'), "control_speakers")
    check("control: the speakers' loopback hears a tone (the measurement works)", ctl is not None and ctl[1] > 0.05, ctl)

    # -- the portable folder as a user unzips it, a trusted mock client, the streamer
    lib.kill_stale()
    lib.b_run(f"robocopy out\\dist\\VisionALVR {inst} /MIR /NFL /NDL /NJH /NJS /NP")
    pin = (lib.builder_client_hostname(), "127.0.0.2")
    sess = ROOT / "harness/cache/audio_chain_session.json"
    lib.derive_session(sc, sess, pin)
    lib.b_put(sess, f"runs/{rid}/VisionALVR/config/session.json")
    streamer = lib.start_limited(rid, "va_chain_streamer", "streamer.bat", f"{inst}\\bin\\streamer.exe --test\r\n", "streamer.rc")
    time.sleep(8)
    client = lib.b_popen(f'set ALVR_BIND_IP=127.0.0.2&& set RUST_LOG=warn&& set VISIONALVR_CLIENT_AUDIO_DEVICE={SINK}&& {lib.CLIENT_EXE} --report {rd}\\client_report.json --frames-csv {rd}\\frames.csv '
                         f'--seconds 50 --connect-timeout 45 --yaw-amp 0.5 --yaw-hz 0.25 > {rd}\\client_stdout.txt 2>&1')
    app = {"frames": 4000, "timeout_s": 40, "runtime": inst + "\\bin", "env": {"XR_PROBE_SCENE": f"{inst}\\bench\\littlest_tokyo.vab"}}
    t = threading.Thread(target=lambda: lib.launch_app(app, rid, rd, print)); t.start()
    time.sleep(22)   # connected, streaming, the music playing
    during_state = parse(route("snapshot"))
    lv = chain(f'levels "{level_exe}" 6 "{PC}" "{SINK}"')
    print("\n".join(lv))
    t.join(); 
    for rc_ in range(20):
        if (lib.b_run(f"if exist {rd}\\client_stdout.txt echo Y")[1].strip() == "Y"): break
    close = chain("closestreamer")
    print("\n".join(close))
    after = parse(route("snapshot"))

    for role in ("console", "multimedia", "communications"):
        check(f"during: {role} default is the VB-Audio cable", cable in during_state["default"].get(role, ""), during_state["default"].get(role))
    check("during: the PC speakers are muted, their volume unchanged", during_state["device"][pc] == (0.55, True), during_state["device"][pc])
    c, s, k = level(lv, "cable"), level(lv, "speakers"), level(lv, "sink")
    check("during: the cable carries the scene's music (loopback)", c is not None and c[1] > 0.01, c)
    check("during: the muted PC speakers are silent (loopback)", s is not None and s[1] < 0.001, s)
    check("during: the mock headset received the audio over the network and played it (sink loopback)", k is not None and k[1] > 0.01, k)
    check("after: the streamer exited when its window was closed", any("exited" in l for l in close), close)
    check("after: defaults are back on the PC output, speakers unmuted", all(after["default"].get(r) == pc for r in ("console", "multimedia", "communications")) and after["device"][pc][1] is False, (after["default"], after["device"][pc]))
    for f in ("app.log", "client_stdout.txt"):
        lib.b_get(f"{rd.replace(chr(92), '/')}/{f}", ROOT / "harness/cache" / f"chain_{f}")
    applog = (ROOT / "harness/cache/chain_app.log").read_text(errors="replace") if (ROOT / "harness/cache/chain_app.log").exists() else ""
    check("the scene app started its music", "scene sound" in applog and "playing" in applog, applog[-400:])
finally:
    lib.kill_stale()
    route(f'setdefault "{PC}"'); route(f'mute "{PC}" 0')
    for n, (v, m) in orig.items():
        if "cable" in n.lower() or PC.lower() in n.lower(): route(f'set "{n}" {v}')
    print("defaults, mute and volumes restored")
sys.exit(1 if fails else 0)
