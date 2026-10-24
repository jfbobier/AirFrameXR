#!/usr/bin/env python3
"""The streamer window against scripted host events (docs/PLAN_0.5.md, UI round 2). For each fixture in harness/fixtures/streamer_events/*.jsonl (host
events in the host's own {"event","data"} format, taken from real host_stdout.txt files or written for a case; {"dump":"label"} lines snapshot the window),
`uitest.exe --replay` builds the streamer, feeds the events to RuntimeForm.HostEvent (the function the live host drives) and writes the text of every
visible control per snapshot. The assertions below are on that text. Exit 0 = every check passes.

    python3 harness/streamer_events.py [fixture ...]
"""
import re, subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

ROOT = lib.ROOT
FIX = ROOT / "harness/fixtures/streamer_events"

# fixture -> [(snapshot label, [text that must be shown], [text that must not be shown])]
EXPECT = {
    "game90": [
        ("idle", [], ["Game: LiesOfP.exe"]),
        ("live", ["Game: LiesOfP.exe", "44.8 fps", "Streaming (live"], ["Streaming: last session"]),
        ("ended", ["Game: none (last: LiesOfP.exe, 1.5 min)"], []),
        ("after_run", ["last session, LiesOfP.exe, 1.5 min, Apple Vision Pro", "44.8 fps", "57 ms"], []),
        ("idle_status_after", ["last session, LiesOfP.exe", "44.8 fps"], ["Streaming (live"]),   # a status with no game never replaces the last session's numbers
    ],
    "absurd_latency": [
        ("live_absurd", ["n/a", "not measured"], ["4958802102", "137"]),
        ("run_absurd", ["n/a", "not measured"], ["4958802102", "29752812252"]),
        ("run_clean", ["57 ms", "p95 70"], ["n/a"]),   # 2 outliers of 800 samples: still believable
    ],
    "controls_avp": [
        ("avp_auto", ["headset reports 1.62 m", "Eye height", "Debug logging", "Export logs...", "Open logs folder", "Frame generation", "Position", "Picture", "Desktop mirror"], ["Between games"]),
        ("frame_gen_on", ["sent frame_gen on"], []),
        ("rate", ["sent frame_gen_rate two_thirds"], []),
        ("uncapped_on", ["sent frame_gen_rate uncapped", "check[x] Uncapped"], []),
        ("uncapped_off", ["sent frame_gen_rate two_thirds", "check[ ] Uncapped"], []),   # back to the slider's position (2/3, set above)
        ("recenter", ["sent recenter"], []),
        ("manual", ["sent height 1.30"], ["headset reports"]),
        ("height", ["sent height 1.55"], []),
        ("auto_again", ["sent height 0.00", "headset reports 1.62 m"], []),
        ("gamma", ["sent gamma 1.50"], []),
        ("sharpening", ["sent color 0.00 0.00 0.00 0.40"], []),
        ("color", ["sent color 0.00 0.00 0.10 0.40"], []),
        ("debug_on", ["sent debug on", "check[x] Debug logging", "button Export logs..."], []),   # the export row is there with debug on
        ("debug_off", ["sent debug off", "check[ ] Debug logging", "button Export logs...", "button Open logs folder"], []),   # and off
        ("end", ["range height 0.5 2.2"], []),
    ],
    "controls_quest": [
        ("quest", ["Meta Quest 3"], ["headset reports"]),
        ("quest_manual", ["sent height 0.00"], []),   # a Quest's floor is right: whatever the choice, the engine is told 0
    ],
    "audio_line": [
        ("quiet", [], ["Audio:"]),   # nothing reported, nothing said
        ("forced", ["Audio: CABLE Input (VB-Audio Virtual Cable) was at 40 %, set back to 100 %"], []),
        ("failed", ["Audio: CABLE Input (VB-Audio Virtual Cable) could not be set to 100 %"], []),
        ("channels", ["has 16 channels: ALVR cannot capture more than 8"], []),
    ],
    "window_size": [],
    "short12": [
        ("ended", [], []),
        ("after_short_run", ["last session, Short.exe, 0.2 min", "60.0 fps", "50 ms"], ["Streaming (live"]),   # a run under 20 s still updates the tiles
    ],
    "mirror_between": [
        ("mirror_before_game", ["Game: none"], ["mirrordesktop", "90.0 fps"]),
        ("after_game", ["last session, Cyberpunk2077.exe, 5.0 min", "44.8 fps"], ["mirrordesktop"]),
        ("mirror_after_game", ["last session, Cyberpunk2077.exe, 5.0 min", "44.8 fps", "Game: none (last: Cyberpunk2077.exe"], ["mirrordesktop", "90.0 fps"]),
    ],
}


