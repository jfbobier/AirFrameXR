# Plan: end-to-end frame timing experiment (streamer + visionOS TestFlight)

Written 2026-11-26 for a future session. Nothing in this plan is built. It follows the measurements of 2026-11-25/26
(`docs/PERFORMANCE_FINDINGS.md`, section of that date) and the client telemetry work of 2026-11-08 to 11-12
(`docs/VISIONOS_TELEMETRY_LEARNINGS.md`). Read both before starting; this plan does not repeat them.

Evidence words follow `docs/progress.md`: say what was tested, how, and what was not. Hypotheses are marked as such.

## 1. What this is for

Today the chain is timed by constants: the host aims its frames 7 ms ahead of the headset's render tick, the client waits up to 8 ms
for a late frame, and Apple's compositor decides the rest. The experiment asks whether the two ends, exchanging a few timing signals,
can show each frame earlier **without adding repeated frames or hitches**.

Owner's setup and judge: RTX 5090, Apple Vision Pro at 100 Hz, dedicated access point, the same games as 2026-11-25
(Dead Space with frame generation x2, Gears of War: E-Day with x3, plus one game without generation).

Expected size, stated before any work: **5 to 10 ms less between a frame's arrival at the headset and its display, at best**, of about
43 ms today. Head rotation is already corrected at display time, so a gain shows on hands, controllers and game response, not on
head stability. If the first telemetry session (step S1) shows less than 3 ms available, stop there.

## 2. Where we start (2026-11-25/26, medians per 10 s window, Dead Space x2, 275 Mbps)

| Leg, as the headset reports it | ms | Whose |
|---|---|---|
| Game (tracking sample to the frame leaving the game) | 6.3 | host |
| Encode | 3.0 | host |
| Network | 7.6 | a remainder, see below |
| Decode | 6.0 | client |
| Decoded frame waits for the render loop ("decoder queue") | 10.2 | both: the host's 7 ms margin + the arrival spread |
| Client render (pick to the submit report) | 6.7 | client |
| Submit report to display ("vsync queue") | 20.5 | client + Apple, split unknown |

Things a reader must know about these figures:

- **"Decoder queue" is not a wait before decoding.** ALVR (`alvr/client_core/src/statistics.rs`) measures it from the end of the decode
  to `alvr_report_compositor_start`, the client's pick.
- **"Network" is not measured.** ALVR takes the total and subtracts the other legs (telemetry learnings, finding 11).
- **The window's total is a median of per-frame totals one display period apart (57 or 67 ms); the legs are means.** At x2 the two
  groups are near half and half, so the total flips by 10 ms on a small shift. Judge changes on the legs, never on the total.
- **None of the two waits moved** with the bitrate (275 or 150 Mbps), the game, the generation rate or the early hand-over.
- **Repeated display periods are about 1 %** in every arm. That is the hiccup baseline to protect.

Statements made in the 2026-11-26 conversation that this plan corrects:

- "5 to 10 ms of slack before Apple's deadline" was a guess. The November telemetry builds logged that slack: the rig model made from
  them uses 4.3 ms, and misses happen when a render-thread spike exceeds it. So the gain, if any, is not in submitting later. It is in
  **where Apple places the start of our frame** and in **what the host aims at** (section 4).
- The 2 ms "network" gain at 150 Mbps is a change of the remainder, not a measured transit.

## 3. How the chain is timed today

**Host** (`tools/host/src`):

- A clock thread runs a display grid at the headset's rate. The game's release, the pick, the encode and the send follow it.
- `phase.rs` (the phase lock) slides that grid. Its input is the client's per-frame `decoder_queue`; it takes the arrival phases
  modulo one period, and steers the 10th percentile of the cluster to `AIRFRAMEXR_PHASE_MARGIN_MS` (7) before the render tick, in steps
  of at most 2 ms every half second. `AIRFRAMEXR_PHASE_LOCK=0` disables it.
- Known flaw: it never settles in the lab or in field logs (estimates 0 to 5 ms against 7, endless -2 ms slides). Cause open.
- Arrival spread: 8 to 9 ms of a 10 ms period (2026-10-17), with or without generation. With generation the outputs alternate:
  rendered ones are ready 2.6 to 3 ms after the boundary, generated ones 3 to 8 ms (6 to 10 ms without the early hand-over).
- Messages to the client: `ServerControlPacket::Reserved(String)`, JSON `{"airframexr":1,...}` (`vision.rs`, patch
  `tools/patches/headset-view-message.patch`); live example: `client_sharpen on|off`.
- Messages from the client: the per-frame ALVR statistics packet (fixed struct) and log lines; `logs.rs` already parses the
  `va_ticks {json}` line both clients send every 10 s.

