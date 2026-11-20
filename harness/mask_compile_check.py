"""docs/PLAN_1_4_FOUNDATION.md F3 / F4 / F5: the launcher's real SessionCompiler (uitest.exe --session-compile-selftest) writes the encoder's mask (video.encode_mask,
polygon_uv_v1) and the session's foveation flag, on this PC (WSL interop, no builder): build the GUIs first with tools/wsl/check_gui.sh.

    python3 harness/mask_compile_check.py        one line per case, PASS when the written mask equals this file's port of FovMask.Polygon + MaskEncoding.ToUv

Cases (the shipped "AVP optimized trade-off" profile: the author's fit with per-eye edges and corner lines): the Vision Pro with the profile in force and the encoding
foveated (the mask over the box the stream is sized for = the restriction sent to the app), the same with "no foveation" (no mask, foveation off), a profile without a
mask, the restriction changed by hand after the profile was chosen (the mask belongs to another rectangle: none), and a Quest 3 (the mask over its declared box).
In each case the settings panel (uitest.exe --mask-choice-selftest) must name the mask in its Encoding radios when the profile has one, and on the Vision Pro show the
compiled foveation numbers read-only.
"""
import json, shutil, subprocess, sys, tempfile
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
from density_check import DEFAULT, V, setp, getp, windows_temp

ROOT = Path(__file__).resolve().parent.parent
AVP_BOX_AT_FOV1 = [1.7909, 1.02175, 1.2211, 1.02175]   # PipelineModel.StreamDensity.AvpBoxAtFov1
_SHIPPED = json.loads((ROOT / "profiles/apple-vision-pro.tradeoff-fov.profile.json").read_text())
FIT = _SHIPPED.get("fov", _SHIPPED)   # the shipped file keeps the numbers under "fov"


def polygon(tan, mask):
    """tools/gui/UserFovProfiles.cs FovMask.Polygon: the rectangle (tangents, x outward from the nose, y up) cut by the four corner lines, counter-clockwise
    from the lower-outer corner"""
    o, i, u, l = tan
    corners = ((o, u), (-i, u), (-i, -l), (o, -l))
    p = [corners[3], corners[0], corners[1], corners[2]]
    for k, (cx, cy) in enumerate(corners):
        if mask is None:
            break
        f = max(0.4, min(1.0, mask[k]))
        if f >= 1.0:
            continue
        lim = f * (cx * cx + cy * cy)
        ins = lambda q: lim - (q[0] * cx + q[1] * cy)
        out = []
        for n in range(len(p)):
            a, b = p[n], p[(n + 1) % len(p)]
            fa, fb = ins(a), ins(b)
            if fa >= 0:
                out.append(a)
            if (fa >= 0) != (fb >= 0):
                t = fa / (fa - fb)
                out.append((a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])))
        p = out
    d = []
    for q in p:
        if not d or abs(q[0] - d[-1][0]) > 1e-9 or abs(q[1] - d[-1][1]) > 1e-9:
            d.append(q)
    if len(d) > 1 and abs(d[0][0] - d[-1][0]) < 1e-9 and abs(d[0][1] - d[-1][1]) < 1e-9:
        d.pop()
    return d


def convex(p):
    """one turning direction and one full turn (a self-crossing outline fails): what nvh.cpp footprintHidden needs"""
    import math
    if not p or len(p) < 3:
        return False
    sign, turn = 0, 0.0
    for k in range(len(p)):
        a, b, c = p[k], p[(k + 1) % len(p)], p[(k + 2) % len(p)]
        cr = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
        if abs(cr) < 1e-12:
            continue
        s = 1 if cr > 0 else -1
        if sign == 0:
            sign = s
        elif s != sign:
            return False
        turn += math.atan2(cr, (b[0] - a[0]) * (c[0] - b[0]) + (b[1] - a[1]) * (c[1] - b[1]))
    return sign != 0 and abs(abs(turn) - 2 * math.pi) < 1e-6


def area_hidden(p):
    a = sum(p[k][0] * p[(k + 1) % len(p)][1] - p[(k + 1) % len(p)][0] * p[k][1] for k in range(len(p)))
    return max(0.0, 1 - abs(a) / 2)


def encoder_hidden(p, n=64):
    """the share of an n x n block grid over the eye that the encoder's own test (lib.mask_footprint_hidden = nvh.cpp footprintHidden) hides: independent of
    this file's port of the compile (field 2026-11-20: a self-crossing outline passed the port and hid 100 % in the encoder)"""
    from lib import mask_footprint_hidden
    return sum(mask_footprint_hidden(p, i / n, (i + 1) / n, j / n, (j + 1) / n) for j in range(n) for i in range(n)) / (n * n)


