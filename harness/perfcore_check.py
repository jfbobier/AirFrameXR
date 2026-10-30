#!/usr/bin/env python3
"""perfcore.dll (the Bench flavour's probe, tools/bench/PerfCore.cs, docs/TELEMETRY_PLAN.md) on the builder: builds it, runs perfprobe.exe for an L0 capture, an
L1 capture during which a watched settings file changes, and an L1 capture without process names, then checks the session folders:
files and headers, row counts for the rate, QPC times increasing, an empty field is never a 0 stand-in (capabilities say why a column is empty), the settings
change is a state event, the logger health has no write errors and its max tick is recorded, session.json is complete.

    python3 harness/perfcore_check.py [--no-build]
"""
import csv, json, subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

if "--no-build" not in sys.argv:
    r = subprocess.run([sys.executable, str(lib.ROOT / "harness" / "run.py"), "build", "bench"], capture_output=True, text=True)
    print(r.stdout.strip().splitlines()[-1] if r.stdout.strip() else "build: no output")
    if "build bench: ok" not in r.stdout:
        print("CHECK FAIL build"); sys.exit(2)

rid = f"perfcore_{time.strftime('%H%M%S')}"
subprocess.run(["bash", str(lib.BUILDER), "ps", str(lib.ROOT / "tools/remote/perfcore_check.ps1"), rid], capture_output=True)
loc = Path(tempfile.mkdtemp(prefix="perfcore_"))
for d in ("l0", "l1", "l1_nonames"):
    lib.b_get(f"runs/{rid}/{d}", loc / d)
lib.b_get(f"runs/{rid}/codes.txt", loc / "codes.txt")

fails = []
def check(ok, what, detail=""):
    print(("  [PASS] " if ok else "  [FAIL] ") + what + (f"  ({detail})" if detail else ""))
    if not ok: fails.append(what)

def rows(d, name):
    p = loc / d / name
    return list(csv.DictReader(open(p, newline=""))) if p.exists() else None

HEAD = {
    "hardware_gpu.csv": "sample_qpc,gpu_util_pct,memory_util_pct,core_clock_mhz,memory_clock_mhz,board_power_w,temp_c,vram_used_bytes,vram_total_bytes,pstate,throttle_reasons,nvenc_util_pct,nvdec_util_pct,nvenc_sessions,nvenc_avg_fps,nvenc_avg_latency_us,tick_us",
    "hardware_system.csv": "sample_qpc,cpu_total_pct,ram_available_bytes,page_faults_per_s,nic_bytes_per_s,disk_busy_pct,tick_us,disk_read_bytes_per_s,disk_write_bytes_per_s,disk_read_latency_ms,pages_input_per_s",
    "hardware_process.csv": "sample_qpc,process,cpu_cores,private_bytes,io_read_bytes_per_s,io_write_bytes_per_s,instances",
    "hardware_pcie.csv": "sample_qpc,pcie_tx_kbps,pcie_rx_kbps,call_ms",
    "hardware_engines.csv": "sample_qpc,pid,process,engine,util_pct,instances",
    "logger_health.csv": "sample_qpc,rows_gpu,rows_system,rows_pcie,rows_engines,write_errors,sample_errors,max_tick_ms_gpu,max_tick_ms_system,max_tick_ms_pcie,max_tick_ms_engines,probe_process_cpu_pct",
    "state_events.csv": "event_qpc,event_type,old_value,new_value,scope,config_hash,reason",
    "capabilities.csv": "provider,capability,supported,measurement_quality,reason,provider_version",
}
codes = (loc / "codes.txt").read_text(errors="replace") if (loc / "codes.txt").exists() else ""
print("== runs"); check("exit codes: 0 0 0" in codes.replace("\x00", ""), "all three captures exit 0", codes.strip())

