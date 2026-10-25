#!/usr/bin/env python3
"""The launcher (bin\\launcher.exe; the root AirFrameXR.exe is a stub that starts it, tested last) steps aside while a tool is open and returns when it is closed (docs/PLAN_0.5.md). For each tool: a copy of the package on the builder,
AirFrameXR.exe --test-flow "<tool>" launches the tool exactly as its Launch button does (the tool closes itself after its own screenshot), and the
launcher samples its state to a log. Checks, per tool:
  - the launcher is visible before the launch
  - it hides (no visible sample while a tool process runs) and a tool really ran meanwhile
  - it is visible again once every tool has closed
Run python3 harness/run.py build gui package first.
"""
import subprocess, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

TOOLS = ["Game statistics", "Diagnostics (experimental)", "Restart headset setup", "Streamer"]
only = sys.argv[1:] or TOOLS
fails = 0
stamp = time.strftime("%Y%m%d_%H%M%S")
rc, home = lib.b_run("echo %USERPROFILE%")
base = f"{home.strip().splitlines()[-1].strip()}\\{lib.ROOTNAME}"
for i, tool in enumerate(TOOLS):
    if tool not in only:
        continue
    rid = f"flow_{stamp}_{i}"
    lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
    rc, out = lib.b_run(f"robocopy out\\dist\\AirFrameXR runs\\{rid}\\AirFrameXR /E /NFL /NDL /NJH /NJS /NP")
    log = f"{base}\\runs\\{rid}\\flow.log"
    body = f"runs\\{rid}\\AirFrameXR\\bin\\launcher.exe --test-flow \"{tool}\"\r\n"
    proc = lib.start_limited(rid, f"va_flow{i}", "flow.bat", body, "rc.txt", env={"AIRFRAMEXR_TEST_FLOW_LOG": log})
    try:
        proc.wait(120)
    except subprocess.TimeoutExpired:
        print(f"CHECK FAIL {tool}: the launcher did not finish in 120 s")
        fails += 1
        continue
    dst = Path(tempfile.gettempdir()) / f"{rid}.log"
    dst.unlink(missing_ok=True)
    lib.b_get(f"runs/{rid}/flow.log", dst)
    lines = dst.read_text(errors="replace").splitlines() if dst.exists() else []
    def vis(l): return "visible=1" in l
    def tools(l): return int(l.split("tools=")[1].split()[0])
    launch_i = next((k for k, l in enumerate(lines) if "hide: launched" in l), None)
    show_i = next((k for k, l in enumerate(lines) if "show: every tool closed" in l), None)
    before = [l for l in lines if "before launch" in l]
    ok_before = bool(before) and vis(before[0]) and tools(before[0]) == 0
    during = lines[launch_i + 1:show_i] if launch_i is not None and show_i is not None else []
    hidden_ok = launch_i is not None and show_i is not None and any(not vis(l) and tools(l) >= 1 for l in during) and not any(vis(l) and tools(l) >= 1 for l in during)
    tool_ran = any(tools(l) >= 1 for l in during)
    back_ok = show_i is not None and vis(lines[show_i]) and tools(lines[show_i]) == 0 and any(vis(l) and tools(l) == 0 for l in lines[show_i:])
    for name, ok, detail in [("visible before the launch", ok_before, before[0] if before else "no line"),
                             ("hidden while the tool is open", hidden_ok, f"{len(during)} samples, {sum(1 for l in during if not vis(l))} hidden"),
                             ("the tool really ran", tool_ran, ""),
                             ("back when every tool is closed", back_ok, lines[show_i] if show_i is not None else "never shown again")]:
        print(f"CHECK {'PASS' if ok else 'FAIL'} {tool}: {name}: {detail}")
        fails += 0 if ok else 1
# the stub in the root: AirFrameXR.exe starts bin\launcher.exe, passes the arguments on and exits at once; the whole flow then runs from it
if not only or "Game statistics" in only:
    rid = f"flow_{stamp}_stub"
    lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
    lib.b_run(f"robocopy out\\dist\\AirFrameXR runs\\{rid}\\AirFrameXR /E /NFL /NDL /NJH /NJS /NP")
    log = f"{base}\\runs\\{rid}\\flow.log"
    root_files = lib.b_run(f"dir /b runs\\{rid}\\AirFrameXR")[1].split()
    proc = lib.start_limited(rid, "va_flowstub", "flow.bat", f"runs\\{rid}\\AirFrameXR\\AirFrameXR.exe --test-flow \"Game statistics\"\r\n", "rc.txt", env={"AIRFRAMEXR_TEST_FLOW_LOG": log})
    proc.wait(30)   # the stub returns at once
    ok = False
    for _ in range(25):
        time.sleep(2)
        txt = lib.b_run(f"if exist runs\\{rid}\\flow.log (type runs\\{rid}\\flow.log)")[1]
        if "show: every tool closed" in txt:
            ok = True
            break
    print(f"CHECK {'PASS' if ok else 'FAIL'} root stub: AirFrameXR.exe started bin\\launcher.exe with its arguments and the round trip ran from it")
    fails += 0 if ok else 1
    print(f"CHECK {'PASS' if sorted(x.lower() for x in root_files if x.lower().endswith('.exe') or '.' in x) == ['airframexr.exe'] else 'FAIL'} root stub: the root holds only AirFrameXR.exe ({', '.join(root_files)})")
print("launcher_flow:", "OK" if fails == 0 else f"{fails} FAILED")
sys.exit(1 if fails else 0)
