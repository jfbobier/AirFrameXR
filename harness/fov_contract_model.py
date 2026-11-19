"""Python reference of the launcher's stream sizing for the Vision Pro (tools/gui/PipelineModel.cs: PictureEdgesFoveation.ChooseEye and
RateMapFoveation.Milder, AirFrameXR 1.3.11), plus the what-if variants of docs/FOV_CONTRACT_PLAN.md (Sharper / Faster, foveation that follows
Apple's map inside the window, a lower reference bits per pixel). No builder, no C#: numpy only.

    python3 harness/fov_contract_model.py --selftest   reproduces the 2026-11-14 SWGR compile line (2176 x 1760, centre 0.798 x 0.818, shift
                                                       0.527 / 0.135, edge ratio 3.2 / 4.0, encoded 1888 x 1536 per eye); a few minutes
    python3 harness/fov_contract_model.py --table      the plan's section 3 table (fixed eye sizes: seconds)
    python3 harness/fov_contract_model.py --phase0     the plan's phase-0 rows (bitrate-sized: a few minutes)

Densities are at the centre of the view: px / (tan left + tan right) x pi / 180, per axis. Never px / degrees: over the Vision Pro's frustum that reads
about 1.5 x the centre value.
"""
import json, math, sys
from pathlib import Path
import numpy as np

f32 = np.float32
PROFILE = Path(__file__).resolve().parent.parent / "profiles" / "apple-vision-pro.profile.json"
MAX_W, MAX_H = 4096, 8192                     # PipelineModel.MaxEyeWidth / MaxEyeHeight
MARGIN = 0.6                                  # RateMapFoveation.Margin
ALVR_C = (0.45 * 1.1, 0.40 * 1.1)             # RateMapFoveation.AlvrCenter
ALVR_E = (4.0 * 0.8, 5.0 * 0.8)               # RateMapFoveation.AlvrEdge
WIN_MARGIN_DEG = 2.0                          # RateMapFoveation.WindowMarginDeg
# the host's Vision Pro views (tools/host/src/main.rs AVP_DEFAULT_FOV: left eye l r u d, right eye l r u d), per edge averaged over the eyes
_HD = [1.7823, 1.0186, 1.2169, 1.0186, 1.0249, 1.7995, 1.2253, 1.0249]
AVP_FRUSTUM = ((_HD[0] + _HD[5]) / 2, (_HD[1] + _HD[4]) / 2, (_HD[2] + _HD[6]) / 2, (_HD[3] + _HD[7]) / 2)   # outer, inner, upper, lower
QUALITY_WINDOW = (1.22, 0.81, 0.90, 0.77)     # profiles/apple-vision-pro.quality-fov.profile.json
PERFORMANCE_WINDOW = (0.97, 0.71, 0.69, 0.63)  # profiles/apple-vision-pro.profile.json
BASE_EYE = (3648, 3200)                       # the Vision Pro's default stream size (golden master)


def load_ratemap(path=PROFILE):
    p = json.load(open(path))
    rm = p["ratemap"]
    P, L = rm["frustum_tan"], rm["logical_px"]
    return {"P": tuple(P), "density": (L[0] / (P[0] + P[1]), L[1] / (P[2] + P[3])), "x": rm["x"], "y": rm["y"],
            "bpp": p["stream_budget"]["reference_bits_per_pixel"]}


def deg(t): return math.degrees(math.atan(t))
def align32(v): return int(math.floor(v / 32.0)) * 32


def ppd(eye, box):
    """centre px/deg per axis of an eye size over a box (outer, inner, upper, lower tangents)"""
    return (eye[0] / (box[0] + box[1]) * math.pi / 180, eye[1] / (box[2] + box[3]) * math.pi / 180)


def rate_at(tbl, d):
    if d <= tbl[0][0]: return tbl[0][1]
    if d >= tbl[-1][0]: return tbl[-1][1]
    for a, b in zip(tbl, tbl[1:]):
        if a[0] <= d <= b[0]: return a[1] + (b[1] - a[1]) * (d - a[0]) / (b[0] - a[0])
    return tbl[-1][1]


def rate(rm, axis, d): return rate_at(rm["x"], d) if axis == 0 else rate_at(rm["y"], d)


