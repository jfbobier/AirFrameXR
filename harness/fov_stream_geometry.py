#!/usr/bin/env python3
"""The geometry of a stream frame shrunk to the calibrated window (docs/FOV_PIPELINE_OPTIM.md), computed, not guessed.

Given the box the headset declares today (D), the calibrated rectangle (R), the stream's per-eye size over D and the session's
foveation geometry, prints: the per-eye size that keeps today's pixels per tangent unit over R (ALVR aligns it to 32), the
encoded (foveated) frame for each case through the exact port of ALVR's FFR.cpp maths (harness/lib.py fov_vars), where the
foveation's full-density band sits in degrees, and the foveation parameters that keep that band when the frame changes:
in source space the band is [(1 - cs) (1 + sh) / 2, that + cs] of the eye (derived from CompressAxisAlignedPixelShader.hlsl), so
cs = band width and sh = 2 start / (1 - cs) - 1.

    python3 harness/fov_stream_geometry.py                      # the Vision Pro numbers of 2026-10-29
    python3 harness/fov_stream_geometry.py --json               # the same as JSON (for other checks)
    python3 harness/fov_stream_geometry.py --declared 1.79,1.02,1.22,1.02   # the stock visionOS app's box
    python3 harness/fov_stream_geometry.py --selftest           # the band formula against the shader's own equations
    python3 harness/fov_stream_geometry.py --restrict 50 --no-foveation --split-mode 0 --engines 3   # a stock-client user: 50 % restriction, full frame, auto split
    python3 harness/fov_stream_geometry.py --quality 1 --panel 3552x3200   # the render quality slider at the top: the game renders the panel size over R

The parameter space the launcher's pipeline model must cover (docs/FOV_PIPELINE_OPTIM.md section 2b): the box the client declares, the
rectangle (client-declared or the streamer's override, --restrict as a percent of the declared area down to 50), the stream density
(--eye over the declared box), the render quality slider (--quality 0..1 between the stream's size over R and the panel's size, --panel),
foveation on / off (--no-foveation) and its geometry, the split mode (--split-mode 0 auto, 1 auto-forced, 2, 3, 15 off), the NVENC preset and
tuning (--preset 1..7, --tuning ll|ull|hq) and the GPU's engine count (--engines). `warnings` lists what the UI must say.

Tangents are the left eye's outer, inner, upper, lower (the right eye mirrors). Stdlib + numpy (through lib.fov_vars).
"""
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib  # noqa: E402

# the Vision Pro through the AirFrameXR client (what the 5090's headset declared in October 2026, docs/AVP_FOV.md) and the
# calibrated rectangle of release 1.1.1
DECLARED = (0.94, 0.93, 0.90, 0.76)
RECT = (0.96, 0.71, 0.69, 0.61)
EYE = (3552, 3200)
FOVEATION = {"foveation_center_size_x": 0.45, "foveation_center_size_y": 0.4, "foveation_center_shift_x": 0.4,
             "foveation_center_shift_y": 0.1, "foveation_edge_ratio_x": 4.0, "foveation_edge_ratio_y": 5.0}


def align32(v):
    return int(math.floor(v / 32.0)) * 32   # server_core connection.rs align32 (floor; what an Absolute size becomes)


def band_src(cs, sh):
    """The full-density band of ALVR's compress shader in source eye uv (0 = the outer edge), as [start, end]."""
    start = (1.0 - cs) * (1.0 + sh) / 2.0
    return start, start + cs


def band_params(start, width):
    """cs, sh for a wanted band [start, start + width] in source eye uv."""
    cs = width
    sh = 2.0 * start / (1.0 - cs) - 1.0
    return cs, sh


def band_src_from_shader(cs, sh, er):
    """The same band through the shader's own equations (the oracle for band_src)."""
    c0 = (1.0 - cs) / 2.0
    c1 = (er - 1.0) * c0 * (sh + 1.0) / er
    c2 = (er - 1.0) * cs + 1.0
    lo = c0 * (sh + 1.0) / c2
    hi = c0 * (sh - 1.0) / c2 + 1.0
    return lo * c2 / er + c1, hi * c2 / er + c1


def tan_band(uv_band, outer, inner):
    """source uv band -> tangent band (negative = outward)."""
    span = outer + inner
    return -outer + uv_band[0] * span, -outer + uv_band[1] * span


def deg(t):
    return math.degrees(math.atan(t))


# NVENC Video Codec SDK programming guide 13.0, section 8.14 (read 2026-10-29): split frame encoding (HEVC / AV1, >= 2 NVENCs). Auto mode
# needs the frame at least 2112 high (HEVC; 2048 AV1) and P1-P2 with the high-quality tuning or P1-P4 with low / ultra-low latency; the forced
# modes split "for all configurations", the strip count capped by the engine count.
SPLIT_MIN_HEIGHT = {"hevc": 2112, "av1": 2048}


