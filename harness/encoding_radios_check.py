#!/usr/bin/env python3
"""The Encoding radio buttons of the settings panel are one group: checking Full frame unchecks Foveated and back (1.0 had them in different containers, both could
be checked and the save then stored full frame: a user's foveated choice came back as full frame after a restart). Run python3 harness/ui_matrix.py first (it builds uitest.exe).

    python3 harness/encoding_radios_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

rid = f"enc_{time.strftime('%H%M%S')}"
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --encoding-selftest 1 --out runs\\{rid}")
loc = Path(tempfile.mkdtemp(prefix="enc_"))
lib.b_get(f"runs/{rid}/encoding.txt", loc / "encoding.txt")
lines = (loc / "encoding.txt").read_text(errors="replace").splitlines() if (loc / "encoding.txt").exists() else []
print("\n".join(lines))
ok = lines[:1] == ["radios=2"] and "full_then: fov=False full=True" in lines and "fov_then: fov=True full=False" in lines
print("CHECK", "PASS" if ok else "FAIL", "the Encoding radio buttons exclude each other")
sys.exit(0 if ok else 1)
