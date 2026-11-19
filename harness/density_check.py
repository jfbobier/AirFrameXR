"""docs/FOV_CONTRACT_PLAN.md phase 0: the launcher's real SessionCompiler (uitest.exe --session-compile-selftest) against harness/fov_contract_model.py for the
Faster / Sharper stream sizes, on this PC (WSL interop, no builder): build the GUIs first with tools/wsl/check_gui.sh.

    python3 harness/density_check.py        prints one line per case, PASS when the stream size, the encoded size and the foveation equal the model's

Cases: the SWGR settings of 2026-11-14 (Vision Pro, 275 Mbps, 100 Hz, the quality window as the restriction) with the headset's box at sliders 0.8 / 0.9 and at 0.80 / 0.76,
in the three modes; "bitrate" must stay the 1.3.11 result (today's rule), the two new modes follow the model with the foveation inside the window following Apple's map.
"""
import json, shutil, subprocess, sys, tempfile
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import fov_contract_model as m

ROOT = Path(__file__).resolve().parent.parent
DEFAULT = json.loads((ROOT / "tools/install/session.default.json").read_text())
V = "session_settings.video."


def setp(d, path, v):
    ks = path.split(".")
    for k in ks[:-1]:
        d = d.setdefault(k, {})
    d[ks[-1]] = v


def getp(d, path, default=None):
    for k in path.split("."):
        if not isinstance(d, dict) or k not in d:
            return default
        d = d[k]
    return d


def windows_temp():
    users = [p for p in Path("/mnt/c/Users").iterdir() if p.name.lower() not in ("public", "default", "default user", "all users") and (p / "AppData/Local/Temp").is_dir()]
    return users[0] / "AppData/Local/Temp"


def compile_case(work, mode, box, window, golden=(3648, 3200), mbps=275, fps=100):
    root = work / f"case_{mode}_{len(list(work.iterdir()))}"
    (root / "bin").mkdir(parents=True); (root / "config").mkdir(); (root / "profiles").mkdir()
    shutil.copy(windows_temp() / "airframexr_gui" / "uitest.exe", root / "bin" / "uitest.exe")
    for p in (ROOT / "profiles").glob("apple-vision-pro*.profile.json"):
        shutil.copy(p, root / "profiles" / p.name)
    s = json.loads(json.dumps(DEFAULT))
    for k in ("transcoding_view_resolution", "emulated_headset_view_resolution"):
        setp(s, V + k, {"variant": "Absolute", "Absolute": {"width": golden[0], "height": {"set": True, "content": golden[1]}}})
    setp(s, V + "bitrate.mode", {"variant": "ConstantMbps", "ConstantMbps": mbps})
    setp(s, V + "preferred_fps", fps)
    (root / "config" / "session.base.json").write_text(json.dumps(s, indent=1))
    v = {"fov_override": True, "fov_outer": window[0], "fov_inner": window[1], "fov_upper": window[2], "fov_lower": window[3]}
    v["stream_density"] = mode
    (root / "config" / "airframexr.json").write_text(json.dumps({"headset": {"name": "Apple Headset"}, "video": v}, indent=1))
    (root / "config" / "airframexr_views.json").write_text(json.dumps({"fov_tan": [box[0], box[1], box[2], box[3], box[1], box[0], box[2], box[3]]}))
    exe = subprocess.run(["wslpath", "-w", str(root / "bin" / "uitest.exe")], capture_output=True, text=True).stdout.strip()
    out = subprocess.run(["wslpath", "-w", str(root)], capture_output=True, text=True).stdout.strip()
    subprocess.run([str(root / "bin" / "uitest.exe"), "--session-compile-selftest", "--out", out], cwd=str(root / "bin"), capture_output=True, timeout=300)
    comp = json.loads((root / "config" / "session.json").read_text())
    line = (root / "session_compile.txt").read_text(errors="replace").strip() if (root / "session_compile.txt").exists() else ""
    return comp, line


def main():
    rm = m.load_ratemap(); F = m.AVP_FRUSTUM; W = m.QUALITY_WINDOW
    work = windows_temp() / ("airframexr_density_" + next(tempfile._get_candidate_names()))
    work.mkdir()
    fails = 0
    for sl in ((0.8, 0.9), (0.80, 0.76)):
        box = (F[0] * sl[0], F[1] * sl[0], F[2] * sl[1], F[3] * sl[1])
        for mode in ("bitrate", "performance", "balanced", "sharp"):
            comp, line = compile_case(work, mode, box, W)
            eye = (int(getp(comp, "openvr_config.eye_resolution_width", 0)), int(getp(comp, "openvr_config.eye_resolution_height", 0)))
            g = [getp(comp, V + "foveated_encoding.content." + k, 0) for k in ("center_size_x", "center_size_y", "center_shift_x", "center_shift_y", "edge_ratio_x", "edge_ratio_y")]
            if mode == "bitrate":
                want_eye, want_g, _ = m.choose_eye(box, rm, 275, 100, W)
            else:
                # 1.4: the headset app declares the restriction (video.client_box): the stream covers the restriction capped at the Vision Pro's box, no window
                cb = tuple(round(min(W[k], m.AVP_FRUSTUM[k]), 4) for k in range(4))
                want_eye = m.mode_eye(mode, cb, rm)
                want_g = m.milder(cb, want_eye, rm, None)
            ok = tuple(eye) == tuple(want_eye) and all(abs(a - b) <= 0.0015 for a, b in zip(g[:2] + g[4:], [want_g[0], want_g[1], want_g[4], want_g[5]]))
            fails += 0 if ok else 1
            dens = line.split("density at the centre: ")[1].split(";")[0] if "density at the centre: " in line else "(no density in the log line)"
            print(f"{'PASS' if ok else 'FAIL'}  sliders {sl[0]:.2f}/{sl[1]:.2f} {mode:8s} C# eye {eye[0]}x{eye[1]} model {want_eye[0]}x{want_eye[1]}; "
                  f"C# cs {g[0]:.3f}x{g[1]:.3f} er {g[4]:.2f}/{g[5]:.2f} model cs {want_g[0]:.3f}x{want_g[1]:.3f} er {want_g[4]:.2f}/{want_g[5]:.2f}; {dens}", flush=True)
    print("density_check:", "PASS" if fails == 0 else f"FAIL ({fails})")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
