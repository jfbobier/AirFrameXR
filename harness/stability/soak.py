"""Soak schedule generator (docs/STABILITY_PLAN.md, scenario `soak_tokyo`): a seeded, replayable list of plausible events for a long run in Little Tokyo with
frame generation and motion vectors on.

    soak.plan(seconds, seed) -> {"stdin": [{"t", "line"}], "client_blackouts": "start:dur[:frame],...", "mv_fault": "...", "faults": [{"type": "suspend", ...}], "windows": [...]}

What a player does: re-centres, changes the eye height, nudges gamma, toggles frame generation and changes its rate, turns the game's vectors off and on, flips debug
logging; what the network and the machine do: the headset's tracking goes quiet for a moment, a vector provider drops frames or re-creates its feature, the host or the
headset freezes for seconds. Rates are per hour, with a floor so that a 10-minute run sees every kind at least once. `windows` lists the injected disturbances
(client clock seconds, approximate) so the judge can tell an explained stall from an unexplained one."""
import random


def _times(rng, seconds, per_hour, first=45, last_margin=40, floor=1):
    n = max(floor, round(per_hour * seconds / 3600.0))
    lo, hi = first, max(first + 1, seconds - last_margin)
    return sorted(round(rng.uniform(lo, hi), 1) for _ in range(n))


def plan(seconds, seed=7):
    rng = random.Random(seed)
    stdin, windows = [], []
    for t in _times(rng, seconds, 20):
        stdin.append({"t": t, "line": "recenter"})
    for t in _times(rng, seconds, 12):
        stdin.append({"t": t, "line": f"height {rng.choice([1.45, 1.55, 1.65, 1.75, 1.85])}"})
    for t in _times(rng, seconds, 10):
        stdin.append({"t": t, "line": f"gamma {rng.choice([1.0, 1.2, 1.4, 1.6])}"})
    for t in _times(rng, seconds, 6):   # generation off for a while, then on again
        back = round(t + rng.uniform(5, 40), 1)
        stdin += [{"t": t, "line": "frame_gen off"}, {"t": back, "line": "frame_gen on"}]
    for t in _times(rng, seconds, 5):
        stdin.append({"t": t, "line": f"frame_gen_rate {rng.choice(['half', 'two_thirds', 'third', 'uncapped'])}"})
    for t in _times(rng, seconds, 5):   # the game's vectors off, then on again
        stdin += [{"t": t, "line": "game_mv off"}, {"t": round(t + rng.uniform(5, 30), 1), "line": "game_mv on"}]
    for t in _times(rng, seconds, 3):
        stdin += [{"t": t, "line": "debug on"}, {"t": round(t + rng.uniform(10, 60), 1), "line": "debug off"}]
    stdin.sort(key=lambda e: e["t"])
    # the headset's tracking goes quiet (the client's own clock, from the stream start)
    bo = []
    for t in _times(rng, seconds, 12, first=60):
        dur = round(rng.choice([0.5, 1, 2, 5, 8]), 1)
        bo.append(f"{t}:{dur}" + (":frame" if rng.random() < 0.6 else ""))
        windows.append({"kind": "tracking_blackout", "t": t, "dur": dur})
    # the vector provider: a little loss all the time, blackouts, feature re-creation, texture re-creation
    mvf = ["drop:0.02", "churn:97", "recreate:41", "resize:53"]
    for t in _times(rng, seconds, 8, first=60):
        mvf.append(f"blackout:{t}:{round(rng.choice([3, 6, 12]), 1)}")
    faults = []
    for t in _times(rng, seconds, 2, first=120, floor=1):
        dur = round(rng.choice([5, 10, 20]), 1)
        faults.append({"type": "suspend", "process": "alvr_host", "at_s": t, "for_s": dur})
        windows.append({"kind": "host_freeze", "t": t, "dur": dur})
    for t in _times(rng, seconds, 2, first=150, floor=1):
        dur = round(rng.choice([3, 6, 12]), 1)
        faults.append({"type": "suspend", "process": "alvr_client_headless", "at_s": t, "for_s": dur})
        windows.append({"kind": "client_freeze", "t": t, "dur": dur})
    return {"stdin": stdin, "client_blackouts": ",".join(bo), "mv_fault": ",".join(mvf), "faults": faults, "windows": windows}
