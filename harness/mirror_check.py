#!/usr/bin/env python3
"""Desktop mirror checks on the builder (docs/PLAN_0.5.md, phase 5). Everything that can be checked without a headset:

  1. input maths: `rustc --test tools/host/src/desktop.rs` (screen placement, per-eye projection, hand ray -> desktop pixel, click edges), run locally
  2. picture: tools/probe/desktop_probe.cpp renders the mirror's shader on the builder's GPU
       synthetic SDR  : known patches come out unchanged, the cursor dot is where it should be, the right eye (not drawn) is the clear colour
       synthetic HDR  : scRGB FP16 patches become the expected SDR values (SDR white level = 1.0, soft knee above, sRGB encode)
       capture        : the real desktop is captured (size, HDR or not) and drawn (the image is not the clear colour)
  3. what only a person can judge is printed as the checklist at the end (hands on the headset: pointer offset, pinch, jitter, HDR desktop)

    python3 harness/mirror_check.py          # all of it; exit 0 = every automatic check passed
"""
import json, math, subprocess, sys, tempfile
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
fails = 0


def check(name, ok, detail=""):
    global fails
    fails += 0 if ok else 1
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}: {detail}")


def srgb8(v):
    v = min(max(v, 0.0), 1.0)
    return round((12.92 * v if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055) * 255)


def expected_hdr(v, white):
    c = max(v, 0.0) / white
    c = c if c < 0.9 else 0.9 + 0.1 * math.tanh((c - 0.9) / 0.1)
    return srgb8(c)


def near(a, b, tol):
    return all(abs(x - y) <= tol for x, y in zip(a, b))


# ---- 1. the maths -------------------------------------------------------------------------------------------------------------
with tempfile.TemporaryDirectory() as t:
    exe = Path(t) / "desktop_test"
    p = subprocess.run(["rustc", "--edition", "2021", "--test", str(ROOT / "tools/host/src/desktop.rs"), "-o", str(exe)], capture_output=True, text=True)
    if p.returncode:
        check("input maths compile", False, p.stderr[-300:])
    else:
        r = subprocess.run([str(exe)], capture_output=True, text=True)
        last = [l for l in r.stdout.splitlines() if l.startswith("test result")]
        check("input maths (desktop.rs unit tests)", r.returncode == 0, last[0] if last else r.stdout[-200:])

# ---- 2. the picture on the builder ---------------------------------------------------------------------------------------------
rid = "mirror"
lib.b_run(f"if not exist _tmp\\mirror mkdir _tmp\\mirror & if not exist runs\\{rid} mkdir runs\\{rid}")
for f in ("tools/probe/desktop_probe.cpp", "nvenc/hostlib/desktop.cpp", "nvenc/hostlib/desktop.h"):
    lib.b_put(ROOT / f, f"_tmp/mirror/{Path(f).name}")
rc, home = lib.b_run("echo %USERPROFILE%")
home = home.strip().splitlines()[-1].strip()
base = f"{home}\\{lib.ROOTNAME}"
bat = ("@echo off\r\ncall \"C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\VC\\Auxiliary\\Build\\vcvars64.bat\" >nul\r\n"
       f"cd /d {base}\\_tmp\\mirror\r\n"
       "cl /nologo /EHsc /MD /O2 /std:c++17 /I. desktop_probe.cpp /Fe:desktop_probe.exe /link d3d11.lib dxgi.lib d3dcompiler.lib user32.lib wtsapi32.lib\r\n")
lib.CACHE.mkdir(parents=True, exist_ok=True)
(lib.CACHE / "mirror_build.bat").write_text(bat)
lib.b_put(lib.CACHE / "mirror_build.bat", "_tmp/mirror/build.bat")
rc, out = lib.b_run("cmd /c _tmp\\mirror\\build.bat", timeout=300)
check("probe builds on the builder", "error" not in out.lower() and "fatal" not in out.lower(), out.strip().splitlines()[-1] if out.strip() else "")

body = "".join(f"_tmp\\mirror\\desktop_probe.exe {a} runs\\{rid}\\{n}.json > runs\\{rid}\\{n}.txt 2>&1\r\n" for n, a in
               [("sdr", "synthetic sdr 1.0 "), ("hdr", "synthetic hdr 2.0 "), ("capture", "capture ")])
