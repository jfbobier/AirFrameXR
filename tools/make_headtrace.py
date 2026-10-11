#!/usr/bin/env python3
"""Cut a replayable head trace out of a debug session's head_samples.csv:
    python3 tools/make_headtrace.py logs/<session>/head_samples.csv out.csv [--from T] [--to T] [--seconds 60]
Keeps the longest gap-free run (no 0.5 s hole), drops rows before tracking was valid (zero quaternion) and non-increasing timestamps.
The mock client replays it with --replay (poses, timestamps and the original arrival timing)."""
import csv, sys
import numpy as np
a = sys.argv[1:]
src, dst = a[0], a[1]
opt = lambda k, d=None: float(a[a.index(k) + 1]) if k in a else d
rows = list(csv.reader(open(src)))
hdr, rows = rows[0], rows[1:]
rows = [r for r in rows if np.linalg.norm([float(x) for x in r[2:6]]) > 0.5]
t0, t1 = opt("--from"), opt("--to")
rows = [r for r in rows if (t0 is None or float(r[0]) >= t0) and (t1 is None or float(r[0]) <= t1)]
out, last = [], -1
for r in rows:
    if int(r[1]) > last:
        out.append(r)
        last = int(r[1])
T = np.array([float(r[0]) for r in out])
gaps = np.where(np.diff(T) > 0.5)[0]
starts, ends = [0] + list(gaps + 1), list(gaps + 1) + [len(out)]
s, e = max(zip(starts, ends), key=lambda x: x[1] - x[0])
seg = [r for r in out[s:e] if float(r[0]) <= float(out[s][0]) + opt("--seconds", 60)]
with open(dst, "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(hdr)
    w.writerows(seg)
print(dst, len(seg), "rows,", round(float(seg[-1][0]) - float(seg[0][0]), 1), "s")
