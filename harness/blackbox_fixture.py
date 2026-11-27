#!/usr/bin/env python3
"""A sanitized field-timing fixture from a Black Box recording, for the replay tests (owner, 2026-11-27: "sanitize the derived timing
fixture to the public repo").

    python3 harness/blackbox_fixture.py <recording>/logs/debug/<stamp> <label>    # writes harness/fixtures/field/<label>/

What it keeps, as integers relative to the session's first head sample: the headset's sample timestamps and when each arrived at the PC
(head_timing.csv.gz), each displayed frame's waits in the headset (client_waits.csv.gz), the phase lock's evaluations and the experiment's
arms (lock.csv.gz), and per game only what the timing needs (meta.json: display rate, frame generation level, rendered fps, time span).

What it drops, by construction (an allow-list, not a filter): head and hand poses (motion data can identify a person: Nair et al.,
"Unique Identification of 50,000+ Virtual Reality Users from Head & Hand Motion Data", USENIX Security 2023), hostnames, IP addresses,
paths, game executable names, dates and wall-clock times, the session and settings files. A last scan refuses to write if any text in the
output looks like one of them.
"""
import csv, gzip, io, json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    src, label = Path(sys.argv[1]), sys.argv[2]
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{2,40}", label):
        sys.exit("label: lower-case letters, digits and dashes")
    events = [json.loads(l) for l in open(src / "host_events.jsonl", errors="replace") if l.startswith("{")]

    # the time axes: head_samples / client_frames run on the PC's monotonic seconds, host_events on ms since the host started; the first
    # tracking event carries the first head sample's headset timestamp, which ties the two exactly
    heads = list(csv.DictReader(open(src / "head_samples.csv")))
    t0, ts0 = float(heads[0]["t"]), int(heads[0]["ts_ns"])
    tr = next(e for e in events if e.get("event") == "tracking" and int(e["data"]["sample_ts_ns"]) == ts0)
    ev_offset_s = t0 - tr["t_ms"] / 1000.0   # host_events time -> the monotonic seconds
    rel_us = lambda t_s: int(round((t_s - t0) * 1e6))
    ev_us = lambda t_ms: rel_us(t_ms / 1000.0 + ev_offset_s)

    out = ROOT / "fixtures" / "field" / label
    out.mkdir(parents=True, exist_ok=True)
    files = {}

    def write(name, header, rows):
        buf = io.StringIO(); w = csv.writer(buf, lineterminator="\n"); w.writerow(header); w.writerows(rows)
        files[name] = buf.getvalue()

    write("head_timing.csv.gz", ["arrival_us", "headset_ts_us"],
          [[rel_us(float(r["t"])), (int(r["ts_ns"]) - ts0) // 1000] for r in heads])

    def us(v):
        try: return int(round(float(v) * 1000))
        except ValueError: return ""
    clients = list(csv.DictReader(open(src / "client_frames.csv")))
    write("client_waits.csv.gz", ["arrival_us", "headset_ts_us", "decode_us", "decoder_queue_us", "vsync_queue_us"],
          [[rel_us(float(r["t_recv"])), (int(r["ts_ns"]) - ts0) // 1000, us(r["decode_ms"]), us(r["decoder_queue_ms"]), us(r["vsync_queue_ms"])] for r in clients])

    lock_rows = []
    for e in events:
        d = e.get("data") or {}
        if e.get("event") == "phase_lock":
            lock_rows.append([ev_us(e["t_ms"]), "eval", d.get("version", 1), d.get("target_ms", ""), d.get("est_ms", ""), int(bool(d.get("acted"))), d.get("total_slide_ms", ""), d.get("period_ms", "")])
        elif e.get("event") == "exp_timing" and d.get("arm"):
            lock_rows.append([ev_us(e["t_ms"]), "arm:" + re.sub(r"[^a-z0-9_]", "", str(d["arm"]).lower()), d.get("lock_v", ""), d.get("target", ""), "", "", "", ""])
    write("lock.csv.gz", ["t_us", "kind", "version", "target_ms", "est_ms", "acted", "total_slide_ms", "period_ms"], lock_rows)

    games, start = [], None
    for e in events:
        d = e.get("data") or {}
        if e.get("event") == "game_started": start = e["t_ms"]
        elif e.get("event") == "game_run" and start is not None:
            games.append({"label": f"game {len(games) + 1}", "start_us": ev_us(start), "end_us": ev_us(e["t_ms"]),
                          "display_hz": d.get("display_hz"), "frame_generation": d.get("level"), "rendered_fps": round(d.get("rendered_fps", 0), 1)})
            start = None
    meta = {
        "schema": 1, "made_by": "harness/blackbox_fixture.py", "headset": "Apple Vision Pro" if any(e.get("event") == "client_connected" and "Apple" in str((e.get("data") or {}).get("name")) for e in events) else "other",
        "duration_s": round((float(heads[-1]["t"]) - t0), 1), "head_samples": len(heads), "client_frames": len(clients), "games": games,
        "columns": {"head_timing": "arrival_us: when the sample reached the PC (the PC's clock), headset_ts_us: its timestamp (the headset's clock); both from the first sample",
                    "client_waits": "per displayed frame: the PC's receive time and the frame's headset timestamp, then ALVR's decode, decoder queue (the wait for the render tick) and vsync queue, us",
                    "lock": "the phase lock's evaluations (estimate, acted, the cumulative slide) and the experiment's arm changes"},
        "removed": "poses, hostnames, IP addresses, paths, executable names, dates and wall-clock times, settings",
    }
    files["meta.json"] = json.dumps(meta, indent=1) + "\n"

    # the last guard: nothing that looks like an address, a path, a name, a date or a wall-clock time
    bad = re.compile(r"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|[A-Za-z]:\\|\\\\|\.exe\b|\.client\b|20\d\d-\d\d-\d\d|unix_s|jean|mulder|Users", re.I)
    for name, text in files.items():
        m = bad.search(text)
        if m:
            sys.exit(f"refused: {name} contains '{m.group(0)}' (nothing written)")
    for name, text in files.items():
        p = out / name
        if name.endswith(".gz"):
            with gzip.GzipFile(p, "wb", mtime=0) as g: g.write(text.encode())   # mtime 0: the archive carries no date either
        else:
            p.write_text(text)
    for p in sorted(out.iterdir()):
        print(f"{p.name:22} {p.stat().st_size / 1024:8.1f} KB")


if __name__ == "__main__":
    main()