def ffr_vars(eyeW, eyeH, csX, csY, shX, shY, erX, erY):
    """ALVR's FFR.cpp CalculateFoveationVars in float32, as PipelineModel.FfrVars: optimized eye w / h, aligned centre x / y, aligned shift x / y"""
    tw, th = f32(eyeW), f32(eyeH)
    cx, cy, sx, sy, rx, ry = (f32(v) for v in (csX, csY, shX, shY, erX, erY))
    ex = f32(tw - f32(cx * tw)); ey = f32(th - f32(cy * th))
    cxa = f32(1.0 - math.ceil(float(ex) / (float(rx) * 2.0)) * (float(rx) * 2.0) / float(tw))
    cya = f32(1.0 - math.ceil(float(ey) / (float(ry) * 2.0)) * (float(ry) * 2.0) / float(th))
    exa = f32(tw - f32(cxa * tw)); eya = f32(th - f32(cya * th))
    sxa = f32(math.ceil(float(f32(sx * exa)) / (float(rx) * 2.0)) * (float(rx) * 2.0) / float(exa)) if exa > 0 else f32(0)
    sya = f32(math.ceil(float(f32(sy * eya)) / (float(ry) * 2.0)) * (float(ry) * 2.0) / float(eya)) if eya > 0 else f32(0)
    fsx = f32(float(cxa) + (1.0 - float(cxa)) / float(rx)); fsy = f32(float(cya) + (1.0 - float(cya)) / float(ry))
    ow = f32(fsx * tw); oh = f32(fsy * th)
    return (int(math.ceil(float(f32(ow / f32(32))))) * 32, int(math.ceil(float(f32(oh / f32(32))))) * 32,
            float(cxa), float(cya), float(sxa), float(sya))


def band_params(start, width):
    if not (width < 1.0 - 1e-9): return (min(1.0, max(0.0, width)), 0.0)
    return (width, 2.0 * start / (1.0 - width) - 1.0)


def fov_f(e, cs, sh, er):
    """ALVR's compress shader, output -> source mapping of one axis"""
    c0 = (1.0 - cs) / 2.0; c1 = (er - 1.0) * c0 * (sh + 1.0) / er; c2 = (er - 1.0) * cs + 1.0
    lo = c0 * (sh + 1.0) / c2; hi = c0 * (sh - 1.0) / c2 + 1.0; center = e * c2 / er + c1
    d2 = e * c2; d3 = (e - 1.0) * c2 + 1.0
    if e < lo: g1 = e / lo; return g1 * center + (1.0 - g1) * d2
    if e > hi: g2 = (1.0 - e) / (1.0 - hi); return g2 * center + (1.0 - g2) * d3
    return center


def axis_deg(s, loT, hiT, axis): return deg(-loT + s * (loT + hiT)) if axis == 0 else deg(hiT - s * (loT + hiT))


def axis_loss(cs, sh, er, loT, hiT, axis, rm, m, n, free=None):
    fs = cs + (1.0 - cs) / er
    es = [i / (n - 1) for i in range(n)]; src = [fov_f(e, cs, sh, er) for e in es]
    worst = 0.0
    for i in range(n):
        a, b = max(0, i - 1), min(n - 1, i + 1)
        r = (src[b] - src[a]) / (es[b] - es[a]) / fs
        d = axis_deg(src[i], loT, hiT, axis)
        allow = max(1.0, m / max(1e-6, rate(rm, axis, d)))
        if free is not None and (d < free[0] or d > free[1]): allow = 1e9
        worst = max(worst, r / allow)
    return worst


def band_uv(view, axis, rm, contour):
    loT, hiT = (view[0], view[1]) if axis == 0 else (view[3], view[2])
    loD, hiD = -deg(loT), deg(hiT)
    first = last = None; near, near_abs = 0, 1e9
    for i in range(2001):
        d = loD + (hiD - loD) * i / 2000
        if abs(d) < near_abs: near_abs, near = abs(d), d
        if rate(rm, axis, d) >= contour:
            if first is None: first = d
            last = d
    if first is None: first = last = near
    a, b, span = math.tan(math.radians(first)), math.tan(math.radians(last)), loT + hiT
    return ((a + loT) / span, (b + loT) / span) if axis == 0 else ((hiT - b) / span, (hiT - a) / span)


