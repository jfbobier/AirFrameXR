# Plan: end-to-end frame timing experiment (streamer + visionOS TestFlight)

Written 2026-11-26; revised the same day after reading the client code and the saved field logs (section 2) and with the owner's
simplified terms (section 1). Nothing in this plan is built. It follows the measurements of 2026-11-25/26
(`docs/PERFORMANCE_FINDINGS.md`, section of that date) and the client telemetry work of 2026-11-08 to 11-12
(`docs/VISIONOS_TELEMETRY_LEARNINGS.md`). Read both before starting; this plan does not repeat them.

Evidence words follow `docs/progress.md`: say what was tested, how, and what was not. Hypotheses are marked as such.

## 1. What this is for, and the owner's terms

Today the chain is timed by constants and one controller: the host's phase lock aims its frames 7 ms ahead of the headset's pick,
the client waits up to 8 ms for a late frame, and Apple's compositor decides the rest. The experiment finds how much of the time
between a frame's arrival at the headset and its display (about 43 ms) can be removed, and which settings do it without adding
repeated frames or hitches.

Setup and judge: RTX 5090, Apple Vision Pro at 100 Hz, dedicated access point, the owner's usual mix: modded and native games, with
frame generation (Dead Space x2, Gears of War: E-Day x3) and without (AC Black Flag with its mod, Cyberpunk 2077, Mortal Shell 2).

**The owner's terms (2026-11-26):**

- **An experimental bundle.** The streamer is built from an experiment branch and is not a shipped streamer: every engine change the
  experiment and its logging need is allowed there (host, shim, frame path). Nothing of it reaches `main` without a separate decision.
- **A temporary TestFlight build** of the client; afterwards TestFlight goes back to the previous build.
- **The owner puts the headset on and plays.** The settings change in the background and tune themselves; the owner gives his
  impressions. Nothing to do during play, no buttons, no instructions per game.
- **Hiccups during the measurement are acceptable.** A hitch avoided must come from a good design choice, not from caution in the test.

## 2. Where we start

### 2.1 The legs (2026-11-25/26, medians of 10 s windows, Dead Space x2, 275 Mbps)

| Leg, as the headset reports it | ms | What it is |
|---|---|---|
| Game (tracking sample to the frame leaving the game) | 6.3 | host |
| Encode | 3.0 | host |
| Network | 7.6 | not measured: ALVR subtracts the other legs from the total (telemetry learnings, finding 11) |
| Decode | 6.0 | client |
| "Decoder queue" | 10.2 | decode end to `alvr_report_compositor_start`, which runs after the pick and the drawable query (`alvr/client_core/src/statistics.rs`) |
| "Client render" | 6.7 | compositor start to the end of the GPU work |
| "Vsync queue" | 20.5 | end of the GPU work to the presentation time: `alvr_report_submit` is called from the command buffer's completion handler (`Renderer.swift`, `renderFrame`) |

- The window's total is a median of per-frame totals one display period apart (57 or 67 ms); the legs are means. Judge on the legs,
  or on the mean of the total (the sum of the leg means), never on the median total.
- **Apple's part of the vsync queue is already measured.** On TestFlight 1.3.8 (64) the client's timeline lines put the pick about
  27 ms before the presentation and the rendering deadline about 17.7 ms before it (telemetry learnings, the headset A/B entry of
  2026-11-10). So about 17.7 ms of the 20.5 ms is Apple's (deadline to presentation) and about 2.8 ms is GPU slack: at most about 3 ms
  of the vsync queue is ours to remove.
- Same source: the pick runs about 9.3 ms before the rendering deadline, while the CPU work after the pick takes 1.5 ms on average
  (single frames up to 14 ms). How Apple places the start of that window is the second candidate (H2).
- None of the waits moved with the bitrate (275 or 150 Mbps), the game, the generation rate or the early hand-over (2026-11-25/26).
- Repeated display periods are about 1 % in every arm of 2026-11-25. A shipped design must not worsen that.

### 2.2 What the saved field logs say (read 2026-11-26)

Debug-flavour sessions with per-frame client statistics (`client_frames.csv` in `private/logs/<session>/`), Vision Pro at 100 Hz.
First 10 % of each file left out. "Over a period" = the decoded frame waited more than 10.5 ms for its pick.

| Session | Frames | Mean wait, ms | Over a period | Longest run | Of those in runs of 10+ | Mean wait without runs of 10+ | Runs of 10+ per 1000 frames |
|---|---|---|---|---|---|---|---|
| Gears E-Day x3, 1.4.1, 2026-11-21 | 12,231 | 11.4 | 54 % | 171 | 56 % | 8.3 | 11 |
| Hellraiser, 1.4.2, 2026-11-22 | 5,670 | 10.4 | 50 % | 288 | 72 % | 6.8 | 8 |
| Hellraiser, 1.5.0 test, 2026-11-22 | 7,533 | 10.5 | 54 % | 215 | 94 % | 5.5 | 9 |
| AC Black Flag, 2026-11-24 | 30,375 | 7.3 | 30 % | 117 | 60 % | 5.5 | 6 |

