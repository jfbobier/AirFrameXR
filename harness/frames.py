#!/usr/bin/env python3
"""Follow the frames: join every game frame's life across the chain instead of averaging it.

    python3 harness/frames.py <results dir | debug session dir> [--no-pixels] [--list N]

A frame n goes through   G  the game asks its pose (display time tg) and is released by the shim,
                         R  the shim composes it and the GPU finishes (publish),
                         E  the host takes it at a boundary, encodes it, sends it,
                         D  the client receives it, decodes it, shows it at a display tick.
Sources (written by the lab; the headset sessions have the same files in logs/debug/<session>/ except the client's):
    shim_frames.csv         per published frame: display time, release, pose read, head sample arrival, predicted/repeated flags,
                            submit, publish, rendered orientation                        (shim clock = QPC seconds)
    host_pipeline_fates.csv per frame: A arrival, S superseded, P picked (boundary idx), O dropped by the encoder queue
    host_pipeline.csv       per sent frame: boundary, encode start/ms, send time, hold      (host clock = the same QPC seconds)
    frames.csv              client: per received frame: ts, arrival, decode done, queue drop             (client clock, relative)
    display.csv             client: per display tick: shown ts, repeat flag                              (client clock, relative)
    poses.csv               client: every tracking sample sent: target ts, yaw, send time
    app_markers.csv, client.hevc   the probe's world-fixed marker in the left eye (where the app drew it) and the decoded stream
Output: frames_table.csv (one row per game frame) and a report: stage cadence and jitter, fates, pose correctness (data and pixels).
The client clock is mapped to the host's with the median (client arrival - host send): a loopback stream, so +-0.5 ms.
"""
import csv, math, os, subprocess, sys, tempfile
from pathlib import Path
import numpy as np

PERIOD = 1.0 / 90.0


def rd(path):
    try:
        with open(path, newline="") as f:
            return list(csv.DictReader(f))
    except Exception:
        return []


def fl(x, d=float("nan")):
    try:
        return float(x)
    except Exception:
        return d


def q(v, p):
    v = sorted(x for x in v if x == x)
    return v[min(len(v) - 1, int(len(v) * p))] if v else float("nan")


def stats(v, scale=1.0):
    v = [x * scale for x in v if x == x]
    if not v:
        return "n=0"
    a = np.array(v)
    return f"n={len(v)} p01 {q(v,.01):7.2f} p05 {q(v,.05):7.2f} p50 {q(v,.5):7.2f} p95 {q(v,.95):7.2f} p99 {q(v,.99):7.2f} max {a.max():7.2f} sd {a.std():6.2f}"


def yaw_of(qx, qy, qz, qw):
    return math.atan2(2 * (qw * qy + qx * qz), 1 - 2 * (qy * qy + qx * qx))


def project(yaw, pos, tan4, w, h, P=(0.0, 1.3, -3.0)):
    """Pixel of the world point P for a head yawed about +Y at `pos` (the probe's marker projection)."""
    c, s = math.cos(yaw), math.sin(yaw)
    dx, dy, dz = P[0] - pos[0], P[1] - pos[1], P[2] - pos[2]
    # view = R^-1 (P - pos), R = rotation about +Y by yaw
    vx, vz = c * dx - s * dz, s * dx + c * dz
    tl, tr, tu, td = tan4
    tx, ty = vx / -vz, dy / -vz
    return (tx + tl) / (tl + tr) * w, (1 - (ty + td) / (tu + td)) * h


