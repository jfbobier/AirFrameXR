#!/usr/bin/env python3
"""The host's unit tests (cargo test -p alvr_host --bins) on the builder: syncs the sources like `run.py build host` (which also builds), then runs the tests.
U7's aggregator tests live in logs.rs (client_stats::tests) and main.rs (net_summary_tests). Exit 0 = all pass."""
import subprocess, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

rc = subprocess.run([sys.executable, str(Path(__file__).parent / "run.py"), "build", "host"], capture_output=True, text=True)
print(rc.stdout.strip().splitlines()[-1] if rc.stdout.strip() else rc.stderr[-300:])
if rc.returncode:
    sys.exit(2)
r, out = lib.b_run('cd src\\ALVR-v20.14.1 && set PATH=%PATH%;%USERPROFILE%\\.cargo\\bin && cargo test --release -p alvr_host --bins 2>&1', timeout=900)
lines = [l for l in out.splitlines() if l.startswith("test ") or l.startswith("test result") or l.startswith("error")]
print("\n".join(lines))
sys.exit(0 if any("test result: ok" in l for l in lines) and not any(" FAILED" in l or l.startswith("error") for l in lines) else 1)