**Finding A: a standing second frame.** The client picks one frame per display period, so a decoded frame can wait more than a
period only if another frame was ahead of it in the queue, or if the resubmit rule showed the previous frame again while it waited
(`EventHandler.pollFrameForDisplay`). These waits come in runs of up to 288 consecutive frames (about 3 s). The pick takes the oldest
frame, and catch-up has been off since 1.3.9, so once a second frame is queued it stays until the stream misses a tick. Taking 10 ms
off every frame inside the runs of 10 or more lowers the mean wait by 1.8 to 5 ms. That is the upper bound for a pick that ends such
runs, before its cost: each run ended skips one frame. The first version of this plan assumed no standing second frame (its H4); the
logs say otherwise.

**Finding B: the phase lock sits 3 to 7 ms below its target in every field log.** 19 run logs with phase lock lines, 2026-11-14 to
11-24: the cumulative slide is a straight line against time (R² 0.986 to 0.999), at −0.20 to −0.69 ms per second, and nearly every
correction is negative. The median estimate is 0.0 to 3.7 ms against a target of 7. **The effective margin in the field is about 0 to
4 ms, not 7**, so a margin sweep with today's lock would sweep a target that is never reached.

**The cause (found in the lab, 2026-11-26, section 10.1): the 7 ms target cannot be reached.** `phase.rs` takes each wait modulo one
period and folds the cluster's start into the half period on either side of the tick, so a start more than half a period before the
tick reads as "already late". Replaying its estimator on waits with a known position: exact up to 5 ms at 100 Hz, then a true 6 ms
reads −4 and a true 7 ms reads −3. The target was 3 ms until 2026-10-17 (reachable) and is 7 ms since. With 7, nearly every
evaluation reads too late: 98.2 % of the 26,965 corrections in the saved field logs read below the target. Each one slides the grid
earlier, about −1.5 ms every 2.5 s, which is the −0.2 to −0.7 ms/s of every log. The rest of the spread comes from two smaller
effects:
- **The grid's period is not the display's.** Measured from the client's tracking timestamps over the whole file, the display period
  was 9.9941 ms (100.06 Hz, Hellraiser 1.5.0), 9.9995 ms (Gears) and 10.0009 ms (AC Black Flag). The host's grid ran at 10.0 ms
  (1.5.0, AC Black Flag) and at 10.0145 ms in the Gears session (`vision.rs` `RateEstimator`: a median of 90 gaps, kept for the whole
  session once a later median agrees within 1 %). The host's and headset's clocks also differ, by 30 to 180 ppm.
- **The input reads the wrong way in two cases.** A frame the 8 ms wait caught reports a wait of about 0 ("on the tick"), however late
  it came. A frame standing behind another reports only its excess over a period ("late").

**The two findings have one cause (hypothesis, consistent with every number above).** Sliding earlier for ever makes the host deliver
about 0.05 % more frames than the display shows, one every 20 s or so. That is the field's queue-overflow rate (0.05 %, telemetry
learnings finding 10). Each surplus frame stands in the client's queue, adding a period to every frame behind it, until an overflow
drop or a gap in the stream removes it; a Wi-Fi stall released as a burst starts runs as well. Standing behind another frame, a frame
is never late, which explains the history: when the target went from 3 to 7 ms on 2026-10-17, repeated periods went from 1.2–2.3 %
to 0.5 % and the owner found it "much better". That smoothness was most likely the host running a frame ahead. The experiment has to
keep it while it takes the standing frame away.

**Statements made on 2026-11-26 that this version corrects:**

- "5 to 10 ms of slack before Apple's deadline" was a guess. The gain is not in submitting later.
- The first version placed `alvr_report_submit` before the encode and called the split of the vsync queue unknown. It is reported
  when the GPU work ends, and Apple's share was measured on 1.3.8 (above).
- "The host aims 7 ms ahead": that is the lock's target, not where the frames land (Finding B).
- The 2 ms "network" gain at 150 Mbps is a change in the remainder, not a measured transit.

## 3. How the chain is timed today

**Host** (`tools/host/src`):

- A clock thread runs a display grid at the period in force (the session's nominal rate, or the rate estimator's value). The game's
  release, the pick, the encode and the send follow it (`main.rs`, display clock).
- `phase.rs` (the phase lock) slides that grid. Its input is the client's per-frame `decoder_queue`, taken modulo one period. It works
  on the cluster of phases after the largest empty gap, and its estimate is the median of the last three 10th percentiles. It acts when
  the estimate is more than 1.2 ms off the target (`AIRFRAMEXR_PHASE_MARGIN_MS`, 7). The step is 0.3 x the error, at most 2 ms, and
  the window restarts after each step. The target is read from the process environment, so changing it needs a restart.
  `AIRFRAMEXR_PHASE_LOCK=0` disables it.
- Arrival spread: 8 to 9 ms of a 10 ms period (2026-10-17). With generation the outputs alternate: rendered ones are ready 2.6 to 3 ms
  after the boundary, generated ones 3 to 8 ms after it.
- Messages to the client: `ServerControlPacket::Reserved(String)`, JSON `{"airframexr":1,...}` (`vision.rs`, patch
  `tools/patches/headset-view-message.patch`). Live example: `client_sharpen on|off`.
- Messages from the client: the per-frame ALVR statistics packet (a fixed struct) and log lines. `logs.rs` already parses the
  `va_ticks {json}` line that both clients send every 10 s.

