# visionOS client performance: plan (not executed yet)

Status 2026-11-10. Findings behind it: `docs/VISIONOS_TELEMETRY_LEARNINGS.md`. The client work is released as 1.3.8 (client repo `kteque/alvr-visionos`, branches `1.3.8` and `main`, both `c5375a0` + the README commit); the old test branch `visionos_client_performance` is deleted. The lab model (mock client latency model, `pace_*` scenarios) is on this repo's `main`; the Swift protocol harness is in the client repo (`tools/protocol-harness`).

Done: A (refetch skip, measured: mixed, kept alternating in 1.3.8, see the findings doc), B1/B3 in part (the catch-up pick shipped in 1.3.8, works in the field: queue depth 1.05 -> 0.83, age in queue 5.9 -> 3.4 ms). Open: B2, B4, B5 and section C below, which is the next session.

## A. Fewer blocking tracking queries (deadline misses, 0.2 % of frames)

Evidence: the render-thread spikes (3 to 14 ms) sit in single `queryDeviceAnchor` calls; the walk-back loop never retries (0 of 50,942 calls). Three queries per frame: the refetch and the loop query in `WorldTracker.sendTracking`, one in `Renderer` after it.

Candidate: remove the refetch (`queryDeviceAnchor(lastHeadTimestamp)`). Its only use is `lastHeadPose`, the base of the head velocity sent to the streamer. Replace by the previous frame's stored pose.

- Pros: one blocking call less per frame; fewer misses if the stall is per call.
- Cons: the gain may be nil if the first query of a frame absorbs the stall; the velocity gets worse (two predictions made 10 ms apart instead of one moment), and the streamer uses it for pose prediction; device anchors have hidden state, so removing a query may change the others; the tracking packet changes.
- Test: a constant, then alternate the variant every 60 s inside one session, each telemetry window printing its mode; compare miss rates (about 100 misses per mode per 20 minutes) and judge head motion by feel. Keep only if misses fall and the motion feels the same. Otherwise look at the `Renderer` query.

## B. Display rate changes (90/96/100 Hz chosen by visionOS)

Input: `deep-research-report (1).md` (2026-11-09, a literature search, not our measurement) and our logs. Agreed: no public API locks the rate (Apple documents the system choosing 90/96/100 Hz from the lighting, with a flicker sensor; `preferredFrameRateRange` is a hint; `AVDisplayCriteria` is for tvOS; no entitlement; private APIs break App Review 2.5.1). The design is already the one it recommends: the compositor clock is the master (`presentationTime`), the host follows the headset's vsync and phase, latest-wins queue, reprojection when no frame, no codec reset on a rate change.

Field fact that makes this worth doing: in `alvr (4).log` the display changed once, 21:16:44 100 -> 90 Hz and 10 s later back. The 90 Hz window had 78 of the session's 353 deadline misses (8.7 % of its frames against 0.2 % normally) and one 80 ms display interval. The 10 s windows cannot show what happened inside.

