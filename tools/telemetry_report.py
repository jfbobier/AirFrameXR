#!/usr/bin/env python3
"""Report for one AVP telemetry session (docs/TELEMETRY.md).

    python3 tools/telemetry_report.py <session dir> [--trim 3] [--out <dir>]

Session dir = logs/debug/<yyyymmdd-hhmmss>/ of the host. Reads
  telemetry_client.log   host-receive-time + `[pt] ...` lines of the visionOS client and `[tel] ...` lines of the host scheduler
  telemetry_host.csv     one row per frame the host sent during the run (restamp mode in force, stamps, warp)
  head_samples.csv       the tracking samples as the host received them (arrival gaps = Wi-Fi uplink; poses for the stamp deltas)
and writes telemetry_client_rows.csv (every client row, with the host-side columns joined by stamp) and telemetry_report.csv
(the per-condition numbers printed below). Standard library only.

Joins: client rows and host rows meet on the stamp (client `timestamp_ns` = host `final_ts_ns`), so the clocks of the two sides
never have to agree. The first --trim seconds after each condition change are dropped (the client's rows by their own clock, the host's
rows by theirs). A row whose client segment and host segment disagree is dropped (the two schedules are offset by a second at most).
"""
import argparse
import csv
import math
import os
import re
import sys
from collections import defaultdict

BUCKETS = (("still", 0.0, 5.0), ("slow", 5.0, 30.0), ("fast", 30.0, 1e9))


def pct(values, p):
    v = sorted(x for x in values if x is not None and not math.isnan(x))
    if not v:
        return None
    return v[min(len(v) - 1, int(p * (len(v) - 1) + 0.5))]


