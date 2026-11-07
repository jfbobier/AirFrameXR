#!/usr/bin/env python3
"""A writer saves a 20 KB JSON through the GUI's Json.Save as fast as it can while a reader opens it with share read/write/delete (as the Rust host does) and checks it: how often
the file is missing or partial with the old way (File.Replace) and with the atomic rename (MoveFileEx). Builds uitest.exe on the builder and runs --json-race-selftest.

    python3 harness/json_atomic_check.py [seconds per strategy, default 6]
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild


def main(argv):
    secs = int(argv[0]) if argv else 6
    rid = f"race_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    loc = Path(tempfile.mkdtemp(prefix="race_"))
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --json-race-selftest {secs} --out runs\\{rid}")
    lib.b_get(f"runs/{rid}/json_race.txt", loc / "json_race.txt")
    res = {}
    for line in (loc / "json_race.txt").read_text(errors="replace").splitlines():
        f = line.split("|"); res[f[0]] = {k: int(v) for k, v in (x.split("=") for x in f[1:])}
        print(" ", line)
    new = res.get("MoveFileEx", {})
    ok = bool(new) and new["file_missing"] == 0 and new["partial"] == 0 and new["reads"] > 1000
    print("CHECK", "PASS" if ok else "FAIL", "atomic save: the reader never sees a missing or partial file with the rename;", f"File.Replace missed {res.get('File.Replace', {}).get('file_missing', '?')} reads")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
