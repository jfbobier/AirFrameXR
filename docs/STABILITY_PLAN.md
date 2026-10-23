# Stability plan (2026-10-21)

Goal: find and fix the failures that only show after hours, under memory pressure, or when an input the chain assumes goes away (tracking, motion vectors, audio device), without costing performance. Frame generation with game motion vectors is the most complicated part of the chain, so every scenario runs with it.

## What triggered it: Gears of War E-Day, 2026-10-20

Logs: `VisionALVR-GoWEDay-Steam-20261020-2340.zip` (debug was off). Two stalls with frame generation on:

| | Stall 1 | Stall 2 |
|---|---|---|
| When | 18:45:37 - 18:46:15 | 23:01:21 - 23:02:18 |
| Tracking gap ("tracking spacing max") | 29.5 s | 30.2 s |
| Stream | about 23 fps in the window | 0.1 fps afterwards |
| Generation boundaries | most skipped for want of a tracking sample | 910 of 910 skipped |
| Recovery | by itself | headset reconnect at 23:02:00, then generation off at 23:02:18 cured it instantly |

Generation back on at 23:03 ran 37 minutes clean. The host log shows 99.9 fps streamed until 23:40:23.

Probable mechanism (not yet reproduced): with generation on, `fg_plan_app` (`tools/host/src/pipeline.rs`) returns `Plan::Skip` when no tracking sample newer than `last_sent_ts` exists, and sends nothing. The visionOS client sends views and tracking only after it receives a frame. A pose-uplink hiccup or a reconnect therefore becomes a deadlock: the host waits for a pose, the client waits for a frame. With generation off, the shim's `SelectHostHead` waits 3 ms and repeats the last sample, so frames keep flowing and the client recovers.

Secondary suspect: `last_sent_ts` is a monotone max that is never reset when the client clock restarts (a reconnect). The clean run after 23:03 argues against it as the cause here; test it anyway.

Other findings:
- the "releases late" counter wrapped (u64 underflow) at 16:19:35 after a game restart: per-game counters are not reset;
- 429 "height:" lines flood the general log;
- the DLSS stream count went 2 -> 0 at 18:44:47, 50 s before stall 1 (the motion-vector source was lost; possibly a coincidence);
- VRAM sat on a 12-14.8 GB plateau, no clear leak; the log cannot separate host, shim and game.

## Principles

- The harness always runs the **debug** flavour (full instrumentation); a few runs on **release** confirm the always-on probes work there.
- Only probes needed in a session where debug was off stay in release, and only if near-free.
- Tests run under **artificial VRAM and RAM limits**: the dev machines have far more headroom than many users' machines.
- Fault injection never ships: it lives in the harness, the mock client and the probe, and in debug-only engine hooks.
- Scenarios are seeded and replayable.

## Phase 0: observability

Release flavour (always on, near zero cost):
- stall detector event in the general log: no frame sent for more than 1 s while connected, or tracking silent for more than 1 s, naming the cause (a comparison once a second on an existing tick, a log line only when something is wrong);
- one coarse memory line (host, shim and game private bytes, dedicated VRAM) in the existing 10 s `frame generation perf` line;
- fix the u64 wrap and reset the per-game counters;
- throttle the "height:" log line.

Debug flavour only (compiled out of release):
- per-second memory sampling and handle counts;
- allocation counters for NVOF, tap copies and ring slots (promote to release only if the soak shows they are needed: one relaxed atomic increment per allocation or free);
- per-frame tracing and every fault-injection hook.

## Phase 1: harness machinery (`harness/stability/`)

One runner, JSON scenarios (fault schedule + pass criteria), built on the real-client path (mock client, `xr_probe`, the `e2e_avp_late_views` behaviour). The simrig is not suitable: its synthetic head never goes silent.

- Mock client: `--tracking-blackout t:dur`, `--disconnect t:dur`, bursty and reordered tracking.
- Probe: `XR_PROBE_MV=drop:p|blackout:t:dur|garbage|wrongsize`, DLSS stream churn (2 -> 0 -> 2), resolution change, exit and relaunch.
- Memory pressure: a D3D12 VRAM ballast leaving the host and game only N GB (profiles 6, 8, 12 GB); job-object RAM caps per process plus a RAM ballast.
- Watchdog and judge: memory sampled each second; slope fitted over the last 80 % of the run, fail above a growth threshold per hour; fail on any stall over 2 s and on a generation output fraction below a floor.

## Phase 2: scenarios

Each with generation on at half and two-thirds rates and motion vectors on. Short runs about 5 minutes; the soak runs unattended for hours.

1. `tracking_blackout`: 0.5, 5 and 30 s of silence, with and without a reconnect, in AVP mode (views after the first frame). Pass: frames keep flowing during the blackout, recovery under 1 s after tracking returns. Expected to fail today.
2. `reconnect_storm`: 20 disconnect/reconnect cycles at random intervals during a game, including a client clock restart.
3. `mv_loss`: random drops of 1 to N frames, whole-stream loss for 10 s, garbage vectors, a size change. Pass: no flow errors accumulate, synthesis falls back to flow, no VRAM growth.
4. `stream_churn`: DLSS streams 2 -> 0 -> 2 every few seconds, 50 game relaunches. Pass: tap copies and ring slots are freed.
5. `fg_toggle_soak`: generation on/off and rate changes every few seconds for an hour.
6. `bursty_wifi`: bunched, delayed and reordered tracking and packets.
7. `memory_soak_tokyo`: 4-8 hours in Little Tokyo, generation and vectors on, under the 8 GB VRAM and capped-RAM profiles. Seeded random but plausible events: audio start/stop and default device change, game muted/unmuted, mirror toggled, recenter, eye height, resolution-scale restart, settings reload, the wizard test mid-game.
8. `resource_exhaustion`: the ballast squeezes VRAM mid-run, RAM capped. Pass: no hang, a logged fallback (generation off or lower flow scale), recovery when memory is released.

