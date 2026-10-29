#!/usr/bin/env python3
"""The Advanced window's profile button (docs/HMD_PROFILES.md, key `fov`): with a headset profile that has a measured visible window it fills the four fields (the visible
angles plus the margin, as tangents) and the render quality slider, ticks the override and saves. Run python3 harness/ui_matrix.py first (it builds uitest.exe).
Also checks the shipped Vision Pro profile against the calibrated row of docs/AVP_FOV.md.

    python3 harness/fov_profile_check.py
"""
import json, math, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

root = Path(__file__).resolve().parent.parent
prof = json.loads((root / "profiles" / "apple-vision-pro.profile.json").read_text())["fov"]
want = [round(math.tan(math.radians(prof["visible_deg"][k] + prof["margin_deg"])), 2) for k in ("outer", "inner", "upper", "lower")]
doc = [1.00, 0.71, 0.73, 0.66]   # docs/AVP_FOV.md, the calibrated row of 2026-10-29 night
ok_doc = want == doc and prof["render_quality"] == 0 and prof.get("client_fov_scale") == 0.8
print("CHECK", "PASS" if ok_doc else "FAIL", f"shipped Vision Pro profile gives {want} and quality {prof['render_quality']} (docs/AVP_FOV.md: {doc}, 0) and client FoV scale {prof.get('client_fov_scale')} (0.8)")

rid = f"fovp_{time.strftime('%H%M%S')}"
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-profile-selftest 1 --out runs\\{rid}")
loc = Path(tempfile.mkdtemp(prefix="fovp_"))
lib.b_get(f"runs/{rid}/fovprofile.txt", loc / "fovprofile.txt")
lines = (loc / "fovprofile.txt").read_text(errors="replace").splitlines() if (loc / "fovprofile.txt").exists() else []
print("\n".join(lines))
ok = "button=True visible=True" in lines and "override=True outer=0.94 inner=0.93 upper=0.90 lower=0.76 quality=0.50" in lines
print("CHECK", "PASS" if ok else "FAIL", "the button fills the fields and the slider, ticks the override and saves")

# an override made for the Vision Pro must not be applied to a headset with another field of view (a Quest's lower edge is 55 degrees; the Vision Pro's tuned 0.76 would cut it to 37; the Vision Pro with its app's FoV Scale at 0.8 keeps its override)
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-other-headset-selftest 1 --out runs\\{rid}")
lib.b_get(f"runs/{rid}/fovother.txt", loc / "fovother.txt")
lines2 = (loc / "fovother.txt").read_text(errors="replace").splitlines() if (loc / "fovother.txt").exists() else []
print("\n".join(lines2))
ok2 = (len(lines2) == 3 and lines2[0] == "fits_avp=True fits_quest=False fits_unknown=True fits_avp_scaled=True fits_same_name_new_shape=True fits_unnamed_first_box_to_ceiling=True fits_unnamed_first_box_on_quest=False" and lines2[1].startswith("tick_on_quest=False lower_shown=1.43")
       and lines2[2] == "after_tick: override=True lower=1.43 fits_quest=True")
print("CHECK", "PASS" if ok2 else "FAIL", "an override saved for the Vision Pro stays on the Vision Pro whatever box its app declares (the client's ceiling), is off on a Quest (the fields show the Quest's own values) and ticking starts from the Quest's values")
sys.exit(0 if ok and ok_doc and ok2 else 1)