**Client** (visionOS, current line: branch `1.4-foundation`, local checkout `external/alvr-visionos-1311`; remotes `origin` =
jfbobier/airframexr-visionos, `kteque` = kteque/alvr-visionos where the TestFlight workflow runs). `Renderer.swift`, `renderFrame()`:

1. `queryNextFrame`, `predictTiming`, `startUpdate` / `endUpdate`.
2. Wait until Apple's **optimal input time** (O).
3. `startSubmission`.
4. `EventHandler.pollFrameForDisplay(timeout: 0.008)`: the pick. A fresh frame is taken at once; otherwise it waits up to 8 ms, then
   resubmits or reprojects the last one. The queue holds at most 2 decoded frames; catch-up is off (1.3.9).
5. `queryDrawable`, `alvr_report_compositor_start`.
6. `WorldTracker.sendTracking` (contains `queryDeviceAnchor`, which spikes 3 to 14 ms now and then), then the display-time anchor query.
7. `alvr_report_submit` with `presentationTime - now` (the "vsync queue").
8. Encode, `commit`, `endSubmission`. Apple's **rendering deadline** (R) comes after, the **presentation time** (P) after that.

The client receives streamer messages in `EventHandler.applyReservedMessage` and sends lines through the client log
(`reportTicksIfDue`, the `va_ticks` line). Core changes are patches in `patches/` applied by `scripts/apply_core_patches.sh`.

## 4. Hypotheses, and the number that settles each

| | Hypothesis | Settled by (step S1) |
|---|---|---|
| H1 | Most of the 20.5 ms is Apple's: R to P is about 1.5 periods and cannot be shortened by us. | histogram of `P - R` |
| H2 | Apple places O from the duration of our submissions (step 3 to step 8). Our wait for a late frame sits inside that window, so it makes Apple start us earlier than our real work needs. | `R - O` against our real work; `R - O` in windows with many waited frames against windows with few |
| H3 | Frames that arrive after O but before the last moment we could still render them make their tick thanks to the wait. So the true deadline is later than the tick the host aims at, and part of the 7 ms margin is spent twice. | arrival slack against O and against the latest safe pick time |
| H4 | The 10 ms decoded-frame wait is the 7 ms margin plus the spread, with no standing second frame. | queue depth at pick, share of picks at depth 2 |
| H5 | The phase lock's input is too poor to settle (a wait modulo one period). An explicit slack report would let it settle. | lock corrections per minute against the slack distribution |

If H1 holds and H2 does not, the "vsync queue" cannot be reduced and only the margin work (section 5, B) remains.

## 5. The design to test

A frame for display tick N has, on the headset's clock, Apple's three times O, R, P. Define the **latest safe pick** K = R minus the
client's own work (p99 of pick to `endSubmission`, measured over the last 10 s) minus a safety of 2 ms.

**A. Client, the "vsync queue" change (two parts, each its own switch).**

- A1, lean submission: the wait for a late frame moves before `startSubmission`, so the window Apple measures holds only real work.
  If H2 holds, Apple moves O later and every frame is picked later for the same display tick: latency drops for all frames.
- A2, deadline wait: the fixed 8 ms becomes "until K". A late frame is waited for exactly as long as it can still be shown, never
  longer. This is the net that part B relies on.

**B. Negotiated margin (both ends).**

- The client reports the arrival slack of its picks (how long before K the frame was decoded), as percentiles once a second.
- The host replaces the constant 7 ms by a margin that follows that report, under the rules of section 6. The phase lock keeps its
  mechanism (sliding the grid); only its input and its target change.

**C. Standing queue removal (client, small).** After a stall a second decoded frame can stay queued, one more period of age that
never drains (noted 2026-11-12, "sustained catch-up"). Drop the older frame only when the queue has held 2 for 50 picks in a row.
Build it only if S1 shows picks at depth 2 in steady play.

**Not in this experiment** (one variable at a time, and each has failed before):

- The tracking-sample path: no re-stamping, no predicted samples, no change of when the client sends its sample. Earlier attempts
  trembled on the Vision Pro.
- Translation correction, the display rate request, the bitrate, the just-in-time game release.

## 6. Rules against hiccups

These are requirements, not advice. A build that breaks one does not go to the headset.

1. **Default is today's behaviour.** Every change is a switch that is off unless the streamer turns it on with a message. A client that
   receives no message behaves as 1.4.x; a stock client keeps the fixed margin.
2. **The net before the squeeze.** A2 is in place and measured before B reduces any margin.
3. **Back off fast, advance slowly.** The margin grows by 1 ms as soon as more than 1 % of the frames of one second were late; it
   shrinks by 0.5 ms only after 10 clean seconds. Bounds 3 to 9 ms, at most one change per second, and never faster than the phase
   lock's own slew (2 ms per half second), so no change is a visible skip.
