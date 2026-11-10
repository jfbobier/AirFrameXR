#!/usr/bin/env python3
"""OpenComposite in the launcher (tools/gui/OpenComposite.cs): the real download of its two DLLs and the switch of the OpenVR runtime path, in a temporary folder on the builder
(a copy of an openvrpaths.vrpath of its own; the real OpenVR files are never touched). Builds uitest.exe and runs --opencomposite-selftest.

    python3 harness/opencomposite_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild


def main():
    rid = f"oc_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --opencomposite-selftest runs\\{rid}")
    loc = Path(tempfile.mkdtemp(prefix="oc_"))
    lib.b_get(f"runs/{rid}/opencomposite.txt", loc / "oc.txt")
    L = {l.split("|")[0]: l for l in (loc / "oc.txt").read_text(errors="replace").splitlines()}
    for l in L.values():
        print(" ", l)
    steam = "C:\\Steam\\SteamVR"
    checks = [
        ("starts on SteamVR, nothing installed", "active=False" in L["start"] and "installed=False" in L["start"] and steam in L["start"]),
        ("cannot switch before the install", "not installed" in L["enable_before_install"]),
        ("the download gives two programs", "error=none" in L["install"] and "installed=True" in L["install"] and "dll32=-1" not in L["install"] and "dll64=-1" not in L["install"]),
        ("enable points the runtime at our folder", "active=True" in L["enable"] and steam not in L["enable"]),
        ("the other keys of the file are kept, a backup exists", all(k in L["kept_keys"] for k in ("config", "external_drivers", "jsonid", "runtime", "version")) and "backup=True" in L["kept_keys"]),
        ("enabling twice does not lose the SteamVR entry", L["enable_twice"].split("runtime=")[1] == L["enable"].split("runtime=")[1]),
        ("disable restores SteamVR", "active=False" in L["disable"] and steam in L["disable"]),
    ]
    ok = True
    for name, c in checks:
        print("CHECK", "PASS" if c else "FAIL", name); ok &= bool(c)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
