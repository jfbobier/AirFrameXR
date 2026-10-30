#!/usr/bin/env python3
"""perfreport (tools/bench/PerfReport.cs, docs/TELEMETRY_PLAN.md) against fixture sessions with KNOWN faults and known non-faults: the report must name the fault, and
stay quiet when there is none (a diagnosis that fires on a clean run is as wrong as one that misses). Fixtures are generated here (stdlib, seeded: the same every run),
staged on the builder, run through perfreport.exe twice each (the report must be byte-identical).

    python3 harness/perfreport_check.py [--no-build]
"""
import random, re, subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

P = 1 / 90.0
T0 = 10000.0
HOST_HEAD = "frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms,t_send,hold_ms,bytes,idr"
GPU_HEAD = "sample_qpc,gpu_util_pct,memory_util_pct,core_clock_mhz,memory_clock_mhz,board_power_w,temp_c,vram_used_bytes,vram_total_bytes,pstate,throttle_reasons,nvenc_util_pct,nvdec_util_pct,nvenc_sessions,nvenc_avg_fps,nvenc_avg_latency_us,tick_us"
HEALTH_HEAD = "sample_qpc,rows_gpu,rows_system,rows_pcie,rows_engines,write_errors,sample_errors,max_tick_ms_gpu,max_tick_ms_system,max_tick_ms_pcie,max_tick_ms_engines,probe_process_cpu_pct"
SESSION = '{\n  "schema_version": 1,\n  "started_qpc_s": %.6f,\n  "complete": true\n}\n' % T0


def host(seconds, late_every=0, late_ms=9.0, kind="encode", slow_enc_ms=14.0, late_from=0.0, late_to=1e9):
    rows = [HOST_HEAD]
    n = int(seconds / P)
    rnd = random.Random(7)
    for k in range(n):
        b = T0 + k * P
        arr = b - 0.003
        enc = 3.0 + rnd.random() * 0.4
        delay = 0.0
        if late_every and k > 100 and k % late_every == 0 and late_from <= k * P <= late_to:
            delay = late_ms / 1000.0
            if kind == "encode": enc = slow_enc_ms
            if kind == "upstream": arr += delay
        es = arr + 0.0005
        send = es + enc / 1000.0 + 0.002 + (delay if kind == "after" else 0.0) + (0.0 if kind in ("encode", "upstream") else 0.0)
        if kind == "encode" and delay: send = es + enc / 1000.0 + 0.002
        if kind == "upstream" and delay: send = es + enc / 1000.0 + 0.002
        rows.append(f"{k},{int(b*1e9)},{k},{b:.6f},{arr:.6f},{es:.6f},{enc:.3f},{send:.6f},2.000,40000,0")
    return "\n".join(rows) + "\n"


def host_fg(seconds, late_every=45):
    """frame generation at half rate: every output frame has a perfect host boundary; the arrival of a re-stamped (-3) or generated (-2) frame is that of its SOURCE frame (a game frame every 2 periods)"""
    rows = [HOST_HEAD]
    rnd = random.Random(11)
    for k in range(int(seconds / P)):
        b = T0 + k * P
        src = T0 + (k - k % 2) * P - 0.027          # the source game frame's arrival
        slot = -3 if k % 2 == 0 else -2
        enc = 8.0 + rnd.random() * 0.3     # both kinds alike: the point is the arrival column, not the encode cost of a kind
        if late_every and k > 100 and k % late_every == 0: enc = 19.0
        es = b + 0.0021
        send = es + enc / 1000.0 + 0.0001
        rows.append(f"{k},{int(b*1e9)},{slot},{b:.6f},{src:.6f},{es:.6f},{enc:.3f},{send:.6f},0.000,200000,0")
    return "\n".join(rows) + "\n"


def gpu(seconds, throttle_share=0.0, hz=5, thr_const=None):
    rows = [GPU_HEAD]
    rnd = random.Random(3)
    for i in range(int(seconds * hz)):
        t = T0 + i / hz
        thr = thr_const if thr_const else ("0x24" if rnd.random() < throttle_share else "0x1")
        clk = 1500 if thr != "0x1" else 2400
        rows.append(f"{t:.6f},55,20,{clk},7001,90.0,60,8000000000,17179869184,P0,{thr},20,0,1,90,3000,200")
    return "\n".join(rows) + "\n"


