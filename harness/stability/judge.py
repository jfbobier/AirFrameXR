#!/usr/bin/env python3
"""Stability verdicts from a harness run's artifacts (frames.csv, client_stdout.txt, mem.csv, host_stdout.txt).

    python3 harness/stability/judge.py <results dir> [criteria.json]      # judge a finished run
    python3 harness/stability/judge.py --selftest                         # the judge on synthetic data (must pass: a healthy run passes, a stall / a leak fails)

Criteria (all optional): {"warmup_s": 15, "alive_at_end_s": 3, "max_stall_ms": 1500, "blackouts_resume": true, "min_fps_during_blackout": 20, "max_recovery_s": 2,
"mem": {"warmup_frac": 0.2, "min_duration_s": 120, "max_growth_mb_per_h": {"private": 200, "gpu": 200, "handles": 2000}, "max_gpu_mb": 8000,
"processes": ["alvr_host", "xr_probe"]}, "host_stalls_max": 0, "host_stalls_min": 1, "host_memory_events": 2}
Stdlib only."""
import csv, json, sys
from pathlib import Path


def events(path):
    out = []
    if Path(path).exists():
        for line in Path(path).read_text(errors="replace").splitlines():
            line = line.strip()
            if line.startswith("{"):
                try:
                    out.append(json.loads(line))
                except ValueError:
                    pass
    return out


def arrivals(path):
    """Frame arrival times in seconds on the client's clock."""
    out = []
    if Path(path).exists():
        with open(path, newline="") as f:
            for r in csv.DictReader(f):
                try:
                    out.append(int(r["arrival_ns"]) / 1e9)
                except (KeyError, ValueError):
                    pass
    out.sort()
    return out


def gaps(arr, t_from):
    """(start_s, gap_s) between consecutive arrivals after t_from."""
    a = [t for t in arr if t >= t_from]
    return [(a[i], a[i + 1] - a[i]) for i in range(len(a) - 1)]


def slope_per_hour(pts):
    """Least squares slope of (t_s, value) in units per hour."""
    n = len(pts)
    if n < 3:
        return 0.0
    mt = sum(t for t, _ in pts) / n
    mv = sum(v for _, v in pts) / n
    den = sum((t - mt) ** 2 for t, _ in pts)
    return 0.0 if den == 0 else 3600.0 * sum((t - mt) * (v - mv) for t, v in pts) / den


def read_mem(path):
    """{process name: {"private": [(t, mb)], "gpu": [...], "handles": [...]}}, and the system rows {"ram_free": [...], "vram_used": [...]}."""
    procs, sysrows = {}, {"ram_free": [], "vram_used": []}
    if not Path(path).exists():
        return procs, sysrows
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            try:
                t = float(r["t_s"])
                if r["kind"] == "sys":
                    sysrows["ram_free"].append((t, float(r["private_mb"])))
                    if r["gpu_mb"].strip():
                        sysrows["vram_used"].append((t, float(r["gpu_mb"])))
                    continue
                d = procs.setdefault(r["name"], {"private": [], "gpu": [], "handles": []})
                d["private"].append((t, float(r["private_mb"])))
                d["gpu"].append((t, float(r["gpu_mb"])))
                d["handles"].append((t, float(r["handles"])))
            except (KeyError, ValueError):
                pass
    return procs, sysrows


