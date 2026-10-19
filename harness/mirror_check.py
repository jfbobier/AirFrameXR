#!/usr/bin/env python3
"""Desktop mirror checks (docs/PLAN_0.5.md, phase 5). Everything that can be checked without a headset, across the desktops people have:

  1. input maths: tools/mirrordesktop/math_test.cpp (screen placement for every desktop shape, the lazy follow, per-eye projection, ray -> screen point, the beam)
  2. the picture on the builder's GPU (tools/probe/desktop_probe.cpp), for 7 desktop shapes x 4 display modes:
       desktops: 1080p, 1440p, 4K, 16:10, ultrawide 21:9, super ultrawide 32:9, portrait (names without ':' - not valid in a Windows file name)
       modes   : SDR, and HDR (scRGB) with Windows' SDR white level at 80, 200 and 480 nits
     per case: the 8 known patches come out right (SDR unchanged; HDR converted with the SDR white level as 1.0, a soft knee above, sRGB), the screen quad
     sits where the maths put it for that shape and stays inside the eye's view, the cursor dot has the same size to the eye at every resolution,
     outside the quad and in the eye that does not see the screen the frame is the clear colour
  3. the real desktop is captured (size, HDR or not) and drawn: BLOCKED, not FAIL, where the OS denies desktop duplication (capture protection, locked screen)
  4. what only a person can judge is the checklist at the end

    python3 harness/mirror_check.py          # all of it; exit 0 = every automatic check passed
"""
import json, math, subprocess, sys, tempfile, time
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


# the screen as desktop.rs places it: 1.5 m ahead, 45 degrees wide, at most 40 degrees tall, 5 cm below the eyes; the eye sees +-1.2 (tangent) each way
def quad_for(w, h):
    dist, fov, max_tall, tan = 1.5, 45.0, 40.0, 1.2
    width = 2 * dist * math.tan(math.radians(fov / 2))
    height = width * h / w
    max_h = 2 * dist * math.tan(math.radians(max_tall / 2))
    if height > max_h:
        height = max_h
        width = height * w / h
    hx, hy, cy = width / 2 / dist / tan, height / 2 / dist / tan, -0.05 / dist / tan
    return -hx, hx, cy + hy, cy - hy


# ---- 1. the maths (tools/mirrordesktop/mirror_math.h: placement, follow, projection, ray against the screen, the beam) --------------------------------
with tempfile.TemporaryDirectory() as t:
    exe = Path(t) / "math_test"
    cxx = next((c for c in ("g++", "clang++") if subprocess.run(["which", c], capture_output=True).returncode == 0), None)
    if cxx is None:
        check("input maths compile", False, "no C++ compiler (g++ / clang++) on this machine")
    else:
        p = subprocess.run([cxx, "-std=c++17", "-O1", "-o", str(exe), str(ROOT / "tools/mirrordesktop/math_test.cpp")], capture_output=True, text=True)
        if p.returncode:
            check("input maths compile", False, p.stderr[-300:])
        else:
            r = subprocess.run([str(exe)], capture_output=True, text=True)
            fails_ = [l for l in r.stdout.splitlines() if l.startswith("FAIL")]
            check("input maths (mirror_math.h tests)", r.returncode == 0, r.stdout.strip().splitlines()[-1] if r.stdout.strip() else "no output")
            for l in fails_:
                print("   ", l)