**Client** (visionOS, branch `1.4-foundation`, local checkout `external/alvr-visionos-1311`; remotes `origin` =
jfbobier/airframexr-visionos, `kteque` = kteque/alvr-visionos where the TestFlight workflow runs). `Renderer.swift`, `renderFrame()`:

1. `queryNextFrame`, `predictTiming`, `startUpdate` / `endUpdate`.
2. Wait until Apple's **optimal input time** (O).
3. `startSubmission`.
4. `EventHandler.pollFrameForDisplay(timeout: 0.008)`, the pick:
   - if the frame shown last has not completed on the GPU yet and fewer than 2 frames are queued, it shows that frame again
     (resubmit);
   - otherwise it takes the oldest queued frame (catch-up off since 1.3.9; the queue holds at most 2 decoded frames);
   - otherwise it spins for up to 8 ms waiting for a frame, then reprojects the last one.
5. `queryDrawable`, then `alvr_report_compositor_start`, which ends the "decoder queue".
6. `WorldTracker.sendTracking` (contains `queryDeviceAnchor`, which spikes 3 to 14 ms now and then), then the display-time anchor query.
7. Encode, `commit`, `endSubmission`. Apple's **rendering deadline** (R) must come after `endSubmission`: a later one is a deadline
   miss and a doubled display period.
8. In the command buffer's completion handler: `alvr_report_submit` with `presentationTime - now`, the "vsync queue".

Apple's **presentation time** (P) is about 17.7 ms after R (section 2.1). Streamer messages arrive in `EventHandler.applyReservedMessage`.
Lines to the streamer go through the client log (`reportTicksIfDue`, the `va_ticks` line). Core changes are patches in `patches/`,
applied by `scripts/apply_core_patches.sh`.

## 4. Hypotheses, and the number that settles each

