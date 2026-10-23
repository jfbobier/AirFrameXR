#!/usr/bin/env python3
"""Game history on the fixture records (harness/fixtures/ui_matrix/config/games), as text: the desktop mirror's record is not listed (U8), a record with an absurd
latency shows n/a (U7). `uitest.exe --dump-performance <game>`. Exit 0 = pass."""
import sys, time, subprocess
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

rid = "perf_check_" + time.strftime("%Y%m%d_%H%M%S")
errs = uibuild.build(rid)
if errs:
    print("uitest.exe does not build:\n" + "\n".join(errs[:10])); sys.exit(2)
fails = 0
stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
def check(name, ok, detail=""):
    global fails
    fails += not ok
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}" + ("" if ok else f": {detail}"))
for game in ("Cyberpunk2077.exe", "LiesOfP-Win64-Shipping.exe", "mirrordesktop.exe"):
    stem = game[:-4]
    lib.b_run(f"if not exist runs\\{rid}\\{stem} mkdir runs\\{rid}\\{stem}")
    proc = lib.start_limited(rid, "va_perf_check", f"perf_{stem}.bat", f"_tmp\\uitest\\run\\uitest.exe --dump-performance {game} --out runs\\{rid}\\{stem} > runs\\{rid}\\{stem}.txt 2>&1\r\n", f"rc_{stem}.txt")
    try:
        proc.wait(120)
    except subprocess.TimeoutExpired:
        print("BLOCKED: uitest.exe did not finish"); sys.exit(2)
    dest = lib.ROOT / "harness/results/performance_check" / stamp / stem
    lib.b_get(f"runs/{rid}/{stem}/performance.txt", dest / "performance.txt")
    f = dest / "performance.txt"
    txt = f.read_text(errors="replace") if f.exists() else ""
    check(f"{stem}: window dumped", bool(txt))
    check(f"{stem}: the mirror is not listed", "mirror" not in txt.lower() and "mirrordesktop" not in txt.lower(), "the mirror shows in the window")
    if stem == "LiesOfP-Win64-Shipping":
        check("absurd latency shows n/a", "n/a" in txt and "4958802102" not in txt and "4958802102" not in txt.replace(" ", ""), "no n/a or the number leaked")
    if stem == "Cyberpunk2077":
        check("streamer latency line (encoder + transport + decoder)", "Streamer latency 33 ms = encoder 6 + transport 12 + decoder 15" in txt, "line missing or wrong")
        check("list holds the real games", "item Lies of P" in txt, "games missing")
sys.exit(1 if fails else 0)