def health(seconds, werr=0, nvml_ms=3.0):
    rows = [HEALTH_HEAD]
    for i in range(int(seconds)):
        rows.append(f"{T0+i+1:.6f},{i*5},{i*5},{i},{i},{werr},0,{nvml_ms},2.0,60.0,10.0,1.2")
    return "\n".join(rows) + "\n"


def session(name, hostcsv, gpucsv=None, healthcsv=None, torn=False, ended=None):
    d = {"session.json": SESSION if ended is None else SESSION.replace('"complete": true', '"ended_qpc_s": %.6f,\n  "complete": true' % (T0 + ended))}
    if hostcsv is not None: d["host_frames.csv"] = hostcsv + ("90,12,0,0.0" if torn else "")
    if gpucsv is not None: d["hardware_gpu.csv"] = gpucsv
    if healthcsv is not None: d["logger_health.csv"] = healthcsv
    return name, d


FIX = [
    session("clean", host(40), gpu(40), health(40)),
    session("slow_encode", host(40, 45, kind="encode"), gpu(40), health(40)),
    session("upstream_late", host(40, 45, kind="upstream"), gpu(40), health(40)),
    session("after_send_late", host(40, 45, kind="after"), gpu(40), health(40)),
    session("no_host", None, gpu(40), health(40)),
    session("gpu_limited", host(40), gpu(40, throttle_share=0.5), health(40)),
    session("recorder_lost", host(40), gpu(40), health(40, werr=3)),
    session("nvml_stall", host(40), gpu(40), health(40, nvml_ms=1250.0)),
    session("torn_last_line", host(40), gpu(40), health(40), torn=True),
    session("cut_window", host(60), gpu(60), health(60), ended=25),
    session("fg_encode_jitter", host_fg(40), gpu(40), health(40)),
    session("gpu_unknown_bit", host(40), gpu(40, thr_const="0x400"), health(40)),
    session("hotspot", host(60, 4, kind="encode", late_from=30, late_to=42), gpu(60), health(60)),
]

# name -> [(finding id or "CAT:<category>", expected status)], verdict status
G, C_, M, N_, V = "CAT:GPU", "CAT:CPU", "CAT:Memory and I/O", "CAT:Network", "CAT:VR rendering"
NODATA = [(C_, "INSUFFICIENT"), (M, "INSUFFICIENT"), (N_, "INSUFFICIENT")]    # the fixtures carry no system, process or headset files
EXPECT = {
    "clean": ([("T02", "OK"), ("T10", "OK"), (G, "OK"), (V, "OK")] + NODATA, "OK"),
    "slow_encode": ([("T02", "PROBLEM"), ("T03", "PROBLEM"), (G, "PROBLEM"), (V, "PROBLEM")], "PROBLEM"),
    "upstream_late": ([("T02", "PROBLEM"), ("T03", "PROBLEM"), (G, "OK"), (V, "PROBLEM")], "PROBLEM"),
    "after_send_late": ([("T02", "PROBLEM"), ("T03", "PROBLEM"), (G, "OK"), (V, "PROBLEM")], "PROBLEM"),
    "no_host": ([("T01", "INSUFFICIENT"), (V, "INSUFFICIENT")], "INSUFFICIENT"),
    "gpu_limited": ([("T02", "OK"), (G, "WATCH"), (V, "OK")], "WATCH"),
    "recorder_lost": ([("T10", "PROBLEM")], "PROBLEM"),
    "nvml_stall": ([("T10", "WATCH")], "WATCH"),
    "torn_last_line": ([("T01", "OK"), ("T02", "OK")], "OK"),
    "cut_window": ([("T01", "OK"), ("T02", "OK")], "OK"),
    "fg_encode_jitter": ([("T02", "PROBLEM"), ("T03", "PROBLEM"), (G, "PROBLEM")], "PROBLEM"),
    "gpu_unknown_bit": ([(G, "OK")], "OK"),
    "hotspot": ([("T02", "PROBLEM")], "PROBLEM"),
}
VERDICT_HAS = {"slow_encode": "GPU:", "fg_encode_jitter": "GPU:", "upstream_late": "No resource stands out", "after_send_late": "No resource stands out", "recorder_lost": "lost data", "gpu_limited": "Closest to a limit: GPU"}
CLASS = {"slow_encode": "Encode took long", "upstream_late": "Upstream", "after_send_late": "After the encode", "fg_encode_jitter": "Encode took long"}

