#!/usr/bin/env python3
"""The golden master and the compiled session (tools/gui/PipelineModel.cs SessionCompiler, docs/ARCHITECTURE.md "Settings: golden master and compiled session").

config\\session.base.json is the only session file the windows edit; config\\session.json is compiled from it and the settings at every streamer start. Builds uitest.exe on the builder,
lays out one config\\ folder per case next to a copy of it, runs --session-compile-selftest, and checks with an independent calculation:

  fit_restriction    stream fit on, the headset declares a smaller box: session.json gets the fit size, session.base.json is byte for byte unchanged (the restriction never changes the resolution)
  fit_off            fit off, size typed (manual): session.json keeps the golden size
  hand_edit_lost     a key edited by hand in session.json is gone after the compile
  pairing_taken      ALVR trusted another headset while running (live newer than the master): the master takes it, as Disconnected
  pairing_kept       a headset paired in a window after that (master newer than live): the live file's old pairing does not overwrite it
  migrate            no master yet (an older install): created from session.json with the size the user chose (stream_reference_eye) put back, not the fit
  idempotent         a second compile changes nothing in the master

    python3 harness/session_compile_check.py
"""
import json, math, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

ROOT = lib.ROOT
DEFAULT = json.loads((ROOT / "tools/install/session.default.json").read_text())
REF_FOV = [0.94, 0.93, 0.90, 0.76]
DECLARED = [0.97, 0.71, 0.69, 0.63]   # the AirFrameXR client declaring the calibrated window
V = "session_settings.video."


def setp(d, path, v):
    ks = path.split(".")
    for k in ks[:-1]:
        d = d.setdefault(k, {})
    d[ks[-1]] = v


def getp(d, path, default=None):
    for k in path.split("."):
        if not isinstance(d, dict) or k not in d:
            return default
        d = d[k]
    return d


def golden(w, h, paired="hs-a"):
    s = json.loads(json.dumps(DEFAULT))
    for k in ("transcoding_view_resolution", "emulated_headset_view_resolution"):
        setp(s, V + k, {"variant": "Absolute", "Absolute": {"width": w, "height": {"set": True, "content": h}}})
    setp(s, V + "bitrate.mode", {"variant": "ConstantMbps", "ConstantMbps": 300})
    s["client_connections"] = {paired: {"display_name": "Apple Headset", "current_ip": "192.168.1.50", "manual_ips": ["192.168.1.50"], "trusted": True, "connection_state": "Disconnected"}}
    return s


def settings(fit=True, manual=True):
    v = {"stream_reference_eye": [3552.0, 3200.0], "stream_reference_fov": REF_FOV}
    if fit:
        v["stream_fit_window"] = True
    if manual:
        v["stream_size_manual"] = True
    return {"headset": {"name": "Apple Headset"}, "video": v}


def align32(x):
    return int(math.floor(x + 1e-6)) // 32 * 32


def fit_eye(ref, rf, dec):
    return [align32(ref[0] * (dec[0] + dec[1]) / (rf[0] + rf[1])), align32(ref[1] * (dec[2] + dec[3]) / (rf[2] + rf[3]))]


def case(name, base, st, live=None, live_newer=True, views=DECLARED, no_base=False):
    return dict(name=name, base=base, st=st, live=live, live_newer=live_newer, views=views, no_base=no_base)