def judge(outdir, crit):
    out = []

    def chk(name, ok, value, expected):
        out.append({"name": name, "ok": bool(ok), "value": value, "expected": expected})

    outdir = Path(outdir)
    if crit.get("storm"):
        return judge_storm(outdir, crit, out)
    ev = events(outdir / "client_stdout.txt")
    arr = arrivals(outdir / "frames.csv")
    start_s = next((e["t_ms"] / 1000 for e in ev if e.get("event") == "streaming_started"), 0.0)
    chk("client_streamed", len(arr) > 100, len(arr), "> 100 frames received")
    if len(arr) < 3:
        return out
    # the app starts after the stream (green idle first): judge from the first frame plus the warmup
    warm = arr[0] + crit.get("warmup_s", 15)
    gp = gaps(arr, warm)
    big = [g for g in gp if g[1] > 1.0]
    if "max_stall_ms" in crit:
        worst = max(gp, key=lambda g: g[1]) if gp else (0, 0)
        chk("max_stall", worst[1] * 1000 <= crit["max_stall_ms"], f"{worst[1] * 1000:.0f} ms at +{worst[0] - arr[0]:.0f} s ({len(big)} gaps > 1 s)", f"<= {crit['max_stall_ms']} ms between frames")
    # soak: every stall must be explained by an injected disturbance (soak_plan.json: host / client freezes), and be over soon after it
    plan_f = outdir / "soak_plan.json"
    if crit.get("soak") and plan_f.exists():
        wins = [w for w in json.loads(plan_f.read_text())["windows"] if w["kind"] in ("host_freeze", "client_freeze")]
        rec = crit["soak"].get("recovery_s", 12)
        unexplained = []
        for st, g in gp:
            if g <= crit["soak"].get("min_stall_s", 1.5):
                continue
            rel = st - arr[0]
            if not any(w["t"] - 8 <= rel <= w["t"] + w["dur"] + 8 and g <= w["dur"] + rec for w in wins):
                unexplained.append(f"{g:.1f} s at +{rel:.0f} s")
        chk("soak_unexplained_stalls", not unexplained, "none" if not unexplained else "; ".join(unexplained[:5]), f"every stall over {crit['soak'].get('min_stall_s', 1.5)} s lies in an injected freeze (+ {rec} s recovery)")
    # the stream must be alive at the end of the run (a deadlock leaves the tail silent, which no gap between two frames shows)
    if "alive_at_end_s" in crit:
        end_s = max([e["t_ms"] / 1000 for e in ev if "t_ms" in e] + [arr[-1]])
        chk("alive_at_end", end_s - arr[-1] <= crit["alive_at_end_s"], f"last frame {end_s - arr[-1]:.1f} s before the end", f"<= {crit['alive_at_end_s']} s")
    # tracking blackouts injected by the mock client: the stream must keep flowing through them and the uplink must come back
    bo = [e for e in ev if e.get("event") == "tracking_blackout"]
    res = {e["data"]["blackout"]: e for e in ev if e.get("event") == "tracking_resumed"}
    run_end_s = max([e["t_ms"] / 1000 for e in ev if "t_ms" in e] + [arr[-1]])
    for e in bo:
        i = e["data"]["blackout"]
        r = res.get(i)
        # a reconnect restarts the mock client's schedule, so a blackout can begin shortly before the run ends: not judged when its planned end is past the end
        if r is None and e["t_ms"] / 1000 + e["data"]["dur_s"] > run_end_s:
            continue
        if crit.get("blackouts_resume", True):
            chk(f"blackout{i}_tracking_resumed", r is not None, (f"after {r['data']['silent_s']:.1f} s" if r else "never"), f"tracking resumes (planned {e['data']['dur_s']} s" + (", and only once a frame arrives)" if e["data"].get("until_frame") else ")"))
        if r is not None and "min_fps_during_blackout" in crit:
            fps = r["data"]["frames_during"] / max(r["data"]["silent_s"], 1e-3)
            chk(f"blackout{i}_frames_flow", fps >= crit["min_fps_during_blackout"], f"{fps:.1f} fps ({r['data']['frames_during']} frames in {r['data']['silent_s']:.1f} s)", f">= {crit['min_fps_during_blackout']} fps while tracking is silent")
        if r is not None and "max_recovery_s" in crit:
            over = r["data"]["silent_s"] - e["data"]["dur_s"]
            chk(f"blackout{i}_recovery", over <= crit["max_recovery_s"], f"{over:.1f} s past the planned end", f"<= {crit['max_recovery_s']} s")
    if crit.get("ballast_effective"):
        bl = outdir / "ballast.log"
        txt = bl.read_text(errors="replace") if bl.exists() else ""
        chk("ballast_took_its_share", bool(txt) and "(SHORT)" not in txt, "ok" if txt and "(SHORT)" not in txt else ("short of memory: " + ([l for l in txt.splitlines() if "SHORT" in l] or ["no ballast log"])[0][:140]), "the ballast held what it was asked (else the limit was not applied)")
    # the host's own stall events (watchdog.rs: "stall_detected" in its stdout events) and memory lines
    hev = events(outdir / "host_stdout.txt")
    stalls = [e for e in hev if e.get("event") == "stall_detected"]
    if "host_stalls_max" in crit:
        chk("host_reported_stalls", len(stalls) <= crit["host_stalls_max"], len(stalls), f"<= {crit['host_stalls_max']}")
    if "host_stalls_min" in crit:
        kinds = sorted({e["data"].get("kind") for e in stalls})
        chk("host_noticed_the_stall", len(stalls) >= crit["host_stalls_min"], f"{len(stalls)} ({', '.join(map(str, kinds))})", f">= {crit['host_stalls_min']} stall_detected events")
    # whenever the generator ran: it must not have failed (flow engine or output pass), whatever the scenario injects
    gens = [e["data"].get("gen") for e in hev if e.get("event") == "fg_perf" and isinstance(e["data"].get("gen"), dict)]
    g = next((x for x in reversed(gens) if x.get("on")), None)
    if g:
        errs = {k: g.get(k, 0) for k in ("flow_errors", "render_errors")}
        chk("generator_errors", not any(errs.values()), errs, "no flow or render errors")
    if crit.get("host_audio_events"):
        n = sum(1 for e in hev if e.get("event") == "audio_device" and (e.get("data") or {}).get("n", 0) > 0)
        chk("host_saw_audio_changes", n >= crit["host_audio_events"], n, f">= {crit['host_audio_events']} audio_device events after the start (the injected mute / volume / default changes)")
    if crit.get("host_memory_events"):
        n = sum(1 for e in hev if e.get("event") == "memory")
        chk("host_memory_events", n >= crit["host_memory_events"], n, f">= {crit['host_memory_events']} memory events (one every 30 s while connected)")
    m = crit.get("mem")
    if m:
        procs, sysrows = read_mem(outdir / "mem.csv")
        want = m.get("processes")
        for name, d in sorted(procs.items()):
            if want and name not in want:
                continue
            pts = d["private"]
            dur = pts[-1][0] - pts[0][0] if pts else 0
            if dur < m.get("min_duration_s", 120):
                continue
            cut = pts[0][0] + dur * m.get("warmup_frac", 0.2)
            for kind, lim in m.get("max_growth_mb_per_h", {}).items():
                series = [(t, v) for t, v in d[kind] if t >= cut]
                s = slope_per_hour(series)
                span = (series[-1][0] - series[0][0]) if len(series) > 1 else 0
                grown = s * span / 3600.0   # what the fit says was gained over the window: a short run's hourly slope is noise unless this is real memory
                floor = m.get("min_growth_mb", 25) if kind != "handles" else m.get("min_growth_handles", 300)
                chk(f"mem_{name}_{kind}_growth", s <= lim or grown < floor, f"{s:+.0f} per hour, {grown:+.0f} over {span:.0f} s (peak {max(v for _, v in d[kind]):.0f})", f"<= {lim} per hour (or under {floor} gained) after {int(m.get('warmup_frac', 0.2) * 100)} % warmup")
        if "max_gpu_mb" in m and sysrows["vram_used"]:
            peak = max(v for _, v in sysrows["vram_used"])
            chk("vram_peak", peak <= m["max_gpu_mb"], f"{peak:.0f} MB", f"<= {m['max_gpu_mb']} MB used on the GPU")
    return out


