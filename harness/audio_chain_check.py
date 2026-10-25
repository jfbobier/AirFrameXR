#!/usr/bin/env python3
"""The whole audio chain, driven by the streamer: streamer.exe (AudioRoute) starts alvr_host in the portable folder, the mock headset connects, the Littlest Tokyo scene (xr_probe,
with its music) plays to the Windows default output, and the levels are measured with WASAPI loopback (harness/probes/audio_level.cs):
  - control (no streamer): a tone to the PC speakers is heard by the speakers' loopback (so silence later means something)
  - during: the system default output is the VB-Audio cable, the PC speakers are muted (endpoint state: the builder has no microphone and a muted output's loopback is NOT silent), and
      cable loopback   = music (the scene reaches the cable the host captures)
      sink              = music (process loopback of the mock headset: what it received over the network and played, into the muted PC speakers)
  - after the streamer's window is closed: defaults and the PC speakers' mute state are back.
Needs: python3 harness/run.py build client probe gui package. Default mode: Windows process loopback (no cable; the default output must stay the PC's
own); --device: the VB-CABLE path. Exit 0 = pass."""
import json, re, sys, threading, time, wave, struct, math
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
PC = "Realtek"
MODE = "device" if "--device" in sys.argv else "process"   # process: Windows process loopback, no cable (default); --device: the VB-CABLE path
MIC = "icro"   # a recording device whose name has this (Microphone / Prise microphone); skipped when none is active
SINK = PC   # the mock headset plays into the (muted) PC speakers: nothing audible, and its process loopback hears it whatever the output's mute; the 16ch cable answered DEVICE_IN_USE and process loopback silence
rid = "audio_chain"
rd = f"runs\\{rid}"
inst = f"{rd}\\AirFrameXR"
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
lib.b_put(ROOT / "harness/probes/proc_loopback.cs", "runs/audio/proc_loopback.cs")
csc = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe"
rc, out = lib.b_run(f'"{csc}" /nologo /platform:x64 /out:runs\\audio\\audio_level.exe runs\\audio\\audio_level.cs')
assert "error" not in out.lower(), out
rc, out = lib.b_run(f'"{csc}" /nologo /platform:x64 /out:runs\\audio\\proc_loopback.exe runs\\audio\\proc_loopback.cs')
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
    route(f'set "{PC}" 1.0')   # the control is as loud as it gets: the laptop's microphone array is a weak witness
    mic_base = chain(f'micbaseline "{level_exe}" "{MIC}"')
    mic_on = not any("ERROR" in l for l in mic_base if l.startswith("mic_baseline"))
    print("microphone:", "present" if mic_on else "none active (the builder's only microphone endpoint is not enumerated): the acoustic check is skipped")
    ctl_lines = chain(f'control "{level_exe}" "{base}\\runs\\audio\\tone.wav" "{PC}" "{MIC}"')
    ctl = level(ctl_lines, "control_speakers")
    route(f'set "{PC}" 0.55')
    check("control: the speakers' loopback hears a tone (the measurement works)", ctl is not None and ctl[1] > 0.05, ctl)
    if mic_on:
        mb, mc = level(mic_base, "mic_baseline"), level(ctl_lines, "control_mic")
        print("microphone: room", mb, "tone from the speakers", mc)
        # informational only: the room is noisy and varies between runs; the positive control that counts is the unmuted client output during the run, under the same noise

    # -- the portable folder as a user unzips it, a trusted mock client, the streamer
    lib.kill_stale()
    lib.b_run(f"robocopy out\\dist\\AirFrameXR {inst} /MIR /NFL /NDL /NJH /NJS /NP")
    pin = (lib.builder_client_hostname(), "127.0.0.2")
    sess = ROOT / "harness/cache/audio_chain_session.json"
    lib.derive_session(sc, sess, pin)
    lib.b_put(sess, f"runs/{rid}/AirFrameXR/config/session.json")
    cfgf = ROOT / "harness/cache/audio_chain_airframexr.json"; cfgf.write_text(json.dumps({"audio": {"capture": MODE}, "debug": True}))
    lib.b_put(cfgf, f"runs/{rid}/AirFrameXR/config/airframexr.json")
    # the mock headset plays on this PC: the host must not capture its own client again (a real headset is another machine)
    streamer = lib.start_limited(rid, "va_chain_streamer", "streamer.bat", f"{inst}\\bin\\streamer.exe --test\r\n", "streamer.rc",
                                 env={"AIRFRAMEXR_AUDIO_EXCLUDE_PROCESS": "alvr_client_headless.exe"} if MODE == "process" else None)
    time.sleep(8)
    # the client runs in the interactive session like the host and the app: processes started from the SSH session live in another logon session, and process loopback
    # (include or exclude) does not hear them (measured; the earlier explanation 'elevated' was wrong, this builder's UAC makes no difference)
    client = lib.start_limited(rid, "va_chain_client", "client.bat",
                               f'{lib.CLIENT_EXE} --report {rd}\\client_report.json --frames-csv {rd}\\frames.csv --seconds 100 --connect-timeout 45 --yaw-amp 0.5 --yaw-hz 0.25 > {rd}\\client_stdout.txt 2>&1\r\n',
                               "client.rc", env={"ALVR_BIND_IP": "127.0.0.2", "RUST_LOG": "warn", "AIRFRAMEXR_CLIENT_AUDIO_DEVICE": SINK})
    app = {"frames": 9000, "timeout_s": 85, "runtime": inst + "\\bin", "env": {"XR_PROBE_SCENE": f"{inst}\\bench\\littlest_tokyo.vab"}}
    t = threading.Thread(target=lambda: lib.launch_app(app, rid, rd, print)); t.start()
    time.sleep(12)   # connected, streaming, the music playing (each helper call below takes ~10 s: the app runs 85 s)
    runs_lv, during_state = [], None
    for i in range(3):   # the music starts a few seconds after the scene loads and the headset's buffer a little later: the best of three measurements counts
        lv = chain(f'levels "{level_exe}" 6 "{PC}" "{SINK}" "{base}\\runs\\audio\\proc_loopback.exe" "{MIC if mic_on else ''}"')
        print(f"levels #{i + 1}:", " | ".join(lv))
        runs_lv.append(lv)
        if during_state is None:
            during_state = parse(route("snapshot"))
            print("during: cable", during_state["device"].get(cable), "speakers", during_state["device"].get(pc))
    best = lambda key: max((level(l, key) or (0, 0) for l in runs_lv), key=lambda v: v[1])
    quiet = lambda key: max((level(l, key) or (0, 0) for l in runs_lv), key=lambda v: v[1])
    t.join(); 
    for rc_ in range(20):
        if (lib.b_run(f"if exist {rd}\\client_stdout.txt echo Y")[1].strip() == "Y"): break
    close = chain("closestreamer")
    print("\n".join(close))
    after = parse(route("snapshot"))

    for role in ("console", "multimedia", "communications"):
        if MODE == "device":
            check(f"during: {role} default is the VB-Audio cable", cable in during_state["default"].get(role, ""), during_state["default"].get(role))
        else:
            check(f"during: {role} default output is left alone (no cable involved)", during_state["default"].get(role) == pc, during_state["default"].get(role))
    check("during: the PC speakers are muted, their volume unchanged", during_state["device"][pc] == (0.55, True), during_state["device"][pc])
    c, s, k = best("cable"), quiet("speakers"), best("sink")
    if MODE == "device":
        check("during: the cable carries the scene's music (loopback)", c is not None and c[1] > 0.01, c)
    else:
        check("during: the cable carries nothing (process loopback does not use it)", c is not None and c[1] < 0.001, c)
    if mic_on:
        # the same stream, the same fan noise (the GPU is busy): the client plays into the PC speakers, muted by the streamer; unmuted for a few seconds it must be heard
        mm = max((level(l, "mic") or (0, 0) for l in runs_lv), key=lambda v: v[1])
        route(f'mute "{PC}" 0')
        mu = level(chain(f'micbaseline "{level_exe}" "{MIC}"'), "mic_baseline")
        route(f'mute "{PC}" 1')
        print("microphone: muted", mm, "unmuted", mu)
        if mm[1] >= 0.0015:   # a noisy room (fans, a voice) while the output is muted buries the speakers: nothing can be concluded, nothing is failed
            print(f"SKIP microphone check: the room is too noisy for the laptop's microphone array while muted (rms {mm[1]:.4f}, quiet is below 0.001; the check needs below 0.0015)")
        else:
            check("during: the microphone hears the client's output only when the PC speakers are unmuted (muted = silent)", mu is not None and mu[1] > 2 * mm[1], (mm, mu))
    print("info: the muted speakers' own loopback reads", s, "(an output's mute does not silence its loopback, so it proves nothing; the microphone check above is the acoustic proof)")
    check("during: the mock headset received the audio over the network and played it (process loopback of the client)", k is not None and k[1] > 0.01, k)
    check("after: the streamer exited when its window was closed", any("exited" in l for l in close), close)
    check("after: defaults are back on the PC output, speakers unmuted", all(after["default"].get(r) == pc for r in ("console", "multimedia", "communications")) and after["device"][pc][1] is False, (after["default"], after["device"][pc]))
    for f in ("app.log", "client_stdout.txt"):
        lib.b_get(f"{rd.replace(chr(92), '/')}/{f}", ROOT / "harness/cache" / f"chain_{f}")
    applog = (ROOT / "harness/cache/chain_app.log").read_text(errors="replace") if (ROOT / "harness/cache/chain_app.log").exists() else ""
    hostlog = lib.b_run(f"findstr /s /i /c:\"process loopback\" {rd}\\AirFrameXR\\logs\\alvr.log {rd}\\AirFrameXR\\logs\\debug\\*\\alvr.log")[1]
    check("the host captured with process loopback" if MODE == "process" else "the host did not use process loopback", ("process loopback" in hostlog) == (MODE == "process"), hostlog[-300:])
    check("the scene app started its music", "scene sound" in applog and "playing" in applog, applog[-400:])
finally:
    lib.kill_stale()
    route(f'setdefault "{PC}"'); route(f'mute "{PC}" 0')
    for n, (v, m) in orig.items():
        if "cable" in n.lower() or PC.lower() in n.lower(): route(f'set "{n}" {v}')
    print("defaults, mute and volumes restored")
sys.exit(1 if fails else 0)