4. **Freeze when the picture is not steady.** No margin change during the first 20 s of a stream, while the game is under its cap
   (loading screens), during a deadline-miss storm on the client (5 misses in a second), after a display rate change, after a
   reconnect. Without a report for 2 s the margin returns to 7 ms at the same slow rate.
5. **Nothing heavy on the client's render thread.** No allocation, lock, string formatting or logging there. It writes numbers into
   preallocated arrays; a background queue computes the window and sends it. `queryDeviceAnchor` is the known source of misses: do
   not add calls to it.
6. **The telemetry is itself an arm.** A live switch turns the client's recording off. One alternation of telemetry on / off must
   show the same deadline misses and repeats before any other result is trusted (the host's telemetry flavour costs 5 ms a frame; the
   2026-10-17 margin was found through that side effect).
7. **Give Apple time to adapt.** Arms that change what Apple measures (A1) last 60 s, and the first two windows after a switch are
   dropped. Watch `R - O` drift inside an arm.
8. **Lab before headset**, in this order: the client's Linux harness (`tools/protocol-harness`: the stall and switch scenarios), the
   host's stability rows (`harness/stability`: host freeze, reconnect storm, client freeze, tracking blackouts), then the lab with the
   mock headset for the control loop with injected slack reports.

## 7. Telemetry: what must be logged

All of it in the **release flavour**, as 10 s window lines in the game's run log (`logs\games\<exe>\*.log`), like the
`early hand-over experiment` line of 2026-11-25. No per-frame files.

**Client, one line every 10 s through the client log** (same route as `va_ticks`; the host parses it in `logs.rs` and copies it into
the game's log). Times are per render tick, on the headset's clock, in ms. Each list is p1, p10, p50, p90, p99.

```
va_time {"v":1,"t":10.0,"hz":100.0,"arm":"<client switches in force>","n":1000,
 "o_r":[..],      // R - O: how long Apple gives us (H2)
 "r_p":[..],      // P - R: Apple's own part (H1)
 "work":[..],     // pick -> endSubmission: our real work
 "sub":[..],      // startSubmission -> endSubmission: what Apple sees
 "slack_r":[..],  // R - endSubmission: negative = deadline miss
 "ready_o":[..],  // O - decoded: arrival slack against the tick the host aims at (negative = waited for)
 "ready_k":[..],  // K - decoded: arrival slack against the latest safe pick (negative = missed its tick)
 "age":[..],      // P - frame handed to the decoder: the result we want smaller
 "track":[..],    // sendTracking duration (the spike source)
 "fresh":..,"resubmit":..,"reprojected":..,"none":..,          // as va_ticks
 "waited":..,"late":..,"depth2":..,"drops":..,                 // picks that waited; frames that missed; picks at queue depth 2; queue drops
 "miss":..,"doubled":..,"run_max":..,"runs3":..}               // deadline misses; display intervals over 1.5 periods; longest run of non-fresh ticks; runs of 3 or more
```

**Client, once a second, only while part B is on:** `va_slack {"n":100,"p1":..,"p10":..,"late":..,"miss":..}` (from `ready_k`).

**Host, one line every 10 s:** `frame timing experiment: {json}` with: the arm, the margin in force and its changes in the window,
the phase lock's corrections (count and sum) and its estimate, outputs ready after the boundary p50 / p95 per kind (rendered,
generated; the accumulator exists on `perf-early-handover`), outputs later than a period, game fps, stream fps, boundaries without a
tracking sample, packets lost, the GPU line, the ALVR statistics window (as today), and the last `va_time` window.

**Report script:** `harness/exp_timing_report.py`, modelled on `harness/exp_early_report.py` (branch `perf-early-handover`): windows
grouped by arm and generation rate, the first two windows of each log and the windows after a switch left out, median and quartiles.

## 8. Steps

Branches: host on a new branch from `perf-early-handover` (it carries the 10 s experiment line, the in-session alternation and the
package scenarios); client on a new branch from `1.4-foundation`. Check first what build is on TestFlight and start from that.
Engine files change only on that branch (the owner authorised experiment branches; ask again before anything reaches `main`).

