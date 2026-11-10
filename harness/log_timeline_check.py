#!/usr/bin/env python3
"""The stats window's "last run over time" (LogTimeline in tools/gui/StatsCharts.cs) against a real general log: for each game given, the run found in the log (start, end, how
many 10 s frame generation samples and 30 s GPU memory samples). The expected counts are computed here from the same file with plain Python.

    python3 harness/log_timeline_check.py <AirFrameXR.log> <game.exe>[,<game.exe>...]
"""
import re, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild


def expected(text, exe):
    cur = last = None
    for l in text.splitlines():
        m = re.match(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\.\d+ \[host\] INFO  (game started|game ended): (.+?)(?: after .*)?\s*$", l)
        if m and m.group(3).lower() == exe.lower():
            if m.group(2) == "game started":
                cur = last = {"start": m.group(1), "perf": 0, "gpu": 0, "arr": 0}
            elif cur:
                cur["end"] = m.group(1); cur = None
            continue
        if cur is None:
            continue
        if re.search(r"frame generation perf: app [\d.]+ fps.*? stream [\d.]+ fps.*? lag [\d.]+ ms", l):
            cur["perf"] += 1
        elif re.search(r"GPU \d+ of \d+ MB used", l):
            cur["gpu"] += 1
        elif re.search(r"phase lock: arrivals -?[\d.]+ ms before the render tick \(target [\d.]+\)", l):
            cur["arr"] += 1
    return last


def main(argv):
    log, exes = Path(argv[0]), argv[1]
    text = log.read_text(errors="replace")
    rid = f"tl_{time.strftime('%H%M%S')}"
    errs = uibuild.build(rid)
    if errs:
        print("uitest.exe does not build:\n" + "\n".join(errs[:10]))
        return 2
    lib.b_run(f"mkdir runs\\{rid}")
    lib.b_put(log, f"runs/{rid}/AirFrameXR.log")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --log-timeline-selftest runs\\{rid}\\AirFrameXR.log --exes \"{exes}\" --out runs\\{rid}")
    loc = Path(tempfile.mkdtemp(prefix="tl_"))
    lib.b_get(f"runs/{rid}/log_timeline.txt", loc / "t.txt")
    ok = True
    for line, exe in zip((loc / "t.txt").read_text().splitlines(), exes.split(",")):
        print(" ", line)
        e = expected(text, exe)
        good = (e is None and line.endswith("|none")) or (e is not None and f"start={e['start']}" in line and f"perf={e['perf']}|" in line and f"gpu={e['gpu']}|" in line and f"arr={e['arr']}|" in line)
        print("CHECK", "PASS" if good else "FAIL", exe)
        ok &= good
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