## Phase 3: hardening (driven by the runs)

Each fix keeps its performance cost near zero.
- Tracking deadlock: after a short wait with no usable sample (about 50-100 ms), generation sends the last frame stamped with the last real sample's timestamp instead of `Plan::Skip`. The timestamp is reused, never predicted or invented (see `PERFORMANCE_FINDINGS.md`: the client's lookup is exact). Prove the failure in scenario 1 first, then fix, then re-run.
- Reset `last_sent_ts` and other per-session state when the client clock restarts.
- On allocation failure, fall back to generation off or a lower flow scale instead of failing silently.
- Stall watchdog: nothing sent for over 1 s while connected forces the safe path and logs it.

## Order

1. Phase 0.
2. The mock client's tracking-blackout flag and scenario 1: reproduce the deadlock.
3. The deadlock fix.
4. Ballast, `mv_loss` and `stream_churn` scenarios.
5. The overnight soak.


## Status 2026-10-22 (3080 Ti laptop, Vision Pro size, debug flavour)

The harness exists and runs: `harness/README.md` (Stability harness), `harness/stability/`. What the lab found:

**The Gears stall is reproduced and fixed.** Two layers, both seen in the logs:
1. Something held the host for about 30 s (stall 1: the statistics line missing for 38 s and a 29.5 s gap between tracking events; the game kept releasing frames
   meanwhile). The cause is not known yet; the watchdog (below) names it next time. Whole-process suspension is the closest lab model.
2. With generation on, a boundary without a fresh tracking sample sent nothing; the visionOS client only sends tracking while frames reach it, so any pause turned into a
   stall far longer than the pause (lab: 5 s of silent uplink -> 61 s; a 30 s host freeze -> 86 s; a 30 s client freeze -> 56 s). Generation off was never affected.
   Fix (`pipeline.rs`, `fg_stale_sample`): after 100 ms without a fresh sample (`VISIONALVR_FG_DARK_MS`, 0 = off) boundaries send frames stamped with the last real
   sample, as the generation-off path does. Lab: 5 s silence -> 0.1 s stall; 30 s host freeze -> 31 s (the freeze itself); 30 s client freeze passes. Normal operation
   is unchanged (a late sample still skips one boundary, as before; `e2e_fg_latency` passes).

**Phase 0 (release flavour):** `watchdog.rs`: `stall_detected` / `stall_ended` events and log lines naming the cause (process not running, main loop, tracking, frames) and a
memory line every 30 s (host private / working set / handles, RAM, GPU used, host VRAM and its budget); the per-game counter wrap and the height log flood fixed.
Debug-only allocation counters are not built yet.

**Memory:** at the Vision Pro's size the chain holds host 1.9 GB RAM + 2.0 GB VRAM, probe 0.8 GB VRAM (2.8 GB in all); no growth in 150 s runs; a squeeze to 1.8 GB of VRAM
(Windows pages) or 2.3 GB of RAM is survived. Vector loss, churn (new stream ids, size changes, textures re-created) and garbage do not stall, error or grow.
Not yet run: hours-long soaks (`soak_tokyo --seconds 14400`), and the leak verdicts need them.

**A second cause of the 30 s freeze was found and fixed.** The streamer window reads the host's stdout through a pipe; `event()` used a blocking `println!`, so a reader that
stops (a modal dialog, a busy UI thread) froze every thread that printed an event, the main loop and the new watchdog included: lab `gui_pipe_stall_30s`, a 30 s reader stall
stopped the stream for 27 s. Events now go through a bounded queue to one writer thread (`send_event_line` in `main.rs`; past 4096 queued lines they are dropped and
counted). Lab: 27 s -> 44 ms. Whether this is what happened in Gears is not proven; the watchdog will say next time.

**Suite 2026-10-22:** all 18 scenarios pass (`run_stability.py run all`, about 90 minutes on the 3080 Ti laptop; `client_freeze_5s` once needed a re-run for a lab pairing flake,
which the runner now retries). A 15-minute `soak_tokyo`: every stall inside an injected freeze, host +8 MB and probe +0 MB over 11 minutes. `e2e_baseline` fails its pacing
limits on this laptop with or without these changes (checked against the host built from 373ea48), and `e2e_installed_layout` was already failing on 2026-10-19.

**Open:** the cause of the Gears host freeze if it was not the pipe; a D3D12 vector provider and the debug allocation counters (`harness-dlss-coverage-gap`); audio events in the soak
(default device changes, mute, volume: the helpers exist in `harness/audio_route.ps1`); a pipe-stall test (the GUI not reading the host's stdout: a blocked reader would
freeze threads that print, one candidate for layer 1).
