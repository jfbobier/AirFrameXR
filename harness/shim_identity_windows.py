#!/usr/bin/env python3
"""Frame identity per statistics window, read from a field shim log (debug on, VDXR fork build).

    python3 harness/shim_identity_windows.py <shim_*.log> [...] [--flip 12000]

The shim writes three cumulative lines every 900 frames it passes on: `frames N`, `pacing:` (same-pose duplicates seen /
dropped, free waits) and `app frames (VDXR)` (app submits, submits with no swapchain image released, released slices). This
prints the per-window differences, so the two identity tests can be compared frame for frame:

- nonew     submits in which VDXR saw no image released on any slice (the identity signal, PLAN_UNIVERSAL_OPENXR.md H1a)
- samepose  submits with the previous submit's exact render poses (the shim's test today, U0)
- 1-eye     real submits (an image released) that released one eye only: 2 x real - released slices (two eye swapchains)
- pose-only nonew - samepose: a new pose with no new picture (a lower bound; exact when every same-pose submit is also nonew)

Windows whose layer set changed (a quad loading screen: one slice, no projection pose) are marked `*` and left out of the
summary. `--flip` splits the summary at an app submit count (the AC Black Flag mod changes its rhythm at engine frame 12,000).
Field baseline that uses it: docs/PLAN_UNIVERSAL_OPENXR.md section 5.
"""
import re
import statistics
import sys
from datetime import datetime

LINE = re.compile(r'(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) \[\d+\] (.*)')


def parse(path):
    rows, cur, layers_changed = [], {}, False
    for raw in open(path, encoding='utf-8', errors='replace'):
        m = LINE.match(raw.rstrip('\r\n'))
        if not m:
            continue
        t, body = datetime.strptime(m[1], '%Y-%m-%d %H:%M:%S.%f'), m[2]
        if body.startswith('layers ('):
            layers_changed = True
        elif body.startswith('frames '):
            g = re.match(r'frames (\d+):', body)
            cur = {'t': t, 'frames': int(g[1])}
        elif body.startswith('pacing:') and cur:
            g = re.search(r'duplicate frames seen (\d+) dropped (\d+) \(pacing fix (\w+)', body)
            f = re.search(r'free waits (\d+)', body)
            if g:
                cur.update(seen=int(g[1]), dropped=int(g[2]), fix=g[3], free=int(f[1]) if f else 0)
        elif body.startswith('app frames (VDXR)') and cur:
            g = re.search(r'app frames \(VDXR\): (\d+), resubmitted with no new picture (\d+).*?newly released (\d+).*?'
                          r'xrWaitFrame return -> xrEndFrame ms p50 ([\d.]+) p95 ([\d.]+)', body)
            if g and 'seen' in cur:
                cur.update(app=int(g[1]), nonew=int(g[2]), released=int(g[3]), w2e50=float(g[4]), w2e95=float(g[5]),
                           layers_changed=layers_changed)
                rows.append(cur)
                layers_changed = False
            cur = {}
    return rows


def windows(rows):
    out = []
    for a, b in zip(rows, rows[1:]):
        d = {k: b[k] - a[k] for k in ('frames', 'seen', 'dropped', 'free', 'app', 'nonew', 'released')}
        dt = (b['t'] - a['t']).total_seconds()
        real = d['app'] - d['nonew']
        out.append(dict(t=b['t'], app_total=b['app'], dt=dt, fix=b['fix'], w2e50=b['w2e50'], w2e95=b['w2e95'],
                        skip=b['layers_changed'], real=real, one_eye=2 * real - d['released'],
                        pose_only=d['nonew'] - d['seen'], **d))
    return out


def summary(label, ws):
    ws = [w for w in ws if not w['skip'] and w['dt'] > 0]
    if not ws:
        print(f'  {label}: no window')
        return
    med = lambda f: statistics.median(f(w) for w in ws)
    print(f"  {label} ({len(ws)} windows): submits {med(lambda w: w['app'] / w['dt']):.1f}/s, new pictures "
          f"{med(lambda w: w['real'] / w['dt']):.1f}/s, nonew {med(lambda w: 100 * w['nonew'] / w['app']):.1f} %, "
          f"same pose {med(lambda w: 100 * w['seen'] / w['app']):.1f} %, dropped {med(lambda w: 100 * w['dropped'] / w['app']):.1f} %, "
          f"1-eye {med(lambda w: 100 * w['one_eye'] / max(1, w['real'])):.1f} % of new, "
          f"pose-only {med(lambda w: 100 * w['pose_only'] / w['app']):.1f} % of submits, wait->end p50 {med(lambda w: w['w2e50']):.2f} ms")


def main(argv):
    flip = None
    if '--flip' in argv:
        i = argv.index('--flip')
        flip = int(argv[i + 1])
        argv = argv[:i] + argv[i + 2:]
    if not argv:
        print(__doc__)
        return 2
    for path in argv:
        ws = windows(parse(path))
        print(f'== {path}')
        if not ws:
            print('  no app frames (VDXR) line: debug off, or not the VDXR fork build')
            continue
        print('  end       dt   submits nonew samepose dropped freewait  real released 1-eye pose-only  w2e p50   p95 fix')
        for w in ws:
            print(f"  {w['t']:%H:%M:%S} {w['dt']:5.1f} {w['app']:8} {w['nonew']:5} {w['seen']:8} {w['dropped']:7} {w['free']:8} "
                  f"{w['real']:5} {w['released']:8} {w['one_eye']:5} {w['pose_only']:9} {w['w2e50']:8.2f} {w['w2e95']:5.2f} {w['fix']}"
                  f"{' *' if w['skip'] else ''}")
        if flip is None:
            summary('all', ws)
        else:
            summary(f'before submit {flip}', [w for w in ws if w['app_total'] <= flip])
            summary(f'after submit {flip}', [w for w in ws if w['app_total'] - w['app'] >= flip])
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