def main():
    cases = []
    cases.append(case("fit_restriction", golden(3552, 3200), settings(True)))
    cases.append(case("fit_off", golden(3552, 3200), settings(False, True)))
    junk = golden(3552, 3200); junk["hand_edit_marker"] = 1
    cases.append(case("hand_edit_lost", golden(3552, 3200), settings(False, True), live=junk, live_newer=False))
    other = golden(3552, 3200, "hs-b")
    cases.append(case("pairing_taken", golden(3552, 3200, "hs-a"), settings(False, True), live=other, live_newer=True))
    cases.append(case("pairing_kept", golden(3552, 3200, "hs-a"), settings(False, True), live=other, live_newer=False))
    # an older install: its session.json holds the fit size (3168 x 2496), the settings remember the size the user chose
    cases.append(case("migrate", None, settings(False, True), live=golden(3168, 2496), no_base=True))
    cases.append(case("idempotent", golden(3552, 3200), settings(True)))
    rid = f"sc_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10])); return 2
    loc = Path(tempfile.mkdtemp(prefix="sc_"))
    for c in cases:
        d = loc / c["name"]
        (d / "config").mkdir(parents=True)
        (d / "bin").mkdir()
        (d / "config" / "airframexr.json").write_text(json.dumps(c["st"], indent=1))
        (d / "config" / "airframexr_views.json").write_text(json.dumps({"fov_tan": [c["views"][0], c["views"][1], c["views"][2], c["views"][3]] * 2}))
        files = []
        if c["base"] is not None:
            (d / "config" / "session.base.json").write_text(json.dumps(c["base"], indent=1)); files.append("session.base.json")
        if c["live"] is not None:
            (d / "config" / "session.json").write_text(json.dumps(c["live"], indent=1)); files.append("session.json")
        files = ["session.base.json", "session.json"] if c["live_newer"] else ["session.json", "session.base.json"]   # the last one copied is the newest
        files = [f for f in files if (d / "config" / f).exists()]
        lib.b_run(f"if exist runs\\{rid}\\{c['name']} rmdir /s /q runs\\{rid}\\{c['name']}")
        lib.b_run(f"mkdir runs\\{rid}\\{c['name']}\\config runs\\{rid}\\{c['name']}\\bin")
        for f in ("airframexr.json", "airframexr_views.json"):
            lib.b_put(d / "config" / f, f"runs/{rid}/{c['name']}/config/{f}")
        # the order of the copies is the order of the modification times: the one that must be newer goes last
        for f in files:
            time.sleep(2.2)
            lib.b_put(d / "config" / f, f"runs/{rid}/{c['name']}/config/{f}")
        lib.b_run(f"copy /y _tmp\\uitest\\run\\uitest.exe runs\\{rid}\\{c['name']}\\bin\\uitest.exe")
    fails = []
    for c in cases:
        n = c["name"]; d = loc / n
        rel = f"runs\\{rid}\\{n}"
        lib.b_run(f"{rel}\\bin\\uitest.exe --session-compile-selftest --out {rel}")
        for f in ("session.json", "session.base.json"):
            lib.b_get(f"runs/{rid}/{n}/config/{f}", d / ("out_" + f))
        if n == "idempotent":
            before = (d / "out_session.base.json").read_bytes()
            lib.b_run(f"{rel}\\bin\\uitest.exe --session-compile-selftest --out {rel}")
            lib.b_get(f"runs/{rid}/{n}/config/session.base.json", d / "again_base.json")
            if (d / "again_base.json").read_bytes() != before:
                fails.append((n, "the master changed on the second compile"))
        out = json.loads((d / "out_session.json").read_text())
        base = json.loads((d / "out_session.base.json").read_text())
        eye = lambda s: [getp(s, V + "transcoding_view_resolution.Absolute.width"), getp(s, V + "transcoding_view_resolution.Absolute.height.content")]
        cfg = c["st"]["video"]
        if n in ("fit_restriction", "idempotent"):
            want = fit_eye([3552, 3200], REF_FOV, DECLARED)
            if eye(out) != want: fails.append((n, f"session.json eye {eye(out)} != fit {want}"))
            if eye(base) != [3552, 3200]: fails.append((n, f"golden resolution changed: {eye(base)}"))
            if base != c["base"]: fails.append((n, "the master differs from what the case wrote"))
            if [out.get("openvr_config", {}).get("eye_resolution_width"), out.get("openvr_config", {}).get("eye_resolution_height")] != want: fails.append((n, "openvr_config size is not the compiled size"))
        elif n == "fit_off":
            if eye(out) != [3552, 3200]: fails.append((n, f"typed size not kept: {eye(out)}"))
            if base != c["base"]: fails.append((n, "the master changed"))
        elif n == "hand_edit_lost":
            if "hand_edit_marker" in out: fails.append((n, "a hand edit survived in session.json"))
            if "hand_edit_marker" in base: fails.append((n, "a hand edit reached the master"))
        elif n == "pairing_taken":
            if "hs-b" not in base.get("client_connections", {}) or "hs-b" not in out.get("client_connections", {}): fails.append((n, "the new pairing was not taken"))
            if getp(base, "client_connections.hs-b.connection_state") != "Disconnected": fails.append((n, "the state was not reset"))
        elif n == "pairing_kept":
            if list(base.get("client_connections", {})) != ["hs-a"] or list(out.get("client_connections", {})) != ["hs-a"]: fails.append((n, "the older live pairing overwrote the master"))
        elif n == "migrate":
            if eye(base) != [3552, 3200]: fails.append((n, f"the master has {eye(base)}, not the chosen 3552 x 3200"))
            if eye(out) != [3552, 3200]: fails.append((n, f"session.json has {eye(out)}"))
        print(f"  {n:16s} session.json {eye(out)}  master {eye(base)}")
    for f in fails:
        print("  MISMATCH", f[0], f[1])
    print("session_compile_check:", "FAIL" if fails else "PASS")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
