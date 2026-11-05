#!/usr/bin/env python3
"""The field-of-view UI of docs/PICTURE_EDGES_DESIGN.md sections 6 and 9 (the owner's UI review of 2026-11-05), checked on the real forms
(tools/gui/UiTest.cs --fov-ui-plan-selftest, by control names; the contract is in the comment above FovUiPlanSelfTest):

  - Vision Pro: the wizard offers "Whole panel" and "Fit now", no ray calibration and no restriction list; the settings card has the
    picture-edges row and "Fit the picture to your eyes" (the retry); the profile manager has the fit, typed values and a per-eye row.
  - Meta Quest: no headset calibration anywhere (no fit, no picture edges, greyed in the card and the manager), the FOV restriction stays
    (wizard choice, card row, typed values).
  - No headset seen yet: everything enabled.
  - Everywhere: the 30-minute ray calibration and the fovgrid button are gone; calibrate.exe opens on the fit (Retry, the Space instruction).

Written before the UI (test-first): until steps A and B are built most lines FAIL; `today` below is what 2026-11-05's GUI prints.

    python3 harness/fov_ui_plan_check.py            # on the builder (harness/uibuild.py builds uitest.exe)
    python3 harness/fov_ui_plan_check.py --local    # on this Windows laptop through WSL interop (tools/wsl/check_gui.sh builds uitest.exe first)
"""
import subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))

WANT = [
    "wizard_avp: whole_panel=True fit_now=True ray_calibration=False restriction=False",
    "wizard_quest: whole_panel=False fit_now=False ray_calibration=False restriction=True",
    "manager_avp: fit=True ray_calibration=False fovgrid_button=False typed_values=True per_eye=True",
    "card_avp: picture_edges=True fit=True restriction=True",
    "manager_quest: fit=False ray_calibration=False fovgrid_button=False typed_values=True per_eye=True",
    "card_quest: picture_edges=False fit=False restriction=True",
    "manager_none: fit=True ray_calibration=False fovgrid_button=False typed_values=True per_eye=True",
    "card_none: picture_edges=True fit=True restriction=True",
    "calibrate: retry=True fit_instructions=True ray_sweep=False",
]


def run_local():
    root = Path(__file__).resolve().parent.parent
    r = subprocess.run(["bash", str(root / "tools/wsl/check_gui.sh")], capture_output=True, text=True)
    if "check_gui: OK" not in r.stdout:
        print(r.stdout[-1500:])
        return None
    t = next(Path("/mnt/c/Users").glob("*/AppData/Local/Temp/airframexr_gui"))
    out = t / "planout"
    out.mkdir(exist_ok=True)
    win = subprocess.run(["wslpath", "-w", str(out)], capture_output=True, text=True).stdout.strip()
    subprocess.run(["./uitest.exe", "--fov-ui-plan-selftest", "1", "--out", win], cwd=t, timeout=180)
    f = out / "fovuiplan.txt"
    return f.read_text(errors="replace").replace("\r", "").splitlines() if f.exists() else []


def run_builder():
    import lib, uibuild
    rid = f"fovui_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return None
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-ui-plan-selftest 1 --out runs\\{rid}")
    loc = Path(tempfile.mkdtemp(prefix="fovui_"))
    lib.b_get(f"runs/{rid}/fovuiplan.txt", loc / "fovuiplan.txt")
    f = loc / "fovuiplan.txt"
    return f.read_text(errors="replace").splitlines() if f.exists() else []


def main(argv):
    got = run_local() if "--local" in argv else run_builder()
    if got is None:
        return 2
    bad = 0
    for i, w in enumerate(WANT):
        g = got[i] if i < len(got) else "(missing)"
        ok = g == w
        bad += not ok
        print("CHECK", "PASS" if ok else "FAIL", w if ok else f"{w}\n       got: {g}")
    return 1 if bad or len(got) != len(WANT) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