def fnum(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def parse_tokens(rest):
    """key=value tokens (values without spaces) of a `[pt]` line; returns (verb, dict)."""
    parts = rest.split()
    verb = parts[0] if parts else ""
    kv = {}
    for tok in parts[1:]:
        if "=" in tok:
            k, v = tok.split("=", 1)
            kv[k] = v
    return verb, kv


def read_client_log(path):
    """Returns dict with start, conds (list), sums (list), rows (list of dicts), tel (host scheduler events), end."""
    out = {"start": None, "conds": [], "sums": [], "rows": [], "tel": [], "end": None, "lines": 0}
    if not os.path.exists(path):
        return out
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            m = re.match(r"^(\d+\.\d+) (\[(?:pt|tel)\] .*)$", line)
            if not m:
                continue
            t, text = float(m.group(1)), m.group(2)
            out["lines"] += 1
            if text.startswith("[tel] "):
                verb, kv = parse_tokens(text[6:])
                out["tel"].append({"t": t, "verb": verb, **kv})
                continue
            body = text[5:]
            if body.startswith("rows"):
                head, _, data = body.partition("|")
                verb, kv = parse_tokens(head)
                cols = (kv.get("cols") or "").split(",")
                seg = int(kv.get("seg", -1))
                for rec in data.strip().split(";"):
                    rec = rec.strip()
                    if not rec:
                        continue
                    vals = rec.split(",")
                    row = {c: fnum(v) for c, v in zip(cols, vals)}
                    row["seg"] = seg
                    row["host_t"] = t
                    out["rows"].append(row)
                continue
            verb, kv = parse_tokens(body)
            kv["host_t"] = t
            if verb == "start":
                out["start"] = kv
            elif verb == "cond":
                out["conds"].append(kv)
            elif verb == "sum":
                out["sums"].append(kv)
            elif verb == "end":
                out["end"] = kv
    return out


def read_csv(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8", errors="replace") as f:
        return list(csv.DictReader(f))


def autocorr_peak(times, lo_s=0.2, hi_s=1.2, bin_s=0.02):
    """Autocorrelation of the 'late arrival' indicator over lags lo..hi: (peak value, lag in s). times = arrival times of the late events."""
    if len(times) < 20:
        return None, None
    t0, t1 = min(times), max(times)
    n = int((t1 - t0) / bin_s) + 1
    if n < int(hi_s / bin_s) * 3:
        return None, None
    x = [0.0] * n
    for t in times:
        x[int((t - t0) / bin_s)] = 1.0
    mean = sum(x) / n
    xs = [v - mean for v in x]
    var = sum(v * v for v in xs)
    if var <= 0:
        return None, None
    best = (-1.0, 0)
    for lag in range(int(lo_s / bin_s), int(hi_s / bin_s) + 1):
        c = sum(xs[i] * xs[i + lag] for i in range(n - lag)) / var
        if c > best[0]:
            best = (c, lag)
    return best[0], best[1] * bin_s


def head_sample_stats(rows):
    """Arrival gap statistics of the host's received tracking samples; late = gap over 20 ms."""
    ts = [fnum(r.get("t")) for r in rows]
    ts = [t for t in ts if t is not None]
    gaps = [(b - a) * 1000.0 for a, b in zip(ts, ts[1:])]
    late_times = [b for a, b in zip(ts, ts[1:]) if (b - a) * 1000.0 > 20.0]
    peak, lag = autocorr_peak(late_times)
    return {"n": len(ts), "gap_p50": pct(gaps, 0.5), "gap_p99": pct(gaps, 0.99), "gap_max": max(gaps) if gaps else None,
            "late20": len(late_times), "ac_peak": peak, "ac_lag_s": lag}


def quat_deg(a, b):
    d = abs(sum(x * y for x, y in zip(a, b)))
    return 2.0 * math.degrees(math.acos(min(1.0, d)))


def fmt(v, nd=2):
    return "-" if v is None else f"{v:.{nd}f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session")
    ap.add_argument("--trim", type=float, default=3.0, help="seconds dropped after each condition change")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    d, outd = args.session, args.out or args.session
    cl = read_client_log(os.path.join(d, "telemetry_client.log"))
    host = read_csv(os.path.join(d, "telemetry_host.csv"))
    heads = read_csv(os.path.join(d, "head_samples.csv"))
    if not cl["rows"]:
        print(f"no client rows in {d}/telemetry_client.log ({cl['lines']} telemetry lines): did the telemetry client run?")
        return 1

    # ---- conditions announced by the client, per segment
    seg_cond = {}
    for c in cl["conds"]:
        try:
            seg_cond[int(c.get("seg", -1))] = (c.get("focal", "?"), c.get("pick", "?"))
        except ValueError:
            pass
    host_seg_mode = {}
    for e in cl["tel"]:
        if e["verb"] == "cond" and "seg" in e and "mode" in e:
            host_seg_mode[int(e["seg"])] = e["mode"]

    # ---- host rows by final stamp
    hostby = {}
    for r in host:
        ts = r.get("final_ts_ns")
        if ts:
            hostby[ts] = r

    # ---- trim: per segment, drop the first --trim seconds (client present_s clock for client rows, host t_send for host rows)
    seg_first_client = {}
    for r in cl["rows"]:
        p = r.get("present_s")
        if p is not None:
            seg_first_client[r["seg"]] = min(seg_first_client.get(r["seg"], p), p)
    seg_first_host = {}
    for r in host:
        s, t = r.get("seg"), fnum(r.get("t_send"))
        if s not in (None, "") and t is not None:
            s = int(s)
            seg_first_host[s] = min(seg_first_host.get(s, t), t)

    kept = []
    dropped = defaultdict(int)
    for r in cl["rows"]:
        p = r.get("present_s")
        if p is None or p < seg_first_client.get(r["seg"], p) + args.trim:
            dropped["trim"] += 1
            continue
        h = hostby.get(str(int(r["timestamp_ns"]))) if r.get("timestamp_ns") is not None else None
        if h is None:
            dropped["no host row"] += 1
            continue
        hseg = int(h["seg"])
        ht = fnum(h.get("t_send"))
        if hseg != r["seg"]:
            dropped["segment mismatch"] += 1
            continue
        if ht is not None and ht < seg_first_host.get(hseg, ht) + args.trim:
            dropped["trim"] += 1
            continue
        r["host_mode"] = h["mode"]
        r["host_restamped"] = fnum(h.get("restamped"))
        r["host_warp_mdeg"] = fnum(h.get("warp_mdeg"))
        r["host_queue_wait_ms"] = fnum(h.get("queue_wait_ms"))
        r["host_encode_ms"] = fnum(h.get("encode_ms"))
        r["host_orig_ts_ns"] = h.get("orig_ts_ns")
        focal, pick = seg_cond.get(r["seg"], ("?", "?"))
        r["focal"], r["pick"] = focal, pick
        kept.append(r)

    # ---- stamp pose deltas (orig vs final stamp) from the host's received samples
    poses = {}
    for r in heads:
        try:
            poses[r["ts_ns"]] = ([float(r[k]) for k in ("qx", "qy", "qz", "qw")], [float(r[k]) for k in ("px", "py", "pz")])
        except (KeyError, ValueError):
            pass
    stamp_delta = []  # (mode, deg, mm) for host rows whose stamp differs from the render sample
    for h in host:
        if h.get("orig_ts_ns") and h.get("orig_ts_ns") != h.get("final_ts_ns"):
            a, b = poses.get(h["orig_ts_ns"]), poses.get(h["final_ts_ns"])
            if a and b:
                mm = math.dist(a[1], b[1]) * 1000.0
                stamp_delta.append((h["mode"], quat_deg(a[0], b[0]), mm))

    # ---- grouping
    groups = defaultdict(list)
    for r in kept:
        groups[(r["host_mode"], r["focal"], r["pick"])].append(r)

    # durations per group: sum over its segments of the span of kept rows
    def span(rows, key):
        by = defaultdict(list)
        for r in rows:
            if r.get(key) is not None:
                by[r["seg"]].append(r[key])
        return sum(max(v) - min(v) for v in by.values() if len(v) > 1)

    host_by_group_seg = defaultdict(list)
    for h in host:
        try:
            host_by_group_seg[(h["mode"], int(h["seg"]))].append(fnum(h["t_send"]))
        except (KeyError, ValueError):
            pass

    def row_stats(rows):
        n = len(rows)
        dur = span(rows, "present_s")
        distinct = sum(1 for r in rows if not r.get("repeated"))
        return {
            "frames": n,
            "dur_s": dur,
            "repeat_pct": 100.0 * (n - distinct) / n if n else None,
            "shown_fps": distinct / dur if dur > 0 else None,
            "age_p50": pct([r.get("age_ms") for r in rows], 0.5), "age_p95": pct([r.get("age_ms") for r in rows], 0.95),
            "comp_deg_p50": pct([r.get("comp_deg") for r in rows], 0.5), "comp_deg_p95": pct([r.get("comp_deg") for r in rows], 0.95),
            "comp_mm_p50": pct([r.get("comp_mm") for r in rows], 0.5), "comp_mm_p95": pct([r.get("comp_mm") for r in rows], 0.95),
            "dcomp_deg_p95": pct([r.get("dcomp_deg") for r in rows], 0.95), "dcomp_mm_p95": pct([r.get("dcomp_mm") for r in rows], 0.95),
            "extrap_deg_p95": pct([r.get("extrap_deg") for r in rows], 0.95), "extrap_mm_p95": pct([r.get("extrap_mm") for r in rows], 0.95),
            "walkback_pct": 100.0 * sum(1 for r in rows if (r.get("walkback_ms") or 0) > 0.5) / n if n else None,
            "fallback_pct": 100.0 * sum(1 for r in rows if (r.get("fallback") or 0) > 0) / n if n else None,
            "nosend": sum(1 for r in rows if not r.get("had_send")),
            "queue_p50": pct([r.get("queue_ms") for r in rows], 0.5), "queue_p95": pct([r.get("queue_ms") for r in rows], 0.95),
            "gpu_p95": pct([r.get("gpu_ms") for r in rows], 0.95), "decode_p95": pct([r.get("decode_ms") for r in rows], 0.95),
            "host_restamped_pct": 100.0 * sum(1 for r in rows if r.get("host_restamped")) / n if n else None,
            "host_qwait_p95": pct([r.get("host_queue_wait_ms") for r in rows], 0.95),
        }

    export = []
    order = lambda k: (["off", "shim", "host"].index(k[0]) if k[0] in ("off", "shim", "host") else 9, k[1], k[2])
    print(f"session {d}")
    print(f"client rows {len(cl['rows'])}, kept {len(kept)}, dropped {dict(dropped)}; host rows {len(host)}")
    if cl["start"]:
        print("client:", " ".join(f"{k}={v}" for k, v in cl["start"].items() if k != "host_t"))
    print()
    print("== per condition (host re-stamp mode | focal depth | pick policy)  [trim %g s]" % args.trim)
    hdr = ("condition", "frames", "dur s", "rpt%", "shown fps", "age p50/p95", "comp deg p50/p95", "comp mm p50/p95",
           "dcomp deg p95", "dcomp mm p95", "walkbk%", "nosend", "queue p50/p95", "restamp%")
    lines = [hdr]
    for key in sorted(groups, key=order):
        s = row_stats(groups[key])
        lines.append((" | ".join(key), str(s["frames"]), fmt(s["dur_s"], 0), fmt(s["repeat_pct"], 1), fmt(s["shown_fps"], 1),
                      f"{fmt(s['age_p50'], 1)}/{fmt(s['age_p95'], 1)}", f"{fmt(s['comp_deg_p50'])}/{fmt(s['comp_deg_p95'])}",
                      f"{fmt(s['comp_mm_p50'], 1)}/{fmt(s['comp_mm_p95'], 1)}", fmt(s["dcomp_deg_p95"], 3), fmt(s["dcomp_mm_p95"], 2),
                      fmt(s["walkback_pct"], 1), str(s["nosend"]), f"{fmt(s['queue_p50'], 1)}/{fmt(s['queue_p95'], 1)}",
                      fmt(s["host_restamped_pct"], 1)))
        export.append({"condition": " | ".join(key), "bucket": "all", **s})
    widths = [max(len(l[i]) for l in lines) for i in range(len(hdr))]
    for l in lines:
        print("  ".join(c.ljust(widths[i]) for i, c in enumerate(l)))

    print()
    print("== tremble metric by head speed (dcomp = frame-to-frame change of the residual the reprojection must hide)")
    hdr2 = ("condition", "speed", "frames", "rpt%", "comp mm p50/p95", "dcomp deg p95", "dcomp mm p95")
    lines = [hdr2]
    for key in sorted(groups, key=order):
        for name, lo, hi in BUCKETS:
            rows = [r for r in groups[key] if r.get("head_deg_s") is not None and lo <= r["head_deg_s"] < hi]
            if len(rows) < 30:
                continue
            s = row_stats(rows)
            lines.append((" | ".join(key), f"{name} {lo:.0f}-{hi:.0f}" if hi < 1e8 else f"{name} >{lo:.0f}", str(s["frames"]), fmt(s["repeat_pct"], 1),
                          f"{fmt(s['comp_mm_p50'], 1)}/{fmt(s['comp_mm_p95'], 1)}", fmt(s["dcomp_deg_p95"], 3), fmt(s["dcomp_mm_p95"], 2)))
            export.append({"condition": " | ".join(key), "bucket": name, **s})
    widths = [max(len(l[i]) for l in lines) for i in range(len(hdr2))]
    for l in lines:
        print("  ".join(c.ljust(widths[i]) for i, c in enumerate(l)))

    # ---- host fps vs shown fps per host mode
    print()
    print("== host fps and stamp deltas per host mode")
    by_mode = defaultdict(list)
    for h in host:
        by_mode[h["mode"]].append(h)
    for mode in ("off", "shim", "host"):
        rows = by_mode.get(mode, [])
        if not rows:
            continue
        per_seg = defaultdict(list)
        for h in rows:
            t = fnum(h.get("t_send"))
            if t is not None:
                per_seg[h["seg"]].append(t)
        dur = sum(max(v) - min(v) for v in per_seg.values() if len(v) > 1)
        dist = len({h["final_ts_ns"] for h in rows})
        sd = [x for x in stamp_delta if x[0] == mode]
        print(f"  {mode:5s} frames {len(rows):6d}  host fps {len(rows) / dur if dur else float('nan'):6.1f}  distinct stamps {dist}  "
              f"restamped {sum(1 for h in rows if h.get('restamped') == '1')}  "
              f"stamp delta deg p50/p95 {fmt(pct([x[1] for x in sd], 0.5), 3)}/{fmt(pct([x[1] for x in sd], 0.95), 3)}  "
              f"mm p50/p95 {fmt(pct([x[2] for x in sd], 0.5), 2)}/{fmt(pct([x[2] for x in sd], 0.95), 2)}")

    # ---- controllers, thermal from the 1 Hz summaries
    print()
    print("== controller anchor rate, thermal state (client 1 Hz summaries)")
    by_seg = defaultdict(list)
    for sline in cl["sums"]:
        try:
            by_seg[int(sline.get("seg", -1))].append(sline)
        except ValueError:
            pass
    def mean(vals):
        vals = [v for v in vals if v is not None]
        return sum(vals) / len(vals) if vals else None
    l_hz = mean([fnum(x.get("ctrlL_hz")) for v in by_seg.values() for x in v])
    r_hz = mean([fnum(x.get("ctrlR_hz")) for v in by_seg.values() for x in v])
    thermal = [fnum(x.get("thermal")) for v in by_seg.values() for x in v]
    print(f"  ctrlL {fmt(l_hz, 1)} Hz  ctrlR {fmt(r_hz, 1)} Hz  thermal max {fmt(max([t for t in thermal if t is not None], default=None), 0)}")

    # ---- arrival gaps / AWDL signature
    print()
    print("== tracking-sample arrival gaps at the host (Wi-Fi uplink); 'periodic' flags an AWDL-like 0.2-1.2 s rhythm of >20 ms gaps")
    hs = head_sample_stats(heads)
    if hs["n"]:
        periodic = hs["ac_peak"] is not None and hs["ac_peak"] > 0.3
        print(f"  samples {hs['n']}  gap p50 {fmt(hs['gap_p50'], 1)} ms  p99 {fmt(hs['gap_p99'], 1)} ms  max {fmt(hs['gap_max'], 0)} ms  "
              f">20 ms: {hs['late20']}  autocorr peak {fmt(hs['ac_peak'], 2)} at {fmt(hs['ac_lag_s'], 2)} s  -> {'PERIODIC (AWDL-like)' if periodic else 'not periodic'}")
    else:
        print("  no head_samples.csv in the session folder (the host writes it while a telemetry run is armed; the simrig does not feed it)")

    # ---- exports
    os.makedirs(outd, exist_ok=True)
    cols = ["seg", "present_s", "timestamp_ns", "age_ms", "repeated", "had_send", "walkback_ms", "anchor_minus_used_ms", "fallback", "extrap_deg",
            "extrap_mm", "comp_deg", "comp_mm", "dcomp_deg", "dcomp_mm", "head_deg_s", "gpu_ms", "decode_ms", "queue_ms", "host_t",
            "host_mode", "host_restamped", "host_warp_mdeg", "host_queue_wait_ms", "host_encode_ms", "focal", "pick"]
    allrows = {id(r): r for r in kept}
    with open(os.path.join(outd, "telemetry_client_rows.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(cols + ["kept"])
        for r in cl["rows"]:
            w.writerow([r.get(c, "") if r.get(c) is not None else "" for c in cols] + [1 if id(r) in allrows else 0])
    keys = ["condition", "bucket"] + [k for k in export[0] if k not in ("condition", "bucket")] if export else ["condition"]
    with open(os.path.join(outd, "telemetry_report.csv"), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=keys)
        w.writeheader()
        for e in export:
            w.writerow(e)
    print()
    print(f"wrote {os.path.join(outd, 'telemetry_client_rows.csv')} and {os.path.join(outd, 'telemetry_report.csv')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