def decode_stream(hevc, n_expect):
    """Per decoded frame: the 16-bit frame stamp (left eye, top-left blocks) and the marker position (left eye), via ffmpeg."""
    ids, marks = [], []
    with tempfile.TemporaryDirectory() as td:
        stamp, small = os.path.join(td, "stamp.raw"), os.path.join(td, "small.raw")
        # stamp: 16 blocks of 64 px at the top-left -> one pixel each; small: the whole frame at 1/8 for the marker
        cmd = ["ffmpeg", "-v", "error", "-y", "-i", str(hevc), "-filter_complex",
               "[0:v]split=2[a][b];[a]crop=1024:64:0:0,scale=16:1:flags=area,format=gray[s];[b]scale=iw/8:ih/8:flags=area,format=rgb24[m]",
               "-map", "[s]", "-f", "rawvideo", stamp, "-map", "[m]", "-f", "rawvideo", small]
        p = subprocess.run(cmd, capture_output=True, text=True)
        if p.returncode:
            print("ffmpeg failed:", p.stderr[-300:])
            return None
        sb = np.fromfile(stamp, dtype=np.uint8)
        n = len(sb) // 16
        sb = sb[:n * 16].reshape(n, 16)
        # frame size of the small stream: derive from the file size and n
        mb = np.fromfile(small, dtype=np.uint8)
        per = len(mb) // n if n else 0
        pix = per // 3
        # the stream is SBS: width = 2 * eye width; the small frame keeps the aspect 2.2:1 for 3520x1600 -> 440x200
        W = int(round(math.sqrt(pix * 2.2)))
        for cand in (440, 220, 880):
            if pix % cand == 0:
                W = cand
                break
        Hs = pix // W
        mb = mb[:n * per].reshape(n, Hs, W, 3).astype(np.int16)
        for i in range(n):
            bits = (sb[i] > 128).astype(int)
            ids.append(int(sum(int(b) << k for k, b in enumerate(bits))))
            f = mb[i, :, :W // 2]  # left eye half
            mask = (f[..., 0] > 170) & (f[..., 2] > 170) & (f[..., 1] < 90)
            ys, xs = np.nonzero(mask)
            marks.append(((xs.mean() + 0.5) * 8, (ys.mean() + 0.5) * 8, int(mask.sum())) if len(xs) >= 4 else None)
    return ids, marks, (W * 8, Hs * 8)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    d = Path(args[0])
    pixels = "--no-pixels" not in sys.argv
    list_n = int(sys.argv[sys.argv.index("--list") + 1]) if "--list" in sys.argv else 0
    shim = rd(d / "shim_frames.csv")
    fates = rd(d / "host_pipeline_fates.csv")
    sent = rd(d / "host_pipeline.csv")
    cf = rd(d / "frames.csv")
    disp = rd(d / "display.csv")
    poses = rd(d / "poses.csv")
    marks_app = rd(d / "app_markers.csv")
    if not shim or not sent:
        print("missing shim_frames.csv / host_pipeline.csv: nothing to follow")
        return 1
    # ---- G / R: shim
    F = {}
    for r in shim:
        n = int(r["counter"])
        F[n] = dict(n=n, slot=int(r["slot"]), ts=int(r["client_ts"]), tg=fl(r["display_time"]), t_release=fl(r["t_release"]) or float("nan"),
                    t_read=fl(r["t_read"]), t_arrive=fl(r["t_sample_arrive"]), predicted=int(r["predicted"]), repeated=int(r["repeated"]),
                    t_submit=fl(r["t_submit"]), t_publish=fl(r["t_publish"]), qx=fl(r["qx"]), qy=fl(r["qy"]), qz=fl(r["qz"]), qw=fl(r["qw"]))
    # ---- E: host fates and sent rows
    for r in fates:
        n = int(r["counter"])
        f = F.get(n)
        if not f:
            continue
        k = r["kind"]
        if k == "A":
            f["t_host_arrive"] = fl(r["t"])
        elif k == "P":
            f["t_picked"] = fl(r["t"])
            f["fate"] = "picked"
        elif k == "S":
            f["fate"] = "superseded"
            f["t_dropped"] = fl(r["t"])
        elif k == "O":
            f["fate"] = "enc_overrun"
            f["t_dropped"] = fl(r["t"])
    sent_rows = []
    for r in sent:
        n = int(r["frame"])
        row = dict(n=n, ts=int(r["ts_ns"]), t_boundary=fl(r["t_boundary"]), t_enc0=fl(r["t_encode_start"]), enc_ms=fl(r["encode_ms"]),
                   t_send=fl(r["t_send"]), hold=fl(r["hold_ms"]))
        sent_rows.append(row)
        if n in F:
            F[n].update(t_boundary=row["t_boundary"], t_enc0=row["t_enc0"], enc_ms=row["enc_ms"], t_send=row["t_send"], hold_ms=row["hold"], sent=1)
    # ---- D: client, matched to the sent rows in order (the stream keeps its order)
    C = [dict(ts=int(r["ts_ns"]), arr=int(r["arrival_ns"]) * 1e-9, dec=int(r["decoded_ns"]) * 1e-9 if r.get("decoded_ns") not in (None, "", "0") else float("nan"),
              qdrop=int(r.get("queue_dropped") or 0), yaw=fl(r.get("yaw")), yaw_near=fl(r.get("yaw_near"))) for r in cf]
    j = 0
    pair = []
    for s in sent_rows:
        found = None
        for k in range(j, min(j + 6, len(C))):
            if C[k]["ts"] == s["ts"]:
                found = k
                break
        if found is not None:
            pair.append((s, found))
            j = found + 1
    offs = [C[k]["arr"] - s["t_send"] for s, k in pair if s["n"] >= 0]
    off = float(np.median(offs)) if offs else 0.0
    cl = {}  # client index -> frame number
    for s, k in pair:
        if s["n"] in F:
            F[s["n"]].update(t_c_arr=C[k]["arr"] - off, t_c_dec=C[k]["dec"] - off, qdrop=C[k]["qdrop"], c_idx=k)
        cl[k] = s["n"]
    # display: tick rows; the first unshown client row with the ts is the one shown
    shown_at = {}
    pending = {}
    for k, c in enumerate(C):
        pending.setdefault(c["ts"], []).append(k)
    repeats_of_ts = {}
    t_prev = None
    D = []
    for r in disp:
        t = int(r["tick_ns"]) * 1e-9
        ts = int(r["shown_ts_ns"])
        rep = int(r["repeat"])
        D.append((t - off, ts, rep))
        if not rep and ts in pending and pending[ts]:
            k = pending[ts].pop(0)
            shown_at[k] = t - off
        elif rep and ts:
            repeats_of_ts[ts] = repeats_of_ts.get(ts, 0) + 1
    for n, f in F.items():
        k = f.get("c_idx")
        if k is not None and k in shown_at:
            f["t_shown"] = shown_at[k]
    # ---- pose correctness (data): the yaw the app rendered with vs the yaw the client had at the frame's timestamp
    sent_yaw = {int(r["target_ts_ns"]): fl(r["yaw"]) for r in poses}
    keys = np.array(sorted(sent_yaw)) if sent_yaw else np.array([])
    mk = {}
    for r in marks_app:
        if r["eye"] == "0":
            mk[int(r["frame"])] = r
    for n, f in F.items():
        m = mk.get(n)
        if m:
            f["app_yaw"] = yaw_of(fl(m["qx"]), fl(m["qy"]), fl(m["qz"]), fl(m["qw"]))
            f["mark_px"], f["mark_py"] = fl(m["px"]), fl(m["py"])
            f["pos"] = (fl(m["posx"]), fl(m["posy"]), fl(m["posz"]))
            f["tan4"] = (fl(m["tl"]), fl(m["tr"]), fl(m["tu"]), fl(m["td"]))
            f["wh"] = (int(m["w"]), int(m["h"]))
        ts = f["ts"]
        if ts in sent_yaw:
            f["client_yaw"], f["ts_exact"] = sent_yaw[ts], 1
        elif len(keys):
            i = int(np.searchsorted(keys, ts))
            cand = [keys[x] for x in (i - 1, i) if 0 <= x < len(keys)]
            best = min(cand, key=lambda x: abs(int(x) - ts))
            if abs(int(best) - ts) <= 3000:
                f["client_yaw"], f["ts_exact"] = sent_yaw[int(best)], 0
            else:
                f["client_yaw"], f["ts_exact"] = float("nan"), -1
        if "app_yaw" in f and f.get("client_yaw") == f.get("client_yaw"):
            e = f["app_yaw"] - f["client_yaw"]
            f["yaw_err"] = (e + math.pi) % (2 * math.pi) - math.pi
    # ---- pixels
    px_note = ""
    if pixels and (d / "client.hevc").exists() and mk:
        dec = decode_stream(d / "client.hevc", len(C))
        if dec:
            ids, marks, (dw, dh) = dec
            px_note = f"decoded {len(ids)} pictures for {len(C)} received frames"
            by_id = {}
            for pi, pid in enumerate(ids):
                if pid != 65535:
                    by_id.setdefault(pid, []).append(pi)
            for n, f in F.items():
                pis = by_id.get(n & 0xFFFF)
                if not pis:
                    continue
                pi = pis[0]
                f["dec_id"] = ids[pi]
                f["pic_idx"] = pi
                mm = marks[pi]
                if mm and "client_yaw" in f and f.get("client_yaw") == f.get("client_yaw") and "pos" in f:
                    # the decoded frame is the whole SBS picture: the left eye is its left half
                    ew = dw // 2
                    ex, ey = project(f["client_yaw"], f["pos"], f["tan4"], ew, dh)
                    ax, ay = project(f["app_yaw"], f["pos"], f["tan4"], ew, dh)
                    f["marker_x"], f["marker_y"] = mm[0], mm[1]
                    f["marker_err_px"] = mm[0] - ex            # decoded marker vs where the client's pose says it should be
                    f["marker_app_err_px"] = mm[0] - ax        # decoded marker vs where the app's own pose puts it (pipeline fidelity)
    # ---- table
    cols = ["n", "slot", "ts", "ts_exact", "predicted", "repeated", "tg", "t_release", "t_read", "t_arrive", "t_submit", "t_publish", "t_host_arrive", "t_picked",
            "fate", "t_boundary", "t_enc0", "enc_ms", "t_send", "hold_ms", "t_c_arr", "t_c_dec", "qdrop", "t_shown", "app_yaw", "client_yaw", "yaw_err",
            "dec_id", "marker_err_px", "marker_app_err_px"]
    with open(d / "frames_table.csv", "w", newline="") as fo:
        w = csv.writer(fo)
        w.writerow(cols)
        for n in sorted(F):
            f = F[n]
            w.writerow([("%.6f" % f[c]) if isinstance(f.get(c), float) else f.get(c, "") for c in cols])
    # ---- report
    ns = sorted(F)
    ms = 1e3
    lines = []
    P = lines.append
    P(f"== follow the frames: {d}  ({len(ns)} game frames; client clock offset {off*1e3:.2f} ms; {px_note})")
    # fates
    fate = {}
    for n in ns:
        f = F[n]
        st = f.get("fate", "?")
        if st == "picked" and not f.get("sent"):
            st = "picked_not_sent"
        if st == "picked" and f.get("sent") and "t_c_arr" not in f:
            st = "sent_not_received"
        if st == "picked" and "t_c_arr" in f and "t_shown" not in f:
            st = "received_not_shown" + ("(queue-dropped)" if f.get("qdrop") else "")
        if st == "picked" and "t_shown" in f:
            st = "shown"
        fate[st] = fate.get(st, 0) + 1
    P("-- fates of the game's frames: " + ", ".join(f"{k} {v} ({100*v/len(ns):.1f}%)" for k, v in sorted(fate.items(), key=lambda kv: -kv[1])))
    # stage cadence: the intervals t(n+1)-t(n) at each stage vs the ideal period
    def cadence(name, key, scale=ms):
        t = [F[n].get(key, float("nan")) for n in ns]
        iv = [(b - a) for a, b in zip(t, t[1:]) if a == a and b == b]
        P(f"   {name:34s} interval ms: " + stats(iv, scale) + f"   <0.5T {sum(1 for x in iv if x < 0.5*PERIOD)}  >1.5T {sum(1 for x in iv if x > 1.5*PERIOD)}")
    P(f"-- cadence (ideal {PERIOD*ms:.2f} ms)")
    cadence("G  display time asked by the game", "tg")
    cadence("G  shim releases the game", "t_release")
    cadence("G  game reads the pose", "t_read")
    cadence("R  game submits the frame", "t_submit")
    cadence("R  frame published (GPU done)", "t_publish")
    cadence("E  host takes it (boundary)", "t_picked")
    cadence("E  host sends", "t_send")
    cadence("D  client receives", "t_c_arr")
    cadence("D  client shows", "t_shown")
    P("-- per-frame stage durations ms")
    def dur(name, a, b):
        v = [F[n][b] - F[n][a] for n in ns if a in F[n] and b in F[n] and F[n][a] == F[n][a] and F[n][b] == F[n][b]]
        P(f"   {name:34s} " + stats(v, ms))
    dur("G read pose -> submit (game work)", "t_read", "t_submit")
    dur("G release -> pose read", "t_release", "t_read")
    dur("R submit -> published (GPU)", "t_submit", "t_publish")
    dur("E published -> picked (queue wait)", "t_publish", "t_picked")
    dur("E picked -> sent (encode+hold)", "t_picked", "t_send")
    dur("D sent -> received", "t_send", "t_c_arr")
    dur("D received -> decoded", "t_c_arr", "t_c_dec")
    dur("D decoded -> shown", "t_c_dec", "t_shown")
    dur("TOTAL pose read -> shown", "t_read", "t_shown")
    dur("TOTAL head sample arrival -> shown", "t_arrive", "t_shown")
    P("-- how far ahead of its tick the game's frame was asked for: tg - t_read  " + stats([F[n]["tg"] - F[n]["t_read"] for n in ns if "t_read" in F[n]], ms))
    # head samples
    pred = sum(F[n]["predicted"] for n in ns)
    rep = sum(F[n]["repeated"] for n in ns)
    dupts = sum(1 for a, b in zip(ns, ns[1:]) if F[a]["ts"] == F[b]["ts"])
    ex = [F[n].get("ts_exact") for n in ns if F[n].get("ts_exact") is not None]
    P(f"-- head samples: predicted {pred}, repeated (waited, none arrived) {rep}, same ts as the previous frame {dupts}; "
      f"ts matches a client sample exactly {sum(1 for x in ex if x==1)}, only within 3 us {sum(1 for x in ex if x==0)}, none {sum(1 for x in ex if x==-1)}")
    ye = [abs(F[n]["yaw_err"]) for n in ns if "yaw_err" in F[n]]
    if ye:
        P("-- pose data: |yaw the app rendered with - yaw the client had at the frame's ts| mrad: " + stats(ye, 1e3))
    pe = [abs(F[n]["marker_err_px"]) for n in ns if "marker_err_px" in F[n]]
    pa = [abs(F[n]["marker_app_err_px"]) for n in ns if "marker_app_err_px" in F[n]]
    if pe:
        P("-- pixels: |decoded marker - where the client's pose for the frame's ts puts it| px (1760-px eye): " + stats(pe))
        P("   pixels: |decoded marker - where the app's own pose put it| px (pipeline fidelity):      " + stats(pa))
        bad = [n for n in ns if abs(F[n].get("marker_err_px", 0)) > 6]
        P(f"   frames with the marker > 6 px off the client's pose: {len(bad)} ({100*len(bad)/max(1,len(pe)):.1f}%)")
    ids = [(n, F[n]["dec_id"]) for n in ns if "dec_id" in F[n]]
    if ids:
        P(f"-- identity: decoded stamp == game frame number for {sum(1 for n, i in ids if (n & 0xFFFF) == i)} of {len(ids)} frames found by their stamp in the decoded stream")
    # shown order: frames from the past (a shown frame older than one shown before)
    shown = sorted((F[n]["t_shown"], n) for n in ns if "t_shown" in F[n])
    back = sum(1 for (a, na), (b, nb) in zip(shown, shown[1:]) if nb < na)
    gaps = [nb - na for (a, na), (b, nb) in zip(shown, shown[1:]) if nb > na]
    P(f"-- display order: {back} frames shown after a newer one (from the past); frame-number steps between consecutive shown frames: "
      + ", ".join(f"{k}x{v}" for k, v in sorted(__import__('collections').Counter(gaps).items())[:8]))
    age = [(t - F[n]["tg"]) for t, n in shown]
    P("-- age at display: shown time - the display time the game was asked for (ms): " + stats(age, ms))
    P(f"-- display ticks {len(D)}; repeats {sum(1 for x in D if x[2])} ({100*sum(1 for x in D if x[2])/max(1,len(D)):.1f}%)")
    if list_n:
        P(f"-- first {list_n} frames (times in ms after the first read)")
        t0 = F[ns[0]].get("t_read", 0)
        for n in ns[:list_n]:
            f = F[n]
            def t(k):
                v = f.get(k)
                return f"{(v - t0)*ms:8.2f}" if isinstance(v, float) and v == v else "       -"
            P(f"   {n:5d} ts..{f['ts']%100000:5d} {'P' if f['predicted'] else ' '}{'R' if f['repeated'] else ' '} tg {t('tg')} rel {t('t_release')} read {t('t_read')} sub {t('t_submit')} pub {t('t_publish')} "
              f"pick {t('t_picked')} send {t('t_send')} rcv {t('t_c_arr')} shown {t('t_shown')} {f.get('fate','?')}")
    out = "\n".join(lines)
    (d / "frames_report.txt").write_text(out + "\n")
    print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
