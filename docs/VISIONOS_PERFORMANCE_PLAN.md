# visionOS client performance: plan (not executed yet)

Status 2026-11-09. Findings behind it: `docs/VISIONOS_TELEMETRY_LEARNINGS.md`. Branch for tests: `visionos_client_performance` (client repo `kteque/alvr-visionos`, lab in this repo). Nothing below is built unless marked.

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
