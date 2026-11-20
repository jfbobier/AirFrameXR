#!/usr/bin/env python3
"""OFXR lab report (stdlib only): reads result folders of e2e_ofxr_tokyo_* and prints, for the steady window of each run (default 10-40 s of the
mock display), what the game and the display did: app fps, new frames shown per second, unique timestamps per second, repeated ticks, the shim's view
of the submits (duplicate share) and the dropped duplicates.  Several runs of one scenario give the run-to-run spread (the noise floor).

  python3 harness/ofxr_report.py [--from 10 --to 40] DIR [DIR ...]     (DIR = harness/results/<run>)
"""
import csv, json, re, sys, statistics as st
from pathlib import Path


def window(d, t0, t1):
    r = list(csv.DictReader(open(d / "display.csv")))
    base = float(r[0]["tick_ns"])
    seg = [x for x in r if t0 * 1e9 <= float(x["tick_ns"]) - base < t1 * 1e9]
    sec = float(seg[-1]["tick_ns"]) - float(seg[0]["tick_ns"])
    sec = sec / 1e9 if sec > 0 else (t1 - t0)
    new = [x for x in seg if x["repeat"] == "0"]
    return {"new_fps": len(new) / sec, "uniq_fps": len({x["shown_ts_ns"] for x in new}) / sec, "repeat_pct": 100.0 * sum(1 for x in seg if x["repeat"] == "1") / max(len(seg), 1)}


def app_fps(d):
    m = re.findall(r"app fps ([0-9.]+)", (d / "app.log").read_text(errors="replace"))
    return float(m[-1]) if m else None


def shim(d):
    t = (d / "ovrshim.log").read_text(errors="replace") if (d / "ovrshim.log").exists() else ""
    m = re.findall(r"duplicate frames seen (\d+) dropped (\d+) \(pacing fix (on|off)", t)
    f = re.findall(r"frames (\d+): ([0-9.]+) fps", t)
    return {"dups_seen": int(m[-1][0]), "dups_dropped": int(m[-1][1]), "fix": m[-1][2], "submits": int(f[-1][0])} if m and f else {}


def name(d):
    return re.sub(r"^\d+T\d+Z_", "", d.name)


def main(a):
    t0, t1, dirs = 10.0, 40.0, []
    i = 0
    while i < len(a):
        if a[i] == "--from": t0 = float(a[i + 1]); i += 2
        elif a[i] == "--to": t1 = float(a[i + 1]); i += 2
        else: dirs.append(Path(a[i])); i += 1
    rows = {}
    for d in dirs:
        try:
            w = window(d, t0, t1)
        except Exception as e:
            print(f"{d.name}: no display data ({e})"); continue
        s = shim(d)
        rows.setdefault(name(d), []).append({"app_fps": app_fps(d), **w, "dup_seen": s.get("dups_seen"), "dropped": s.get("dups_dropped")})
    print(f"window {t0:.0f}-{t1:.0f} s of the display; app fps = whole run")
    print(f"{'scenario':32} {'n':>2} {'app fps':>9} {'new/s':>9} {'unique/s':>9} {'repeat %':>9} {'dups seen':>10} {'dropped':>8}")
    for k, v in rows.items():
        def col(f):
            xs = [x[f] for x in v if x[f] is not None]
            return (f"{st.mean(xs):6.1f}" + (f"±{st.pstdev(xs):.1f}" if len(xs) > 1 else "")) if xs else "-"
        print(f"{k:32} {len(v):>2} {col('app_fps'):>9} {col('new_fps'):>9} {col('uniq_fps'):>9} {col('repeat_pct'):>9} {col('dup_seen'):>10} {col('dropped'):>8}")


if __name__ == "__main__":
    main(sys.argv[1:])