# ---- 2. the picture on the builder ---------------------------------------------------------------------------------------------
DESKTOPS = [("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160), ("16x10", 2560, 1600), ("21x9", 3440, 1440), ("32x9", 5120, 1440), ("portrait", 1080, 1920)]
MODES = [("SDR", None), ("HDR80", 1.0), ("HDR200", 2.5), ("HDR480", 6.0)]
rid = "mirror_" + time.strftime("%Y%m%d_%H%M%S")
for d in ("_tmp\\mirror", f"runs\\{rid}"):
    lib.b_run(f"if not exist {d} mkdir {d}")
for f in ("tools/probe/desktop_probe.cpp", "tools/mirrordesktop/desktop.cpp", "tools/mirrordesktop/desktop.h"):
    lib.b_put(ROOT / f, f"_tmp/mirror/{Path(f).name}")
rc, home = lib.b_run("echo %USERPROFILE%")
base = f"{home.strip().splitlines()[-1].strip()}\\{lib.ROOTNAME}"
bat = ("@echo off\r\ncall \"C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\VC\\Auxiliary\\Build\\vcvars64.bat\" >nul\r\n"
       f"cd /d {base}\\_tmp\\mirror\r\n"
       "cl /nologo /EHsc /MD /O2 /std:c++17 /I. desktop_probe.cpp /Fe:desktop_probe.exe /link d3d11.lib dxgi.lib d3dcompiler.lib user32.lib wtsapi32.lib\r\n")
lib.CACHE.mkdir(parents=True, exist_ok=True)
(lib.CACHE / "mirror_build.bat").write_text(bat)
lib.b_put(lib.CACHE / "mirror_build.bat", "_tmp/mirror/build.bat")
rc, out = lib.b_run("cmd /c _tmp\\mirror\\build.bat", timeout=300)
check("probe builds on the builder", "error" not in out.lower() and "fatal" not in out.lower(), out.strip().splitlines()[-1] if out.strip() else "")

body = ""
for dn, w, h in DESKTOPS:
    xl, xr, yt, yb = quad_for(w, h)
    for mn, white in MODES:
        kind = "sdr 1.0" if white is None else f"hdr {white}"
        body += f"_tmp\\mirror\\desktop_probe.exe synthetic {kind} runs\\{rid}\\{dn}_{mn}.json {w} {h} {xl:.5f} {xr:.5f} {yt:.5f} {yb:.5f} > runs\\{rid}\\{dn}_{mn}.txt 2>&1\r\n"
for dn, w, h in DESKTOPS:   # the cursor dot at 1 desktop pixel = 1 eye pixel (SDR: the dot does not depend on the colour path)
    body += f"_tmp\\mirror\\desktop_probe.exe synthetic sdr 1.0 runs\\{rid}\\{dn}_zoom.json {w} {h} 0 0 0 0 zoom > runs\\{rid}\\{dn}_zoom.txt 2>&1\r\n"
body += f"_tmp\\mirror\\desktop_probe.exe synthetic sdr 1.0 runs\\{rid}\\persp.json 1920 1080 0 0 0 0 persp > runs\\{rid}\\persp.txt 2>&1\r\n"   # a screen seen at an angle: not warped
body += f"_tmp\\mirror\\desktop_probe.exe synthetic sdr 1.0 runs\\{rid}\\beam.json 1920 1080 -0.5 0.5 0.4 -0.4 beam > runs\\{rid}\\beam.txt 2>&1\r\n"   # the pointing beam
body += f"_tmp\\mirror\\desktop_probe.exe capture runs\\{rid}\\capture.json > runs\\{rid}\\capture.txt 2>&1\r\n"
proc = lib.start_limited(rid, "va_mirror", "mirror_run.bat", body, "rc.txt")
try:
    proc.wait(1500)
except subprocess.TimeoutExpired:
    check("probe ran", False, "timeout")
loc = Path(tempfile.mkdtemp(prefix="mirror_"))
lib.b_get(f"runs/{rid}", loc)
res_dir = loc / rid

table = {}
for dn, w, h in DESKTOPS:
    xl, xr, yt, yb = quad_for(w, h)
    for mn, white in MODES:
        f = res_dir / f"{dn}_{mn}.json"
        if not f.exists():
            t = res_dir / f"{dn}_{mn}.txt"
            check(f"{dn} {mn}", False, t.read_text(errors="replace")[-160:] if t.exists() else "no output")
            table[(dn, mn)] = "FAIL"
            continue
        r = json.loads(f.read_text())
        bad = []
        # the screen inside the eye's view
        if not (-1 <= xl and xr <= 1 and -1 <= yb and yt <= 1):
            bad.append(f"the screen leaves the view: x [{xl:.2f}, {xr:.2f}] y [{yb:.2f}, {yt:.2f}]")
        # patches
        vals = r["vals"]
        if white is None:
            exp = [[p * 36] * 3 for p in range(8)]
        else:
            exp = [[expected_hdr(v, white)] * 3 for v in vals]
        for i in range(8):
            if i != 5 and not near(r["patches"][i], exp[i], 3):
                bad.append(f"patch {i}: {r['patches'][i]} expected {exp[i]}")
        # the cursor dot at 1 desktop pixel = 1 eye pixel (SDR only): white core, black ring, then the patch again, at a size that follows the desktop's height
        if mn == "SDR":
            z = res_dir / f"{dn}_zoom.json"
            if not z.exists():
                bad.append("no cursor zoom output")
            else:
                zr = json.loads(z.read_text())
                if not near(zr["cursor_core"], [255, 255, 255], 8): bad.append(f"cursor core {zr['cursor_core']}")
                if not near(zr["cursor_ring"], [0, 0, 0], 40): bad.append(f"cursor ring {zr['cursor_ring']}")
                if not near(zr["cursor_outside"], exp[5], 6): bad.append(f"cursor outside {zr['cursor_outside']} expected {exp[5]}")
                want_scale = max(1.0, h / 1080)
                if abs(zr["cursor_scale"] - want_scale) > 0.01: bad.append(f"cursor scale {zr['cursor_scale']} expected {want_scale:.2f}")
        # clear colour where nothing is drawn
        if not near(r["right_eye"], [0x11, 0x22, 0x33], 1):
            bad.append(f"right eye {r['right_eye']}")
        if r.get("outside_quad") and not near(r["outside_quad"], [0x11, 0x22, 0x33], 1):
            bad.append(f"outside the quad {r['outside_quad']}")
        table[(dn, mn)] = "FAIL" if bad else "ok"
        if bad:
            check(f"{dn} {w}x{h} {mn}", False, "; ".join(bad))
print()
print(f"{'':10}" + "".join(m.rjust(9) for m, _ in MODES))
for dn, w, h in DESKTOPS:
    print(f"{dn:10}" + "".join(table.get((dn, m), "-").rjust(9) for m, _ in MODES) + f"   {w}x{h}")
n_ok = sum(1 for v in table.values() if v == "ok")
check(f"picture: {len(DESKTOPS)} desktop shapes x {len(MODES)} display modes", n_ok == len(table) and len(table) == len(DESKTOPS) * len(MODES), f"{n_ok}/{len(DESKTOPS) * len(MODES)} cases pass")

# ---- 2b. no warping, and the pointing beam ------------------------------------------------------------------------------------
pj = res_dir / "persp.json"
if pj.exists():
    r = json.loads(pj.read_text())
    bad = [f"patch {i}: {r['patches'][i]} expected {[i * 36] * 3}" for i in range(8) if i != 5 and not near(r["patches"][i], [i * 36] * 3, 3)]
    check("a screen turned 40 degrees is not warped (patches found where a pinhole projection puts them)", not bad, "; ".join(bad) or "8 patches in place")
else:
    check("a screen turned 40 degrees is not warped", False, (res_dir / "persp.txt").read_text(errors="replace")[-160:] if (res_dir / "persp.txt").exists() else "no output")
bj = res_dir / "beam.json"
if bj.exists():
    r = json.loads(bj.read_text())
    clear = [0x11, 0x22, 0x33]
    bad = []
    if not near(r["beam_mid"], [255, 89, 26], 6): bad.append(f"beam over the clear colour {r['beam_mid']}")
    if not near(r["beam_beside"], clear, 1): bad.append(f"beside the beam {r['beam_beside']} (not the clear colour)")
    if not near(r["beam_end"], [255, 89, 26], 6): bad.append(f"beam over the screen {r['beam_end']}")
    if not near(r["beam_right_eye"], clear, 1): bad.append(f"the other eye {r['beam_right_eye']} (no beam there)")
    check("the pointing beam is drawn in the eye, over the clear colour and over the screen, and not in the eye without it", not bad, "; ".join(bad) or "ok")
else:
    check("the pointing beam", False, (res_dir / "beam.txt").read_text(errors="replace")[-160:] if (res_dir / "beam.txt").exists() else "no output")

# ---- 3. the real desktop --------------------------------------------------------------------------------------------------------
cap = res_dir / "capture.json"
if cap.exists():
    r = json.loads(cap.read_text())
    m = r["mean_left_eye"]
    check("real desktop captured and drawn", r["desktop"][0] > 0 and not near(m, [0x11, 0x22, 0x33], 3), f"{r['desktop'][0]}x{r['desktop'][1]}{' HDR' if r['hdr'] else ' SDR'}, mean colour {m}")
else:
    txt = (res_dir / "capture.txt").read_text(errors="replace") if (res_dir / "capture.txt").exists() else "no output"
    if "0x80070005" in txt:
        print("BLOCKED capture: the OS denied desktop duplication on this machine (access denied: capture protection, a locked screen). Run the harness on a PC without it.")
    else:
        check("real desktop captured and drawn", False, txt[-200:])

print("""
BY HAND (needs the headset; record what you see in the run notes):
  [ ] Vision Pro: between games the desktop floats ~1.5 m ahead on the green passthrough background, readable; small head movements leave it alone, turning away makes it glide back in front of you
  [ ] Quest (hand tracking): same
  [ ] the dot follows your right hand's pointing; offset or jitter: note how far and when
  [ ] pinch (index + thumb) = left click, grip = right click; no clicks while the pointer is off the screen
  [ ] End (the recenter key) puts the screen in front of you again
  [ ] a 4K desktop: text readable in the headset (it is shown scaled to ~45 degrees); a portrait or ultrawide monitor looks right (not stretched)
  [ ] HDR desktop on / off in Windows, and its SDR brightness slider moved: white stays white, no washed-out grey
  [ ] more than one monitor: the primary one is mirrored and the pointer lands on it
  [ ] starting a game removes the screen and gives the hand back to the game
  [ ] stopping the game brings the desktop back
""")
print("mirror_check:", "OK" if fails == 0 else f"{fails} FAILED")
sys.exit(1 if fails else 0)
