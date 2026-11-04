#!/usr/bin/env python3
"""The user's field-of-view profiles (config\\user_fov_profiles.json) and the check of four typed numbers (tools/gui/UserFovProfiles.cs): red for numbers that cannot
be a field of view, yellow for valid but strange ones, the store's add / rename / remove / corrupt-file behaviour. Run python3 harness/ui_matrix.py first (it builds uitest.exe).

    python3 harness/fov_store_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

WANT = [
    "parse=0.96|1.05|nan|nan|nan",
    "nan_red=Invalid",
    "zero_red=Invalid",
    "negative_red=Invalid",
    "deg90_red=Invalid",
    "over_declared_yellow=Strange area=0.94",     # above the declared box is a note, not an error (the streamer caps each edge); the share counts each edge at most the declared one
    "avp_calibration_valid=Valid",
    "quest_trim_valid=Valid area=0.90",          # the Quest 3 starting point: 51 / 40 / 43 / 54 degrees, about 10 % less area than the declared box
    "quest_declared_valid=Valid area=1.00",
    "degrees_as_tangents_yellow=Strange",
    "side_ratio_yellow=Strange",
    "vertical_ratio_yellow=Strange",
    "tiny_yellow=Strange",
    "small_share_yellow=Strange area=0.27",
    "avp_total=79.5x66.8",
    "empty=0",
    "after_add=b:mine (2),a:Mine window_b=2 quality_b=0.5",
    "rename=True renamed=Renamed",
    "remove=True remove_again=False left=1",
    "corrupt=0",
    "card=red:False valid:True/0 yellow:True/True partial:False",
    "card_added=1:My values:1.2349:False",
    "seed=1 name=Test AVP (standard calibration) tan=0.97/0.71/0.69/0.63 scale=0.8",
    "seed_again=0 after_delete=0",
    "seed_quest=1 name=Test Quest (starting point, not measured) tan=1.23/0.84/0.93/1.38",
    "apply=True 0.90/0.71/0.69/0.60 id=std-t-avp for=Apple Headset",
]

rid = f"fovs_{time.strftime('%H%M%S')}"
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --fov-store-selftest 1 --out runs\\{rid}")
loc = Path(tempfile.mkdtemp(prefix="fovs_"))
lib.b_get(f"runs/{rid}/fovstore.txt", loc / "fovstore.txt")
got = (loc / "fovstore.txt").read_text(errors="replace").splitlines() if (loc / "fovstore.txt").exists() else []
bad = 0
for i, w in enumerate(WANT):
    g = got[i] if i < len(got) else "(missing)"
    ok = g == w
    bad += not ok
    print("CHECK", "PASS" if ok else "FAIL", w if ok else f"{w}   got: {g}")
sys.exit(1 if bad or len(got) != len(WANT) else 0)