def _worst(size, cs, sh, er, loT, hiT, axis, rm, m, free):
    ow, _, cx, _, sx, _ = ffr_vars(size, size, cs, cs, sh, sh, er, er)
    return axis_loss(cx, sx, er, loT, hiT, axis, rm, m, 801, free), ow   # PipelineModel: 801 samples


def milder(view, eye, rm, window=None, margin=MARGIN, inside_follow_map=False):
    """RateMapFoveation.Milder: (cs x, cs y, shift x, shift y, er x, er y). window = the restriction (outer, inner, upper, lower) or None.
    inside_follow_map: the plan's fix, the map's contours inside the window instead of the whole window at full density (outside it stays free)."""
    o = [0.0] * 6
    for axis in (0, 1):
        loT, hiT = (view[0], view[1]) if axis == 0 else (view[3], view[2])
        span, size = loT + hiT, eye[axis]
        free = None
        if window is not None:
            gLo = window[0] if axis == 0 else window[3]; gHi = window[1] if axis == 0 else window[2]
            gLo = min(loT, math.tan(math.atan(gLo) + math.radians(WIN_MARGIN_DEG))); gHi = min(hiT, math.tan(math.atan(gHi) + math.radians(WIN_MARGIN_DEG)))
            free = (-deg(gLo), deg(gHi))
            if inside_follow_map:
                bands = [band_uv(view, axis, rm, k / 100.0) for k in range(30, 96, 5)]
            else:
                bands = [((loT - gLo) / span, (loT + gHi) / span) if axis == 0 else ((hiT - gHi) / span, (hiT + gLo) / span)]
        else:
            bands = [band_uv(view, axis, rm, k / 100.0) for k in range(30, 96, 5)]
        best = None
        for b in bands:
            u0, u1 = max(0, min(1, b[0])), max(0, min(1, b[1]))
            if u1 - u0 < ALVR_C[axis]:
                mid, w = (u0 + u1) / 2, ALVR_C[axis]
                u0 = max(0, min(1 - w, mid - w / 2)); u1 = u0 + w
            cs, sh = band_params(u0, u1 - u0)
            if _worst(size, cs, sh, 1.0, loT, hiT, axis, rm, margin, free)[0] > 1 + 1e-6: continue
            lo, hi = 1.0, ALVR_E[axis]
            if _worst(size, cs, sh, hi, loT, hiT, axis, rm, margin, free)[0] <= 1 + 1e-6: er = hi
            else:
                for _ in range(22):
                    mid = (lo + hi) / 2
                    if _worst(size, cs, sh, mid, loT, hiT, axis, rm, margin, free)[0] <= 1 + 1e-6: lo = mid
                    else: hi = mid
                er = lo
            px = _worst(size, cs, sh, er, loT, hiT, axis, rm, margin, free)[1]
            if best is None or px < best[3]: best = (cs, sh, er, px)
        best = best or (ALVR_C[axis], 0.0, 1.0, 0)
        o[axis], o[2 + axis], o[4 + axis] = best[0], best[1], best[2]
    return o


def encoded(eye, g):
    ow, oh, *_ = ffr_vars(eye[0], eye[1], *g)
    return ow, oh


def choose_eye(view, rm, mbps, fps, window=None, bpp=None, **kw):
    """PictureEdgesFoveation.ChooseEye: the eye size whose foveated frame is bitrate / (fps x bits per pixel) pixels, equal density on both axes"""
    bpp = bpp or rm["bpp"]
    budget = mbps * 1e6 / (fps * bpp); sx, sy = view[0] + view[1], view[2] + view[3]
    def eye_at(d): return (max(256, min(MAX_W, align32(d * sx))), max(256, min(MAX_H, align32(d * sy))))
    def px(e):
        ow, oh = encoded(e, milder(view, e, rm, window, **kw)); return 2.0 * ow * oh
    hi, lo = max(rm["density"]), 600.0
    if px(eye_at(hi)) <= budget: best = eye_at(hi)
    elif px(eye_at(lo)) >= budget: best = eye_at(lo)
    else:
        for _ in range(20):
            mid = (lo + hi) / 2
            if px(eye_at(mid)) <= budget: lo = mid
            else: hi = mid
        best = eye_at(lo)
    g = milder(view, best, rm, window, **kw)
    return best, g, encoded(best, g)


