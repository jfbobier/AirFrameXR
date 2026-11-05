#!/usr/bin/env python3
"""The Swift headset (harness/swift_headset/README.md) without a streamer: it builds, its view declaration (the client's own
Drawable.swift + Renderer code) declares Apple's frustum unchanged under the standard projection layout, and a run with no streamer
starts the real client_core and EventHandler, ticks the render loop at the display rate and ends with a report in the Rust mock's format.

    python3 harness/swift_headset_check.py [--ref airframexr-client] [--no-build]

The declaration check is EXPECTED TO FAIL on clients up to 1.3.2 (2026-11-05): _extractFrustumTangents returns (left, right, bottom, top)
and Renderer.swift declares the third value as `up`, so with a standard matrix the box goes out upside down (up 1.0 / down 1.192 for
Apple's 1.192 / 1.0). Whether Apple's matrix is standard is the open question: one raw computeProjection line from a headset decides.
"""
import json, subprocess, sys, tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BIN = ROOT / "harness" / "cache" / "swift_headset" / "swift_headset"


def check(ok, what):
    print("CHECK", "PASS" if ok else "FAIL", what)
    return ok


def main(argv):
    ref = argv[argv.index("--ref") + 1] if "--ref" in argv else "airframexr-client"
    results = []
    if "--no-build" not in argv:
        r = subprocess.run(["bash", str(ROOT / "harness" / "swift_headset" / "build.sh"), "--ref", ref], capture_output=True, text=True)
        if not check(r.returncode == 0 and BIN.exists(), f"the Swift headset builds from {ref} with the real client_core"):
            print(r.stdout[-1500:], r.stderr[-1500:])
            return 1
    # 1. the view declaration, both projection conventions
    r = subprocess.run([str(BIN), "--declare-selftest"], capture_output=True, text=True, timeout=30)
    rows = [json.loads(l) for l in r.stdout.splitlines() if l.startswith("{")]
    std = next((x for x in rows if x["projection"].startswith("standard")), None)
    flp = next((x for x in rows if x["projection"] == "y flipped"), None)
    results.append(check(bool(std) and std["declared_equals_frustum"],
                         f"the client declares Apple's frustum unchanged (standard projection): frustum {std and std['frustum_left_oiud']} -> declared "
                         f"{std and std['declared_left_oiud']}" + (" (UP AND DOWN SWAPPED)" if std and std["up_down_swapped"] else "")))
    print("INFO", f"with a y-flipped projection the client declares {flp and flp['declared_left_oiud']}")
    # 2. a run with no streamer: the real core and the client's threads start, the render loop ticks, the report is written
    d = Path(tempfile.mkdtemp(prefix="swifthead_"))
    r = subprocess.run([str(BIN), "--report", str(d / "r.json"), "--frames-csv", str(d / "f.csv"), "--poses-csv", str(d / "p.csv"), "--out", str(d / "c.hevc"),
                        "--connect-timeout", "4", "--seconds", "3", "--real-views"], capture_output=True, text=True, timeout=60)
    rep = json.loads((d / "r.json").read_text()) if (d / "r.json").exists() else {}
    log = (d / "r.json.log").read_text(errors="replace") if (d / "r.json.log").exists() else ""
    disp = rep.get("swift", {}).get("display", {})
    results.append(check(rep.get("end_reason") == "connect_timeout" and rep.get("connected") is False and r.returncode == 1,
                         f"no streamer: ends at the connect timeout with a report (end {rep.get('end_reason')}, exit {r.returncode})"))
    results.append(check("Initialize ALVR" in log and "Start event thread" in log and "hud message updated" in log,
                         "the client's EventHandler initialised the real client_core and received its first HUD message"))
    results.append(check(330 <= disp.get("ticks", 0) <= 380, f"the render loop ticked at 90 Hz ({disp.get('ticks')} ticks in about 4 s)"))
    results.append(check(rep.get("swift", {}).get("ignored_flags") == ["--real-views"], "a Rust mock flag it does not implement is reported, not fatal"))
    keys = {"pass", "connected", "codec", "negotiated_config", "frames", "interarrival_ms", "frames_with_matching_sent_pose", "views_sent_at_ms"}
    results.append(check(keys <= set(rep), "the report carries the Rust mock's fields the e2e checks read"))
    print("INFO", f"run folder {d}")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