def judge_storm(outdir, crit, out):
    """Reconnect storm: every client session of the run must stream, quickly after it connects."""
    def chk(name, ok, value, expected):
        out.append({"name": name, "ok": bool(ok), "value": value, "expected": expected})
    sc = crit["storm"]
    sessions = sorted(outdir.glob("frames_s*.csv"), key=lambda p: int(p.stem.split("_s")[1]))
    chk("sessions_ran", len(sessions) >= sc.get("min_sessions", 1), len(sessions), f">= {sc.get('min_sessions', 1)} client sessions")
    bad = []
    worst_first = 0.0
    for p in sessions:
        i = int(p.stem.split("_s")[1])
        arr = arrivals(p)
        ev = events(outdir / f"client_s{i}_stdout.txt")
        started = next((e["t_ms"] / 1000 for e in ev if e.get("event") == "streaming_started"), None)
        if started is None or len(arr) < sc.get("min_frames", 100):
            bad.append(f"session {i}: {'never streamed' if started is None else str(len(arr)) + ' frames'}")
            continue
        first = arr[0] - started
        worst_first = max(worst_first, first)
        if first > sc.get("max_first_frame_s", 6):
            bad.append(f"session {i}: first frame {first:.1f} s after the stream started")
    chk("every_session_streams", not bad, "all " + str(len(sessions)) + " streamed" if not bad else "; ".join(bad[:4]), f"each session >= {sc.get('min_frames', 100)} frames, the first within {sc.get('max_first_frame_s', 6)} s of the stream starting")
    chk("worst_first_frame", worst_first <= sc.get("max_first_frame_s", 6), f"{worst_first:.1f} s", f"<= {sc.get('max_first_frame_s', 6)} s")
    hev = events(outdir / "host_stdout.txt")
    if "host_stalls_max" in crit:
        n = sum(1 for e in hev if e.get("event") == "stall_detected")
        chk("host_reported_stalls", n <= crit["host_stalls_max"], n, f"<= {crit['host_stalls_max']}")
    if crit.get("host_memory_events"):
        n = sum(1 for e in hev if e.get("event") == "memory")
        chk("host_memory_events", n >= crit["host_memory_events"], n, f">= {crit['host_memory_events']}")
    return out


