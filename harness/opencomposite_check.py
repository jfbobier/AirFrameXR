#!/usr/bin/env python3
"""OpenComposite in the launcher (tools/gui/OpenComposite.cs): the DLLs the package ships (third_party/opencomposite, with the hashes SOURCE.txt states) and the switch of the
OpenVR runtime path, in a temporary folder on the builder (a vrpath file of its own; the real OpenVR files are never touched). Builds uitest.exe and runs --opencomposite-selftest.

    python3 harness/opencomposite_check.py
"""
import hashlib, re, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

BUNDLE = lib.ROOT / "third_party/opencomposite"


def main():
    src = (BUNDLE / "SOURCE.txt").read_text()
    stated = dict(re.findall(r"(vrclient(?:_x64)?\.dll)\s+sha256 ([0-9a-f]{64})", src))
    tar = next(BUNDLE.glob("OpenOVR-*.tar.gz"))
    tar_stated = re.search(r"sha256 ([0-9a-f]{64})\s*\n\s*\nAirFrameXR", src)
    rid = f"oc_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    lib.b_run(f"mkdir runs\\{rid}\\bundle")
    for n in ("vrclient.dll", "vrclient_x64.dll"):
        lib.b_put(BUNDLE / n, f"runs/{rid}/bundle/{n}")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --opencomposite-selftest runs\\{rid}")
    loc = Path(tempfile.mkdtemp(prefix="oc_"))
    lib.b_get(f"runs/{rid}/opencomposite.txt", loc / "oc.txt")
    L = {l.split("|")[0]: l for l in (loc / "oc.txt").read_text(errors="replace").splitlines()}
    for l in L.values():
        print(" ", l)
    steam = "C:\\Steam\\SteamVR"
    sha = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()
    checks = [
        ("the shipped DLLs are the ones SOURCE.txt states (sha256)", all(sha(BUNDLE / n) == h for n, h in stated.items()) and len(stated) == 2),
        ("the source archive is the one SOURCE.txt states, and it is named after the commit", bool(tar_stated) and sha(tar) == tar_stated.group(1) and re.search(r"[0-9a-f]{40}", tar.name).group(0) in src),
        ("the licence texts ship (GPL-3 and the two bundled)", all((lib.ROOT / "third_party/licenses" / f).exists() for f in ("OpenComposite-LICENSE.txt", "OpenComposite-OpenVR-headers-LICENSE.txt", "OpenComposite-inih-LICENSE.txt"))),
        ("starts on SteamVR, nothing there", "active=False" in L["start"] and "installed=False" in L["start"] and steam in L["start"]),
        ("cannot switch to what is not there", "has no OpenComposite" in L["enable_without_bundle"]),
        ("the package's two DLLs are found", "installed=True" in L["bundle"] and "dll32=2261504" in L["bundle"] and "dll64=2447872" in L["bundle"]),
        ("enable points the runtime at our folder", "active=True" in L["enable"] and steam not in L["enable"]),
        ("the other keys of the file are kept, a backup exists", all(k in L["kept_keys"] for k in ("config", "external_drivers", "jsonid", "runtime", "version")) and "backup=True" in L["kept_keys"]),
        ("enabling twice does not lose the SteamVR entry", L["enable_twice"].split("runtime=")[1] == L["enable"].split("runtime=")[1] and steam in L["disable"]),
        ("disable restores SteamVR", "active=False" in L["disable"] and steam in L["disable"]),
    ]
    ok = True
    for name, c in checks:
        print("CHECK", "PASS" if c else "FAIL", name); ok &= bool(c)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