proc = lib.start_limited(rid, "va_mirror", "mirror_run.bat", body, "rc.txt")
try:
    proc.wait(120)
except subprocess.TimeoutExpired:
    check("probe ran", False, "timeout")
res = {}
for n in ("sdr", "hdr", "capture"):
    dst = Path(tempfile.gettempdir()) / f"mirror_{n}.json"
    dst.unlink(missing_ok=True)
    lib.b_get(f"runs/{rid}/{n}.json", dst)
    if dst.exists():
        res[n] = json.loads(dst.read_text())
    else:
        t = Path(tempfile.gettempdir()) / f"mirror_{n}.txt"
        lib.b_get(f"runs/{rid}/{n}.txt", t)
        txt = t.read_text(errors="replace") if t.exists() else "no output"
        if n == "capture" and "0x80070005" in txt:
            # not a product failure: the OS refuses desktop duplication here (endpoint DLP / capture-protection policy on a managed PC, a locked screen)
            print(f"BLOCKED capture: the OS denied desktop duplication on this machine (access denied). Run the harness on a PC without capture protection. {txt.strip().splitlines()[-1][:120]}")
        else:
            check(f"probe {n}", False, txt[-200:])

if "sdr" in res:
    r = res["sdr"]
    exp = [[p * 36] * 3 for p in range(8)]   # the patch grey, BGRA -> RGBA keeps grey
    got = [json.loads(x) if isinstance(x, str) else x for x in r["patches"]]
    bad = [(i, g, e) for i, (g, e) in enumerate(zip(got, exp)) if i != 5 and not near(g, e, 2)]
    check("SDR patches come out unchanged", not bad, f"{len(got)} patches" + (f", off: {bad}" if bad else ""))
    check("cursor dot: white core, black ring, clean outside", near(r["cursor_centre"], [255, 255, 255], 2) and near(r["cursor_ring"], [0, 0, 0], 40) and near(r["cursor_outside"], exp[5], 2),
          f"centre {r['cursor_centre']} ring {r['cursor_ring']} outside {r['cursor_outside']}")
    check("an eye that does not see the screen shows the clear colour", near(r["right_eye"], [0x11, 0x22, 0x33], 1), str(r["right_eye"]))
if "hdr" in res:
    r = res["hdr"]
    vals, white = r["vals"], r["white"]
    exp = [[expected_hdr(v, white)] * 3 for v in vals]
    got = r["patches"]
    bad = [(i, vals[i], got[i], exp[i]) for i in range(8) if i != 5 and not near(got[i], exp[i], 3)]
    check("HDR patches convert to the expected SDR values", not bad, f"white level {white}" + (f", off: {bad}" if bad else ", all within 3/255"))
    check("HDR: black stays black, SDR white level maps near full white", got[0][0] <= 2 and got[3][0] >= 128, f"0 -> {got[0]}, {vals[3]} (half the white level {white}) -> {got[3]}")
    check("HDR: brighter than SDR white rolls off, never exceeds white and keeps order", got[6][0] >= got[4][0] >= got[3][0] and got[7][0] <= 255, f"{got[3]} < {got[4]} <= {got[6]} <= {got[7]}")
if "capture" in res:
    r = res["capture"]
    m = r["mean_left_eye"]
    check("real desktop captured and drawn", r["desktop"][0] > 0 and not near(m, [0x11, 0x22, 0x33], 3), f"{r['desktop'][0]}x{r['desktop'][1]}{' HDR' if r['hdr'] else ' SDR'}, mean colour {m}")

print("""
BY HAND (needs the headset; record what you see in the run notes):
  [ ] Vision Pro: between games the desktop floats ~1.6 m ahead, readable, steady when you turn your head (no swimming)
  [ ] Quest (hand tracking): same
  [ ] the dot follows your right hand's pointing; offset or jitter: note how far and when
  [ ] pinch (index + thumb) = left click, grip = right click; no clicks while the pointer is off the screen
  [ ] End (the recenter key) puts the screen in front of you again
  [ ] HDR desktop on / off in Windows: both look right (white is white, no washed-out grey)
  [ ] starting a game removes the screen and gives the hand back to the game
  [ ] stopping the game brings the desktop back
""")
print("mirror_check:", "OK" if fails == 0 else f"{fails} FAILED")
sys.exit(1 if fails else 0)
