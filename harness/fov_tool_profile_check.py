#!/usr/bin/env python3
"""What the headset tools save (docs/PICTURE_EDGES_DESIGN.md sections 6 and 6b, step B): a schema 2 profile with the eyes apart, the method (quickfit / restriction), the light seal size and the panel at
the measurement, and applying it writes the left eye's numbers plus the right eye's own (`video.fov_override_right`). Runs tools/gui/UiTest.cs --tool-profile-selftest.

    python3 harness/fov_tool_profile_check.py            # on the builder (harness/uibuild.py builds uitest.exe)
    python3 harness/fov_tool_profile_check.py --local    # on this Windows laptop through WSL interop (tools/wsl/check_gui.sh builds uitest.exe first)
"""
import subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))


def run_local():
    root = Path(__file__).resolve().parent.parent
    r = subprocess.run(["bash", str(root / "tools/wsl/check_gui.sh")], capture_output=True, text=True)
    if "check_gui: OK" not in r.stdout:
        print(r.stdout[-1500:]); return None
    t = next(Path("/mnt/c/Users").glob("*/AppData/Local/Temp/airframexr_gui"))
    out = t / "toolprofileout"; out.mkdir(exist_ok=True)
    win = subprocess.run(["wslpath", "-w", str(out)], capture_output=True, text=True).stdout.strip()
    (out / "toolprofile.txt").unlink(missing_ok=True)
    subprocess.run(["./uitest.exe", "--tool-profile-selftest", "1", "--out", win], cwd=t, timeout=120)
    f = out / "toolprofile.txt"
    return f.read_text(errors="replace").replace("\r", "").splitlines() if f.exists() else []


def run_builder():
    import lib, uibuild
    rid = f"toolprofile_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10])); return None
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --tool-profile-selftest 1 --out runs\\{rid}")
    loc = Path(tempfile.mkdtemp(prefix="toolprofile_"))
    lib.b_get(f"runs/{rid}/toolprofile.txt", loc / "toolprofile.txt")
    f = loc / "toolprofile.txt"
    return f.read_text(errors="replace").splitlines() if f.exists() else []


def main(argv):
    got = run_local() if "--local" in argv else run_builder()
    if got is None: return 2
    for g in got: print(g)
    return 0 if got and all(g.startswith("CHECK PASS") for g in got) and len(got) == 7 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