def mode_eye(mode, window, rm, frustum=AVP_FRUSTUM, base=BASE_EYE):
    """the plan's two anchors: Faster keeps the base density (base eye over the frustum), Sharper keeps the base pixel count over the window,
    each capped at the headset's centre density (Apple's full-rate density)"""
    cap = rm["density"]
    mode = {"balanced": "faster", "sharp": "sharper"}.get(mode, mode)   # 1.4 names
    if mode in ("faster", "performance"):
        share = 0.75 if mode == "performance" else 1.0   # StreamDensity.PerformanceShare
        d = (share * base[0] / (frustum[0] + frustum[1]), share * base[1] / (frustum[2] + frustum[3]))
    else:
        d = (base[0] / (window[0] + window[1]), base[1] / (window[2] + window[3]))
    d = (min(d[0], cap[0]), min(d[1], cap[1]))
    return (min(MAX_W, align32(d[0] * (window[0] + window[1]) + 1e-6)), min(MAX_H, align32(d[1] * (window[2] + window[3]) + 1e-6)))   # StreamDensity.Eye


def row(name, eye, g, box, mbps=275, fps=100):
    enc = encoded(eye, g); mpx = 2 * enc[0] * enc[1] / 1e6; p = ppd(eye, box)
    print(f"| {name} | {eye[0]} x {eye[1]} | {2 * enc[0]} x {enc[1]} = {mpx:.1f} | {mbps * 1e6 / (fps * mpx * 1e6):.2f} | {p[0]:.1f} x {p[1]:.1f} |", flush=True)


def main(argv):
    rm = load_ratemap()
    if "--selftest" in argv:
        eye, g, enc = choose_eye(rm["P"], rm, 275, 100, QUALITY_WINDOW)
        want = ((2176, 1760), (0.798, 0.818, 0.527, 0.135, 3.2, 4.0), (1888, 1536))
        got = (eye, tuple(round(x, 3) for x in g), enc)
        ok = got[0] == want[0] and got[2] == want[2] and all(abs(a - b) <= 0.001 for a, b in zip(got[1], want[1]))
        print("selftest", "PASS" if ok else "FAIL", got)
        return 0 if ok else 1
    if "--table" in argv:
        print("| Case (275 Mbps, 100 Hz) | Eye | Encoded, Mpx | Bits per pixel | Centre px/deg |")
        print("|---|---|---|---|---|")
        for wname, w in (("quality", QUALITY_WINDOW), ("performance", PERFORMANCE_WINDOW)):
            for mode in ("faster", "sharper"):
                eye = mode_eye(mode, w, rm)
                row(f"{mode.capitalize()}, the client declares the {wname} window", eye, milder(w, eye, rm, None), w)
        return 0
    if "--phase0" in argv:
        sliders = (AVP_FRUSTUM[0] * 0.8, AVP_FRUSTUM[1] * 0.8, AVP_FRUSTUM[2] * 0.9, AVP_FRUSTUM[3] * 0.9)
        print("| Case (275 Mbps, 100 Hz, today's client at H 0.8 / V 0.9, PC restriction = quality window) | Eye | Encoded, Mpx | Bits per pixel | Centre px/deg |")
        print("|---|---|---|---|---|")
        eye, g, _ = choose_eye(rm["P"], rm, 275, 100, QUALITY_WINDOW); row("as run 2026-11-14 (compiled over Apple's frustum)", eye, g, sliders)
        eye, g, _ = choose_eye(sliders, rm, 275, 100, QUALITY_WINDOW); row("compiled over the declared box, today's rule", eye, g, sliders)
        eye, g, _ = choose_eye(sliders, rm, 275, 100, QUALITY_WINDOW, inside_follow_map=True); row("declared box, map inside the window", eye, g, sliders)
        g = milder(sliders, BASE_EYE, rm, QUALITY_WINDOW, inside_follow_map=True); row("Sharper (base kept), map inside the window", BASE_EYE, g, sliders)
        g = milder(sliders, BASE_EYE, rm, QUALITY_WINDOW); row("Sharper (base kept), today's rule", BASE_EYE, g, sliders)
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
