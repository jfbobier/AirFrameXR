#!/usr/bin/env python3
"""Resample a head trace (harness/fixtures/headtraces/*.csv) to another tracking rate, e.g. a 100 Hz real recording to 60 Hz for a 60 fps scenario:
    python3 tools/resample_headtrace.py in.csv out.csv 60
Poses are slerped along the recording at the new timestamps; each new sample keeps the uplink delay (arrival minus timestamp, de-meaned) of the original
sample nearest in time, so the real timing pattern survives."""
import csv, sys
import numpy as np


def slerp(a, b, f):
    if float(np.dot(a, b)) < 0:
        b = -b
    q = a * (1 - f) + b * f
    return q / np.linalg.norm(q)


src, dst, hz = sys.argv[1], sys.argv[2], float(sys.argv[3])
rows = list(csv.reader(open(src)))
hdr, rows = rows[0], rows[1:]
t = np.array([float(r[0]) for r in rows])
ts = np.array([int(r[1]) for r in rows], dtype=float)
Q = np.array([[float(x) for x in r[2:6]] for r in rows])
P = np.array([[float(x) for x in r[6:9]] for r in rows])
delay = (t - ts * 1e-9)
delay -= np.median(delay)
period = 1e9 / hz
new_ts = np.arange(ts[0], ts[-1], period)
out = []
for k, x in enumerate(new_ts):
    i = int(np.searchsorted(ts, x))
    i = min(max(i, 1), len(ts) - 1)
    f = (x - ts[i - 1]) / (ts[i] - ts[i - 1])
    q = slerp(Q[i - 1], Q[i], f)
    p = P[i - 1] * (1 - f) + P[i] * f
    j = i - 1 if f < 0.5 else i
    arrival = t[0] + (x - ts[0]) * 1e-9 + delay[j]
    out.append([f"{arrival:.6f}", f"{int(x)}"] + [f"{v:.7f}" for v in q] + [f"{v:.4f}" for v in p])
with open(dst, "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(hdr)
    w.writerows(out)
print(f"{len(out)} samples at {hz} Hz -> {dst}")
