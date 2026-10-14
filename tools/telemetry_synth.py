#!/usr/bin/env python3
"""Synthetic AVP telemetry session in the exact file formats the host and the telemetry client write (a fixture for
tools/telemetry_report.py; no headset involved). The "effects" are made up so the report has something to find:
host-mode re-stamp trembles more (dcomp up), shim mode removes repeats, focal 2.5 m halves the translation residual,
head samples arrive with an AWDL-like 1 Hz burst when --awdl is given.

    python3 tools/telemetry_synth.py <out dir> [--awdl] [--seconds-scale 1.0]
"""
import argparse
import math
import os
import random

WARMUP, SEG = 120, 45
SCHEDULE = ["off", "shim", "host", "off", "shim", "host",
            "host", "shim", "off", "host", "shim", "off",
            "off", "off", "shim", "shim",
            "shim", "shim", "off", "off"]
# client conditions per segment: tier A: focal default for segments 1-3 and 7-9 (cycle halves), 2.5 for 4-6 and 10-12; tier B: pick oldest/newest
FOCAL = [0] * 3 + [2.5] * 3 + [0] * 3 + [2.5] * 3 + [0] * 8
PICK = ["oldest"] * 12 + ["oldest", "newest", "oldest", "newest", "newest", "oldest", "newest", "oldest"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--awdl", action="store_true")
    ap.add_argument("--seconds-scale", type=float, default=1.0)
    a = ap.parse_args()
    random.seed(7)
    os.makedirs(a.out, exist_ok=True)
    warm, seg_s = WARMUP * a.seconds_scale, SEG * a.seconds_scale
    host_t0, client_t0 = 5000.0, 90000.0  # QPC seconds on the host, the headset's clock
    fps = 90.0
    log, host, heads = [], [], []
    n = 0

    def pt(host_t, text):
        nonlocal n
        n += 1
        verb, _, rest = text.partition(" ")
        log.append(f"{host_t:.6f} [pt] {verb} n={n} {rest}".rstrip())

    pt(host_t0, "start ver=1 build=synth0 renderer=rk telemetry=1")
    log.append(f"{host_t0 + 0.01:.6f} [tel] armed warmup_s={WARMUP} segment_s={SEG} segments=20")
    log.append(f"{host_t0 + 0.01:.6f} [tel] cond seg=0 mode=off t={host_t0 + 0.01:.6f}")
    pt(host_t0, f"cond seg=0 focal=0 pick=oldest t={client_t0:.3f}")
    total = warm + seg_s * 20
    frames = int(total * fps)
    ts_ns = 1_000_000_000_000
    cur_seg, batch, host_n = -1, [], 0
    cols = "present_s,timestamp_ns,age_ms,repeated,had_send,walkback_ms,anchor_minus_used_ms,fallback,extrap_deg,extrap_mm,comp_deg,comp_mm,dcomp_deg,dcomp_mm,head_deg_s,gpu_ms,decode_ms,queue_ms"
    last_comp_mm = last_comp_deg = 0.0
    last_ts = None
    deferred = 0

    def flush(seg, t):
        nonlocal batch
        if batch:
            pt(t, f"rows seg={seg} cols={cols} | " + ";".join(batch))
            batch = []

    for i in range(frames):
        t = i / fps
        if t < warm:
            seg, mode = 0, "off"
        else:
            idx = int((t - warm) / seg_s)
            seg, mode = idx + 1, SCHEDULE[idx]
        if seg != cur_seg:
            flush(cur_seg, host_t0 + t)
            cur_seg = seg
            focal = FOCAL[seg - 1] if seg else 0
            pick = PICK[seg - 1] if seg else "oldest"
            if seg:
                log.append(f"{host_t0 + t:.6f} [tel] cond seg={seg} mode={mode} t={host_t0 + t:.6f}")
                pt(host_t0 + t, f"cond seg={seg} focal={focal:g} pick={pick} t={client_t0 + t:.3f}")
        # head speed profile: a slow sweep plus bursts
        head_deg_s = abs(40.0 * math.sin(t * 0.7)) * (0.3 + abs(math.sin(t * 0.13)))
        # repeats: off mode repeats ~8 % of frames, shim ~1 %, host ~3 %
        rep_p = {"off": 0.08, "shim": 0.01, "host": 0.03}[mode]
        repeated = 1 if (random.random() < rep_p and last_ts is not None) else 0
        orig_ts = ts_ns if not repeated else last_ts
        restamped = 0
        final_ts = orig_ts
        warp_mdeg = 0.0
        if mode == "host" and repeated:
            final_ts = ts_ns + 11_111_111
            restamped, warp_mdeg, repeated = 1, head_deg_s * 11.1, 0
        if not repeated:
            last_ts = final_ts
        ts_ns += 11_111_111
        base_mm = 0.6 + head_deg_s * 0.05
        focal_scale = 0.5 if (seg and FOCAL[seg - 1] == 2.5) else 1.0
        trem = {"off": 1.0, "shim": 1.0, "host": 2.6}[mode]
        comp_mm = base_mm * focal_scale * (1 + random.random() * 0.5)
        comp_deg = 0.05 + head_deg_s * 0.04 * (1 + random.random() * 0.3)
        d_mm = abs(comp_mm - last_comp_mm) * trem
        d_deg = abs(comp_deg - last_comp_deg)
        last_comp_mm, last_comp_deg = comp_mm, comp_deg
        age = 92 + random.gauss(0, 3) + (3 if mode == "shim" else 0)
        row = [client_t0 + t, final_ts, age, repeated, 1, 0.0 if random.random() > 0.01 else 5.0, random.random() * 0.2, 0,
               head_deg_s * 0.08, base_mm * 4, comp_deg, comp_mm, d_deg, d_mm, head_deg_s, 3.0 + random.random(), 6.0 + random.random(),
               2.0 + random.random() * 3]
        batch.append(",".join(f"{v:.4f}" if isinstance(v, float) else str(v) for v in row))
        if len(batch) >= 12:
            flush(cur_seg, host_t0 + t)
        if i % 90 == 0:
            pt(host_t0 + t, f"sum seg={cur_seg} frames=90 repeat=3 nosend=0 walkback=1 fallback=0 age_p50={age:.1f} age_p95={age + 5:.1f} "
                            f"ctrlL_hz=88.5 ctrlR_hz=89.1 thermal=1 gpu_ms_p95=4.2 decode_ms_p95=7.1")
        # host row (every sent frame: the repeated ones are sent too, with the same stamp)
        host.append(f"{host_t0 + t + 0.02:.6f},{seg},{mode},{host_n},{orig_ts},{final_ts},{restamped},{warp_mdeg:.1f},{random.random() * 3:.3f},"
                    f"{4.0 + random.random():.3f},{random.random():.3f},{20000 + int(random.random() * 9000)},0,{deferred},0")
        host_n += 1
    flush(cur_seg, host_t0 + total)
    pt(host_t0 + total, "end")
    log.append(f"{host_t0 + total:.6f} [tel] end t={host_t0 + total:.6f}")

    # head samples on the stamp grid (one per client sample, 90 Hz) with jittery arrival; optional AWDL burst once a second (a 30-60 ms hole)
    k, delay = 0, 0.0
    while k / fps < total:
        arr = host_t0 + k / fps + 0.004 + random.random() * 0.004 + delay
        if a.awdl and (k / fps) % 1.0 < 1 / fps:
            delay = 0.03 + random.random() * 0.03
            arr += delay
        delay *= 0.5
        q = [0.0, math.sin(k / fps * 0.05) * 0.1, 0.0, math.sqrt(1 - (math.sin(k / fps * 0.05) * 0.1) ** 2)]
        heads.append(f"{arr:.6f},{1_000_000_000_000 + k * 11_111_111},{q[0]:.6f},{q[1]:.6f},{q[2]:.6f},{q[3]:.6f},{0.01 + 0.001 * (k % 7):.5f},1.6,0.02")
        k += 1

    with open(os.path.join(a.out, "telemetry_client.log"), "w") as f:
        f.write("\n".join(sorted(log, key=lambda l: float(l.split()[0]))) + "\n")
    with open(os.path.join(a.out, "telemetry_host.csv"), "w") as f:
        f.write("t_send,seg,mode,frame,orig_ts_ns,final_ts_ns,restamped,warp_mdeg,queue_wait_ms,encode_ms,hold_ms,bytes,idr,deferred_total,dropped_total\n")
        f.write("\n".join(host) + "\n")
    with open(os.path.join(a.out, "head_samples.csv"), "w") as f:
        f.write("t,ts_ns,qx,qy,qz,qw,px,py,pz\n")
        f.write("\n".join(heads) + "\n")
    print(f"wrote {a.out}: {len(log)} client lines, {len(host)} host rows, {len(heads)} head samples")


if __name__ == "__main__":
    main()
