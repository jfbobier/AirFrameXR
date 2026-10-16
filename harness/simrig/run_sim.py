#!/usr/bin/env python3
"""Runs the simrig (host built with the cargo feature `simrig`, see docs/DEVELOPMENT.md) on the PC and judges the result.

    python3 harness/simrig/run_sim.py build                 # builds the simrig host on the PC (python3 harness/live.py build host --simrig does the same)
    python3 harness/simrig/run_sim.py run <scenario>[,<scenario>...] [--seconds 30] [--show 0] [--real-encode 0] [--seed 7] [--eye-w 1760] [--fg] [--env K=V]
    python3 harness/simrig/run_sim.py list

Frame generation (docs/FRAME_GENERATION.md): `run stutter,stutter_fg` compares a 50-70 fps app without and with the generator; the judge's "motion"
verdict (the moving marker) is the smoothness of in-scene motion at the display, the usual verdict the head-rotation path. The generated frames are
rendered and encoded for real whatever --real-encode says (the viewer must see them).

The probe (XR_PROBE_SCENE=dots, black sphere, white rectangles every 10 deg on the horizon) runs through the installed OpenXR runtime and the
real shim; the host runs the real pipeline against a synthetic client (constant rotation or still, timing from a recorded profile) and the
viewer writes dots.csv + meta.json; judge.py gives the verdict. Results: harness/results/<UTC>_simrig_<scenario>/.
The installed streamer must not be running; the PC's interactive session is used (windows appear there). Needs the PC of tools/pc.local."""
import json, os, subprocess, sys, time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import live

ROOT = live.ROOT
PROFILES = ROOT / "harness" / "simrig" / "profiles"
SIMEXE = os.environ.get("VISIONALVR_SIMEXE", "alvr_host_simrig.exe")   # which build to run (A/B: copies next to it)

