#!/usr/bin/env python3
"""Stability harness runner (docs/STABILITY_PLAN.md): fault-injection and soak scenarios on the real chain (host + shim + probe + mock client on the builder),
judged by judge.py (stalls, tracking resumption, memory growth).

    python3 harness/stability/run_stability.py list
    python3 harness/stability/run_stability.py run <name>[,<name>...|all] [--seconds N] [--host-env K=V ...]   # --seconds stretches a scenario (host/client/app), e.g. for a soak;
                                                                                                          # --host-env sets host variables (AIRFRAMEXR_FG_DARK_MS=0 turns the dark-tracking fix off)
    python3 harness/stability/run_stability.py judge <results dir> <scenario>              # re-judge an existing run

A scenario is scenarios.json: a base (derived from a regular harness scenario, `base_from`) + an override (deep merge; null removes a key) + criteria
for judge.py. Results: harness/results/stability/<UTC>_<name>/ (frames.csv, client_stdout.txt with the injected faults, mem.csv, host_stdout.txt, result.json).
Needs the builder (tools/builder.local) with the host, shim, probe and mock client built; the debug flavour for the full instrumentation."""
import copy, json, sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
import judge as J

SPEC = json.load(open(HERE / "scenarios.json"))


def merge(a, b):
    """Deep merge of b into a copy of a; None in b removes the key."""
    r = copy.deepcopy(a)
    for k, v in b.items():
        if v is None:
            r.pop(k, None)
        elif isinstance(v, dict) and isinstance(r.get(k), dict):
            r[k] = merge(r[k], v)
        else:
            r[k] = copy.deepcopy(v)
    return r


def build(name, seconds=None, host_env=None):
    import lib
    spec = SPEC["scenarios"][name]
    base = json.load(open(lib.ROOT / "harness" / "scenarios" / f"{SPEC['base_from']}.json"))
    base.pop("checks", None)
    sc = merge(merge(base, SPEC["base"]), spec.get("override", {}))
    if seconds:
        sc["host"]["seconds"] = seconds + 10
        sc["client"]["seconds"] = seconds
        sc["app"]["timeout_s"] = seconds - 10
        sc["timeout_s"] = seconds + 160
    if spec.get("faults"):
        sc["faults"] = spec["faults"]
    soak_plan = None
    if spec.get("soak"):
        # a seeded schedule of plausible events (soak.py): host commands through the stdin feed, tracking blackouts, process freezes
        import soak
        total = seconds or spec["soak"].get("seconds", 900)
        soak_plan = soak.plan(total, spec["soak"].get("seed", 7))
        soak_plan["seconds"] = total
        sc["stdin_feed"] = {"events": soak_plan["stdin"], "hold_s": total + 20}
        sc["client"]["track_blackout"] = soak_plan["client_blackouts"]
        sc["faults"] = soak_plan["faults"]
        if spec["soak"].get("audio") and "--no-audio" not in sys.argv:
            sc["audio_events"] = soak_plan["audio"]
        sc["host"]["seconds"] = total + 15
        sc["client"]["seconds"] = total
        sc["app"]["timeout_s"] = total - 5
        sc["timeout_s"] = total + 200
    if host_env:
        sc["host"].setdefault("env", {}).update(host_env)
    sc["name"] = name
    sc["description"] = spec["description"]
    sc["_soak_plan"] = soak_plan
    return sc, spec["criteria"]


def never_paired(outdir):
    """The mock client never reached a stream (it only searched for the streamer): the lab's pairing flake, not a verdict on the product."""
    p = Path(outdir) / "client_stdout.txt"
    return p.exists() and "streaming_started" not in p.read_text(errors="replace")


def run(name, seconds=None, host_env=None, tries=2):
    import lib
    for attempt in range(tries):
        status, outdir = run_once(name, seconds, host_env)
        if status != "FAIL" or attempt == tries - 1 or "reconnect_storm" in SPEC["scenarios"][name].get("override", {}) or not never_paired(outdir):
            return status
        print(f"   the client never paired with the host (lab flake): running {name} again")
    return status


def run_once(name, seconds=None, host_env=None):
    import lib
    sc, crit = build(name, seconds, host_env)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    outdir = lib.RESULTS / "stability" / f"{stamp}_{name}"
    print(f"== {name}: {sc['description']}")
    plan = sc.pop("_soak_plan", None)
    result = {"scenario": name, "started": stamp, "outdir": str(outdir)}
    try:
        steps = lib.run_scenario(sc, outdir, log=print)
        if plan:
            (outdir / "soak_plan.json").write_text(json.dumps(plan, indent=1))
        checks = J.judge(outdir, crit)
        result.update(status="PASS" if checks and all(c["ok"] for c in checks) else "FAIL", steps=steps, checks=checks)
    except lib.Blocked as e:
        outdir.mkdir(parents=True, exist_ok=True)
        result.update(status="BLOCKED", reason=str(e), checks=[])
    (outdir / "result.json").write_text(json.dumps(result, indent=1))
    for c in result["checks"]:
        print(f"    [{'PASS' if c['ok'] else 'FAIL'}] {c['name']}: {c['value']}  (expected {c['expected']})")
    print(f"   -> {result['status']}" + (f" ({result['reason']})" if result.get("reason") else "") + f"   [{outdir}]")
    return result["status"], outdir


def main(argv):
    if len(argv) < 2 or argv[1] == "list":
        for n, s in SPEC["scenarios"].items():
            print(f"{n:32} {s['description'][:150]}")
        return 0
    if argv[1] == "judge":
        crit = SPEC["scenarios"][argv[3]]["criteria"]
        checks = J.judge(argv[2], crit)
        for c in checks:
            print(f"    [{'PASS' if c['ok'] else 'FAIL'}] {c['name']}: {c['value']}  (expected {c['expected']})")
        return 0 if all(c["ok"] for c in checks) else 1
    if argv[1] == "run":
        seconds = int(argv[argv.index("--seconds") + 1]) if "--seconds" in argv else None
        names = list(SPEC["scenarios"]) if argv[2] == "all" else argv[2].split(",")
        host_env = dict(a.split("=", 1) for a in argv[argv.index("--host-env") + 1:] if "=" in a) if "--host-env" in argv else None   # e.g. --host-env AIRFRAMEXR_FG_DARK_MS=0
        st = [run(n, seconds, host_env) for n in names]
        print("\n" + "\n".join(f"{s:8} {n}" for s, n in zip(st, names)))
        return 0 if all(s == "PASS" for s in st) else 1
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
