#!/usr/bin/env python3
"""Windows Firewall review (Firewall.Analyze in tools/gui/Common.cs) against real rules on the builder: throwaway rules named VT-<stamp>-* are created for a
dummy alvr_host.exe, the analysis code runs on them, and the rules are removed afterwards. Scenarios: no rule, one good rule, duplicate / redundant / stale /
another-copy rules, a blocking rule, a rule for a network profile that is not the current one. Run python3 harness/ui_matrix.py first (it builds uitest.exe).

    python3 harness/firewall_check.py
"""
import sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

stamp = time.strftime("%H%M%S")
P = f"VT-{stamp}-"
rc, home = lib.b_run("echo %USERPROFILE%")
home = home.strip().splitlines()[-1].strip()
base = f"{home}\\{lib.ROOTNAME}"
d = f"{home}\\fwtest_{stamp}"
exe, other, gone = f"{d}\\alvr_host.exe", f"{d}\\other\\alvr_host.exe", f"{home}\\fwtest_gone_{stamp}\\bin\\alvr_host.exe"
fails = 0


def ps(script):
    f = Path(tempfile.gettempdir()) / f"fw_{stamp}.ps1"
    f.write_text(script)
    p = lib.sh(["bash", str(lib.BUILDER), "ps", str(f)])
    return p.stdout


def rule(n, program, action="Allow", proto="Any", profile="Any", enabled="True"):
    return f"New-NetFirewallRule -Name '{P}{n}' -DisplayName 'VT {n}' -Direction Inbound -Action {action} -Program '{program}' -Protocol {proto} -Profile {profile} -Enabled {enabled} | Out-Null\n"


def analyse():
    rid = f"fw_{stamp}_{time.time_ns() % 100000}"
    lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
    lib.b_run(f"_tmp\\uitest\\run\\uitest.exe --firewall \"{exe}\" --out runs\\{rid}")
    loc = Path(tempfile.mkdtemp(prefix="fw_"))
    lib.b_get(f"runs/{rid}/firewall.txt", loc / "firewall.txt")
    lines = (loc / "firewall.txt").read_text(errors="replace").splitlines() if (loc / "firewall.txt").exists() else []
    state = next((l.split("=")[1] for l in lines if l.startswith("state=")), "?")
    rules = {l.split("|")[0][len(P):]: l.split("|") for l in lines if l.startswith(P)}
    return state, rules


def check(name, ok, detail=""):
    global fails
    fails += 0 if ok else 1
    print(f"CHECK {'PASS' if ok else 'FAIL'} {name}: {detail}")


ps(f"New-Item -ItemType Directory -Force '{d}\\other' | Out-Null; Set-Content '{exe}' 'x'; Set-Content '{other}' 'x'\n")
try:
    # which network profile is NOT in use: a rule for it does not let anything in
    active = ps("(Get-NetConnectionProfile | ForEach-Object { [string]$_.NetworkCategory }) -join ','").strip()
    idle = "Domain" if "Domain" not in active else "Public"
    st, _ = analyse()
    check("no rule at all", st == "Missing", st)
    ps(rule("1", exe))
    st, r = analyse()
    check("one good rule", st == "Allowed" and "INPLACE" in r["1"], f"{st} {r.get('1')}")
    ps(rule("2", exe, proto="TCP", profile="Public") + rule("3", exe, proto="UDP", profile="Public") + rule("4", gone, proto="UDP") + rule("5", other))
    st, r = analyse()
    check("redundant protocol rules are proposed", st == "Allowed" and "PROPOSE" in r["2"] and "PROPOSE" in r["3"], f"{r['2'][-2:]} {r['3'][-2:]}")
    check("a rule for a file that is gone is proposed as stale", "PROPOSE" in r["4"] and "stale" in r["4"][-1], r["4"][-1])
    check("another copy that still exists is left alone", "PROPOSE" not in r["5"], r["5"][-1])
    check("the rule that counts is not proposed", "PROPOSE" not in r["1"], r["1"][-1])
    ps(rule("6", exe, action="Block"))
    st, r = analyse()
    check("a blocking rule: Blocked, and it is proposed", st == "Blocked" and "PROPOSE" in r["6"], f"{st} {r['6'][-1]}")
    ps(f"Remove-NetFirewallRule -Name '{P}*'\n" + rule("7", exe, profile=idle))
    st, r = analyse()
    check(f"a rule only for the {idle} network (not in use: {active}) does not count", st == "Missing", f"{st} {r['7'][-1]}")
    ps(f"Remove-NetFirewallRule -Name '{P}*'\n" + rule("8", exe, enabled="False"))
    st, r = analyse()
    check("a switched-off rule does not count", st == "Missing", f"{st} {r['8'][-1]}")
    # tools/check_firewall_rules.bat: the same judgement, answered Y by the test hook, then only the in-place rule and the other copy are left
    exe_bin = f"{d}\\bin\\alvr_host.exe"   # the one the .bat finds next to itself
    ps(f"New-Item -ItemType Directory -Force '{d}\\bin' | Out-Null; Copy-Item '{exe}' '{exe_bin}' -Force\n")
    ps(f"Remove-NetFirewallRule -Name '{P}*'\n" + rule("1", exe_bin) + rule("2", exe_bin, proto="TCP", profile="Public") + rule("3", exe_bin, proto="UDP", profile="Public") + rule("4", gone, proto="UDP") + rule("5", other) + rule("6", exe_bin, action="Block", profile="Public"))
    lib.b_put(lib.ROOT / "tools/check_firewall_rules.bat", f"runs/fwbat_{stamp}.bat")
    ps(f"Copy-Item '{base}\\runs\\fwbat_{stamp}.bat' '{d}\\check_firewall_rules.bat' -Force\n")
    out = lib.b_run(f'cmd /c "set FWCHECK_ANSWERS=Y& \"{d}\\check_firewall_rules.bat\" < nul"', timeout=120)[1]
    # the bat looked at <d>\bin\alvr_host.exe (a copy of the dummy exe): rules were made for <d>\alvr_host.exe, so this is the "not inside a package" path only if they differ
    left = ps(f"(Get-NetFirewallRule -Name '{P}*' | Sort-Object Name | ForEach-Object {{ $_.Name.Substring({len(P)}) }}) -join ','").strip()
    check("the .bat reports and removes the proposed rules", "proposed for removal" in out and "removed:" in out, out.strip().splitlines()[-3] if out.strip() else "no output")
    check("the .bat leaves only the rule that counts and the other copy", left == "1,5", f"left: {left}")
finally:
    ps(f"Remove-NetFirewallRule -Name '{P}*' -ErrorAction SilentlyContinue\n")
    left = ps(f"(Get-NetFirewallRule -Name '{P}*' -ErrorAction SilentlyContinue | Measure-Object).Count").strip()
    check("test rules removed", left in ("0", ""), f"{left} left")
print("firewall_check:", "OK" if fails == 0 else f"{fails} FAILED")
sys.exit(1 if fails else 0)