def split_strips(height, mode, engines, preset=3, tuning="ll", codec="hevc", conditions_met=True):
    """Strips NVENC is expected to encode, and why, for an encoded frame of this height."""
    if engines < 2:
        return 1, "one NVENC engine: no split"
    if not conditions_met:
        return 1, "split disabled by the configuration (weighted prediction, alpha, sub-frame readback or video-memory output)"
    if mode == 15:
        return 1, "split mode off"
    if mode in (2, 3, 4):
        n = min(mode, engines)
        return n, f"forced {mode}-strip mode" + (f" capped by {engines} engines" if n < mode else "")
    if mode == 1:
        return min(engines, 3), "auto-forced: the driver chooses the strip count"
    # auto
    if height < SPLIT_MIN_HEIGHT[codec]:
        return 1, f"auto mode splits only frames at least {SPLIT_MIN_HEIGHT[codec]} high ({codec.upper()}); this one is {height}"
    if (tuning == "hq" and preset > 2) or (tuning in ("ll", "ull") and preset > 4):
        return 1, f"auto mode does not split with P{preset} {tuning}: P1-P2 high quality or P1-P4 low latency only"
    return min(engines, 3), "auto mode: height and preset allow it, the driver chooses the strip count"


def even_at_least_64(v):
    return max(64, (int(round(v)) + 1) // 2 * 2)   # ovrshim/render_size.h EvenAtLeast64


def game_render_size(floor, ceiling, quality):
    """ovrshim/render_size.h GameRenderSize: quality 0 = the floor (the stream's density over R), 1 = the ceiling."""
    q = min(1.0, max(0.0, quality))
    return (even_at_least_64(floor[0] + (ceiling[0] - floor[0]) * q), even_at_least_64(floor[1] + (ceiling[1] - floor[1]) * q))


def case(name, box, eye, fov):
    outer, inner, up, down = box
    v = lib.fov_vars(eye[0], eye[1], fov)
    bx = tan_band(band_src(v["center"][0], v["shift"][0]), outer, inner)
    by_uv = band_src(v["center"][1], v["shift"][1])
    by = (up - by_uv[0] * (up + down), up - by_uv[1] * (up + down))   # v = 0 is the top
    return {
        "name": name, "box": box, "eye": list(eye), "span_tan": [round(outer + inner, 3), round(up + down, 3)],
        "density_px_per_tan": [round(eye[0] / (outer + inner)), round(eye[1] / (up + down))],
        "frame_mpx": round(2 * eye[0] * eye[1] / 1e6, 2),
        "encoded": [2 * v["opt_w"], v["opt_h"]], "encoded_mpx": round(2 * v["opt_w"] * v["opt_h"] / 1e6, 2),
        "foveation": {"center_size": [round(fov["foveation_center_size_x"], 3), round(fov["foveation_center_size_y"], 3)],
                      "center_shift": [round(fov["foveation_center_shift_x"], 3), round(fov["foveation_center_shift_y"], 3)],
                      "aligned_center_size": [round(v["center"][0], 3), round(v["center"][1], 3)], "aligned_shift": [round(v["shift"][0], 3), round(v["shift"][1], 3)]},
        "band_tan": {"x": [round(bx[0], 3), round(bx[1], 3)], "y": [round(by[1], 3), round(by[0], 3)]},
        "band_deg": {"x": [round(deg(bx[0]), 1), round(deg(bx[1]), 1)], "y": [round(deg(by[1]), 1), round(deg(by[0]), 1)]},
    }


def compute(declared=DECLARED, rect=RECT, eye=EYE, fov=FOVEATION, foveated=True, quality=0.0, panel=None, split_mode=3, engines=3,
            preset=3, tuning="ll", restrict_pct=None):
    if restrict_pct is not None:   # a uniform restriction of the declared box's area, as the streamer's override could ask (down to 50 %)
        k = math.sqrt(restrict_pct / 100.0)
        rect = tuple(round(v * k, 3) for v in declared)
    out = {"today": case("today: stream over the declared box", declared, eye, fov), "rect": list(rect), "foveated": foveated}
    # the same pixels per tangent unit over R, aligned the way ALVR aligns an Absolute size
    dens = (eye[0] / (declared[0] + declared[1]), eye[1] / (declared[2] + declared[3]))
    eye_r = (align32(dens[0] * (rect[0] + rect[1])), align32(dens[1] * (rect[2] + rect[3])))
    out["eye_r"] = list(eye_r)
    out["same_fractions"] = case("R frame, the session's foveation fractions (band narrower in degrees)", rect, eye_r, fov)
    # the band today, kept in degrees on the R frame: solve cs / sh per axis
    t = out["today"]["band_tan"]
    span_x, span_y = rect[0] + rect[1], rect[2] + rect[3]
    ux = ((t["x"][0] + rect[0]) / span_x, (t["x"][1] + rect[0]) / span_x)
    uy = ((rect[2] - t["y"][1]) / span_y, (rect[2] - t["y"][0]) / span_y)
    ux = (max(0.0, ux[0]), min(1.0, ux[1]))
    uy = (max(0.0, uy[0]), min(1.0, uy[1]))
    csx, shx = band_params(ux[0], ux[1] - ux[0])
    csy, shy = band_params(uy[0], uy[1] - uy[0])
    iso = dict(fov, foveation_center_size_x=csx, foveation_center_size_y=csy, foveation_center_shift_x=shx, foveation_center_shift_y=shy)
    out["same_band"] = case("R frame, foveation re-anchored so the full-density band keeps today's degrees", rect, eye_r, iso)
    for k in ("same_fractions", "same_band"):
        c = out[k]
        c["saving_vs_today"] = {"frame_px_pct": round(100 * (1 - c["frame_mpx"] / out["today"]["frame_mpx"]), 1),
                                "encoded_px_pct": round(100 * (1 - c["encoded_mpx"] / out["today"]["encoded_mpx"]), 1)}
    # without foveation the encoded frame is the stream frame itself
    for k in ("today", "same_fractions", "same_band"):
        c = out[k]
        c["encoded_sent"] = c["encoded"] if foveated else [2 * c["eye"][0], c["eye"][1]]
        c["encoded_sent_mpx"] = c["encoded_mpx"] if foveated else c["frame_mpx"]
    # the game's render size: floor = the stream over R, ceiling = the panel reference (the stream's size over the declared box when none given)
    ceiling = tuple(panel) if panel else tuple(eye)
    out["game_render"] = {"floor": list(eye_r), "ceiling": list(ceiling), "quality": quality, "size": list(game_render_size(eye_r, ceiling, quality))}
    # what the UI must say
    warnings = []
    frame_h = eye_r[1] if foveated is False else out["same_band"]["encoded"][1]
    for label, h in (("re-anchored foveation", out["same_band"]["encoded"][1]), ("same fractions", out["same_fractions"]["encoded"][1])) if foveated else (("full frame", eye_r[1]),):
        strips, why = split_strips(h, split_mode, engines, preset, tuning)
        out.setdefault("split", {})[label] = {"encoded_height": h, "expected_strips": strips, "why": why}
        if engines >= 2 and strips < min(engines, 3):
            warnings.append(f"split encoding: {strips} strip(s) expected for the {label} frame ({why}); a forced mode splits at any size")
    if eye_r[0] > 4096 or eye_r[1] > 8192:
        warnings.append(f"stream {eye_r[0]}x{eye_r[1]} per eye exceeds NVENC's 8192-wide limit side by side")
    if quality > 0 and out["game_render"]["size"] == list(eye_r):
        warnings.append("the render quality slider has no room: the floor and the ceiling are the same size (give the slider the panel's size as ceiling)")
    out["warnings"] = warnings
    return out


# ---- the launcher's pipeline model (tools/gui/PipelineModel.cs is a port of this function; harness/pipeline_model_check.py compares them) ----------
NVENC_MAX_EYE = (4096, 8192)


def model(declared, rect=None, eye=EYE, reference_eye=None, reference_fov=None, fit=False, quality=0.0, foveated=True,
          geometry=(0.45, 0.4, 0.4, 0.1, 4.0, 5.0), split_mode=3, preset=3, tuning="ll", engines=3):
    """What the pipeline will do with these settings: the stream's per-eye size (the fit keeps the reference density over whatever box the
    headset declares), the game's render size (floor = the stream's density over the rectangle, ceiling = the reference eye size when the fit
    is on and larger), the frame, the encoded frame, the full-density band in degrees, the foveation parameters that keep the reference
    frame's band, the strips NVENC is expected to use, and the warnings the UI shows. `rect` None = the game renders the declared box."""
    dspan = (declared[0] + declared[1], declared[2] + declared[3])
    rect = tuple(rect) if rect else tuple(declared)
    rect = tuple(min(r, d) for r, d in zip(rect, declared))
    rspan = (rect[0] + rect[1], rect[2] + rect[3])
    warnings, info = [], []
    use_fit = fit and reference_eye is not None and reference_fov is not None
    if use_fit:
        fspan = (reference_fov[0] + reference_fov[1], reference_fov[2] + reference_fov[3])
        # the ratio first, plus a hair, so the same box gives back the same size (3200 x 1.66 / 1.66 lands under 3200 in floating point)
        raw = (align32(reference_eye[0] * (dspan[0] / fspan[0]) + 1e-6), align32(reference_eye[1] * (dspan[1] / fspan[1]) + 1e-6))
        if raw[0] > NVENC_MAX_EYE[0] or raw[1] > NVENC_MAX_EYE[1]:
            warnings.append(f"The stream's {raw[0]} x {raw[1]} per eye exceeds what NVENC encodes ({2 * NVENC_MAX_EYE[0]} across side by side, {NVENC_MAX_EYE[1]} high): capped.")
        fit_eye = (min(raw[0], NVENC_MAX_EYE[0]), min(raw[1], NVENC_MAX_EYE[1]))
    else:
        fit_eye = (int(eye[0]), int(eye[1]))
        if fit_eye[0] > NVENC_MAX_EYE[0] or fit_eye[1] > NVENC_MAX_EYE[1]:
            warnings.append(f"The stream's {fit_eye[0]} x {fit_eye[1]} per eye exceeds what NVENC encodes ({2 * NVENC_MAX_EYE[0]} across side by side, {NVENC_MAX_EYE[1]} high).")
    # the shim's floor and ceiling (ovrshim/render_size.h): floor = eye x the rectangle's share of the declared span, ceiling = the reference eye when the fit is on and larger
    wr, hr = rspan[0] / dspan[0], rspan[1] / dspan[1]
    floor = (fit_eye[0] * wr, fit_eye[1] * hr)
    ceiling = (max(reference_eye[0], fit_eye[0]), max(reference_eye[1], fit_eye[1])) if use_fit else fit_eye
    game = game_render_size(floor, ceiling, quality)
    floor_even = (even_at_least_64(floor[0]), even_at_least_64(floor[1]))
    cs = {"foveation_center_size_x": geometry[0], "foveation_center_size_y": geometry[1], "foveation_center_shift_x": geometry[2],
          "foveation_center_shift_y": geometry[3], "foveation_edge_ratio_x": geometry[4], "foveation_edge_ratio_y": geometry[5]}
    out = {"fit_eye": list(fit_eye), "floor": list(floor_even), "ceiling": [int(ceiling[0]), int(ceiling[1])], "game": list(game), "frame": [2 * fit_eye[0], fit_eye[1]],
           "rect": list(rect), "foveated": foveated, "reanchored": None, "band_x": None, "band_y": None}
    if foveated:
        v = lib.fov_vars(fit_eye[0], fit_eye[1], cs)
        out["encoded"] = [2 * v["opt_w"], v["opt_h"]]
        bx = tan_band(band_src(v["center"][0], v["shift"][0]), declared[0], declared[1])
        byu = band_src(v["center"][1], v["shift"][1])
        by = (declared[2] - byu[1] * dspan[1], declared[2] - byu[0] * dspan[1])   # down (negative) .. up
        out["band_x"] = [round(deg(bx[0]), 1), round(deg(bx[1]), 1)]
        out["band_y"] = [round(deg(by[0]), 1), round(deg(by[1]), 1)]
        if use_fit:
            # the band of the reference frame (its geometry over the reference box), kept in degrees on this frame
            vr = lib.fov_vars(reference_eye[0], reference_eye[1], cs)
            rbx = tan_band(band_src(vr["center"][0], vr["shift"][0]), reference_fov[0], reference_fov[1])
            rbyu = band_src(vr["center"][1], vr["shift"][1])
            rby = (reference_fov[2] - rbyu[0] * fspan[1], reference_fov[2] - rbyu[1] * fspan[1])   # up .. down in tan
            ux = (max(0.0, (rbx[0] + declared[0]) / dspan[0]), min(1.0, (rbx[1] + declared[0]) / dspan[0]))
            uy = (max(0.0, (declared[2] - rby[0]) / dspan[1]), min(1.0, (declared[2] - rby[1]) / dspan[1]))
            csx, shx = band_params(ux[0], ux[1] - ux[0])
            csy, shy = band_params(uy[0], uy[1] - uy[0])
            out["reanchored"] = [round(csx, 3), round(csy, 3), round(shx, 3), round(shy, 3)]
    else:
        out["encoded"] = [2 * fit_eye[0], fit_eye[1]]
    strips, why = split_strips(out["encoded"][1], split_mode, engines if engines is not None and engines >= 0 else 0, preset, tuning)
    if engines is None or engines < 0:
        strips, why = 0, "the GPU's NVENC engine count is not known yet"
        info.append("The GPU's NVENC engine count is not known yet: run the streamer once.")
    elif engines >= 2 and strips < min(engines, 3):
        warnings.append(f"Split encoding: {strips} strip(s) expected ({why}). A forced mode splits at any size.")
    if quality > 0 and tuple(game) == floor_even and ceiling[0] <= floor[0] + 1e-9 and ceiling[1] <= floor[1] + 1e-9:
        warnings.append("The render quality slider has no room: the game's size and its ceiling are the same (tick the stream fit or set a larger resolution).")
    out.update({"strips": strips, "strips_why": why, "warnings": warnings, "info": info})
    return out


# ---- foveation from Apple's rasterization rate map (docs/PICTURE_EDGES_DESIGN.md section 5b) -------------------------------------------------
# Orientation, read from ALVR's shaders: the compress pass draws a full-screen quad whose uv.y = 0 is the TOP of the frame
# (d3d-render-utils/QuadVertexShader.hlsl: position.y = -(uv.y - 0.5) * 2), and TextureToEyeUV mirrors x for the right eye, so in eye uv
# x = 0 is the outer edge and y = 0 the upper edge, for both eyes. center_shift_y > 0 moves the band DOWN.
AVP_PROFILE = Path(__file__).resolve().parent.parent / "profiles" / "apple-vision-pro.profile.json"
AUTHOR_FIT = (0.97, 0.71, 0.69, 0.63)   # the shipped profile's rectangle (docs/AVP_FOV.md), one wearer
TODAY_GEOMETRY = (0.45, 0.4, 0.4, 0.1, 4.0, 5.0)


def load_ratemap(path=AVP_PROFILE):
    """The headset profile's `ratemap`: frustum P, the panel's full-rate density in px per tangent unit, and the two axis tables."""
    rm = json.loads(Path(path).read_text())["ratemap"]
    p = tuple(rm["frustum_tan"])
    lp = rm["logical_px"]
    return {"P": p, "density": (lp[0] / (p[0] + p[1]), lp[1] / (p[2] + p[3])), "x": [tuple(a) for a in rm["x"]], "y": [tuple(a) for a in rm["y"]]}


def rate_at(tbl, d):
    """Linear in degrees between samples; beyond the outermost sample the end value (the map is flat-ish there, never zero)."""
    if d <= tbl[0][0]:
        return tbl[0][1]
    if d >= tbl[-1][0]:
        return tbl[-1][1]
    for (a, ra), (b, rb) in zip(tbl, tbl[1:]):
        if a <= d <= b:
            return ra + (rb - ra) * (d - a) / (b - a)
    return tbl[-1][1]


def _axis_deg(s, lo_t, hi_t, axis):
    """source eye uv -> degrees from the axis (x: negative = outer; y: negative = down)."""
    t = (-lo_t + s * (lo_t + hi_t)) if axis == 0 else (hi_t - s * (lo_t + hi_t))
    return math.degrees(math.atan(t))


def axis_loss(cs, sh, er, lo_t, hi_t, axis, tbl, m=1.0, n=2001):
    """Worst over-compression on one axis: max over the encoded axis of r / max(1, m / rate), r = the local compression (source pixels per
    encoded pixel, 1 in the band, 2 er - 1 at the frame edge). cs, sh = ALVR's ALIGNED values (lib.fov_vars). 1.0 = no visible loss against the
    map. lo_t / hi_t: x outer / inner, y down / up tangents. Returns (worst, degrees, r there, what the map allows there)."""
    fs = cs + (1.0 - cs) / er
    es = [i / (n - 1) for i in range(n)]
    src = [lib._fov_f(e, cs, sh, er) for e in es]
    worst = (0.0, None, None, None)
    for i in range(n):
        a, b = max(0, i - 1), min(n - 1, i + 1)
        r = (src[b] - src[a]) / (es[b] - es[a]) / fs
        d = _axis_deg(src[i], lo_t, hi_t, axis)
        allow = max(1.0, m / max(1e-6, rate_at(tbl, d)))
        if r / allow > worst[0]:
            worst = (r / allow, round(d, 1), round(r, 2), round(allow, 2))
    return worst


def _band_uv(view, axis, tbl, contour):
    """The interval where rate >= contour, clipped to the view, in source eye uv (x from the outer edge, y from the top)."""
    lo_t, hi_t = (view[0], view[1]) if axis == 0 else (view[3], view[2])
    lo_d, hi_d = -deg(lo_t), deg(hi_t)
    ds = [lo_d + (hi_d - lo_d) * i / 2000 for i in range(2001)]
    inside = [d for d in ds if rate_at(tbl, d) >= contour]
    if not inside:
        inside = [min(ds, key=abs)]
    a, b = math.tan(math.radians(inside[0])), math.tan(math.radians(inside[-1]))
    span = lo_t + hi_t
    if axis == 0:
        return (a + lo_t) / span, (b + lo_t) / span, (inside[0], inside[-1])
    return (hi_t - b) / span, (hi_t - a) / span, (inside[0], inside[-1])


def rule_geometry(view, rm, contour=0.5):
    """Rules 1-2 of section 5b: band = rate >= contour clipped to the view; er = (1 / rate at the stricter edge + 1) / 2, clamped to [1, 10]
    (ALVR has one ratio for both ends of an axis, so the end that allows less sets it). Returns (cs_x, cs_y, sh_x, sh_y, er_x, er_y), bands in deg."""
    out, bands = {}, {}
    for axis, tbl in ((0, rm["x"]), (1, rm["y"])):
        u0, u1, bd = _band_uv(view, axis, tbl, contour)
        cs, sh = band_params(u0, u1 - u0)
        lo_t, hi_t = (view[0], view[1]) if axis == 0 else (view[3], view[2])
        edge_rate = max(rate_at(tbl, -deg(lo_t)), rate_at(tbl, deg(hi_t)))
        er = min(10.0, max(1.0, (1.0 / edge_rate + 1.0) / 2.0))
        out[axis] = (cs, sh, er)
        bands[axis] = [round(bd[0], 1), round(bd[1], 1)]
    return (out[0][0], out[1][0], out[0][1], out[1][1], out[0][2], out[1][2]), bands


def _geometry_dict(g):
    return {"foveation_center_size_x": g[0], "foveation_center_size_y": g[1], "foveation_center_shift_x": g[2],
            "foveation_center_shift_y": g[3], "foveation_edge_ratio_x": g[4], "foveation_edge_ratio_y": g[5]}


def assess(view, eye, g, rm, m=1.0):
    """The encoded frame and the worst over-compression per axis of geometry g over the view at this eye size (ALVR's alignment applied)."""
    v = lib.fov_vars(eye[0], eye[1], _geometry_dict(g))
    wx = axis_loss(v["center"][0], v["shift"][0], g[4], view[0], view[1], 0, rm["x"], m)
    wy = axis_loss(v["center"][1], v["shift"][1], g[5], view[3], view[2], 1, rm["y"], m)
    bx = tan_band(band_src(v["center"][0], v["shift"][0]), view[0], view[1])
    byu = band_src(v["center"][1], v["shift"][1])
    by = (view[2] - byu[1] * (view[2] + view[3]), view[2] - byu[0] * (view[2] + view[3]))
    return {"geometry": [round(x, 3) for x in g], "encoded": [2 * v["opt_w"], v["opt_h"]], "encoded_mpx": round(2 * v["opt_w"] * v["opt_h"] / 1e6, 2),
            "band_deg": {"x": [round(deg(bx[0]), 1), round(deg(bx[1]), 1)], "y": [round(deg(by[0]), 1), round(deg(by[1]), 1)]},
            "worst_x": {"ratio": round(wx[0], 3), "deg": wx[1], "r": wx[2], "allowed": wx[3]},
            "worst_y": {"ratio": round(wy[0], 3), "deg": wy[1], "r": wy[2], "allowed": wy[3]}}


def zero_loss_geometry(view, eye, rm, m=1.0):
    """Rule 3 of section 5b, exact: per axis, over band contours 0.30 .. 0.95, the largest er (bisection) whose worst over-compression is <= 1
    after ALVR's alignment, keeping the contour that gives the fewest encoded pixels on that axis."""
    best = {}
    for axis in (0, 1):
        tbl = rm["x"] if axis == 0 else rm["y"]
        lo_t, hi_t = (view[0], view[1]) if axis == 0 else (view[3], view[2])
        size = eye[axis]
        cand = None
        for k in range(30, 96, 5):
            u0, u1, _ = _band_uv(view, axis, tbl, k / 100.0)
            cs, sh = band_params(u0, u1 - u0)

            def worst(er):
                gd = _geometry_dict((cs, cs, sh, sh, er, er))
                v = lib.fov_vars(size, size, gd)
                return axis_loss(v["center"][0], v["shift"][0], er, lo_t, hi_t, axis, tbl, m, n=801)[0], v["opt_w"]
            if worst(1.0)[0] > 1.0 + 1e-6:
                continue
            a, b = 1.0, 10.0
            if worst(b)[0] <= 1.0 + 1e-6:
                a = b
            else:
                for _ in range(22):
                    mid = (a + b) / 2
                    if worst(mid)[0] <= 1.0 + 1e-6:
                        a = mid
                    else:
                        b = mid
            px = worst(a)[1]
            if cand is None or px < cand[0]:
                cand = (px, cs, sh, a, k / 100.0)
        best[axis] = cand
    g = (best[0][1], best[1][1], best[0][2], best[1][2], best[0][3], best[1][3])
    return g, {"contour_x": best[0][4], "contour_y": best[1][4]}


def density_eye(view, rm, fraction=1.0, cap=NVENC_MAX_EYE, fits=None):
    """Section 5 density rule: eye = min(target density x the view's span per axis, NVENC's cap, what the encode test fits), 32-aligned (floor)."""
    t = (rm["density"][0] * fraction, rm["density"][1] * fraction)
    raw = (t[0] * (view[0] + view[1]), t[1] * (view[2] + view[3]))
    lim = (min(cap[0], fits[0]) if fits else cap[0], min(cap[1], fits[1]) if fits else cap[1])
    return align32(min(raw[0], lim[0])), align32(min(raw[1], lim[1]))


def ratemap_cases(rm, m=1.0, eyes=((3552, 3200), (4096, 3584))):
    P = rm["P"]
    views = [("Panel", P)] + [("0.80 x Panel", tuple(round(0.8 * x, 4) for x in P))] + [("author's fit", AUTHOR_FIT)]
    out = []
    for name, view in views:
        for eye in (eyes if name == "Panel" else eyes[:1]):
            rg, bands = rule_geometry(view, rm)
            zg, contours = zero_loss_geometry(view, eye, rm, m)
            out.append({"view": name, "box": list(view), "eye": list(eye),
                        "density_px_per_tan": [round(eye[0] / (view[0] + view[1])), round(eye[1] / (view[2] + view[3]))],
                        "share_of_panel_density": [round(eye[0] / (view[0] + view[1]) / rm["density"][0], 2), round(eye[1] / (view[2] + view[3]) / rm["density"][1], 2)],
                        "today": assess(view, eye, TODAY_GEOMETRY, rm, m), "rule": dict(assess(view, eye, rg, rm, m), rate_half_band_deg=bands),
                        "zero_loss": dict(assess(view, eye, zg, rm, m), **contours)})
    return out


def print_ratemap(cases):
    for c in cases:
        print(f"- {c['view']} {c['box']} at {c['eye'][0]}x{c['eye'][1]} per eye: {c['density_px_per_tan'][0]} x {c['density_px_per_tan'][1]} px/tan "
              f"({int(100 * c['share_of_panel_density'][0])} / {int(100 * c['share_of_panel_density'][1])} % of the panel's)")
        for k in ("today", "rule", "zero_loss"):
            a = c[k]
            g = a["geometry"]
            print(f"    {k:9s} cs {g[0]:.3f} x {g[1]:.3f}  shift {g[2]:+.3f} / {g[3]:+.3f}  er {g[4]:.2f} / {g[5]:.2f}  -> encoded {a['encoded'][0]}x{a['encoded'][1]} "
                  f"({a['encoded_mpx']} Mpx), band x {a['band_deg']['x']} y {a['band_deg']['y']} deg; worst over-compression x {a['worst_x']['ratio']} at "
                  f"{a['worst_x']['deg']} deg, y {a['worst_y']['ratio']} at {a['worst_y']['deg']} deg")


def selftest():
    bad = 0
    for cs in (0.3, 0.45, 0.6):
        for sh in (-0.5, 0.0, 0.4, 0.8):
            for er in (2.5, 4.0, 5.0):
                a, b = band_src(cs, sh), band_src_from_shader(cs, sh, er)
                if abs(a[0] - b[0]) > 1e-9 or abs(a[1] - b[1]) > 1e-9:
                    bad += 1
                    print("MISMATCH", cs, sh, er, a, b)
                c2, s2 = band_params(a[0], a[1] - a[0])
                if abs(c2 - cs) > 1e-9 or abs(s2 - sh) > 1e-9:
                    bad += 1
                    print("INVERSE MISMATCH", cs, sh, c2, s2)
    print("CHECK", "PASS" if bad == 0 else "FAIL", "band formula against the shader equations (36 cases, forward and inverse)")
    rules = [((1664, 0, 3, 3, "ll"), 1), ((2112, 0, 3, 3, "ll"), 3), ((2496, 0, 3, 5, "ll"), 1), ((2496, 0, 3, 2, "hq"), 3), ((2496, 0, 3, 3, "hq"), 1),
             ((1312, 3, 3, 3, "ll"), 3), ((1312, 3, 2, 3, "ll"), 2), ((1312, 2, 3, 3, "ll"), 2), ((3200, 0, 1, 1, "ll"), 1), ((3200, 15, 3, 1, "ll"), 1)]
    bad2 = 0
    for (h, mode, eng, p, t), want in rules:
        got = split_strips(h, mode, eng, p, t)[0]
        if got != want:
            bad2 += 1
            print("SPLIT MISMATCH", h, mode, eng, p, t, "got", got, "want", want)
    print("CHECK", "PASS" if bad2 == 0 else "FAIL", "split-encode strip rules (SDK 13.0 guide 8.14: 2112 rows, preset table, forced modes, engine cap)")
    # the model: the fork client declaring R with the reference saved over the old box gives the sizes of this document
    m = model(RECT, None, EYE, reference_eye=EYE, reference_fov=DECLARED, fit=True, quality=0.0)
    m2 = model(DECLARED, RECT, EYE, fit=False, quality=1.0)
    m3 = model(DECLARED, None, EYE, fit=False, quality=0.5, engines=-1)
    ok3 = (m["fit_eye"] == [3168, 2496] and m["game"] == [3168, 2496] and m["encoded"] == [3776, 1312] and m["ceiling"] == [3552, 3200] and m["strips"] == 3
           and m2["game"] == [3552, 3200] and m2["floor"] == [3134, 2506] and not m2["warnings"]
           and m3["strips"] == 0 and len(m3["info"]) == 1 and len(m3["warnings"]) == 1)
    if not ok3:
        print("MODEL", m, m2, m3)
    print("CHECK", "PASS" if ok3 else "FAIL", "pipeline model: the fit over R, the quality ceiling, the no-room and unknown-engines lines")
    # rate-map foveation (section 5b)
    rm = load_ratemap()
    P = rm["P"]
    edge = [axis_loss(0.45, 0.4, er, 1.0, 1.0, 0, [(-90, 1.0), (90, 1.0)])[2] for er in (2.5, 4.0, 5.0)]
    ok4 = all(abs(e - (2 * er - 1)) < 0.02 * (2 * er - 1) for e, er in zip(edge, (2.5, 4.0, 5.0)))
    print("CHECK", "PASS" if ok4 else "FAIL", f"local compression at the frame edge = 2 er - 1 (er 2.5 / 4 / 5 -> {edge})")
    rg, bands = rule_geometry(P, rm)
    ok5 = (abs(bands[0][0] + 24) < 0.6 and abs(bands[0][1] - 23) < 0.6 and abs(bands[1][0] + 18) < 0.6 and abs(bands[1][1] - 29) < 0.6
           and rg[3] < 0 and abs(rg[4] - 4.3) < 0.15 and abs(rg[5] - 4.7) < 0.15)
    print("CHECK", "PASS" if ok5 else "FAIL", f"rule at Panel: band x {bands[0]} y {bands[1]} deg (-24..23 / -18..29), shift_y {rg[3]:+.3f} (< 0: the band sits above the middle), er {rg[4]:.2f} / {rg[5]:.2f} (4.3 / 4.7)")
    zg, _ = zero_loss_geometry(P, EYE, rm)
    za = assess(P, EYE, zg, rm)
    ok6 = za["worst_x"]["ratio"] <= 1.0 + 1e-3 and za["worst_y"]["ratio"] <= 1.0 + 1e-3
    print("CHECK", "PASS" if ok6 else "FAIL", f"zero-loss geometry at Panel stays under the map (worst {za['worst_x']['ratio']} / {za['worst_y']['ratio']}), encoded {za['encoded']}")
    return 0 if bad == 0 and bad2 == 0 and ok3 and ok4 and ok5 and ok6 else 1


def main(argv):
    if "--selftest" in argv:
        return selftest()
    if "--ratemap" in argv:
        m = float(argv[argv.index("--margin") + 1]) if "--margin" in argv else 1.0
        cases = ratemap_cases(load_ratemap(), m)
        if "--json" in argv:
            print(json.dumps(cases, indent=1))
        else:
            print(f"foveation against Apple's rate map (m = {m}; worst over-compression 1.0 = nothing the compositor would keep is lost)")
            print_ratemap(cases)
        return 0
    declared, rect, eye = DECLARED, RECT, EYE
    if "--declared" in argv:
        declared = tuple(float(x) for x in argv[argv.index("--declared") + 1].split(","))
    if "--rect" in argv:
        rect = tuple(float(x) for x in argv[argv.index("--rect") + 1].split(","))
    if "--eye" in argv:
        eye = tuple(int(x) for x in argv[argv.index("--eye") + 1].lower().split("x"))
    opt = lambda k, d, f=float: f(argv[argv.index(k) + 1]) if k in argv else d
    panel = tuple(int(x) for x in argv[argv.index("--panel") + 1].lower().split("x")) if "--panel" in argv else None
    out = compute(declared, rect, eye, foveated="--no-foveation" not in argv, quality=opt("--quality", 0.0), panel=panel,
                  split_mode=opt("--split-mode", 3, int), engines=opt("--engines", 3, int), preset=opt("--preset", 3, int),
                  tuning=opt("--tuning", "ll", str), restrict_pct=opt("--restrict", None))
    rect = tuple(out["rect"])
    if "--json" in argv:
        print(json.dumps(out, indent=1))
        return 0
    print(f"declared box D (left eye outer/inner/up/down tan) {declared}, calibrated rectangle R {rect}, stream over D {eye[0]}x{eye[1]} per eye")
    print(f"R at the same pixels per tangent unit: {out['eye_r'][0]}x{out['eye_r'][1]} per eye (aligned to 32 as ALVR does)\n")
    for k in ("today", "same_fractions", "same_band"):
        c = out[k]
        print(f"- {c['name']}")
        print(f"    eye {c['eye'][0]}x{c['eye'][1]} ({c['frame_mpx']} Mpx side by side), {c['density_px_per_tan'][0]} x {c['density_px_per_tan'][1]} px/tan; encoded {c['encoded'][0]}x{c['encoded'][1]} ({c['encoded_mpx']} Mpx)")
        f = c["foveation"]
        print(f"    foveation center_size {f['center_size']} shift {f['center_shift']} (aligned {f['aligned_center_size']} / {f['aligned_shift']})")
        print(f"    full-density band: x {c['band_deg']['x'][0]:+.1f} .. {c['band_deg']['x'][1]:+.1f} deg (outer .. inner), y {c['band_deg']['y'][0]:+.1f} .. {c['band_deg']['y'][1]:+.1f} deg (down .. up)")
        if "saving_vs_today" in c:
            s = c["saving_vs_today"]
            print(f"    against today: frame pixels -{s['frame_px_pct']} % (compose, frame generation, VRAM), encoded pixels -{s['encoded_px_pct']} % (NVENC, network at equal bits per pixel, decode)")
    if not out["foveated"]:
        print(f"- no foveation: the encoded frame is the stream frame, {out['today']['encoded_sent'][0]}x{out['today']['encoded_sent'][1]} today, {2 * out['eye_r'][0]}x{out['eye_r'][1]} over R ({out['same_fractions']['saving_vs_today']['frame_px_pct']} % fewer encoded pixels)")
    g = out["game_render"]
    print(f"- game render size at quality {g['quality']}: {g['size'][0]}x{g['size'][1]} (floor {g['floor'][0]}x{g['floor'][1]}, ceiling {g['ceiling'][0]}x{g['ceiling'][1]})")
    for label, sp in out["split"].items():
        print(f"- split, {label} frame {sp['encoded_height']} high: {sp['expected_strips']} strip(s), {sp['why']}")
    for w in out["warnings"]:
        print(f"- WARNING: {w}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
