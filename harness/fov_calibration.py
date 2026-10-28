#!/usr/bin/env python3
"""FoV calibration geometry (docs/FOV_CALIBRATION.md): coverage and savings of a rendered rectangle R against the visible window V.

Everything is in tangent space for one eye (x = outward from the nose, y = up). V is a polygon, D the declared frustum, R a rectangle given as
(outer, inner, upper, lower) tangents.

    python3 harness/fov_calibration.py --ellipse-from-axes          # phase 0: V = ellipse through the four measured axis crossings (docs/AVP_FOV.md)
    python3 harness/fov_calibration.py --ellipse-from-axes --curve  # plus the coverage/savings table of the minimum-area rectangles
    python3 harness/fov_calibration.py --sweep docs/fov_sweeps/<file>.csv [--sweep other.csv ...]   # phase 3: V from the sweep(s), per eye

Area is counted on a fine grid with an integral image, so the area of any rectangle is four lookups (the search below visits millions).
"""
import argparse, math, sys
import numpy as np

DECLARED = (1.787, 1.020, 1.219, 1.020)    # outer, inner, up, down: docs/AVP_FOV.md, left eye
MEASURED = (0.85, 0.84, 0.81, 0.68)        # tight row of docs/AVP_FOV.md (visible, left eye)
RECOMMENDED = (0.94, 0.93, 0.90, 0.76)     # visible + 3 deg, what the profile and the client constants carry
STEP = 0.002                               # grid cell in tangent units (0.1 deg near the axis)


def ellipse_from_axes(outer, inner, up, down, n=360):
    """Axis-aligned ellipse through the four crossings (centre of the box they span, not the view axis)."""
    cx, cy, a, b = (outer - inner) / 2, (up - down) / 2, (outer + inner) / 2, (up + down) / 2
    t = np.linspace(0, 2 * math.pi, n, endpoint=False)
    return np.stack([cx + a * np.cos(t), cy + b * np.sin(t)], axis=1)


def polygon_from_rays(deg, tangent):
    """V from a sweep: ray azimuth (deg, 0 = outward, counter-clockwise) and the tangent length where the arc vanished along it.
    Tangent here is the radius in tangent space, not the angle: the probe logs the tangent coordinates of the vanishing point."""
    a = np.radians(np.asarray(deg, float))
    r = np.asarray(tangent, float)
    return np.stack([r * np.cos(a), r * np.sin(a)], axis=1)


class Window:
    """A polygon rasterised once; area of any axis-aligned rectangle by integral image."""

    def __init__(self, poly, extent=2.2):
        self.x0 = -extent
        n = int(2 * extent / STEP)
        xs = self.x0 + (np.arange(n) + 0.5) * STEP
        gx, gy = np.meshgrid(xs, xs)
        self.mask = _inside(poly, gx, gy)
        self.ii = np.zeros((n + 1, n + 1))
        self.ii[1:, 1:] = self.mask.cumsum(0).cumsum(1)
        self.n = n
        self.area = self.mask.sum() * STEP * STEP

    def idx(self, v):
        return np.clip(np.round((np.asarray(v) - self.x0) / STEP).astype(int), 0, self.n)

    def rect_area(self, xl, xr, yb, yt):
        """Area of V inside [xl, xr] x [yb, yt]; arguments may be arrays."""
        i0, i1, j0, j1 = self.idx(xl), self.idx(xr), self.idx(yb), self.idx(yt)
        return (self.ii[j1, i1] - self.ii[j0, i1] - self.ii[j1, i0] + self.ii[j0, i0]) * STEP * STEP


def _inside(poly, gx, gy):
    """Even-odd point in polygon, vectorised over the grid."""
    inside = np.zeros(gx.shape, bool)
    px, py = poly[:, 0], poly[:, 1]
    for k in range(len(poly)):
        x1, y1, x2, y2 = px[k], py[k], px[(k + 1) % len(poly)], py[(k + 1) % len(poly)]
        if y1 == y2:
            continue
        cross = ((y1 > gy) != (y2 > gy)) & (gx < (x2 - x1) * (gy - y1) / (y2 - y1) + x1)
        inside ^= cross
    return inside


def rect_edges(r):
    """(outer, inner, upper, lower) -> x range, y range (x grows outward)."""
    return -r[1], r[0], -r[3], r[2]


def coverage(win, r):
    xl, xr, yb, yt = rect_edges(r)
    return float(win.rect_area(xl, xr, yb, yt) / win.area)


def rect_area(r):
    return (r[0] + r[1]) * (r[2] + r[3])


def savings(r, declared=DECLARED):
    return 1 - rect_area(r) / rect_area(declared)


def min_area_rect(win, target, lo=0.4, hi=1.2, step=0.02):
    """Rectangle of minimum area with coverage >= target: coarse grid over the four edges, then refinement at the window's own resolution."""
    best = None
    ax = np.arange(lo, hi + 1e-9, step)
    for pass_step, span in ((step, None), (STEP * 2, step * 1.5)):
        if span is None:
            o, i, u, d = np.meshgrid(ax, ax, ax, ax, indexing="ij")
        else:
            c = best
            f = lambda v: np.arange(v - span, v + span + 1e-9, pass_step)
            o, i, u, d = np.meshgrid(f(c[0]), f(c[1]), f(c[2]), f(c[3]), indexing="ij")
        cov = win.rect_area(-i, o, -d, u) / win.area
        area = (o + i) * (u + d)
        area = np.where(cov >= target, area, np.inf)
        k = np.unravel_index(np.argmin(area), area.shape)
        if not np.isfinite(area[k]):
            return None
        best = (float(o[k]), float(i[k]), float(u[k]), float(d[k]))
    return best


