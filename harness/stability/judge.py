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
    # the stream must be alive at the end of the run (a deadlock leaves the tail silent, which no gap between two frames shows)
    if "alive_at_end_s" in crit:
        end_s = max([e["t_ms"] / 1000 for e in ev if "t_ms" in e] + [arr[-1]])
        chk("alive_at_end", end_s - arr[-1] <= crit["alive_at_end_s"], f"last frame {end_s - arr[-1]:.1f} s before the end", f"<= {crit['alive_at_end_s']} s")
    # tracking blackouts injected by the mock client: the stream must keep flowing through them and the uplink must come back
    bo = [e for e in ev if e.get("event") == "tracking_blackout"]
    res = {e["data"]["blackout"]: e for e in ev if e.get("event") == "tracking_resumed"}
    for e in bo:
        i = e["data"]["blackout"]
        r = res.get(i)
        if crit.get("blackouts_resume", True):
            chk(f"blackout{i}_tracking_resumed", r is not None, (f"after {r['data']['silent_s']:.1f} s" if r else "never"), f"tracking resumes (planned {e['data']['dur_s']} s" + (", and only once a frame arrives)" if e["data"].get("until_frame") else ")"))
        if r is not None and "min_fps_during_blackout" in crit:
            fps = r["data"]["frames_during"] / max(r["data"]["silent_s"], 1e-3)
            chk(f"blackout{i}_frames_flow", fps >= crit["min_fps_during_blackout"], f"{fps:.1f} fps ({r['data']['frames_during']} frames in {r['data']['silent_s']:.1f} s)", f">= {crit['min_fps_during_blackout']} fps while tracking is silent")
        if r is not None and "max_recovery_s" in crit:
            over = r["data"]["silent_s"] - e["data"]["dur_s"]
            chk(f"blackout{i}_recovery", over <= crit["max_recovery_s"], f"{over:.1f} s past the planned end", f"<= {crit['max_recovery_s']} s")
    # the host's own stall events (watchdog.rs: "stall_detected" in its stdout events) and memory lines
    hev = events(outdir / "host_stdout.txt")
    stalls = [e for e in hev if e.get("event") == "stall_detected"]
    if "host_stalls_max" in crit:
        chk("host_reported_stalls", len(stalls) <= crit["host_stalls_max"], len(stalls), f"<= {crit['host_stalls_max']}")
    if "host_stalls_min" in crit:
        kinds = sorted({e["data"].get("kind") for e in stalls})
        chk("host_noticed_the_stall", len(stalls) >= crit["host_stalls_min"], f"{len(stalls)} ({', '.join(map(str, kinds))})", f">= {crit['host_stalls_min']} stall_detected events")
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
                chk(f"mem_{name}_{kind}_growth", s <= lim, f"{s:+.0f} per hour (peak {max(v for _, v in d[kind]):.0f})", f"<= {lim} per hour after {int(m.get('warmup_frac', 0.2) * 100)} % warmup")
        if "max_gpu_mb" in m and sysrows["vram_used"]:
            peak = max(v for _, v in sysrows["vram_used"])
            chk("vram_peak", peak <= m["max_gpu_mb"], f"{peak:.0f} MB", f"<= {m['max_gpu_mb']} MB used on the GPU")
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