def to_uv(tan_pts, view):
    """tools/gui/PipelineModel.cs MaskEncoding.ToUv: into the eye's uv over `view`, clipped to the eye, equal neighbours dropped"""
    sx, sy = view[0] + view[1], view[2] + view[3]
    p = [((view[0] - x) / sx, (view[2] - y) / sy) for x, y in tan_pts]
    for f in (lambda q: q[0], lambda q: 1 - q[0], lambda q: q[1], lambda q: 1 - q[1]):
        o = []
        for k in range(len(p)):
            a, b = p[k], p[(k + 1) % len(p)]
            fa, fb = f(a), f(b)
            if fa >= 0:
                o.append(a)
            if (fa >= 0) != (fb >= 0):
                t = fa / (fa - fb)
                o.append((a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])))
        p = o
        if len(p) < 3:
            return p
    d = []
    for q in p:
        if not d or abs(q[0] - d[-1][0]) > 1e-9 or abs(q[1] - d[-1][1]) > 1e-9:
            d.append(q)
    if len(d) > 1 and abs(d[0][0] - d[-1][0]) < 1e-9 and abs(d[0][1] - d[-1][1]) < 1e-9:
        d.pop()
    return d


def compile_case(work, name, headset, box, prof, restriction, encode_profile="foveated"):
    root = work / name
    (root / "bin").mkdir(parents=True); (root / "config").mkdir(); (root / "profiles").mkdir()
    shutil.copy(windows_temp() / "airframexr_gui" / "uitest.exe", root / "bin" / "uitest.exe")
    for p in (ROOT / "profiles").glob("*.profile.json"):
        shutil.copy(p, root / "profiles" / p.name)
    s = json.loads(json.dumps(DEFAULT))
    for k in ("transcoding_view_resolution", "emulated_headset_view_resolution"):
        setp(s, V + k, {"variant": "Absolute", "Absolute": {"width": 3648, "height": {"set": True, "content": 3200}}})
    setp(s, V + "bitrate.mode", {"variant": "ConstantMbps", "ConstantMbps": 275})
    setp(s, V + "preferred_fps", 100)
    (root / "config" / "session.base.json").write_text(json.dumps(s, indent=1))
    store = {"version": 2.0, "profiles": [prof]}
    (root / "config" / "user_fov_profiles.json").write_text(json.dumps(store, indent=1))
    v = {"fov_override": True, "fov_outer": restriction[0], "fov_inner": restriction[1], "fov_upper": restriction[2], "fov_lower": restriction[3],
         "fov_profile": prof["id"], "encode_profile": encode_profile}
    (root / "config" / "airframexr.json").write_text(json.dumps({"headset": {"name": headset}, "video": v}, indent=1))
    (root / "config" / "airframexr_views.json").write_text(json.dumps({"fov_tan": [box[0], box[1], box[2], box[3], box[1], box[0], box[2], box[3]]}))
    out = subprocess.run(["wslpath", "-w", str(root)], capture_output=True, text=True).stdout.strip()
    subprocess.run([str(root / "bin" / "uitest.exe"), "--session-compile-selftest", "--out", out], cwd=str(root / "bin"), capture_output=True, timeout=300)
    comp = json.loads((root / "config" / "session.json").read_text()) if (root / "config" / "session.json").exists() else {}
    st = json.loads((root / "config" / "airframexr.json").read_text())
    line = (root / "session_compile.txt").read_text(errors="replace").strip() if (root / "session_compile.txt").exists() else ""
    # F4: the settings panel over the same folder (the compiled session.json is there now)
    subprocess.run([str(root / "bin" / "uitest.exe"), "--mask-choice-selftest", "--out", out], cwd=str(root / "bin"), capture_output=True, timeout=300)
    ui = {}
    if (root / "mask_choice.txt").exists():
        for l in (root / "mask_choice.txt").read_text(errors="replace").splitlines():
            k, _, v = l.partition("=")
            ui[k] = v
    return comp, st, line, ui


def fit_profile(with_mask=True, pid="fit"):
    p = {"id": pid, "name": FIT["name"], "headset": "Apple Vision Pro", "source": "author", "created": "2026-11-20T01:00:00", "tan": FIT["tan"],
         "tan_left": FIT["tan_left"], "tan_right": FIT["tan_right"], "render_quality": 0}
    if with_mask:
        p["mask"] = FIT["mask"]
    return p