if "--no-build" not in sys.argv:
    r = subprocess.run([sys.executable, str(lib.ROOT / "harness" / "run.py"), "build", "bench"], capture_output=True, text=True)
    if "build bench: ok" not in r.stdout:
        print(r.stdout[-1500:]); print("CHECK FAIL build"); sys.exit(2)

rid = f"perfreport_{time.strftime('%H%M%S')}"
loc = Path(tempfile.mkdtemp(prefix="perfreport_"))
for name, files in FIX:
    d = loc / "fix" / name
    d.mkdir(parents=True)
    for fn, text in files.items():
        (d / fn).write_text(text, newline="\n")
lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
lib.b_put(loc / "fix", f"runs/{rid}/")
subprocess.run(["bash", str(lib.BUILDER), "ps", str(lib.ROOT / "tools/remote/perfreport_check.ps1"), rid], capture_output=True)
lib.b_get(f"runs/{rid}/out.txt", loc / "out.txt")
for name, _ in FIX:
    lib.b_get(f"runs/{rid}/fix/{name}/report.md", loc / f"{name}.md")
raw = (loc / "out.txt").read_bytes()
text = (raw.decode("utf-16") if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else raw.decode("utf-8", "replace")).replace("\r", "").replace("\x00", "")
blocks = {}
cur = None
for line in text.splitlines():
    if line.startswith("=== "): cur = line[4:].strip(); blocks[cur] = []
    elif cur: blocks[cur].append(line)

fails = []
def check(ok, what, detail=""):
    print(("  [PASS] " if ok else "  [FAIL] ") + what + (f"  ({detail})" if detail else ""))
    if not ok: fails.append(what)

for name, _ in FIX:
    print("==", name)
    b = blocks.get(name, [])
    if not b: check(False, "report produced"); continue
    verdict = b[0].split(":")[0].strip()
    found = {}
    for l in b[1:]:
        m = re.match(r"\s+(T\d+) (\w+) ", l)
        if m: found[m.group(1)] = m.group(2)
        m = re.match(r"\s+CAT (.+?) \| (\w+) \|", l)
        if m: found["CAT:" + m.group(1)] = m.group(2)
    want, vwant = EXPECT[name]
    check(verdict == vwant, f"verdict {vwant}", b[0][:130])
    if name in VERDICT_HAS: check(VERDICT_HAS[name] in b[0], f"the verdict says: {VERDICT_HAS[name]}", b[0][:130])
    for fid, st in want:
        check(found.get(fid) == st, f"{fid} {st}", f"got {found.get(fid)}")
    if name in CLASS:
        md = (loc / f"{name}.md").read_text(errors="replace")
        t03 = [l for l in md.splitlines() if "T03" in l or "Late gaps: mostly" in l or "Each late gap" in l]
        check(CLASS[name].lower() in " ".join(t03).lower(), f"the late frames are attributed to: {CLASS[name]}", (t03[0][:100] if t03 else ""))
    check(any(l.strip() == "deterministic: True" for l in b), "report is byte-identical on a second run")
    md = (loc / f"{name}.md").read_text(errors="replace") if (loc / f"{name}.md").exists() else ""
    if name == "cut_window":
        m = re.search(r"(\d+) sent frames in ([\d.]+) s", md)
        check(bool(m) and abs(float(m.group(2)) - 20.0) < 1.0, "the report window ends where the recording ended (frames after it are not counted)", m.group(0) if m else "no T01 line")
    if name == "hotspot":
        hs = [l for l in md.splitlines() if " in: " in l and "late" in l]
        check(len(hs) >= 1 and any(l.startswith(("- 0:00:2", "- 0:00:3", "- 0:00:4")) for l in hs), "the worst 10 s windows point at the bad stretch (about 30-42 s)", hs[0] if hs else "no hotspot lines")
    if name == "fg_encode_jitter":
        check("mostly upstream" not in md.lower().split("t03")[1].split("\n")[0] if "t03" in md.lower() else False, "frame generation: source-frame arrival gaps are not read as the game being late")
    if name == "gpu_unknown_bit":
        check("not a documented limiter" in md, "an unknown clock-event bit is named and not counted as a limiter")
    if name == "upstream_late":
        check("game is GPU-bound" not in md, "no GPU-bound advice when the GPU was not busy (fixture GPU util is 55 %)")

print("CHECK", "PASS" if not fails else "FAIL", "perfreport fixtures", ("- " + ", ".join(fails)) if fails else "")
sys.exit(0 if not fails else 1)
