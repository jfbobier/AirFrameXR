#!/usr/bin/env python3
"""The Advanced window's profile button (docs/HMD_PROFILES.md, key `fov`): with a headset profile that has a measured visible window it fills the four fields (the visible
angles plus the margin, as tangents) and the render quality slider, ticks the override and saves. Run python3 harness/ui_matrix.py first (it builds uitest.exe).
Also checks the shipped Vision Pro profile against the row of docs/AVP_FOV.md.

    python3 harness/fov_profile_check.py
"""
import json, math, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

root = Path(__file__).resolve().parent.parent
prof = json.loads((root / "profiles" / "apple-vision-pro.profile.json").read_text())["fov"]
want = [round(math.tan(math.radians(prof["visible_deg"][k] + prof["margin_deg"])), 2) for k in ("outer", "inner", "upper", "lower")]
doc = [0.94, 0.93, 0.90, 0.76]   # docs/AVP_FOV.md, the recommended row
ok_doc = want == doc and prof["render_quality"] == 0.5
print("CHECK", "PASS" if ok_doc else "FAIL", f"shipped Vision Pro profile gives {want} and quality {prof['render_quality']} (docs/AVP_FOV.md: {doc}, 0.5)")

rid = f"fovp_{time.strftime('%H%M%S')}"
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-profile-selftest 1 --out runs\\{rid}")
loc = Path(tempfile.mkdtemp(prefix="fovp_"))
lib.b_get(f"runs/{rid}/fovprofile.txt", loc / "fovprofile.txt")
lines = (loc / "fovprofile.txt").read_text(errors="replace").splitlines() if (loc / "fovprofile.txt").exists() else []
print("\n".join(lines))
ok = "button=True visible=True" in lines and "override=True outer=0.94 inner=0.93 upper=0.90 lower=0.76 quality=0.50" in lines
print("CHECK", "PASS" if ok else "FAIL", "the button fills the fields and the slider, ticks the override and saves")
sys.exit(0 if ok and ok_doc else 1)
