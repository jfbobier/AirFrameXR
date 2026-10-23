#!/usr/bin/env python3
"""Step 1 of dropping the virtual cable: does Windows process loopback capture a process's audio with the PC output MUTED (or at 10 %), without any driver?
On the builder: a tone plays from a separate process through the default output; proc_loopback.exe (harness/probes/proc_loopback.cs, plain .NET + COM) captures
  include <player pid>   -> the tone, whatever the device's mute / volume
  include <other pid>    -> silence (only that process tree is heard)
  exclude <explorer pid> -> the tone (everything else)
at three device states: output muted, output at 10 %, output at 100 %. The output's mute and volume are restored at the end. Exit 0 = pass."""
import re, sys, time, wave, struct, math
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
lib.b_run("if not exist runs\\audio mkdir runs\\audio")
lib.b_put(ROOT / "harness/audio_route.ps1", "runs/audio/audio_route.ps1")
lib.b_put(ROOT / "harness/probes/proc_loopback.ps1", "runs/audio/proc_loopback.ps1")
lib.b_put(ROOT / "harness/probes/proc_loopback.cs", "runs/audio/proc_loopback.cs")
tone = ROOT / "harness/cache/tone.wav"
with wave.open(str(tone), "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(48000)
    w.writeframes(b"".join(struct.pack("<hh", v, v) for v in (int(16000 * math.sin(2 * math.pi * 440 * i / 48000)) for i in range(48000 * 5))))
lib.b_put(tone, "runs/audio/tone.wav")
csc = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe"
rc, out = lib.b_run(f'"{csc}" /nologo /platform:x64 /out:runs\\audio\\proc_loopback.exe runs\\audio\\proc_loopback.cs')
assert "error" not in out.lower(), out
home = lib.b_run("echo %USERPROFILE%")[1].strip().splitlines()[-1].strip()
base = f"{home}\\{lib.ROOTNAME}\\runs\\audio"
_n = [0]
_run = time.strftime("%H%M%S")   # file names unique per run: a finished task is recognised by its rc file, a stale one would end the wait at once
def run_ps(script, args, timeout=120):
    _n[0] += 1
    n = _n[0]
    body = f"powershell -NoProfile -ExecutionPolicy Bypass -File runs\\audio\\{script} {args} > runs\\audio\\pl{_run}_{n}.txt 2>&1\r\n"
    lib.start_limited("audio", f"va_pl{n}", f"pl{_run}_{n}.bat", body, f"rcpl{_run}_{n}.txt").wait(timeout)
    dst = ROOT / "harness/cache" / f"pl{_run}_{n}.txt"; dst.unlink(missing_ok=True)
    lib.b_get(f"runs/audio/pl{_run}_{n}.txt", dst)
    return dst.read_text(errors="replace").splitlines() if dst.exists() else []

snap = run_ps("audio_route.ps1", "snapshot")
pc = next(l.split(" ", 2)[2] for l in snap if l.startswith("default console"))
vol0 = float(re.search(r"device " + re.escape(pc) + r" \| vol=([\d.]+)", "\n".join(snap)).group(1))
mute0 = re.search(r"device " + re.escape(pc) + r" \| vol=[\d.]+ mute=(\d)", "\n".join(snap)).group(1) == "1"
print("output:", pc, "volume", vol0, "muted", mute0)
fails = 0
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))
def peaks(lines):
    d = {}
    for l in lines:
        m = re.match(r"(\w+): frames=(\d+) peak=([\d.]+) rms=([\d.]+)", l)
        if m: d[m.group(1)] = (int(m.group(2)), float(m.group(3)))
        elif l.startswith(("include", "exclude")): d[l.split(":")[0]] = l
    return d

try:
    for label, vol, mute in (("muted", 1.0, True), ("10 %", 0.1, False), ("100 %", 1.0, False)):
        run_ps("audio_route.ps1", f'set "{pc}" {vol}'); run_ps("audio_route.ps1", f'mute "{pc}" {1 if mute else 0}')
        lines = run_ps("proc_loopback.ps1", f'"{base}\\proc_loopback.exe" "{base}\\tone.wav"')
        d = peaks(lines)
        print(f"[{label}]", [l for l in lines if ":" in l and "frames" in l or "ERROR" in l])
        check(f"{label}: the player's audio is captured (include its pid)", isinstance(d.get("include_player"), tuple) and d["include_player"][1] > 0.2, d.get("include_player"))
        check(f"{label}: another process is silent", isinstance(d.get("include_other"), tuple) and d["include_other"][1] < 0.01, d.get("include_other"))
        check(f"{label}: exclude-mode hears everything else", isinstance(d.get("exclude_other"), tuple) and d["exclude_other"][1] > 0.2, d.get("exclude_other"))
finally:
    run_ps("audio_route.ps1", f'set "{pc}" {vol0}'); run_ps("audio_route.ps1", f'mute "{pc}" {1 if mute0 else 0}')
    print("output restored")
sys.exit(1 if fails else 0)