| | Hypothesis | Settled by |
|---|---|---|
| H1 | R to P is about 17.7 ms and is Apple's (measured on 1.3.8; the client has changed since). | one histogram of `P - R` in stage C |
| H2 | Apple places O from the duration of our submissions (step 3 to step 7). Our wait for a late frame sits inside that window, so Apple starts us earlier than our real work needs (9.3 ms of window for 1.5 ms of mean work). | `R - O` with the wait inside and outside the submission (`sub`) |
| H3 | Standing runs come from surplus frames (today's lock slides the host ahead) and from bursts after a stall; they last until a drop or a gap. Ending them on the client (`catchup = sustained`) or no longer producing them (a lock that reads a standing frame as early) wins most of Finding A's 1.8 to 5 ms, at the cost of one skipped frame per run. | depth at pick, runs per 1000 frames, `age` and fresh ticks with `catchup` off / sustained and `lock` v1 / v2 |
| H4 | A lock that reads the wait as it is (v2: no fold, over a period = early, near 0 = late) or the client's signed slack (v3) settles at its target, and a rate term then removes the residual steps. | slide per second near 0, estimate within ±1 ms of the target, `lock` v1 / v2 / v3 |
| H5 | With a lock that holds its target, the target trades latency against late frames along a curve. A working lock at 7 ms parks frames 7 to 16 ms before their pick (a mean wait near 11 ms, no better than today's 10.5 with standing runs): the gain is in a lower target, and v3's signed input allows one below 0 (the 8 ms wait as the net). | the target arms of stages A and C, with the deadline misses as its guard |
| H6 | The smoothness the owner gained on 2026-10-17 came from the standing frame. A working lock reaches the same share of fresh ticks at a lower latency only if its target is set on the upper end of the wait (90th percentile), not the 10th. | fresh ticks and the owner's impressions, v1 at 7 against v2/v3 at the chosen target (stages B and E) |

If H1 holds and H2 does not, the vsync queue cannot be reduced and only the client's queue and the margin remain.

**Expected size, stated before any work:**
- The standing runs: 2 to 5 ms of mean wait at best, before the cost of the skipped frames.
- H2: 0 to about 5 ms.
- The margin: unknown until the lock holds its target.
- The vsync queue: under 3 ms of it is ours.
- Head rotation is already corrected at display time, so a gain shows on hands, controllers and game response, not on head stability.

## 5. The switches

Every switch is live, is set by the streamer, and is reported back in every telemetry window. The first value is today's behaviour.
It is also what the client does when it receives no message, and what a stock client keeps.

The **latest safe pick** K for a tick = R minus the client's own CPU work (p99 of pick to `endSubmission` over the last 10 s) minus
2 ms. With the 1.3.8 numbers, K is about O + 3 ms: a deadline wait is expected to be *shorter* than today's 8 ms, not longer. Today's
wait already reaches past the safe point (finding 17: misses 0.6 -> 1.0 per 1000 with the 8 ms wait).

| Side | Switch | Values | What it tests |
|---|---|---|---|
| client | `catchup` | `off`, `sustained` | sustained = drop the older frame once the queue has held 2 frames for N picks in a row (N from the lab, start at 10) (H3) |
| client | `wait` | `fixed8`, `deadline` | deadline = wait until K instead of a fixed 8 ms: fewer misses, perhaps more repeats |
| client | `pick` | `oldest`, `target` | target = with two frames queued, the newest that is not early is shown and older ones whose tick has passed are dropped; the pipeline's distance in ticks is the 10th percentile of the last 64 shown (WiVRn, Steam Link: `docs/IDEAS_FROM_OTHER_STREAMERS.md` 3.1). The rule with no N that `sustained` approximates (H3) |
| client | `sub` | `in`, `lean` | lean = the wait moves before `startSubmission`, so Apple measures only real work (H2). Only together with `deadline`, which caps it |
| host | `lock` | `v1`, `v2`, `v3` | v2 = the wait as it is (no fold), its percentile taken over waits under a period (a frame standing behind another is left out: the grid cannot empty a queue), same steps and limits: works with any client (H3, H4). v3 = v2 steered by the client's signed slack (`va_slack`) when it reports, else v2 (H4, H5). `AIRFRAMEXR_PHASE_LOCK_V`, live through the schedule. Off was measured in the lab: 11.6 % repeated ticks, not an arm |
| host | `rate` | `lock`, `ff` | ff = the grid's period fed forward from the headset's tick timestamps (the mean period-sized gap, times the clocks' ratio from the lower envelope of arrival minus timestamp over a minute), no loop; the lock keeps only its steps (`AIRFRAMEXR_RATE_FF=1`; Sony's patents, Sunshine PR 5282: `docs/IDEAS_FROM_OTHER_STREAMERS.md` 3.4). lock = v2/v3's integral rate term, which ran to its limit in every lab batch it ran in (10.1): ff is the one to keep |
| host | `target` | 7 ms (v1), then the lab's values | v1: the folded 10th percentile, as shipped. v2 / v3: the steered percentile of the wait or the slack, the 90th by default (`AIRFRAMEXR_PHASE_STAT`): the client's wait censors the low end, and a 10th percentile at 7 ms cannot be held at 100 Hz with an 8 to 9 ms spread without frames queuing a period early (10.1). `AIRFRAMEXR_PHASE_MARGIN_MS`, live through `phase::set_margin_ms` (H5). v3 at a low percentile (p1) of the signed slack is WiVRn's rule seen from the headset: the target is the share of frames allowed to come late, and it sits near the end of the wait (3.3 of the ideas document) |

Telemetry is always on. The first stage alone alternates it with off, to show that the recording costs nothing.

**Dropped from the first version, and why:**
- `safety` is fixed at 2 ms.
- `order` was optional.
- `catchup = always` was measured: −1.1 points of fresh frames (finding 17).
- `early` brought no gain the headset counts (2026-11-25).
- `margin = auto`: the tuning searches for a constant. An automatic margin would be a later decision, and only if the best constant
  differs by game.

**Not a switch** (each has failed before): the tracking-sample path (no re-stamping, no predicted samples, no change of when the
client sends its sample), translation correction, the display rate request, the just-in-time game release.

## 6. Design choices that should make the result hiccup-free by themselves

These describe the candidate that ships, not limits on the measurement. During the campaign any arm may hitch; that is data.

- **No standing second frame**, without the 1.3.8 rule: drop only after a sustained depth of 2. A one-frame wobble keeps its frame.
- **A lock that holds its target**, so the margin is what it says, with the same step limits as today (no faster slews).
- **A net that cannot miss Apple's deadline**: the wait ends at K, not at a fixed time.
- **Nothing heavy on the client's render thread**: no allocation, lock, string formatting or logging there. It writes numbers into
  preallocated arrays; a background queue computes the window and sends it. `queryDeviceAnchor` is the known source of misses: no
  new calls to it.
- **Defaults are today's behaviour**: a client that receives no message behaves as 1.4.x.

Hard limits during measurement (breaking them loses the session instead of producing data):

- The app must not be terminated: Apple ends an app that takes too long to submit. No wait may exceed one display period beyond
  today's 8 ms.
- No crash, no loss of the stream, no restart of the streamer by the experiment. Never stop the owner's installed streamer.
- Today's values are always one schedule change away.

## 7. Telemetry: what must be logged

All of it goes into the bundle's run logs (`logs\games\<exe>\*.log`) as window lines, like the `early hand-over experiment` line
of 2026-11-25. Every line carries the stage, the arm, the full set of switch values in force, and whether a switch changed inside
the window. No per-frame files. The client restarts its window when a switch message arrives, so client windows line up with arms.

**Client, one line every 10 s through the client log** (same route as `va_ticks`; the host parses it in `logs.rs` and copies it into
the game's log). Times are per render tick, on the headset's clock, in ms. Each list is p1, p10, p50, p90, p99.

```
va_time {"v":1,"t":10.0,"hz":100.0,"sw":{"catchup":"off","wait":"fixed8","sub":"in"},"n":1000,
 "o_r":[..],      // R - O: how long Apple gives us (H2)
 "r_p":[..],      // P - R: Apple's own part (H1)
 "work":[..],     // pick -> endSubmission: our CPU work
 "sub":[..],      // startSubmission -> endSubmission: what Apple sees
 "slack_r":[..],  // R - endSubmission: negative = deadline miss
 "ready_o":[..],  // O - decoded: arrival slack against the tick (negative = arrived after O)
 "ready_k":[..],  // K - decoded: arrival slack against the latest safe pick (negative = missed its tick)
 "to_pick":[..],  // decoded -> pick (the wait without the drawable query)
 "draw_q":[..],   // pick -> compositor start (the drawable query)
 "age":[..],      // P - frame handed to the decoder: the result we want smaller
 "track":[..],    // sendTracking duration (the spike source)
 "fresh":..,"resubmit":..,"reprojected":..,"none":..,          // as va_ticks
 "waited":..,"late":..,"depth2":..,"drops":..,"cu_drops":..,   // picks that waited; frames that missed; picks at depth 2; queue drops; catch-up drops
 "runs":..,"run_p50":..,"run_max":..,"run_late":..,"run_resub":..,  // standing runs (depth 2 for 10+ picks): count, length, started by a late frame / by a resubmit
 "miss":..,"doubled":..,"stale_max":..,"stale3":..}            // deadline misses; display intervals over 1.5 periods; longest run of non-fresh ticks; runs of 3 or more
```

**From the survey of other streamers (`docs/IDEAS_FROM_OTHER_STREAMERS.md`), measurements only:**
- **First to last packet of a frame**, on both ends (p50 / p99 per window): settles whether the 7.6 ms "network" remainder is air time
  or our send path (4.7). Needs a line in `client_core`'s packet reassembly (a patch in `patches/`) and in the host's send loop.
- **A clock offset from timestamps already in the traffic** (Steam Link's minimum of arrival minus send, both ways, exchanged once a
  second on the message channel): the network leg measured instead of a remainder (3.5). Only if cheap; nothing depends on it.
- Per-kind lead (generated against rendered outputs, 4.1): sized first from the host's existing per-kind "ready after the boundary"
  figures; no new telemetry.

**Client, once a second:** `va_slack {"n":100,"p1":..,"p10":..,"p50":..,"late":..,"miss":..}` (from `ready_o`). It is lock v2's input,
and it is sent in every arm, so its distribution is also known under v1.

**Host, one line every 10 s:** `frame timing experiment: {json}` with:
- the stage, the arm, and the switch values on both sides;
- the game, and whether frame generation is on and at which rate;
- the lock: version, target, the period in force, the display period measured over the long span, corrections (count and sum),
  estimate;
- outputs ready after the boundary, p50 / p95 per kind (rendered, generated; the accumulator exists on `perf-early-handover`), and
  outputs later than a period;
- game fps, stream fps, boundaries without a tracking sample, packets lost;
- the GPU line, the ALVR statistics window (as today), and the last `va_time` window.

**Report script:** `harness/exp_timing_report.py`, modelled on `harness/exp_early_report.py` (branch `perf-early-handover`). Windows
are grouped by game, generation state and switch values. The first two windows of each log, and any window that contains a switch
change, are left out. It gives the median and quartiles per group, and the main effects and two-switch interactions across the games.

## 8. How it runs: the owner plays, the streamer tunes

Revised 2026-11-26 after the lab (10.1): lock v2 needs nothing from the client, so the campaign starts with the streamer alone.

**The mechanism (built on `exp-frame-timing`, `tools/host/src/exp_timing.rs`).** The bundle reads `config\exp_schedule.json`: a list of
stages, each a list of arms that alternate every `arm_s` seconds for so many minutes of play. Only windows with a game streaming
count, and the next stage starts by itself. The progress is saved in `config\exp_state.json`, so the next evening resumes where the
last one stopped. The file is read again whenever it changes, at each 10 s window, without a restart: copying a file is the only
action ever taken on the install, and the streamer is never stopped. Each arm sets the lock's version and target and the client's
switches (the headset view message's `exp` object, which a stock client ignores). One `frame timing experiment` line per window
carries the stage and the arm. **Evening 1 is compiled in** (owner, 2026-11-26: "hard code the experiment in the bundle so I just unzip and use it"): a streaming
host started from an install (the launcher's `--install-dir --shim --daemon`) runs it when there is no schedule file; a file overrides
it, and `{"off": true}` in `config\exp_schedule.json` gives the release behaviour. Lab hosts run nothing unless told. The stages do not
choose their own winner: Claude reads the report between evenings and writes the next schedule (or the next bundle).

**Evening 1: the streamer only** (the experiment bundle; the TestFlight client already installed, no upload).

| Stage | What runs | For | Settles |
|---|---|---|---|
| A | today (v1 at 7 ms) against v2 at 4 ms with the rate fed forward, and v2 at 6 ms with it (the lab's best and a cautious one, 10.1), 40 s each | 40 min of play | H4 in the field (does the slide stop?), H5 for the host's part |
| B | the better v2 arm against today, 3 minutes each, alternated, not announced | the rest of the evening | the owner's impressions, section 9 |

**Evening 2: with the temporary TestFlight client** (`va_slack`, `catchup = sustained`, `wait`, `sub`; S1).

| Stage | What runs | For | Settles |
|---|---|---|---|
| C | evening 1's best alone, with `pick = target`, and with `catchup = sustained` (fewer drops), 40 s each | 40 min | H3 on the client: the lab's largest gain, and the dropped frames' cost |
| D | `sub` in / lean and `wait` fixed8 / deadline at C's best, switched every 4 arms of 40 s (how fast Apple adapts is unknown: watch `o_r`) | 30 min | H2 |
| E | the finalist against today, 3 minutes each, not announced | the rest | section 9, impressions |

A 40 s arm is three measured windows after one dropped window (the lock and the client settle). The arms are too short to feel, so
the owner's impressions count only in the 3-minute stages. Each minute in the log carries its stage and arm, so a remark like
"around 22:10 the hands felt late in Gears" can be matched afterwards.

**What the owner does:** install the bundle (and on evening 2 the TestFlight build), then play the usual mix, each game for
10 minutes or more in steady play. Afterwards, say what stood out (a hitch, trembling, hands late or early, anything) and roughly when.

## 9. What counts as success (stages B and E, the finalist against today)

Per game and generation state, on windows (median and quartiles; a difference inside the quartiles is no result):

- **Result:** `age` p50 (frame handed to the decoder to display) lower by 5 ms or more, or the mean of the total latency lower by the
  same amount. Between 2 and 5 ms: keep only if every guard below is flat. Under 2 ms: drop.
- **Guards, all must hold in every game played:**
  - share of fresh ticks not lower (inside the quartiles, and within 0.2 points);
  - `age` p99 not higher (a faster median with a slower tail is a hiccup machine);
  - rare events, **pooled over all windows of an arm per game** and compared as rates with their Poisson spread: deadline misses per
    1000 frames, runs of 3 or more non-fresh ticks (`stale3`, a hitch of 30 ms and more), catch-up drops per 1000 frames. None may rise
    beyond its spread. Counted per window they are too rare to decide anything (about 30 misses per arm per game);
  - queue drops and packets lost unchanged;
  - the owner saw no new hitch and no trembling in the finalist's blocks.

The same rule, without the owner's part, is how Claude picks the arm that goes on between stages.

The campaign also succeeds if it shows there is nothing to gain: no arm passing the guards, or under 3 ms available after stage A.
That closes the question with numbers.

## 10. Lab work on the 3080 Ti before the headset

The pacing rig of 2026-11-10 runs the real host engine on Windows, and the real `client_core` with the client's pick in Arch WSL, with
a simulated display and network (telemetry learnings, session "the pacing rig"). It predicted the 1.3.9 result within 0.3 points. Its
work disk (`D:\WSL\work.vhdx`) is connected again as of 2026-11-26. Its host branch `rig_pacing` is from 2026-11-10 (before 1.5), and
it reproduced the lock's sawtooth then.

| | What | Done when |
|---|---|---|
| L1 | The rig on the experiment branch (1.5 engine + `perf-early-handover`); the rig client on the client's current pick | `rig_local_steady_100` runs and writes the window lines |
| L2 | Reproduce Findings A and B: the display at 100.06 Hz, and at 99.99 Hz, with the host grid at 10.0 ms | slide per second, estimate, runs per 1000 frames and mean wait near section 2.2's figures; if they are not, find what the rig misses before using it for anything else |
| L3 | Build lock v2 and `catchup = sustained` there, and choose N and the lock's constants | `age`, fresh ticks, drops and runs per arm, interleaved, n >= 3 |
| L4 | Dry-run the bundle: stages advance, messages reach the client, the lines are written, the report reads them; injected late bursts, a missing `va_slack`, a reconnect | every stage ends by its own rule in a compressed schedule |

The rig cannot answer H2 (it has no Apple compositor) and cannot give impressions.

### 10.1 Lab results (3080 Ti, 2026-11-26)

**L1, done.** Engine branch `exp-frame-timing` (worktree `~/openxr-timing`, from `perf-early-handover` plus `rig_pacing` merged), builder
root `VisionALVR_perf` (its VDXR is at the pin and clean; `VisionALVR`'s VDXR submodules differ from their commits by line endings
only, and the build script refuses that tree). Client branch `exp-frame-timing` (worktree `~/openxr-timing-client`, from `1.4-foundation`
plus `rig_client`): the rig compiles the current `EventHandler.swift` (8 ms wait, catch-up off). The rig's `client_core` now carries
the client's four core patches. It is built with a private Rust 1.99.1 (`~/work/toolchains`, unpacked from the pacman cache),
because the distro's Rust 1.100 no longer compiles the `ashpd` crate; the 11-10 library is kept as `libalvr_client_core.so.20261110`.
The protocol harness had not compiled since 1.4.0 (no `alvr_reserved_message` in its fake core); fixed on the client branch.

**L2: Finding B reproduced, Finding A only with Wi-Fi-like stalls.** Today's chain against a 100.06 Hz display, host grid at 10.0 ms,
1280x1120 per eye, 50 Mbps, xr_probe at 98 fps, one run of 155 s each:

| Run | Slide, ms/s (R²) | Lock estimate | Mean wait | Over a period | Runs of 10+ per 1000 | Longest | Repeated ticks |
|---|---|---|---|---|---|---|---|
| Field, Hellraiser 1.5.0 | −0.58 (0.999) | 3.2 | 10.5 | 54 % | 9 | 215 | – |
| Rig, smooth network | −0.56 (0.997) | 3.2 | 4.8 | 6 % | 1.7 | 85 | 2.2 % |
| Rig, 40 stalls a minute of 12–25 ms | −0.56 (0.99) | 2.6 | 5.7 | 17 % | 5.1 | 49 | 3.9 % |

- The lock's behaviour is the rate error under a stepping lock, reproduced to 0.02 ms/s.
- Standing runs need two frames to land close together: a stall released as a burst does that. The rig's network was smooth Gaussian.
- The rig's runs stay short because its game misses 2 % of the ticks (98 fps), and every gap drains the queue. The field session had
  99.3 % of ticks fresh, so its runs last.

**Root cause of Finding B, found here.** Lock v2 first kept the fold and added a rate term: the term ran to its limit (−3000 ppm) while
the steps went on, because moving the grid never moved the estimate. Replaying `phase.rs`'s estimator on waits with a known position
showed why (section 2.2): exact up to half a period, then the sign flips, so 7 ms cannot be reached.

**L3, one run of 155 s per arm (100.06 Hz display, 40 Wi-Fi-like stalls a minute; two runs of today's chain differ by 1.6 points of
repeated ticks, so only large effects count).**

| Arm | Lock slide, ms/s | Mean wait | Over a period | Runs of 10+ per 1000 | Repeated ticks | Deadline misses per 1000 | Dropped by catch-up |
|---|---|---|---|---|---|---|---|
| today (v1, 7 ms), two runs | −0.56 / −0.60 | 5.7 / 6.1 | 17 / 15 % | 5.1 / 5.1 | 3.9 / 2.3 % | 2.5 | 0 |
| lock off | – | 1.5 | 2 % | 0.2 | 11.6 % | 2.3 | 0 |
| today + `catchup = sustained` (10) | −0.57 | 5.1 | 6 % | 0.1 | 2.8 % | 2.5 | 70 |
| v2 without the fold, 10th percentile at 7 / 5 / 3 / 1 ms | −0.79 / −0.57 / −0.31 / +0.03 | 4.8 / 4.9 / 4.9 / 4.4 | 13 / 14 / 12 / 12 % | 3.4 / 4.4 / 4.0 / 2.8 | 2.3 / 2.4 / 2.3 / 2.5 % | 2.5 | 0 |
| v2 (10th at 7 ms) + `sustained` | −0.85 | 4.3 | 8 % | 0.0 | 2.6 % | 2.5 | 42 |

- **The lock must exist** (off: 11.6 % repeats), and **the 10th percentile of the wait is the wrong statistic.** The client's wait
  censors it at 0 (a frame it caught reads 0), and a 10th percentile at 7 ms is not reachable with an 8 to 9 ms spread without frames
  queuing a period early. Only the 1 ms target held (8 corrections in 155 s). v2 and v3 now steer the 90th percentile (the upper end,
  which the wait does not censor and a standing frame raises); batch 3 measures it.
- **`catchup = sustained` removes the standing runs** (5.1 → 0.1 per 1000) for about 4 to 6 dropped frames per 1000.
- **The v3 arms of this batch did not test v3**: the rig's render loop never sent `va_slack` (it did not call `reportTicksIfDue`,
  which `Renderer.swift` does), so v3 fell back to v2. Fixed; batch 3 repeats them.
- **Deadline model.** With the pick's wait counted against a 7.8 ms slack (model B), today's chain misses 8.3 per 1000 against 2.5
  under the 11-10 model. The headset's `slack_r` decides which is closer; until then, every arm that aims into the wait runs under both.

**Batch 3: the 90th percentile, with standing frames counted, cannot be steered either.** v2 aimed at 8, 6 and 4 ms read 9 to 11 ms
throughout and slid later for ever (+0.5 to +0.7 ms/s). The stalls keep 10 to 14 % of frames standing behind another, and moving the
grid cannot empty a queue: one frame still leaves per tick. v3 now received the client's slack (the batch 2 bug fixed). Its 10th
percentile, signed, settled at −0.9 against −2 ms, but its upper end carried the same standing frames. The sustained arm of this batch
had no effect: the host's message reset the rig's flag (fixed: a message without `exp` changes nothing unless an experiment ran).

**Batch 4: the client removes standing frames, the lock leaves them out, the rate is fed forward.** Same scenario, one run each; three
runs of today's chain give wait 5.7 / 6.1 / 6.0 ms and repeats 3.9 / 2.3 / 2.2 %.

| Arm | Lock slide, ms/s | Mean wait | Over a period | Runs of 10+ per 1000 | Repeated ticks | Deadline misses per 1000 | Dropped by the client |
|---|---|---|---|---|---|---|---|
| today, third run | −0.60 | 6.0 | 15 % | 5.2 | 2.2 % | 2.5 | 0 |
| today + `sustained` 10 | −0.60 | 5.1 | 6 % | 0.1 | 2.8 % | 2.5 | 64 |
| today + `pick = target` | −0.60 | 4.5 | 0 % | 0.0 | 3.0 % | 2.5 | 98 |
| v2, 90th percentile of the waits under a period, 6 / 4 ms | +0.20 / +0.55 | 4.7 / 4.4 | 14 / 14 % | 3.9 / 3.4 | 2.6 / 2.5 % | 2.5 | 0 |
| v2 4 ms + `sustained` 10 | +0.47 | 4.0 | 8 % | 0.1 | 2.8 % | 2.5 | 42 |
| v2 4 ms + `pick = target` | +0.18 | 2.1 | 0 % | 0.0 | 3.5 % | 2.5 | 141 |
| **v2 4 ms + `pick = target` + rate fed forward** | **+0.02 (R² 0.11)** | **2.0** | **0 %** | **0.0** | **3.3 %** | 2.5 | 141 |

- **The target pick removes standing frames completely**; `sustained` nearly (6 to 8 %) and drops fewer frames. Either moves the
  wait more than any lock setting.
- **The rate fed forward is the rate term to keep.** It measured the display's period at 9.99468 ms, and the two clocks' ratio at under
  1 ppm (one machine here; the field's 30 to 180 ppm is untested). With it the lock stops sliding (no trend, half the corrections) and
  holds near its target (3.3 against 4 ms). The integral rate term ran to its limit in every batch it ran in: dropped.
- **The lock's own statistic is noisy under stalls**: per 1 s window the median wait jumps between 0.1 and 8.5 ms. WiVRn's window is
  5,000 frames; a longer window is the next step if the headset shows the same.
- **Best so far:** 2.0 ms of wait against 6.0, standing frames gone, repeats inside today's spread. The cost is about 12 dropped frames
  per 1000: each is a skipped picture, not a repeated one, and whether it shows is for the owner to say. One run per arm, rig only
  (no frame generation, 98 fps game, one machine).
- **For the campaign:** the largest part of the gain needs the client (the target pick). Evening 1, the streamer alone, can still check
  in the field that the lock fix stops the slide: v2 at 4 ms plus the rate fed forward, against today. The client's pick then comes
  with the TestFlight build on evening 2.

**L4, the schedule dry run (one run).** A two-stage schedule ran by itself: stage A's two arms alternated every 20 s, and the client
logged its switch each time (catch-up 10 / 0, through the headset view message). Stage B began after 1.0 minute of play, and
`exp_state.json` recorded it. The experiment's window lines go to the game's run log, which a lab host does not have; they are now
also events.

## 11. Steps

Branches: host on a new branch from `perf-early-handover` (it carries the 10 s experiment line, the in-session alternation and the
package scenarios); client on a new branch from `1.4-foundation`. Check first which build is on TestFlight and start from that.

| Step | What | Done when |
|---|---|---|
| S0 | The client's telemetry and switches (sections 5, 7); the host's stage machine, live switches, lock v2, the experiment line, the report script; L1 to L4 | It builds. In the client's Linux harness (`tools/protocol-harness`) every switch toggles live without a crash or a stuck render loop. The rig passes L2 to L4. |
| S1 | TestFlight build to the internal group (owner's go for the upload); the experiment bundle (owner installs it) | Owner confirms both are installed |
| S2 | **The campaign, one or two evenings.** The owner plays; the stages run by themselves | Stage 4 reached, or a stage shows there is nothing to gain |
| S3 | TestFlight back to the previous build; the streamer back to the release. Record the outcome in `docs/PERFORMANCE_FINDINGS.md` and `docs/VISIONOS_TELEMETRY_LEARNINGS.md`. What passed is rebuilt cleanly, as defaults without settings and without the telemetry, for a normal release | Owner's decision on what ships |

## 12. Risks

- Apple may not place O from our submissions at all (H2 false): then `sub = lean` does nothing and the campaign is about the queue,
  the lock and the margin.
- Ending standing runs skips frames: at 6 to 11 runs per 1000 frames that is visible if the skips cluster. The pooled catch-up-drop
  rate and the owner's 3-minute stages judge it.
- A deadline wait is shorter than today's: fewer misses but perhaps more repeats. Stage 1 measures both.
- A lock across Wi-Fi can oscillate; the October Auto controller was removed for that. v2 keeps today's step limits; if it is not calm
  in the rig (L3), it does not go to the headset.
- The stage machine can pick a "best" setting from thin data. Each stage's end rule counts clean windows with and without generation,
  and the report shows the counts.

## 13. Pointers

- Findings of 2026-11-25/26 and their logs: `docs/PERFORMANCE_FINDINGS.md`; `private/logs/exp-early-handover-2026-11-25`,
  `private/logs/exp-early-handover-2026-11-26-150mbps`.
- Field logs of section 2.2: `private/logs/1.4.1-gears-eday-2026-11-21/20261121-105635`,
  `private/logs/1.4.2-hellraiser-15x-2026-11-22/debug/20261122-220131`, `private/logs/1.5.0-test-2026-11-23/debug/20261122-225004`,
  `private/logs/acbfvr-ftw1-2026-11-24/logs/debug/20261124-010041` (`client_frames.csv`, `host_events.jsonl` with `phase_lock`
  events), and the 19 run logs with `phase lock:` lines under `private/logs`.
- Client telemetry history, Apple's render pipeline notes, the 1.3.8 timeline figures, the 1.3.9 screening (the model for the 40 s alternations):
  `docs/VISIONOS_TELEMETRY_LEARNINGS.md`.
- Phase margin history (3 to 7 ms) and the arrival spread: `docs/PERFORMANCE_FINDINGS.md`, section 2026-10-17.
- Experiment branches of 2026-11-25 (local worktree `openxr-perf`): `perf-early-handover`, `perf-experiments`. Rig: `rig_pacing`
  (engine), `rig_client` (client, worktree `openxr-rig-client`).
- Client: `ALVRClient/Renderer.swift` (`renderFrame`), `ALVRClient/EventHandler.swift` (`pollFrameForDisplay`, `reportTicksIfDue`,
  `applyReservedMessage`), `tools/protocol-harness`, `.github/workflows/testflight.yml` (dispatch only).
- Host: `tools/host/src/phase.rs`, `main.rs` (display clock), `vision.rs` (`RateEstimator`), `logs.rs` (`va_ticks`).
- Rules: engine changes go on the experiment branch only (owner, 2026-11-26); ask before anything reaches `main`, before pushing,
  releasing or uploading to TestFlight; never stop the owner's installed streamer on the 5090.