# knobs are VISIONALVR_SIM_* variables (tools/host/src/simrig.rs); a scenario is a set of them
SCENARIOS = {
    "ideal":       {"note": "no timing imperfection at all, no prediction noise: the null case (must be clean, or the rig itself is wrong)"},
    "games":       {"PROFILE": "real_games_pred0.json", "note": "90 deg/s, timing of the real game sessions (prediction 0)"},
    "probe100":    {"PROFILE": "real_probe_pred100.json", "PREDICT_MS": 100, "note": "90 deg/s, timing and prediction noise of the real probe session (prediction 100)"},
    "still100":    {"PROFILE": "real_probe_pred100.json", "PREDICT_MS": 100, "MODE": "still", "note": "head still, prediction noise of pred 100 (the user's still-pose trembling)"},
    "still0":      {"PROFILE": "real_games_pred0.json", "MODE": "still", "note": "head still, no prediction"},
    "noreproject": {"PROFILE": "real_games_pred0.json", "REPROJECT": 0, "note": "the frames as they come: what the stream alone does"},
    "inject":      {"PROFILE": "real_games_pred0.json", "STAMP_ERROR": 0.2, "note": "20% of the frames shown with a neighbouring sample's pose: the judge must say TREMBLE"},
    # frame generation (docs/FRAME_GENERATION.md): a game stuck between 50 and 70 fps (frame time drawn per frame) on the 100 Hz display, the moving marker
    # (XR_PROBE_MOVING) measured by the judge's "motion" verdict; _fg = the same with the generator on (VISIONALVR_FRAME_GEN=1: half-rate pacing + interpolation)
    "stutter":     {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "note": "head still, app at 50-70 fps (jittery), moving marker: today's stream (expect motion JUDDER: repeated frames)"},
    "stutter_fg":  {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "note": "the same with frame generation (expect motion SMOOTH, head CLEAN)"},
    "stutter_turn": {"PROFILE": "real_games_pred0.json", "OMEGA_DPS": 45, "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "note": "head turning 45 deg/s, app at 50-70 fps: the head-rotation path (dots) with a stuttering app"},
    "stutter_turn_fg": {"PROFILE": "real_games_pred0.json", "OMEGA_DPS": 45, "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "note": "the same with frame generation (the generated frames must not make the head rotation tremble)"},
    "steady_fg":   {"MODE": "still", "PROBE:XR_PROBE_MOVING": "25:0.25", "ENV:VISIONALVR_FRAME_GEN": "1",
                    "note": "ideal timing, light app, frame generation on: the generator's null case (expect CLEAN + SMOOTH)"},
    "steady":      {"MODE": "still", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "note": "ideal timing, light app, generator off: the motion metric's reference (the rig's own empty boundaries, ~6 %, read as frozen periods)"},
    # game motion vectors (docs/MOTION_VECTORS.md): the probe stands in for the DLSS provider (XR_PROBE_MV=synthetic, known vector pattern), the shim's
    # tap copies them with every frame, the host attaches them to the generator's history and dumps the first ones (mv_*.ppm / .txt in the results)
    "steady_mv":   {"MODE": "still", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MV": "synthetic", "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_MV_DUMP": "{rd}", "ENV:VISIONALVR_FG_USE_MV": "0",
                    "note": "steady_fg + synthetic DLSS vectors from the probe: the tap's plumbing (check mv_*.ppm against the probe's formula, fg stats mv.frames)"},
    "stutter_mv":  {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "PROBE:XR_PROBE_MV": "truth", "note": "stutter_fg with the probe's exact vectors (XR_PROBE_MV=truth): synthesis from the game's vectors (compare with stutter_fg)"},
    "stutter_turn_mv": {"PROFILE": "real_games_pred0.json", "OMEGA_DPS": 45, "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "PROBE:XR_PROBE_MV": "truth", "note": "stutter_turn_fg with exact vectors: camera motion in the vectors must cancel against the rotation compensation"},
    # uncapped frame generation (docs/FRAME_GENERATION_UNCAPPED.md): the game paced at the display rate, every output interpolated at the fraction its
    # content time falls at between the two newest rendered frames; _unc = flow, _unc_mv = the probe's exact vectors
    "stutter_unc": {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "uncapped", "note": "stutter with uncapped generation (flow): compare with stutter_fg (half rate)"},
    "stutter_unc_mv": {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "uncapped", "PROBE:XR_PROBE_MV": "truth", "note": "uncapped generation from the probe's exact vectors"},
    "stutter_turn_unc_mv": {"PROFILE": "real_games_pred0.json", "OMEGA_DPS": 45, "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "uncapped", "PROBE:XR_PROBE_MV": "truth", "note": "uncapped generation, head turning, exact vectors"},
    "steady_unc":  {"MODE": "still", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall", "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "uncapped",
                    "note": "ideal timing, light app (at the display rate), uncapped generation: the mode's null case (t near 0/1 most of the time)"},
    # jittery input -> smooth output: a game around 75 fps whose frame time swings +-30 % from frame to frame (XR_PROBE_LOAD=jitter), uncapped generation;
    # the judge's step_cv (uniformity of the marker's steps per period) and hf are the smoothness, fg_perf's slack/behind the lag's adequacy
    "jitter_unc":  {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "jitter:30", "PROBE:XR_PROBE_CPU_MS": "13.3", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "uncapped", "note": "~75 fps app with +-30 % frame-time jitter, uncapped generation (flow)"},
    "jitter_unc_mv": {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "jitter:30", "PROBE:XR_PROBE_CPU_MS": "13.3", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "uncapped", "PROBE:XR_PROBE_MV": "truth", "note": "the same with the probe's exact vectors"},
    "stutter_fg_wall": {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:50:70", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "note": "stutter_fg with the marker on the wall clock (an engine's delta-time animation): the half-rate reference for the _unc scenarios"},
    "jitter":      {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "jitter:30", "PROBE:XR_PROBE_CPU_MS": "13.3", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "note": "the same jittery app, generation off: the reference (expect frozen periods and uneven steps)"},
    # the 1/3 rate (game paced at a third of the display rate, two generated frames at t = 1/3 and 2/3 per rendered frame) and the 2/3 rate (the
    # game held to two thirds of the display rate by the shim's minimum release spacing, the content-clock planner interpolates every output)
    "third":       {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:40:50", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "third", "note": "a 40-50 fps app at the 1/3 rate (33 fps budget it always meets): expect SMOOTH, few repeats"},
    "stutter_third": {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:30:45", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "third", "note": "a 30-45 fps app at the 1/3 rate: the budget is missed at times (repeats), compare with stutter_fg"},
    "third_slow":  {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:40:50", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.12", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "third", "note": "the 1/3 rate with the marker at half speed: the planner's test; at full speed the 5 px marker moves 14 px per 30 ms game step, past the flow's 8 px blocks (the thin-mover limit, not the planner)"},
    "third_mv":    {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:40:50", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "third", "PROBE:XR_PROBE_MV": "truth", "note": "the 1/3 rate from the probe's exact vectors"},
    "two_thirds":  {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "fps:80:100", "PROBE:XR_PROBE_CPU_MS": "1", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "two_thirds", "note": "an 80-100 fps app held to 2/3 of the display rate (67 fps at 100 Hz), every output interpolated"},
    "jitter_two_thirds": {"PROFILE": "real_games_pred0.json", "MODE": "still", "PROBE:XR_PROBE_LOAD": "jitter:30", "PROBE:XR_PROBE_CPU_MS": "13.3", "PROBE:XR_PROBE_MOVING": "25:0.25", "PROBE:XR_PROBE_MOVING_CLOCK": "wall",
                    "ENV:VISIONALVR_FRAME_GEN": "1", "ENV:VISIONALVR_FG_RATE": "two_thirds", "note": "the ~75 fps jittery app at the 2/3 rate: compare with jitter_unc"},
}


def host_cmd(rd, env, seconds):
    lines = ["@echo off"] + [f"set VISIONALVR_SIM_{k}={v}" for k, v in env.items() if not (k.startswith("ENV:") or k.startswith("PROBE:"))] \
        + [f"set {k[4:]}={str(v).replace('{rd}', rd)}" for k, v in env.items() if k.startswith("ENV:")] + [
        f'"{live.INSTALL}\\{SIMEXE}" --install-dir "{live.INSTALL}" --shim --simrig {rd}\\out --seconds {seconds + 90} --debug --connect-timeout 30 --report {rd}\\host_report.json > {rd}\\host_stdout.txt 2>&1']
    return lines


def task(name, lines, rd, elevated=False):
    live.SCRATCH.mkdir(parents=True, exist_ok=True)
    f = live.SCRATCH / f"{name}.cmd"
    f.write_bytes(("\r\n".join(lines) + "\r\n").encode())
    live.put(f, f"{rd.replace(chr(92), '/')}/{name}.cmd")
    live.ps(f"""
schtasks /delete /tn {name} /f 2>$null | Out-Null
schtasks /create /tn {name} /tr 'cmd /c {rd}\\{name}.cmd' /sc once /st 00:00 /rl {'HIGHEST' if elevated else 'LIMITED'} /it /f | Out-Null
schtasks /run /tn {name} | Out-Null
""")


def set_stream(eye, fps):
    """Encode smaller/slower samples on a weak encoder (the laptop 3080 Ti): per-eye size WxH and refresh rate, written into the install's
    session.json by text replacement (the pristine copy is kept as session.json.simrig-orig and restored with `--eye default`)."""
    sj = f"{live.INSTALL}\\config\\session.json"
    live.ps(f"if (-not (Test-Path '{sj}.simrig-orig')) {{ Copy-Item '{sj}' '{sj}.simrig-orig' }}")
    if eye == "default":
        live.ps(f"Copy-Item '{sj}.simrig-orig' '{sj}' -Force")
        return
    w, h = (int(x) for x in eye.split("x"))
    edits = [("eye_resolution_width", w), ("eye_resolution_height", h), ("target_eye_resolution_width", w), ("target_eye_resolution_height", h),
             ("refresh_rate", int(fps)), ("preferred_fps", f"{float(fps)}"), ("width", w), ("content", h)]
    body = "\n".join(f"$t = [regex]::Replace($t, '\"{k}\":\\s*[0-9.]+', '\"{k}\": {v}')" for k, v in edits[:6])
    ps = f"""
$p = '{sj}'
Copy-Item '{sj}.simrig-orig' $p -Force
$t = [IO.File]::ReadAllText($p)
{body}
[IO.File]::WriteAllText($p, $t, (New-Object Text.UTF8Encoding $false))
"""
    live.ps(ps)


def kill():
    live.ps(f"Stop-Process -Name xr_probe,{Path(SIMEXE).stem},alvr_host,VisionALVR -Force -ErrorAction SilentlyContinue; "
            "schtasks /delete /tn simrig_host /f 2>$null | Out-Null; schtasks /delete /tn simrig_probe /f 2>$null | Out-Null")
    time.sleep(2)


def build(copy_as=None):
    kill()   # a running probe or host would keep the exes locked (LNK1104 / copy failures)
    live.build("host", simrig=True)
    live.build("shim")
    live.build("probe")
    live.ps(f"""
$src = '{live.CLONE}\\src\\ALVR-v20.14.1\\target\\release\\alvr_host.exe'
Copy-Item $src '{live.INSTALL}\\{copy_as or SIMEXE}' -Force
Copy-Item '{live.CLONE}\\src\\VDXR\\bin-shim\\x64\\Release\\LibOVRRT64_1.dll' '{live.INSTALL}\\LibOVRRT64_1.dll' -Force
Get-Item '{live.INSTALL}\\{SIMEXE}' | Select-Object Name, Length, LastWriteTime | Out-String
""")


def run_one(name, seconds, extra):
    sc = dict(SCENARIOS[name])
    note = sc.pop("note", "")
    # REAL_ENCODE 1: the viewer reads the slot through the encoder's device, which only the real encode path opens (a timer-only encode shows black)
    env = {"SECONDS": seconds, "SEED": 1, "SHOW": 1, "REAL_ENCODE": 1}
    env.update(sc)
    env.update(extra)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    rd = rf"{live.INSTALL}\simrig\{stamp}_{name}"
    outdir = ROOT / "harness" / "results" / f"{stamp}_simrig_{name}"
    outdir.mkdir(parents=True, exist_ok=True)
    live.pc(f'if not exist "{rd}\\out" mkdir "{rd}\\out"')
    if "PROFILE" in env:
        live.put(PROFILES / env["PROFILE"], f"{rd.replace(chr(92), '/')}/profile.json")
        env["PROFILE"] = f"{rd}\\profile.json"
    live.log(f"simrig {name}: {note}")
    kill()
    ok = False
    try:
        task("simrig_host", host_cmd(rd, env, seconds), rd, elevated=True)
        time.sleep(8)
        probe_env = {"XR_PROBE_SCENE": "dots", "XR_PROBE_CPU_MS": "0", "XR_PROBE_LOAD": "steady", "XR_PROBE_MARKERS_CSV": f"{rd}\\app_markers.csv", "XR_PROBE_TIMING_CSV": f"{rd}\\app_timing.csv"}
        probe_env.update({k[6:]: v for k, v in env.items() if k.startswith("PROBE:")})   # scenario keys "PROBE:XR_PROBE_...": the app's load and scene
        probe = ["@echo off", f'set XR_RUNTIME_JSON={live.INSTALL}\\virtualdesktop-openxr.json'] + [f"set {k}={v}" for k, v in probe_env.items()] + [
                 f"cd /d {live.INSTALL}", f'"{live.CLONE}\\build\\probe\\xr_probe.exe" {rd}\\probe.log 200000 {seconds + 60} - - > {rd}\\probe_stdout.txt 2>&1']
        task("simrig_probe", probe, rd)
        t0 = time.time()
        while time.time() - t0 < seconds + 90:
            time.sleep(5)
            if live.ps(f"Test-Path '{rd}\\out\\viewer_summary.json'").strip() == "True":
                ok = True
                # CPU the streamer used (all threads, whole run) before it is stopped; its report is written when it exits by itself
                cpu = live.ps(f"$p = Get-Process {Path(SIMEXE).stem} -ErrorAction SilentlyContinue | Select -First 1; if ($p) {{ '{{\"cpu_s\": ' + [math]::Round($p.TotalProcessorTime.TotalSeconds,2) + ', \"wall_s\": ' + [math]::Round(((Get-Date) - $p.StartTime).TotalSeconds,2) + '}}' }}")
                (outdir / "cpu.json").write_text(cpu.strip() or "{}")
                for _ in range(8):
                    if live.ps(f"Test-Path '{rd}\\host_report.json'").strip() == "True":
                        break
                    time.sleep(2)
                break
    finally:
        kill()
    live.get_dir(rd.replace("\\", "/"), outdir)
    newest = live.ps(f"Get-ChildItem '{live.INSTALL}\\logs\\debug' -Directory | Sort Name | Select -Last 1 | ForEach-Object {{ $_.Name }}").strip()
    if newest:   # the streamer's and the shim's own traces of this run
        live.get_dir(f"{live.INSTALL}\\logs\\debug\\{newest}".replace("\\", "/"), outdir / "debug")
    if not ok:
        live.log("the viewer did not finish (see host_stdout.txt / probe.log in the results)")
        return 2
    r = subprocess.run([sys.executable, str(ROOT / "harness/simrig/judge.py"), str(outdir / "out")], capture_output=True, text=True)
    print(r.stdout + r.stderr)
    (outdir / "verdict.txt").write_text(r.stdout)
    return r.returncode


def main(a):
    if not a or a[0] == "list":
        for k, v in SCENARIOS.items():
            print(f"{k:12s} {v['note']}")
        return 0
    if a[0] == "build":
        build(a[1] if len(a) > 1 else None)
        return 0
    if a[0] == "run":
        names = a[1].split(",")
        extra = {}
        if "--eye" in a:   # --eye 1024x1024 --fps 60 | --eye default
            fps = a[a.index("--fps") + 1] if "--fps" in a else "90"
            set_stream(a[a.index("--eye") + 1], fps)
            extra["PERIOD_MS"] = f"{1000.0 / float(fps):.4f}"
        seconds = int(a[a.index("--seconds") + 1]) if "--seconds" in a else 30
        if "--fg" in a:   # frame generation on for every named scenario (the _fg scenarios carry it themselves)
            extra["ENV:VISIONALVR_FRAME_GEN"] = "1"
        for flag, key in (("--show", "SHOW"), ("--real-encode", "REAL_ENCODE"), ("--seed", "SEED"), ("--eye-w", "EYE_W"), ("--omega", "OMEGA_DPS"),
                          ("--predict-ms", "PREDICT_MS"), ("--dump-every", "DUMP_EVERY"), ("--capture", "CAPTURE")):
            if flag in a:
                extra[key] = a[a.index(flag) + 1]
        for e in [a[i + 1] for i, x in enumerate(a) if x == "--env"]:   # --env VISIONALVR_SPIN_US=1000: any variable for the streamer
            k, v = e.split("=", 1)
            extra["ENV:" + k] = v
        rc = 0
        for n in names:
            rc = max(rc, run_one(n, seconds, extra))
        return rc
    print(__doc__)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
