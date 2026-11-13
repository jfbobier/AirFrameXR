#!/usr/bin/env python3
"""Log hygiene (1.3.10, docs/PLAN_1_3_10_FINISH.md H1, H2): pure functions on log text, used by lib.py's `log_noise` and `log_routing` checks.
`python3 harness/log_hygiene_check.py` runs the self-test (no builder): synthetic logs that must fail and pass each check."""
import re, sys
from collections import Counter

LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[([^\]]+)\] (ERROR|WARN |INFO |VERB |DEBUG) (.*)$")


def shape(line):
    m = LINE.match(line)
    body = m.group(4) if m else line
    return re.sub(r"\d+(\.\d+)?", "#", body)[:60]


def noise(text, max_share=0.25, min_lines=100):
    """No one message shape (digits folded) is more than max_share of the lines once there are min_lines. -> (ok, detail)"""
    lines = [l for l in text.splitlines() if LINE.match(l)]
    if len(lines) < min_lines:
        return True, {"lines": len(lines), "note": f"under {min_lines} lines: not judged"}
    shape_n, (top, n) = None, Counter(shape(l) for l in lines).most_common(1)[0]
    return n / len(lines) <= max_share, {"lines": len(lines), "top_shape": top, "share": round(n / len(lines), 3), "max": max_share}


def routing(main, run):
    """Every WARN/ERROR and [shim: line of the main log inside the run's time window is in the run log too. -> (ok, detail)"""
    rl = [LINE.match(l) for l in run.splitlines()]
    rl = [m for m in rl if m]
    if not rl:
        return False, {"note": "run log has no lines"}
    t0, t1 = rl[0].group(1), rl[-1].group(1)
    have = {(m.group(2), m.group(4)) for m in rl}
    missing = []
    for l in main.splitlines():
        m = LINE.match(l)
        if not m or not (t0 <= m.group(1) <= t1):
            continue
        if m.group(3).strip() in ("WARN", "ERROR") or m.group(2).startswith("shim:") or "client frame gaps" in m.group(4):
            if (m.group(2), m.group(4)) not in have:
                missing.append(l[:120])
    return not missing, {"window": [t0, t1], "missing": missing[:5], "missing_n": len(missing)}


def _self():
    ts = lambda i: f"2026-11-12 10:00:{i % 60:02d}.000"
    flood = "\n".join(f"{ts(i)} [alvr] ERROR [floor] head y 1.{i} floor y 0.00 offset 0 cm" for i in range(86)) + "\n" + \
            "\n".join(f"{ts(i)} [host] INFO  line {i}x{'y' * (i % 7)}" for i in range(14)) + "\n"
    ok, d = noise(flood); assert not ok and d["share"] >= 0.8, d
    varied = "\n".join(f"{ts(i)} [host] INFO  event{chr(97 + i % 26)}{chr(97 + i // 26)} happened" for i in range(120))
    assert noise(varied)[0]
    assert noise("short")[0]
    run = f"{ts(1)} [log] INFO  g.exe: run log started\n{ts(5)} [host] INFO  game ended\n"
    bad = f"{ts(2)} [host] WARN  stall detected\n{ts(3)} [shim:g.exe] INFO  game start\n{ts(30)} [host] WARN  outside window\n"
    ok, d = routing(bad, run); assert not ok and d["missing_n"] == 2, d
    good = run + f"{ts(2)} [host] WARN  stall detected\n{ts(3)} [shim:g.exe] INFO  game start\n"
    assert routing(bad, good)[0]
    print("log_hygiene_check selftest: PASS")


def _dir(d):
    """python3 harness/log_hygiene_check.py <results dir>: the noise and routing checks on the logs/ folder an installed run fetched."""
    from pathlib import Path
    logs = Path(d) / "logs"
    main = (logs / "AirFrameXR.log").read_text(errors="replace")
    runs = [r.read_text(errors="replace") for r in sorted((logs / "games").glob("*/*.log"))]
    ok1, d1 = noise(main)
    print("noise  ", "PASS" if ok1 else "FAIL", d1)
    ok = ok1
    warns = sum(1 for l in main.splitlines() if " WARN " in l or " ERROR " in l)
    for i, r in enumerate(runs):
        ok2, d2 = routing(main, r)
        print(f"routing run {i}", "PASS" if ok2 else "FAIL", d2, f"({warns} WARN/ERROR lines in the main log)")
        ok = ok and ok2
    return 0 if ok and warns else 1


if __name__ == "__main__":
    import sys
    sys.exit(_dir(sys.argv[1]) if len(sys.argv) > 1 else (_self() or 0))