| Step | What | Gate to pass |
|---|---|---|
| S0 | Client: the `va_time` recording and the live switches (telemetry on / off, A1, A2, C), all off. Host: parse `va_time`, the experiment line, the alternation of client switches by message. Harness: extend the Linux client harness so its display model has O, R and P. | Harness scenarios pass with every switch off and on; a release-flavour lab run shows the lines; stability rows pass. |
| S1 | **Headset session 1, measurement only.** One TestFlight build, behaviour unchanged. Alternate only telemetry on / off (30 s). | Telemetry adds no misses or repeats (rule 6). Then read H1 to H5 and decide which of A1, A2, B, C to pursue. Stop if under 3 ms is available. |
| S2 | **Headset session 2, client arms.** Same TestFlight build if S0 put the switches in. A2 against today (30 s arms), then A1 against today (60 s arms). | Section 9. |
| S3 | Host: the negotiated margin (part B), with the lab's injected slack reports, including a late burst, a silent client and a stall. | The margin backs off within 2 s of a burst, returns after it, and falls back to 7 ms when the reports stop. No oscillation over 10 minutes of steady input. |
| S4 | **Headset session 3, negotiated margin** on top of the winning client arm, against the fixed 7 ms (30 s arms). | Section 9. |
| S5 | Decide: what goes into a client release and a streamer release, as defaults without settings, and what is dropped. Record the outcome in `docs/PERFORMANCE_FINDINGS.md` and `docs/VISIONOS_TELEMETRY_LEARNINGS.md`. | Owner's decision. |

Session protocol for the owner (each headset session): the streamer experiment bundle and the TestFlight build named in the session
note; the same three games, 12 minutes or more each in steady play (not menus), same settings as 2026-11-25 (100 Hz, 275 Mbps,
generation x2 in Dead Space, x3 in Gears); nothing to set by hand, the alternation is on by default. Say afterwards whether any
hitch or trembling was seen and roughly when. Then the `logs` folder is pulled from the install.

## 9. What counts as success

Compared between the arms of one session, per game, on windows (median and quartiles; a difference inside the quartiles is no result):

- **Result:** `age` p50 (frame handed to the decoder to display) lower by 5 ms or more. Between 2 and 5 ms: keep only if every
  guard below is flat. Under 2 ms: drop.
- **Guards, all must hold:**
  - share of fresh ticks not lower (inside the quartiles, and within 0.2 points);
  - deadline misses per 1000 frames not above the other arm's upper quartile;
  - runs of 3 or more non-fresh ticks (a hitch of 30 ms and more): no more of them, and `run_max` not larger;
  - `age` p99 not higher (a faster median with a slower tail is a hiccup machine);
  - queue drops and packets lost unchanged;
  - the owner saw no new hitch and no trembling.
- **For part B also:** the margin's time series is calm (fewer than 6 changes a minute in steady play), and the lock's corrections
  per minute go down against the fixed 7 ms.

## 10. Risks

- Apple may not place O from our submissions at all (H2 false): then A1 does nothing and A2 alone is a safety change.
- Waiting until K uses the slack that hides `queryDeviceAnchor` spikes: misses could rise. The safety (2 ms) and the p99 of the work
  are the two numbers to tune, in the harness first.
- A control loop across Wi-Fi can oscillate. The October Auto controller was removed for that. Rules 3 and 4 are the answer; if S3
  cannot make it calm in the lab, part B does not go to the headset.
- The margin and the wait interact with the early hand-over (it narrows the host's part of the spread). Keep the hand-over constant
  within a session.
- The pacing rig of November (`rig_pacing`, `rig_client`, on the 3080 Ti's Arch WSL) predicted the 1.3.9 result within 0.3 points, but
  it has not been touched since 2026-11-11 and predates 1.5. Use it if it revives in an hour; do not rebuild it for this.

## 11. Decisions for the owner before S0

1. The early hand-over in the experiment's streamer: on and constant (recommended: it narrows the spread the margin has to cover and
   showed no harm), or off as in 1.5.0.
2. Whether one TestFlight build may carry all the client switches (recommended: fewer builds, the streamer drives the arms), or one
   build per step.
3. The third game, without frame generation.
4. Where the TestFlight build goes (the internal group only, as for 1.4.0).

## 12. Pointers

- Findings of 2026-11-25/26 and the logs: `docs/PERFORMANCE_FINDINGS.md`; `private/logs/exp-early-handover-2026-11-25`,
  `private/logs/exp-early-handover-2026-11-26-150mbps`.
- Client telemetry history, Apple's render pipeline notes, the 1.3.9 screening: `docs/VISIONOS_TELEMETRY_LEARNINGS.md`.
- Phase margin history (3 to 7 ms) and the arrival spread: `docs/PERFORMANCE_FINDINGS.md`, section 2026-10-17.
- Experiment branches of 2026-11-25 (local worktree `openxr-perf`): `perf-early-handover`, `perf-experiments`.
- Client: `ALVRClient/Renderer.swift` (`renderFrame`), `ALVRClient/EventHandler.swift` (`pollFrameForDisplay`, `reportTicksIfDue`,
  `applyReservedMessage`), `tools/protocol-harness`, `.github/workflows/testflight.yml` (dispatch only).
- Rules: the engine is off limits on `main` without asking; ask before pushing, releasing or uploading to TestFlight; never stop the
  owner's installed streamer on the 5090.
