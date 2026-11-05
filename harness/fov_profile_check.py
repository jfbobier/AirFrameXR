#!/usr/bin/env python3
"""The launcher's FOV card (profile list) (docs/HMD_PROFILES.md, key `fov`): with a headset profile that has a measured visible window it fills the four fields (the visible
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
doc = [0.97, 0.71, 0.69, 0.63]   # docs/AVP_FOV.md, the shipped 99 % rectangle (2026-10-29)
# docs/PICTURE_EDGES_DESIGN.md step A: the shipped rectangle stays (one wearer's fit, offered in the list, never proposed by the wizard) and loses
# client_fov_scale (the stock app's FoV slider: dead advice on our client, which declares Apple's frustum)
ok_doc = want == doc and prof["render_quality"] == 0 and "client_fov_scale" not in prof
print("CHECK", "PASS" if ok_doc else "FAIL", f"shipped Vision Pro profile gives {want} and quality {prof['render_quality']} (docs/AVP_FOV.md: {doc}, 0), no client_fov_scale (has {prof.get('client_fov_scale')})")

rid = f"fovp_{time.strftime('%H%M%S')}"
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-profile-selftest 1 --out runs\\{rid}")
loc = Path(tempfile.mkdtemp(prefix="fovp_"))
lib.b_get(f"runs/{rid}/fovprofile.txt", loc / "fovprofile.txt")
lines = (loc / "fovprofile.txt").read_text(errors="replace").splitlines() if (loc / "fovprofile.txt").exists() else []
print("\n".join(lines))
ok = (len(lines) == 3 and lines[0] == "profiles=2 tick_enabled=True restricted=False" and lines[1] == "override=True outer=0.97 inner=0.71 upper=0.69 lower=0.63 profile=d1"
      and lines[2] == "after_other: outer=0.90 inner=0.65 upper=0.60 lower=0.55 profile=d3")
print("CHECK", "PASS" if ok else "FAIL", "the launcher's FOV card offers only the profiles that fit the headset's box, ticking with a profile saves its tangents and its id, choosing another while ticked saves that one")

# an override made for the Vision Pro must not be applied to a headset with another field of view (a Quest's lower edge is 55 degrees; the Vision Pro's tuned 0.76 would cut it to 37; the Vision Pro with its app's FoV Scale at 0.8 keeps its override)
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-other-headset-selftest 1 --out runs\\{rid}")
lib.b_get(f"runs/{rid}/fovother.txt", loc / "fovother.txt")
lines2 = (loc / "fovother.txt").read_text(errors="replace").splitlines() if (loc / "fovother.txt").exists() else []
print("\n".join(lines2))
ok2 = (len(lines2) == 3 and lines2[0] == "fits_avp=True fits_quest=False fits_unknown=True fits_avp_scaled=True fits_same_name_new_shape=True fits_unnamed_first_box_to_ceiling=True fits_unnamed_first_box_on_quest=False" and lines2[1] == "profiles_on_quest=2 tick_on_quest=False"
       and lines2[2] == "after_tick: override=True lower=0.63 fits_quest=True")
print("CHECK", "PASS" if ok2 else "FAIL", "an override saved for the Vision Pro stays on the Vision Pro whatever box its app declares (the client's ceiling), is off on a Quest and ticking there saves a profile that fits the Quest")
sys.exit(0 if ok and ok_doc and ok2 else 1)