def sections(text):
    out, cur = {}, None
    for line in text.splitlines():
        if line.startswith("== "):
            cur = line[3:]; out[cur] = []
        elif cur is not None:
            out[cur].append(line.strip())
    return {k: "\n".join(v) for k, v in out.items()}


def main():
    only = sys.argv[1:] or sorted(EXPECT)
    rid = "streamer_events_" + time.strftime("%Y%m%d_%H%M%S")
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10])); return 2
    rc, home = lib.b_run("echo %USERPROFILE%")
    base = f"{home.strip().splitlines()[-1].strip()}\\{lib.ROOTNAME}"
    body = ""
    for name in only:
        lib.b_put(FIX / f"{name}.jsonl", f"runs/{rid}/{name}.jsonl")
        body += f"_tmp\\uitest\\run\\uitest.exe --replay runs\\{rid}\\{name}.jsonl --out runs\\{rid}\\{name} >> runs\\{rid}\\uitest.txt 2>&1\r\n"
        lib.b_run(f"if not exist runs\\{rid}\\{name} mkdir runs\\{rid}\\{name}")
    proc = lib.start_limited(rid, "va_streamer_events", "events.bat", body, "rc.txt")
    try:
        proc.wait(300)
    except subprocess.TimeoutExpired:
        print("BLOCKED: uitest.exe --replay did not finish in 5 minutes"); return 2
    dest = ROOT / "harness/results/streamer_events" / time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    fails = 0
    for name in only:
        lib.b_get(f"runs/{rid}/{name}", dest / name)
        txt = dest / name / "replay.txt"
        if not txt.exists():
            print(f"CHECK FAIL {name}: no replay.txt"); fails += 1; continue
        snaps = sections(txt.read_text(errors="replace"))
        if name == "window_size":   # the window is fixed: connect, play, notices, disconnect never change its size; only Advanced does, and closing it restores it
            sizes = {k: re.search(r"clientsize (\d+x\d+)", v).group(1) for k, v in snaps.items() if re.search(r"clientsize (\d+x\d+)", v)}
            fixed = {k: v for k, v in sizes.items() if k not in ("advanced_open",)}
            ok = len(set(fixed.values())) == 1
            print(f"CHECK {'PASS' if ok else 'FAIL'} window_size/fixed: {sorted(set(fixed.values()))}")
            fails += not ok
            ok2 = sizes.get("advanced_open") != sizes.get("start") and sizes.get("advanced_closed") == sizes.get("start")
            print(f"CHECK {'PASS' if ok2 else 'FAIL'} window_size/advanced: open {sizes.get('advanced_open')}, closed {sizes.get('advanced_closed')}, start {sizes.get('start')}")
            fails += not ok2
        for label, must, mustnt in EXPECT[name]:
            s = snaps.get(label)
            if s is None:
                print(f"CHECK FAIL {name}/{label}: no such snapshot"); fails += 1; continue
            bad = [m for m in must if m not in s] + [f"(must not) {m}" for m in mustnt if m in s]
            print(f"CHECK {'FAIL' if bad else 'PASS'} {name}/{label}" + (": " + "; ".join(bad) if bad else ""))
            fails += bool(bad)
    print(f"streamer_events: {fails} failing check(s); dumps in {dest}")
    return 1 if fails else 0


sys.exit(main())