def curve(win, targets, declared=DECLARED):
    rows = []
    for t in targets:
        r = min_area_rect(win, t)
        if r:
            rows.append((t, r, coverage(win, r), savings(r, declared)))
    return rows


def load_sweeps(paths, max_deg=60.0):
    """Per eye and ray, the tightest window seen across the files (rows that timed out or are beyond max_deg are outliers and dropped).
    Returns {eye: {theta: [ecc per file]}}: the per-ray spread is the uncertainty once there are several fits."""
    import csv
    rays = {0: {}, 1: {}}
    for path in paths:
        for r in csv.DictReader(open(path)):
            if r["timeout"] == "1" or float(r["ecc_deg"]) > max_deg:
                continue
            rays[int(r["eye"])].setdefault(float(r["theta_deg"]), []).append(float(r["ecc_deg"]))
    return rays


def poly_of(rays_eye, how=min):
    th = sorted(rays_eye)
    return polygon_from_rays(th, [math.tan(math.radians(how(rays_eye[t]))) for t in th])


def report_window(name, poly, curve_targets):
    win = Window(poly)
    print(f"\n== {name}: area {win.area:.3f}, today's values cover {100 * coverage(win, RECOMMENDED):.1f} % (save {100 * savings(RECOMMENDED):.1f} %), "
          f"tight box would be {tuple(round(v, 2) for v in _bbox(poly))}")
    print("target  coverage  savings   outer inner upper lower   (minimum-area rectangle)")
    for t, r, c, sv in curve(win, curve_targets):
        print(f"{100 * t:5.0f} %  {100 * c:6.2f} %  {100 * sv:5.1f} %   {r[0]:.2f}  {r[1]:.2f}  {r[2]:.2f}  {r[3]:.2f}")
    return win


def _bbox(poly):
    return (poly[:, 0].max(), -poly[:, 0].min(), poly[:, 1].max(), -poly[:, 1].min())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sweep", action="append", help="sweep CSV (xr_probe XR_PROBE_FOVGRID=sweep); repeat for several fits")
    ap.add_argument("--ellipse-from-axes", action="store_true", help="phase 0: V = ellipse through the four measured crossings")
    ap.add_argument("--axes", type=float, nargs=4, default=MEASURED, metavar=("OUTER", "INNER", "UP", "DOWN"))
    ap.add_argument("--curve", action="store_true", help="coverage/savings of the minimum-area rectangles, 90 to 100 %% in 1 %% steps")
    a = ap.parse_args()
    if a.sweep:
        rays = load_sweeps(a.sweep)
        targets = [x / 100 for x in (90, 92, 94, 95, 96, 97, 98, 99, 100)]
        print(f"sweeps: {len(a.sweep)} file(s), rays kept per eye: {len(rays[0])} / {len(rays[1])}")
        for e, nm in ((0, "left eye"), (1, "right eye")):
            report_window(nm, poly_of(rays[e]), targets)
        both = {t: [max(max(rays[0].get(t, [0])), max(rays[1].get(t, [0])))] for t in set(rays[0]) | set(rays[1])}
        report_window("shared set: window = per-ray larger of the two eyes (covers what either eye sees)", poly_of(both), targets)
        return 0
    if not a.ellipse_from_axes:
        ap.error("give --sweep or --ellipse-from-axes")
    win = Window(ellipse_from_axes(*a.axes))
    bbox = tuple(a.axes)
    print(f"V: ellipse through outer {a.axes[0]} inner {a.axes[1]} up {a.axes[2]} down {a.axes[3]}  area {win.area:.3f} (box {rect_area(bbox):.3f}, ellipse/box {win.area / rect_area(bbox):.3f}, pi/4 = {math.pi / 4:.3f})")
    print(f"D: declared {DECLARED}  area {rect_area(DECLARED):.3f}")
    for name, r in (("tight box (100 %)", bbox), ("recommended (today)", RECOMMENDED), ("recommended - 1 deg", tuple(math.tan(math.atan(v) - math.radians(2)) for v in RECOMMENDED))):
        print(f"{name:22s} {tuple(round(v, 3) for v in r)}  coverage {100 * coverage(win, r):6.2f} %  savings {100 * savings(r):5.1f} %")
    if a.curve:
        print("\ntarget  coverage  savings   outer inner upper lower   (minimum-area rectangle)")
        for t, r, c, s in curve(win, [x / 100 for x in range(90, 101)]):
            print(f"{100 * t:5.0f} %  {100 * c:6.2f} %  {100 * s:5.1f} %   {r[0]:.2f}  {r[1]:.2f}  {r[2]:.2f}  {r[3]:.2f}")
        ref = coverage(win, RECOMMENDED)
        print(f"\ntoday's values sit at {100 * ref:.2f} % coverage, {100 * savings(RECOMMENDED):.1f} % savings")


if __name__ == "__main__":
    sys.exit(main())