def selftest():
    import tempfile
    ok = True
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)

        def make(stall, leak, resumes):
            (d / "frames.csv").write_text("ts_ns,arrival_ns\n" + "".join(f"{i},{int(t * 1e9)}\n" for i, t in enumerate(
                [x / 60 for x in range(60 * 90) if not (stall and 40 * 60 <= x < 55 * 60)])))
            lines = [json.dumps({"t_ms": 0, "event": "streaming_started", "data": {}}),
                     json.dumps({"t_ms": 30000, "event": "tracking_blackout", "data": {"blackout": 0, "dur_s": 5, "until_frame": True}})]
            if resumes:
                lines.append(json.dumps({"t_ms": 35000, "event": "tracking_resumed", "data": {"blackout": 0, "silent_s": 5.0, "frames_during": 300}}))
            (d / "client_stdout.txt").write_text("\n".join(lines))
            rows = ["t_s,kind,name,pid,private_mb,ws_mb,handles,gpu_mb"]
            for t in range(0, 300):
                rows.append(f"{t},proc,alvr_host,1,{1000 + (t * 2 if leak else 0)},0,500,{2000 + (t if leak else 0)}")
            rows.append(f"299,sys,ram_free_mb,0,8000,0,0,{9000 if leak else 6000}")
            (d / "mem.csv").write_text("\n".join(rows))
        crit = {"max_stall_ms": 1500, "min_fps_during_blackout": 20, "mem": {"min_duration_s": 100, "max_growth_mb_per_h": {"private": 200, "gpu": 200}, "max_gpu_mb": 8000}}
        for label, args, expect_fail in (("healthy", (False, False, True), set()), ("stall", (True, False, True), {"max_stall"}),
                                         ("leak", (False, True, True), {"mem_alvr_host_private_growth", "mem_alvr_host_gpu_growth", "vram_peak"}), ("deadlock", (False, False, False), {"blackout0_tracking_resumed"})):
            make(*args)
            failed = {c["name"] for c in judge(d, crit) if not c["ok"]}
            good = failed == expect_fail
            ok &= good
            print(f"  [{'PASS' if good else 'FAIL'}] {label}: failed checks {sorted(failed)}")
    print("selftest", "OK" if ok else "FAILED")
    return 0 if ok else 1


def main(argv):
    if len(argv) > 1 and argv[1] == "--selftest":
        return selftest()
    if len(argv) < 2:
        print(__doc__)
        return 2
    crit = json.load(open(argv[2])) if len(argv) > 2 else {}
    checks = judge(argv[1], crit)
    for c in checks:
        print(f"    [{'PASS' if c['ok'] else 'FAIL'}] {c['name']}: {c['value']}  (expected {c['expected']})")
    return 0 if checks and all(c["ok"] for c in checks) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
