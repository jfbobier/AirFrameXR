# Plan: end-to-end frame timing experiment (streamer + visionOS TestFlight)

Written 2026-11-26 for a future session. Nothing in this plan is built. It follows the measurements of 2026-11-25/26
(`docs/PERFORMANCE_FINDINGS.md`, section of that date) and the client telemetry work of 2026-11-08 to 11-12
(`docs/VISIONOS_TELEMETRY_LEARNINGS.md`). Read both before starting; this plan does not repeat them.

Evidence words follow `docs/progress.md`: say what was tested, how, and what was not. Hypotheses are marked as such.

## 1. What this is for

Today the chain is timed by constants: the host aims its frames 7 ms ahead of the headset's render tick, the client waits up to 8 ms
for a late frame, and Apple's compositor decides the rest. The experiment asks whether the two ends, exchanging a few timing signals,
can show each frame earlier, and which combination of choices does it without adding repeated frames or hitches.

Owner's setup and judge: RTX 5090, Apple Vision Pro at 100 Hz, dedicated access point. The owner plays what was played on
2026-11-25: a mix of modded and native games, with frame generation (Dead Space x2, Gears of War: E-Day x3) and without
(AC Black Flag with its mod, Cyberpunk 2077, Mortal Shell 2).

**The owner's terms for the experiment (2026-11-26):**

- It is a short measurement campaign on a temporary TestFlight build; afterwards TestFlight goes back to the previous build.
- **Hiccups during the measurement are acceptable.** Nothing has to be protected while measuring; a hitch avoided must come from a
  good design choice, not from caution in the test.
- **Every switch is in play**, and the streamer drives them during the sessions. The session operator (Claude) sets and changes the
  schedule to collect what is needed; the owner only plays.

Expected size, stated before any work: **5 to 10 ms less between a frame's arrival at the headset and its display, at best**, of about
43 ms today. Head rotation is already corrected at display time, so a gain shows on hands, controllers and game response, not on
head stability.

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
- **Repeated display periods are about 1 %** in every arm. That is the baseline a shipped design must not worsen.

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

| | Hypothesis | Settled by |
|---|---|---|
| H1 | Most of the 20.5 ms is Apple's: R to P is about 1.5 periods and cannot be shortened by us. | histogram of `P - R` |
| H2 | Apple places O from the duration of our submissions (step 3 to step 8). Our wait for a late frame sits inside that window, so it makes Apple start us earlier than our real work needs. | `R - O` against our real work; `R - O` with the wait inside and outside the submission |
| H3 | Frames that arrive after O but before the last moment we could still render them make their tick thanks to the wait. So the true deadline is later than the tick the host aims at, and part of the 7 ms margin is spent twice. | arrival slack against O and against the latest safe pick time; the margin sweep |
| H4 | The 10 ms decoded-frame wait is the 7 ms margin plus the spread, with no standing second frame. | queue depth at pick, share of picks at depth 2 |
| H5 | The phase lock's input is too poor to settle (a wait modulo one period). An explicit slack report would let it settle. | lock corrections per minute against the slack distribution; phase lock on / off |

If H1 holds and H2 does not, the "vsync queue" cannot be reduced and only the margin work remains.

## 5. The switches

A frame for display tick N has, on the headset's clock, Apple's three times O, R, P. Define the **latest safe pick** K = R minus the
client's own work (p99 of pick to `endSubmission`, measured over the last 10 s) minus a safety.

Every switch is live, set by the streamer, and reported back in every telemetry window. The first value is today's behaviour, which
is also what the client does when it receives no message and what a stock client keeps.

