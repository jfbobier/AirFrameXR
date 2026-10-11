#!/usr/bin/env python3
"""Audio telemetry of a debug session: python3 harness/audio_report.py logs/debug/<session>
Needs audio_capture.csv and sched.csv (debug-flavour host). The capture callback is WASAPI loopback of the default output: while a game
plays it fires every ~10 ms; a long gap with frames missing is audio the headset never gets (it skips), a gap with a late catch-up burst is
a stall that is recovered but arrives late/bunched. sched.csv says whether normal-priority threads were being starved at the time."""
import csv, sys
from pathlib import Path

d = Path(sys.argv[1])
def rd(n):
    try:
        return list(csv.DictReader(open(d / n)))
    except Exception:
        return []
cap, sch = rd("audio_capture.csv"), rd("sched.csv")
cb = [r for r in cap if r["kind"] == "C"]
start = [r for r in cap if r["kind"] == "S"]
print(f"== audio telemetry: {d}")
if start:
    print("capture started", len(start), "time(s); first: rate/channels", start[0]["frames"], start[0]["send_failed"])
if not cb:
    print("no capture callbacks (no audio_capture.csv: not a debug-flavour session, or nothing played)")
else:
    rate = int(start[0]["frames"]) if start else 48000
    gaps = [int(r["gap_us"]) / 1000 for r in cb if int(r["gap_us"]) > 0]
    gaps_s = sorted(gaps)
    q = lambda p: gaps_s[min(len(gaps_s) - 1, int(p * len(gaps_s)))]
    print(f"callbacks {len(cb)}; gap ms p50 {q(.5):.1f} p95 {q(.95):.1f} p99 {q(.99):.1f} max {gaps_s[-1]:.1f}; >20 ms {sum(1 for g in gaps if g>20)}, >50 ms {sum(1 for g in gaps if g>50)}, >100 ms {sum(1 for g in gaps if g>100)}")
    print(f"send failures {sum(int(r['send_failed']) for r in cb)}; callback time us p50 {sorted(int(r['callback_us']) for r in cb)[len(cb)//2]} max {max(int(r['callback_us']) for r in cb)}")
    # continuity: frames captured vs wall time between callbacks
    t = [float(r["t"]) for r in cb]
    fr = [int(r["frames"]) for r in cb]
    tot_t = t[-1] - t[0]
    print(f"captured {sum(fr)} frames in {tot_t:.1f} s = {sum(fr)/tot_t:.0f}/s (rate {rate}/s; less = silence, gaps in the loopback)")
    worst = sorted(range(1, len(cb)), key=lambda i: -(int(cb[i]["gap_us"])))[:8]
    print("longest gaps (t, gap ms, frames in the callback after it, frames expected at the rate):")
    for i in sorted(worst):
        g = int(cb[i]["gap_us"]) / 1000
        print(f"   t {t[i]:.3f}  gap {g:7.1f}  frames {fr[i]:5d}  expected {int(rate*g/1000):5d}")
if sch:
    p99 = [int(r["p99_us"]) for r in sch]
    mx = [int(r["max_us"]) for r in sch]
    ov = sum(int(r["over_2ms"]) for r in sch)
    print(f"sched probe: {len(sch)} s; 10 ms sleep overshoot p99 per second: median {sorted(p99)[len(p99)//2]} us, worst {max(p99)} us; max {max(mx)} us; wakeups over 2 ms: {ov}; seconds with p99 > 2 ms: {sum(1 for x in p99 if x > 2000)}")
    # overlap with audio gaps
    if cb:
        bad = [float(r["t"]) for r in sch if int(r["p99_us"]) > 2000]
        gapt = [t[i] for i in range(1, len(cb)) if int(cb[i]["gap_us"]) > 30000]
        near = sum(1 for g in gapt if any(abs(g - b) < 1.5 for b in bad))
        print(f"audio gaps > 30 ms: {len(gapt)}; of them within 1.5 s of a starved second: {near}")