Steps, in order:
1. **Per-event telemetry (client only, no behaviour change).** On a confirmed rate change, log the 2 s around it frame by frame: display interval, slack, queue depth, picked fresh/repeat, and the arrivals per 100 ms. This tells whether the misses are the transition or the 90 Hz period.
2. **Lab: extend the switch scenarios** with 96 Hz, a rapid 100-90-100 and 100-96, and a stall at the switch; measure the frame age at presentation and the repeats around the switch (the report asks for the age distribution, not the mean fps).
3. **Backlog at a switch (behaviour change, lab first).** When a change is confirmed, drop everything but the newest decoded frame (the report's "drop all but newest"); our queue cap is already 2, oldest dropped, so check in the lab whether it changes anything before touching the device.
4. **Confirmation before reacting.** The report suggests three consistent intervals plus an EWMA period. Our client only logs the rate; the host reads the phase and period from the headset reports (engine side, `phase.rs`): check how it treats one odd interval before changing anything. Engine edits need the owner's go.
5. Optional: the host sending the measured vsync rate next to `fps_target` (engine edit, owner's call).

Not planned: forcing a rate; private APIs; Apple's Foveated Streaming framework (owner: no CloudXR).

Order: B1 can ship in the same build as A (both telemetry or alternating test).


## C. Rig session: real client code + real streamer, honest HMD physics (next session, to implement)

Goal: find an end-to-end pacing algorithm (streamer + client) that is more robust at a steady rate, through display-rate transitions and through game hiccups, and first reproduce the open field problem: **the host did not follow either real rate switch** (90 Hz on 2026-11-09 21:16:44, 96 Hz on 2026-11-10 00:17:07; `server_fps` stayed about 100, no `client display rate` host log line, phase lock pinned at -2.00 ms; the lab said it follows). Not a goal: anything that needs the headset to judge (picture, head feel).

### C.0 Rig
- **Client side, Linux (WSL2 here, networking already `mirrored`, `eth1` 10.199.42.65/19, behind Wi-Fi):** the Swift protocol harness (`external/alvr-visionos-pr/tools/protocol-harness`: the real `EventHandler.swift` and pick policy, `DisplaySim.swift` as the render thread). Change it to receive the **real ALVR stream** through the real `client_core` (Rust, `external/alvr-visionos-pr/ALVR/alvr/client_core`) instead of `FakeCore.swift`; the decoder stays a stand-in that sleeps a modelled decode time (frames are not decoded; NAL sizes and timestamps are real).
- **Streamer side, the 3080 Ti builder (`bash tools/builder.sh`, ssh jean-@10.199.93.49, clone `C:\Users\jean-\VisionALVR`):** the real engine (shim, host, NVENC) on a **test branch** (engine edits allowed there by the owner, 2026-11-10; name it `rig_pacing`), driven by `xr_probe` (`XR_PROBE_LOAD=fps:lo:hi`) and the frame generation switches as in the `pace_*` scenarios. NVENC on this laptop GPU caps near 88 to 93 fps at 3520x1600: keep 1280x1120 per eye (harness NVENC preset P2/P3, see memory `harness-nvenc-preset`).
- **Network:** real Wi-Fi, not injected. Note the Linux box is 10.199.42.65/19 and the builder 10.199.93.49: not the same /19, so mDNS discovery will not cross; connect by direct address. Wi-Fi is the physics (the headset is on Wi-Fi too); it is not repeatable, so see C.3.
- **Honest HMD model (all from our logs, `docs/VISIONOS_TELEMETRY_LEARNINGS.md`):** display period 10.0 / 10.42 / 11.11 ms with a switch process (field: 100->90 for 10 s; 100->96 for 15 to 20 s; sessions otherwise fixed), a render-thread stall at a switch (one sample, 80 ms: model 40 to 80 ms), render-thread CPU spikes (cpu split: window maxima median 4 to 6 ms, up to 14 ms, about 0.2 to 0.4 % of frames cause a doubled interval), GPU draw about 4 ms (3.5 to 4.3 ms windows), decode 4 to 7 ms, pick poll 5 ms, queue cap 2 with the catch-up rule, and the compositor rule that a late submission shows the previous frame (display pulls on a fixed grid, Apple's render pipeline page). Replace the lab's pessimistic draw jitter with the measured spread.

### C.1 Spikes (in this order; stop and report if one fails)
1. **client_core on Linux with a stub decoder** receiving a real stream from the builder through WSL2 mirrored networking and a direct address. Success: the harness client connects, `[STATS]` shows frames arriving, the stub decoder paces them. Fallback if it cannot be built: port the pick policy into the builder's Rust `tools/mock_client` (less faithful: two implementations).
2. **Rig versus field network.** Run a steady 100 fps stream for 10 minutes and compare the rig's `network_latency_ms` (mean, p95, spikes) with the field's 2 to 10 ms in `alvr.log` STATS lines. If very different, say so before trusting any number.
3. **Clock offset** between the two machines, good to about 1 ms (ping-pong inside the run, or chrony), so that frame age = display tick minus host send time can be computed. Without it use only client-side metrics (below).

### C.2 First experiment: reproduce "the host did not follow"
Scenario: display 100 Hz steady, then switch to 90 (and a second one to 96) at a known time with a 40 to 80 ms render stall, switch back after 10 to 20 s, real engine with frame generation on and off. The client sends the same tracking timestamps as the real one (`reportedTargetTimestamp` = the drawable's `presentationTime`, one sample per frame). Watch: `tools/host/src/vision.rs` `RateEstimator` (median of 90 gaps, three agreeing evaluations about 3 s apart, 1 % tolerance), `tools/host/src/main.rs` (around lines 2120 to 2150: `display_rate`, `off_nominal` at 2.5 %, `vsync_period_ns`), `tools/host/src/phase.rs` (the -2 ms limit). Add a host log line each time the estimator emits (engine edit, allowed on `rig_pacing`). Questions to answer: does the estimator ever emit during a 10 to 20 s excursion with misses and stalls; why the lab (`pace_switch_*`) re-locked and the field did not; is the phase lock's -2 ms limit the reason the host stays out of phase after a switch.

### C.3 Comparing algorithms honestly
- Wi-Fi varies: **interleave** the algorithms every minute in one session (as the refetch A/B), many repeats, log RTT and jitter per window and exclude or flag noisy windows.
- **Trace capture and replay:** record per-frame arrival time and size from a rig run, and replay it in the Linux model (trace-driven network) for deterministic regression runs of the client policy.
- Scenarios, each scripted and repeatable: steady 100 and 90; 100->90, 100->96, 90->100, rapid 100-90-100-96; a stall at the switch; game hiccups (frame time spikes of 20 to 60 ms from the probe's `XR_PROBE_LOAD`, a game that runs at 95 to 99 fps); a long Wi-Fi hiccup.
- Run the winning algorithm on scenarios it was not tuned on, then on the Vision Pro (TestFlight) before calling it better.

### C.4 Metrics (same for every algorithm)
Frame age at display (p50, p95, p99; needs C.1 step 3), repeated ticks by cause (no frame, late frame, queue drop), doubled intervals per 1000 frames, recovery time after a switch or hiccup (time until frame age is back under 12 ms), frames the game drew that were never shown (waste), steady-state queue depth and age in queue. Without the clock offset: the last four only plus arrival gaps.

### C.5 Algorithm candidates (one at a time, same scenarios)
1. **Explicit rate report:** the client tells the host its measured display period (a small message, or the existing tracking timestamp cadence made explicit); the engine obeys a number and stays headset-agnostic. Replaces the 3-evaluation estimator for the first reaction. Check the ALVR protocol for a client-to-host slot (the v20 reserved message used for the old `min(P, V)` work is in `archive/1.4-experiment`).
2. **Phase controller:** wider range or a re-lock at a confirmed switch instead of the slow -2 ms steps.
3. **Transition handling on both sides:** at a confirmed change the client keeps only the newest frame (the catch-up already does at depth 2), the host drops its encode backlog and re-targets the frame generation clock; no codec reset, no IDR.
4. **Hiccup policy:** compare (a) host paced to the display with frame generation filling gaps, (b) host at game rate and the client reprojecting, (c) a hybrid with a bound on how long the host waits for a late game frame.
5. **Queue and pick:** a target frame age instead of a fixed cap; pick the newest frame not newer than the presentation time.

### C.6 Rules for the session
- Engine edits only on the `rig_pacing` test branch of this repo; nothing in `tools/host`, `ovrshim/`, `nvenc/` on `main` without the owner's go. Client edits on a client-repo branch; harness changes may go to `main`.
- Do not run lab load on the 5090 (the owner's play machine); the 3080 Ti builder is for it (check it is free first: another session may be using it).
- Report what was tested and how, not "works" for a lab result; ship to the headset only through a TestFlight build the owner asks for.
- Ask before pushing, releasing or deleting anything on GitHub.