def same_poly(got, want, tol=2e-4):
    return got is not None and len(got) == len(want) and all(abs(a[0] - b[0]) <= tol and abs(a[1] - b[1]) <= tol for a, b in zip(got, want))


def main():
    work = Path(tempfile.mkdtemp(prefix="maskcheck_", dir=windows_temp()))
    rows, ok_all = [], True
    tan = FIT["tan"]
    avp_view = [round(min(tan[k], AVP_BOX_AT_FOV1[k]), 4) for k in range(4)]   # the box sent to the app (video.client_box) = the stream's view
    quest_box = [1.35, 1.0, 1.05, 1.3]
    cases = [
        ("avp_mask", "Apple Headset", [1.73, 1.0, 1.19, 1.0], fit_profile(), tan, "foveated", avp_view, True),
        ("avp_no_foveation", "Apple Headset", [1.73, 1.0, 1.19, 1.0], fit_profile(), tan, "full-split", None, False),
        ("avp_profile_without_mask", "Apple Headset", [1.73, 1.0, 1.19, 1.0], fit_profile(False), tan, "foveated", None, True),
        ("avp_restriction_changed", "Apple Headset", [1.73, 1.0, 1.19, 1.0], fit_profile(), [tan[0] - 0.05] + tan[1:], "foveated", None, True),
        ("quest3_mask", "Meta Quest 3", quest_box, fit_profile(), tan, "foveated", quest_box, True),
    ]
    for name, headset, box, prof, restr, enc, view, fov_on in cases:
        comp, st, line, ui = compile_case(work, name, headset, box, prof, restr, enc)
        got = getp(st, "video.encode_mask")
        checks = {"session_foveation": getp(comp, "session_settings.video.foveated_encoding.enabled") is fov_on and getp(comp, "openvr_config.enable_foveated_encoding") is fov_on}
        if view is None:
            checks["no_mask"] = got is None
            checks["log"] = ("no foveation" in line) if enc == "full-split" else ("no mask" in line)
        else:
            want = {e: to_uv(polygon(FIT[f"tan_{e}"], FIT["mask"][e]), view) for e in ("left", "right")}
            checks["kind"] = got is not None and got.get("kind") == "polygon_uv_v1" and got.get("profile") == FIT["name"]
            checks["left"] = same_poly((got or {}).get("left"), want["left"])
            checks["right"] = same_poly((got or {}).get("right"), want["right"])
            # independent of the port: convex, and the encoder's block test hides about the outline's area (blocks on the line stay visible: a little less)
            for e in ("left", "right"):
                pe = (got or {}).get(e) or []
                checks[f"{e}_convex"] = convex(pe)
                checks[f"{e}_encoder_share"] = bool(pe) and 0.0 <= area_hidden(pe) - encoder_hidden(pe) <= 0.08
            checks["log"] = "foveated from the mask" in line
        # F4: the radios name the mask when the profile in force has one; on the Vision Pro the six numbers are the compiled ones, read-only
        masked = prof.get("mask") is not None and name != "avp_restriction_changed"
        checks["ui_labels"] = (ui.get("fov_text", "").startswith(f"Foveated from the mask of '{FIT['name']}'") and ui.get("full_text") == "No foveation (full frame + split encode)") if masked \
            else ui.get("fov_text", "").startswith("Foveated (ALVR")
        checks["ui_choice"] = ui.get("fov_checked") == ("False" if enc == "full-split" else "True")
        computed = masked and headset.startswith("Apple")
        checks["ui_fields"] = ui.get("fields_enabled") == ",".join(["False" if computed else "True"] * 6)
        if computed:
            want6 = [getp(comp, "session_settings.video.foveated_encoding.content." + k) for k in ("center_size_x", "center_size_y", "center_shift_x", "center_shift_y", "edge_ratio_x", "edge_ratio_y")]
            got6 = [float(x) for x in ui.get("fields", "").split(",") if x]
            checks["ui_compiled_numbers"] = len(got6) == 6 and all(w is not None and abs(g - w) <= 0.006 for g, w in zip(got6, want6))
        ok = all(checks.values())
        ok_all &= ok
        print(f"{'PASS' if ok else 'FAIL'}  {name}: {checks}" + ("" if ok else f"\n      log: {line[-400:]}\n      mask: {json.dumps(got)[:600]}\n      ui: {ui}"))
    print(f"mask_compile_check: {'PASS' if ok_all else 'FAIL'} ({work})")
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