| Side | Switch | Values | What it tests |
|---|---|---|---|
| client | `tele` | on, off | the recording itself costs nothing (must be shown first) |
| client | `sub` | `in`, `lean` | lean = the wait for a late frame moves before `startSubmission`, so Apple measures only real work (H2: the "vsync queue" change) |
| client | `wait` | `fixed8`, `deadline`, `none` | deadline = wait until K instead of a fixed 8 ms; none = take what is there (the cost of no net) |
| client | `safety` | 2, 1, 3 ms | the safety inside K |
| client | `catchup` | `off`, `sustained`, `always` | sustained = drop the older frame only after 50 picks in a row at depth 2 (a standing queue after a stall); always = the 1.3.8 rule |
| client | `order` (optional, only if simple) | `pick_first`, `pick_last` | pick after the tracking send, so a frame landing during it is still shown |
| host | `margin` | 7, 3, 5, 9 ms, `auto` | the phase margin; auto = it follows the client's slack report (section 6) |
| host | `phase_lock` | on, off | whether the lock helps at all (H5) |
| host | `early` | off, on | the early hand-over of 2026-11-25 (narrows the host's part of the spread) |

**Not a switch in this experiment** (each has failed before): the tracking-sample path (no re-stamping, no predicted samples, no change
of when the client sends its sample), translation correction, the display rate request, the just-in-time game release.

## 6. Design choices that should make the result hiccup-free by themselves

These describe the candidate that ships, not limits on the measurement. During the campaign any arm may hitch; that is data.

- **A net instead of a margin.** `wait = deadline` shows a late frame whenever it can still be shown and never waits longer than
  that. With the net in place the host can aim closer, because a frame that misses the aim is not a frame that misses its tick.
- **A margin that follows what the headset sees.** With `margin = auto` the client reports once a second how long before K its frames
  were decoded (percentiles and the count of late ones). The host grows the margin by 1 ms as soon as more than 1 % of a second's
  frames were late and shrinks it by 0.5 ms after 10 clean seconds, between 3 and 9 ms, never faster than the phase lock's own slew
  (2 ms per half second). It holds still when the picture is not steady: the first 20 s of a stream, a game under its cap (loading),
  a miss storm on the client, a display rate change, a reconnect; with no report for 2 s it returns to 7 ms. Whether this is better
  than a well-chosen constant is what the margin sweep decides: if one constant wins in every game, ship the constant.
- **No standing second frame** (`catchup = sustained`), so a stall does not leave 10 ms behind that never drains.
- **Nothing heavy on the client's render thread**: no allocation, lock, string formatting or logging there. It writes numbers into
  preallocated arrays; a background queue computes the window and sends it. `queryDeviceAnchor` is the known source of misses: no new
  calls to it.
- **Defaults are today's behaviour**: a client that receives no message behaves as 1.4.x.

Hard limits that do apply during measurement, because breaking them loses the session instead of producing data:

- The app must not be terminated: Apple ends an app that takes too long to submit. No wait may exceed one display period beyond
  today's 8 ms.
- No crash, no loss of the stream, no restart of the streamer by the experiment. Never stop the owner's installed streamer.
- A baseline arm (every switch at today's value) is always one schedule change away.

## 7. Telemetry: what must be logged

All of it in the **release flavour**, as window lines in the game's run log (`logs\games\<exe>\*.log`), like the
`early hand-over experiment` line of 2026-11-25. No per-frame files. Every line carries the full set of switch values in force and
whether a switch changed inside the window.

**Client, one line every 10 s through the client log** (same route as `va_ticks`; the host parses it in `logs.rs` and copies it into
the game's log). Times are per render tick, on the headset's clock, in ms. Each list is p1, p10, p50, p90, p99.

```
va_time {"v":1,"t":10.0,"hz":100.0,"sw":{"sub":"in","wait":"fixed8","safety":2,"catchup":"off","order":"pick_first"},"n":1000,
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

**Client, once a second:** `va_slack {"n":100,"p1":..,"p10":..,"late":..,"miss":..}` (from `ready_k`). Sent in every arm, used by
the host only when `margin = auto`, so its distribution is known for the fixed margins too.

**Host, one line every 10 s:** `frame timing experiment: {json}` with: the switch values on both sides, the game and whether frame
generation is on and at which rate, the margin in force and its changes in the window, the phase lock's corrections (count and sum)
and its estimate, outputs ready after the boundary p50 / p95 per kind (rendered, generated; the accumulator exists on
`perf-early-handover`), outputs later than a period, game fps, stream fps, boundaries without a tracking sample, packets lost, the
GPU line, the ALVR statistics window (as today), and the last `va_time` window.

**Report script:** `harness/exp_timing_report.py`, modelled on `harness/exp_early_report.py` (branch `perf-early-handover`). Windows
grouped by game, generation state and switch values; the first two windows of each log and any window that contains or follows a
switch change left out; median and quartiles per group, and main effects per switch across the games.

## 8. How the sessions are driven

**The schedule.** The host reads `config\exp_schedule.json` and re-reads it when the file changes (checked at each 10 s window, no
restart). It is a list of arms, each a set of switch values and a duration, run in a loop. The host sends the client its switches
by message at the start of each arm.

**The operator.** During a session Claude pulls the `logs` folder from the 5090 install (as on 2026-11-25), reads the report, and
copies a new schedule file to the install to move to the next block or to repeat a thin group. The owner does nothing but play.
Copying a file is the only action on the install; the streamer is never stopped or restarted.

**Arm length.** 40 s: the first 10 s window is dropped (the phase lock and the client settle), three windows are measured. Switches
that change what Apple measures (`sub`) alternate slowly, every four arms, because how fast Apple adapts is unknown; watch `o_r`
drift inside those blocks and lengthen them if it has not settled.

**Coverage.** The owner's games change during the evening and each has its own frame generation state. A full cycle of a block must
fit in about 5 minutes so that every game segment of 10 minutes sees every arm twice. A block with more arms than that is split.

**Blocks**, in this order; the default schedule file loops A, then B, then C, and the operator can skip ahead:

| Block | Arms | Purpose |
|---|---|---|
| A | `tele` on / off, everything else at today's values (4 arms, 30 s each) | the recording changes neither misses nor repeats; the baseline numbers for H1, H3, H4, H5 |
| B | screening, 8 arms: a half-fraction over `wait` (fixed8 / deadline), `sub` (in / lean), `margin` (7 / 4), `early` (off / on), `catchup` (off / sustained) | main effect of each switch on `age` and on fresh ticks, in every game; settles H2 |
| C | margin sweep at the best client setting from B: 3, 5, 7, 9 ms and `auto`, plus `phase_lock` off at 7 | the curve of latency against repeats; whether `auto` beats the best constant; H3 and H5 |
| D (if time) | the corners B and C did not explain: `wait = none`, `safety` 1 / 3, `catchup = always`, `order` | the cost of each net |
| E | the candidate against today's values, two arms | the confirmation of section 9 |

## 9. What counts as success (block E, the candidate against today)

Per game and generation state, on windows (median and quartiles; a difference inside the quartiles is no result):

- **Result:** `age` p50 (frame handed to the decoder to display) lower by 5 ms or more. Between 2 and 5 ms: keep only if every
  guard below is flat. Under 2 ms: drop.
- **Guards, all must hold in every game played:**
  - share of fresh ticks not lower (inside the quartiles, and within 0.2 points);
  - deadline misses per 1000 frames not above today's upper quartile;
  - runs of 3 or more non-fresh ticks (a hitch of 30 ms and more): no more of them, and `run_max` not larger;
  - `age` p99 not higher (a faster median with a slower tail is a hiccup machine);
  - queue drops and packets lost unchanged;
  - the owner saw no new hitch and no trembling in the candidate's arms.
- **For `margin = auto` also:** fewer than 6 margin changes a minute in steady play, and fewer phase lock corrections per minute
  than with the fixed 7 ms.

The campaign as a whole also succeeds if it shows there is nothing to gain: under 3 ms available after block A, or no arm that
passes the guards. That closes the question with numbers.

## 10. Steps

Branches: host on a new branch from `perf-early-handover` (it carries the 10 s experiment line, the in-session alternation and the
package scenarios); client on a new branch from `1.4-foundation`. Check first what build is on TestFlight and start from that.
Engine files change only on that branch (the owner authorised experiment branches; ask again before anything reaches `main`).

| Step | What | Done when |
|---|---|---|
| S0 | Client: the `va_time` / `va_slack` recording and every client switch of section 5. Host: the schedule file with hot reload, the switch messages, live `margin` / `phase_lock` / `early`, `margin = auto`, the experiment line, the report script. | It builds; in the client's Linux harness (`tools/protocol-harness`) every switch toggles live without a crash or a stuck render loop; a release-flavour lab run with the mock headset walks a schedule and writes the lines; with injected slack reports `auto` backs off on a late burst and returns to 7 ms when reports stop. |
| S1 | TestFlight build to the internal group (owner's go), streamer experiment bundle (owner installs it). | Owner confirms both are installed. |
| S2 | **Campaign, one or two evenings.** Blocks A to D while the owner plays; the operator pulls logs and moves the schedule. | Every switch has its main effect measured in at least one game with frame generation and one without, 20 windows or more per value. |
| S3 | Choose the candidate from the report; run block E on a further evening, or the same one if time allows. | Section 9 read out per game. |
| S4 | TestFlight back to the previous build; the streamer back to the release. Record the outcome in `docs/PERFORMANCE_FINDINGS.md` and `docs/VISIONOS_TELEMETRY_LEARNINGS.md`. What passed is rebuilt cleanly, as defaults without settings and without the telemetry, for a normal release. | Owner's decision on what ships. |

What the owner does in a session: install what S1 names, play the usual mix for the evening, each game 10 minutes or more in
steady play, and say afterwards whether a hitch or trembling stood out and roughly when (the time is matched to the arm).

## 11. Risks

- Apple may not place O from our submissions at all (H2 false): then `sub = lean` does nothing and the campaign is about the margin.
- Waiting until K uses the slack that hides `queryDeviceAnchor` spikes: misses could rise. `safety` and the p99 of the work are the
  two numbers to tune.
- A control loop across Wi-Fi can oscillate; the October Auto controller was removed for that. If `auto` is not calm in block C, a
  constant ships.
- Many arms over mixed games thin the data: the operator's job is to watch the window counts per group and repeat what is thin,
  not to add arms.
- The pacing rig of November (`rig_pacing`, `rig_client`, on the 3080 Ti's Arch WSL) predicted the 1.3.9 result within 0.3 points, but
  it has not been touched since 2026-11-11 and predates 1.5. Use it if it revives in an hour; do not rebuild it for this.

## 12. Decided, and still to confirm

Decided by the owner (2026-11-26): one temporary TestFlight build carries every switch; the streamer drives them; hiccups during
measurement are acceptable; the games are the usual mix; TestFlight returns to the previous build afterwards.

To confirm when the work starts: the TestFlight group (assumed: the internal group only, as for 1.4.0), and the owner's go for the
upload itself.

## 13. Pointers

- Findings of 2026-11-25/26 and the logs: `docs/PERFORMANCE_FINDINGS.md`; `private/logs/exp-early-handover-2026-11-25`,
  `private/logs/exp-early-handover-2026-11-26-150mbps`.
- Client telemetry history, Apple's render pipeline notes, the 1.3.9 screening (a half-fraction over four switches, the model for
  block B): `docs/VISIONOS_TELEMETRY_LEARNINGS.md`.
- Phase margin history (3 to 7 ms) and the arrival spread: `docs/PERFORMANCE_FINDINGS.md`, section 2026-10-17.
- Experiment branches of 2026-11-25 (local worktree `openxr-perf`): `perf-early-handover`, `perf-experiments`.
- Client: `ALVRClient/Renderer.swift` (`renderFrame`), `ALVRClient/EventHandler.swift` (`pollFrameForDisplay`, `reportTicksIfDue`,
  `applyReservedMessage`), `tools/protocol-harness`, `.github/workflows/testflight.yml` (dispatch only).
- Rules: the engine is off limits on `main` without asking; ask before pushing, releasing or uploading to TestFlight; never stop the
  owner's installed streamer on the 5090.