for d, secs, hz, files in (("l0", 10, 1, ["hardware_gpu.csv", "hardware_system.csv", "logger_health.csv", "state_events.csv", "capabilities.csv"]),
                           ("l1", 12, 5, list(HEAD))):
    print(f"== {d} ({secs} s)")
    for f in files:
        p = loc / d / f
        first = p.read_text().splitlines()[0] if p.exists() and p.stat().st_size else None
        check(first == HEAD[f], f"{f} present with the documented header")
    check(not (loc / d / "hardware_pcie.csv").exists() if d == "l0" else True, "L0 has no PCIe / engine files (they cost: L1 only)")
    g = rows(d, "hardware_gpu.csv") or []
    expect = secs * hz
    check(0.6 * expect <= len(g) <= 1.1 * expect, f"hardware_gpu.csv rows for {hz} Hz", f"{len(g)} rows, expected about {expect}")
    t = [float(r["sample_qpc"]) for r in g]
    check(all(b > a for a, b in zip(t, t[1:])) and (not t or t[0] > 1), "QPC seconds increase")
    s = rows(d, "hardware_system.csv") or []
    check(0.25 * expect <= len(s) <= 1.1 * expect, "hardware_system.csv rows (the Windows counters take a few seconds to start)", f"{len(s)} rows")
    # a stand-in 0 for a reading that is not available would show as 0 in every row of a column the capabilities mark unsupported
    caps = {r["capability"]: r for r in rows(d, "capabilities.csv") or []}
    unsupported = [k for k, v in caps.items() if v["supported"] == "0"]
    check(True, "unsupported capabilities are listed with a reason", ", ".join(f"{k}: {caps[k]['reason']}" for k in unsupported) or "none unsupported on this GPU")
    for k in unsupported:
        check(bool(caps[k]["reason"]), f"capability {k} has a reason")
    h = rows(d, "logger_health.csv") or []
    check(len(h) >= secs - 3, "logger_health.csv has about one row per second", f"{len(h)} rows")
    check(all(r["write_errors"] == "0" for r in h), "no write errors")
    check(all(r["max_tick_ms_gpu"] != "" for r in h), "the max tick of the NVML thread is recorded (a stall shows here)")
    sj = json.load(open(loc / d / "session.json"))
    check(sj.get("complete") is True and sj.get("schema_version") == 1 and sj.get("gpu"), "session.json complete with schema_version and GPU", sj.get("gpu", ""))

print("== L1 specifics")
e = rows("l1", "hardware_engines.csv") or []
check(len(e) > 0 and {r["process"] for r in e} <= {"explorer", "dwm"}, "engine rows only for the named processes", f"{len(e)} rows, processes {sorted({r['process'] for r in e})}")
pr = rows("l1", "hardware_process.csv") or []
check(len(pr) > 0 and {r["process"] for r in pr} <= {"explorer", "dwm"} and all(float(r["private_bytes"]) > 0 for r in pr), "process rows (CPU in cores, private memory, file I/O) only for the named processes", f"{len(pr)} rows, processes {sorted({r['process'] for r in pr})}")
sysr = rows("l1", "hardware_system.csv") or []
check(any(r["disk_read_latency_ms"] != "" for r in sysr) and any(r["pages_input_per_s"] != "" for r in sysr), "the disk and hard-fault columns are filled")
p = rows("l1", "hardware_pcie.csv") or []
check(len(p) >= 8, "PCIe sampled on its own thread at about 1 Hz", f"{len(p)} rows")
ev = rows("l1", "state_events.csv") or []
changed = [r for r in ev if r["scope"] == "airframexr.json" and r["old_value"] and r["old_value"] != r["new_value"]]
check(len(ev) >= 2 and len(changed) == 1, "a change of the watched settings file during the capture is one state event", f"{len(ev)} events, {len(changed)} change(s)")
check(not any(r["scope"] == "session.json" and r["old_value"] for r in ev), "the unchanged settings file produced no change event")
print("== L1 without process names")
nn = rows("l1_nonames", "capabilities.csv") or []
check(any(r["capability"] == "gpu_engine_per_process" and r["supported"] == "0" and r["reason"] for r in nn), "no names: engine counters reported unsupported with a reason, capture still runs")
check(len(rows("l1_nonames", "hardware_gpu.csv") or []) > 10, "no names: the other sources are unaffected")

print("CHECK", "PASS" if not fails else "FAIL", "perfcore probe", ("- " + ", ".join(fails)) if fails else "")
sys.exit(0 if not fails else 1)
