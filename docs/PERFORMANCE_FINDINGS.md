# Performance and smoothness: what we tried and what we learned

A map of the experiments behind the streaming pipeline, so nobody repeats the dead ends. Day-by-day detail with numbers is in
[progress.md](progress.md); the list of ideas that were never started is in [PERFORMANCE_NOTES.md](PERFORMANCE_NOTES.md). Setup of all
measurements: an RTX 5090 PC streaming to an Apple Vision Pro (ALVR client 20.14.1), plus a headset-less lab (mock client, probe app, simrig).

Targets that shaped every decision: consistency before latency (a stable 90 fps beats a lower average with spikes), frame rate before latency,
and no optimisation under about 1 ms.

## What the pipeline looks like now

Game (OpenXR/LibOVR via VDXR + our shim) -> shim composes the ring slot -> host frame queue (`drain` pick) -> foveation pass or full frame ->
NVENC (3 strips on 3 engines for the full-frame profile) -> ALVR sockets -> headset. Head pose comes the other way and is stamped on each frame.

## Kept (measured, in the product)

| What | Result |
|---|---|
| **Realtime GPU scheduling class for the host** | removes the encoder queueing behind the game's GPU work (+5.7 ms under load measured before). Needs the elevated host. |
| **GPU keepalive for lightly loaded encodes** | an idle-ish NVENC loop sat in P8 (video clock 555 MHz, encode about 30 ms against 9 ms at P0). Any encode timing on a lightly loaded GPU is suspect; check `gpu.csv` p-state and clocks. |
| **Dedicated vsync thread following the headset's display rate** | the shim predicts display time from it. A double-ticking bug (about 107 Hz) was found and fixed; the host now follows the client's rate (100 Hz on the Vision Pro in some sessions, 90 in others). |
| **Phase lock** (`phase.rs`) | steers when frames arrive relative to the headset's render tick; works modulo one display period, so it cannot see a whole extra period of queue. |
| **Shim compose index fix** | the shim composed frame F-1's image under pose F (an off-by-two inherited from the reference runtime): any sample bunching became about 0.9 deg of error at 90 deg/s, which is why a still head looked fine and a moving one trembled. Found with the simrig and `stamp_check.py`. |
| **Split encoding (3 strips on 3 NVENC engines)** | full 7104x3200 10-bit encodes in about 8 ms; the wizard now reports whether it engaged (engines and strips per frame). |
| **High-resolution waitable timers** | streamer CPU 0.62 to about 0.50 cores with no visible precision loss. |
| **Intake gate** (`video.intake_gate`, default on, IPC v19) | when the host's queue has stood 3 boundaries in a row (the game runs a frame ahead of the stream), the shim skips one release of the game and stays quiet until the gap has reached the host: every rendered frame is shown, no hole, one render period saved. Lab (GPU-bound scene): pose-to-display 32 ms against 43 in-order and 33 with the drop-based drain, same fps and repeats; neutral on steady and jittery scenes; spikes pay about 1 fps. Headset run pending. |
| **`drain` pick policy (fallback)** | in-order delivery plus one resync drop when a queue has stood 8 boundaries, at most once a second. On the headset (2026-10-13 morning, Cyberpunk) its earlier backoff (3 s doubling to 60 s) let the queue stand most of a run: hold 2.9 ms in one run, 9.9 ms in the next. It now only acts when the gate could not. |
| **Eye-height workflow (IPC v16)** | the head is published at y = 0 and the user's eye height is reported to games (`OVR_KEY_EYE_HEIGHT`). Fixes worlds sitting too low. Confirmed on the headset. |
| **Hand offset derived from VDXR's calibration** | fixes the reversed hand posture. Confirmed on the headset. |

## Tried and removed or left off

| What | What happened | Lesson |
|---|---|---|
| **Latency modes "low" / "auto"** (pick `newest`) | superseded 15-30% of frames in GPU-bound games (Cyberpunk streamed 77-80 fps of 94-95 rendered; Dead Space 59 of 87). | When frame times jitter, two frames land inside one period and "newest" throws one away. Removed; the in-order queue is the only user-facing behaviour. `VISIONALVR_PICK` stays a lab hook. |
| **Slot pacing v1-v6** (deliver exactly one frame per period at a fixed phase, hole filling) | each version regressed Cyberpunk: boundaries sat on the game's completion phase, frames were superseded just before their slot, bunching grew, the headset got about 60 fps with holes. | A compositor loop only works if the app is throttled at frame start; our `xrWaitFrame` released the app every tick regardless of GPU progress. Fixed later by the shim's GPU throttle and running start; the slot idea itself was dropped. |
| **Head stamp `newest` + exact homography warp, "compositor-like" mode** | duplicates fell to about 0 but the moving-head feel did not improve. | The frame deficit was not the root cause of the tremble. Removed. |
| **Predicting a missing head sample** (`video.predict_missing_head`, lab `OVRSHIM_INORDER_POSE`) | the predicted timestamp matched one of the client's own only 16% of the time (client timestamps jitter by tens of ns); the client looks the pose up by timestamp, so the picture came out misplaced (tilted, offset, even the head-locked title). | Never invent timestamps; the client's lookup is exact. Opt-in, off. |
| **`video.head_in_order`** (consume head samples in order, wait up to 8 ms) | lab: duplicates 244 -> 11 and 3.7 ms less pose-to-display. Headset: Cyberpunk 13% -> 7% duplicates, Dead Space unchanged at 20%, and a one-frame flash of the Cyberpunk warning page, shifted out of place. | Tracking packets arrive unevenly (gaps over 12 ms on 13-19% of samples) so the wait often times out; the lab does not capture that. Removed in IPC v18. |
| **Send pacing variants** (`vsync`, slot, `asap`) | no visible gain over `asap`; `asap` stayed. | |
| **Zero-copy encoder input** | about 0.2 ms. | Below the 1 ms bar; it stays on by default (`VISIONALVR_ZEROCOPY=0` disables) because it costs nothing. |
| **Lower render scale (0.75)** | did not help the moving-head feel. | The GPU budget is not what differs from SteamVR. |
| **HEVC QP-delta map** (foveal emphasis) | built and proven against a Python oracle, never judged on the headset. | Milder foveation plus a QP map is encode-bound on a one-engine GPU. Opt-in. |
| **Client queue drain** (`video.client_queue_drain`) | never fired in the lab; untested on the headset. | Opt-in. |
| **Release alignment alone** (`OVRSHIM_ALIGN_RELEASE`) | no effect on duplicates. | Lab only. |
| **Duplicate-timestamp re-stamp** (shim-side wait for the next sample + rotation, and host-side compositor stamping, 2026-10-13) | duplicates 0, shown fps = host fps, headset gaps halved in the heavy games; but the Vision Pro trembles with host-side stamping (the Quest 3 is clean either way) and the shim-side wait moves Wi-Fi jitter into the frame arrivals. | The visionOS client handles stamped frames differently; wait for a recompiled client rather than work around it in the streamer. Parked on branch `exp/restamp-2026-10-13`; the product sends every frame with its render sample's timestamp. |

## Findings that changed our mind

1. **Frames "sent but not displayed" are duplicate timestamps, not drops.** On the headset, displayed + same-timestamp frames equal sent (End of Abyss 175 + 5 = 180 per 2 s). The headset skips a frame whose head-sample timestamp equals the previous one. Cyberpunk and Dead Space produce 7-20% of them because the game reads its pose in bursts (interval p05 7.9 ms, p95 24.5 ms), so two reads fall inside one 10 ms tracking sample. Not fixable by a pick policy; the game side decides.
2. **ALVR's client predicts the head by `headset.max_prediction_ms`.** With 0 (left over from experiments) the client rotated frames by the whole 90-130 ms latency. The default is 100, the personal sessions use 150. This, not our pacing, explained "visibly worse than SteamVR" in one stretch.
3. **SteamVR + ALVR is the right reference, and it is not perfect.** Packet captures show perfect 90 Hz send spacing there (p95 11.7 ms) because the compositor re-presents on a miss, and 3.8% duplicate-timestamp sends. Same-scene totals: ours 117 ms against stock 107 ms; the game stage and the headset's vsync queue (about 20 ms, ALVR's client, identical in both) account for most of it.
4. **A standing queue costs a whole period, and only the game can drain it for free.** The in-order queue at a steady rate held about 11.7 ms between arrival and encode start. Dropping a frame on the host (`drain`) removes it at the price of a rendered frame; skipping one release of the game (the intake gate) removes it with nothing rendered lost. A single left-behind frame is not a standing queue (a late frame paired with an on-time one, both shown in order); three boundaries in a row are.
5. **Pose path is exact.** Follow-the-frames and follow-the-pose harnesses show every hop (client sample, host, shim, app, layer, decoded pixels) stamped with the pose the frame was rendered with, for ours and for stock.
6. **Averages hide the loading screens.** "Game ended" fps includes seconds of loading; judge steady windows (`encoder_stats` per 2 s).
7. **Lab and headset disagree about uplink timing.** The lab replays recorded head traces and uplink delays, which helped, but only the headset showed the in-order wait failing and the flash.

## How we measure

`harness/` (live, frames, pose_compare, stamp_check, baseline), `harness/simrig` (headset-less rig and judge), the debug session logs (`logs\debug\<time>`: `host_events.jsonl`, `client_frames.csv`, `client_graph.csv`, `gpu.csv`, shim logs), `tools/pacing_table.py`, `tools/pipeline_report.py`. Stock reference stats via ALVR's websocket (port 8082, header `X-ALVR: true`). See [DEVELOPMENT.md](DEVELOPMENT.md) for the telemetry build flag.

## Still open

- Duplicate timestamps (5-10% of the Vision Pro's frames in heavy games, 20% in Dead Space): the re-stamp that removes them is parked (`exp/restamp-2026-10-13`) until the visionOS client handles stamped frames; the Quest 3 already shows them.
- Headset decoder queue regimes (12-15 ms against stock 3 ms) when a frame goes missing.
- Whether foveated or full-frame looks better at the same bitrate on the headset (unjudged).

## Future work on the ALVR visionOS client (why re-stamped frames tremble there and not on the Quest)

Observations, 2026-10-13 night (Cyberpunk intro, same host build, A/B by the user): shim-side re-stamp (nearly every frame stamped with the
newest sample and rotated to it) clean on both headsets; host-side stamping (only duplicates re-stamped, with the oldest sample newer than the
last sent) clean on the Quest 3, a mild tremble on the Vision Pro without the rotation and a strong one with it. The lab finds the host's
geometry exact; the simrig is clean in all modes. So the difference is in the client, not in the streamer.

What the two clients do differently. The Quest runs ALVR's Android client: the OpenXR runtime reprojects every frame from the pose the frame
declares (looked up by its timestamp) to the display pose, so any monotonic, real timestamp is fine. The visionOS app is a separate Swift
codebase rendering through Compositor Services: the display time, the device-anchor timestamps and the final reprojection belong to Apple; the
app's only lever is the device anchor it attaches to each drawable, and the timestamps on the wire are the client's own convention (they jitter
by tens of ns and must match exactly, see finding "predicting a missing head sample").

Hypothesis that fits all five observations: the visionOS client compensates the frame's declared pose less than a full per-frame
reprojection, so what matters is how close and how *steady* the declared pose is relative to the display pose. Shim-side stamping keeps
the staleness constant (every frame at the newest sample): clean. Host-side stamping alternates between the real render sample and the next
one, so the staleness jumps frame to frame: a mild tremble even with the picture unchanged. Rotating the picture to a pose the client does not
fully compensate turns the alternation into a strong tremble. A second feedback path: the client predicts its anchor query ahead by the
server's reported latency, and the stamp shortens the measured game stage, so the prediction horizon moves with the stamping mode.

What to check in the client source before changing anything:
1. Where the drawable's `deviceAnchor` comes from: the pose history keyed by the frame's timestamp (full compensation) or a fresh query at
   present time (none). If it is a fresh query, use the stored anchor; the shim-side re-stamp then becomes correct for the Vision Pro as it
   already is for the Quest.
2. How the prediction horizon is computed from the reported latency, and whether a change in the game stage moves it per frame.
3. Whether the per-sample timestamp the client sends carries the anchor's own time, so Compositor Services can extrapolate from it, or a
   client-side value with no relation to the anchor.

What the recorded sessions can and cannot tell: `client_frames.csv` of the Quest stamp-on run against the Vision Pro stamp-on run
(frame interval, decoder queue, vsync queue per frame) separates a display-timing reaction from a pose problem; it cannot show which pose
the client displayed with. The expectation is "pose problem" (the cadence was the same in all three host-side modes while the symptom scaled
with the rotation).

Plan when the client is recompiled: bring the shim-side path back from `exp/restamp-2026-10-13` first (clean on both headsets), then the
host-side one only if the shim-side wait's arrival jitter still matters.

## Branch fg-performance (2026-10-16): four changes from the latency and GPU-time assessment

Measured basis (RTX 5090, Vision Pro 100 Hz, nights of 2026-10-15/16): a GPU-bound game keeps 2 to 3 frames in its render queue (20 to 56 ms,
`fg_uncapped.csv` now - newest); the host's own GPU share per output is small (flow 2.2-3.3 ms of timeline, synthesis 0.4-1 ms, FFR + encode
about 3 ms, a copy about 50 us at 45 MB); the headset's tracking samples miss 1-2 % of boundaries (up to 13 % in the lab, where the phase is
random per run). Bandwidth is not the lever, serialization and queue depth are.

1. **Just-in-time release** (`ipc.h appPacingJit`, settings `video.game_pacing` jit | queue, default jit; `--game-pacing`, `VISIONALVR_GAME_PACING`).
   The shim's queue throttle let the game start a frame while one was still on the GPU, so a GPU-bound game ran 2-3 frames ahead. Now the
   release is also held until the newest submitted frame is predicted to finish on the GPU (last GPU completion + frames pending x the EMA of
   the GPU time per frame, measured from the per-slot GPU-done events) minus the app's CPU time envelope (release -> submit) and a 1 ms lead:
   the frame the app starts reaches the GPU as it frees up, one frame in flight, no idle GPU. With the GPU idle nothing is held. The shim's
   pacing line reports `pacing just-in-time (holds N, mean X ms), app cpu, gpu ms/frame`. This is what the manual fps cap approximated.
2. **Flow only without vectors** (`FrameGen::computeFlow`): the optical flow of a pair is computed by the first render that has an eye without
   game vectors, not at ingest; with vectors for both eyes the two preparation passes and the engine are skipped (`gen.flow_skipped`), zero
   flow and maximum cost are bound so the flow path falls back to the crossfade where a vector does not decide.
3. **Tap copies retired by GPU completion** (`NgxTap::GpuDone`): a replaced or dropped shared copy is freed when the frame after its last use
   completed on the GPU (the shim's GPU-done event, which for a D3D12 game sits after the runtime's fence wait on the game's queue), the 3 s
   age limit stays as the fallback. Deterministic where the timer was a guess.
4. **Predicted sample on a miss** (removed 2026-10-16 evening): a boundary with no tracking sample within the wait stamped the output with a
   pose extrapolated one period past the last timestamp. The lab showed it as TREMBLE under a turn (its viewer matches poses by exact
   timestamp); the headset showed jitter with it on (user, 2026-10-16: "it induces jitter"). Removed with its setting, tick and live
   command. The cause of the misses was found instead, see "Headset night 2026-10-16" below.

Lab A/Bs. Item 1 needs a GPU-bound app: the `fps:lo:hi` scenarios are a CPU busy-wait (a CPU-bound game, where the rule correctly holds
nothing), so `gpu_unc` / `gpu_unc_queue` / `gpu` / `gpu_queue` drive the probe with 2 ms of CPU and 50 x 128 MB GPU copies per frame
(3080 Ti: 13 ms of GPU, 72-78 fps, GPU 91-96 %). Measured per frame from the shim's own release and publish times:

| pacing | release -> GPU done (p50 / p95) | frames in flight at the host (p50) | content lag | app fps | holds |
|---|---|---|---|---|---|
| queue throttle | 28.0 / 34.3 ms = 2.0 game periods | 34.1 ms | 58.5 ms | 72 | none |
| just in time | 16.2 / 21.4 ms = 1.2 game periods | 22.6 ms | 45.8 ms | 71 | 4497 of 4673 releases, 12 ms each |

Same frame rate, one game period less in the queue, 12-13 ms less content lag at 13 ms frames (it scales with the frame time: Carma's
25 ms frames would give back about 25 ms). Generation off, the same app: 25.0 ms (queue) against 15.0 ms (just in time), 80 fps either way.
The full scenario suite is the regression gate (2026-10-16: every verdict equal or
better than before with items 1-3 in, flow skipped on 100 % of vector outputs). Headset check: the shim pacing line's holds and `app cpu` / `gpu` numbers, the
`frame generation perf` line's in-flight proxy, and the feel against the 2026-10-15/16 recordings.


## Headset night 2026-10-16: the regression and the tracking-sample rule

Every mode of the build above (rates 1/3 and 1/2, auto, just-in-time or queue pacing) was judged inferior to the night before on the
5090 + Vision Pro. The logs (`logs/debug/20261016-174349`) gave the mechanism:

- The headset sends one tracking sample per display period (90 Hz, exact on its own clock) and Wi-Fi delivers them bunched: at the host,
  with no game running, the spacing between samples was p50 11.0 ms, p95 18-20 ms, max 23-38 ms, and 25-30 % of the gaps were over
  15 ms (night 1 on the 15th: p95 16.3, 17 % over 15 ms; so the character is the headset's, the degree the evening's).
- The pipeline stamped each output with the newest sample newer than the last sent frame and waited up to 3 ms for one. Taking the
  newest sample as it lands leaves nothing for the next boundary, which then depends on the next sample arriving within 3 ms: 15-30 % of
  the boundaries waited (mean 2.6 ms) and 7-14 % found none and were skipped (`no_sample` 12-24 per 180 boundaries at 1/2, 11-33 at
  1/3; 94-168 during a UEVR shader compilation that starved the receive thread).
- The rates commit of the morning (879094b) had made a skipped synthetic output dropped rather than delayed (the lab's 1/3 case): on
  the headset each miss became a frozen frame and then a double step (the rendered frame right after the repeat), where the night
  before each miss was one repeat with the pattern sliding a boundary. At 1/2 the generator produced 396 synthetic frames for 424
  rendered ones in 10 s, at 1/3 505 for 570 owed; the stream ran at 79-85 fps against a 90 Hz display.
- The predicted-sample tick (item 4) removed the skips (0-1 per 180) at the price of 70-141 predicted stamps per 180: jitter.
- Not the cause: the just-in-time release held nothing in the UE games (their CPU time, release to submit, 25-46 ms, exceeds their GPU
  time, so the rule has nothing to hold); the lazy flow and the tap lifetime left no trace (0 errors, vectors used on 100 % of eyes).

Fix (same evening): one sample in hand (`fg_pick_sample_now`: when two samples at the display cadence are waiting, the older one is
stamped and the newest kept for the next boundary; a miss then needs a sample a whole period late), the delayed output is back for the
rare miss, the predicted sample is gone, and the misses are logged (`frame generation samples` line, game summary). A side finding on the
shim's frame cost: `PendingFrame.releaseS` took the last release at compose time, which for a pipelined engine (UE submits frame N after
frame N+1's release) is the wrong release; the cost read 2 ms at 1/3 and Auto stepped up on it. The releases are now queued and matched to
the frames in order. For a pipelined engine the cost (release to GPU done) is then about two of its frame intervals, which is its real
latency; Auto's step up compares it with the next budget and will not fire for such engines (conservative), the step down on repeats is
unaffected.

## Headset night 2026-10-16, later: the fix verified, the just-in-time A/B, the frame cost at 1/3, Auto by trial

Session 20:38-21:11 on the 5090 + Vision Pro after the user changed the Wi-Fi channel (the 17:43 session had also suffered an exceptional
neighbour's interference, confirmed with stock ALVR; the analysis above stands, its mechanism was in the counters). User verdict on f2ec33a:
"yesterday's performance was restored or enhanced"; Valor Mortis and Carma need 1/3 to be smooth; just-in-time pacing a modest plus.
Per run (`logs/debug/20261016-203858`, `-205821`; client_frames.csv, first 8 s skipped; latency = the client's total; repeats over the
whole run, level loads included):

| run | game / stream fps | missed display periods | repeats per 100 outputs | client latency p50 / p95 ms |
|---|---|---|---|---|
| Valor Mortis 1/2, just-in-time | 43.1 / 98.1 | 2.7 % | 15 | 57.1 / 67.1 |
| Valor Mortis 1/2, queue | 42.6 / 97.7 | 2.7 % | 17 | 66.6 / 67.2 |
| Valor Mortis 1/3, just-in-time | 32.8 / 99.4 | 1.2 % | 7 | 67.0 / 77.1 |
| Valor Mortis 1/3, queue | 33.3 / 99.1 | 2.2 % | 5 | 67.0 / 67.2 |
| Carma 1/3, just-in-time (Auto) | 31.8 / 98.7 | 2.1 % | 9 | 67.0 / 77.0 |
| Dead Space, Auto 1/3 -> 1/2 -> 2/3 | 49.4 / 99.4 | 1.2 % | 0 | 67.0 / 67.2 |
| Lies of P, Auto 1/3 -> 1/2 -> 2/3 -> 1/2 | 53.2 / 98.9 | 1.4 % | 4 | 67.0 / 77.1 |
| Callisto Protocol, generation off | 69.2 / 69.2 | 31 % | - | 86.9 / 97.0 |
| Valor Mortis, generation off | 39.1 / 39.1 | 52 % | - | 87.1 / 97.1 |

- **The tracking-sample rule works.** Boundaries skipped for want of a sample: 0.6-2.4 % per game (80 of 3 400 at Valor Mortis 1/2)
  against 7-14 % in the 17:43 session, with the same bunched tracking (p95 14-18 ms, 7-25 of 200 gaps over 15 ms). The stream runs
  above the display rate (94-99 fps of 90) because a delayed output and the next boundary's own go out together: the client shows one.
- **Just-in-time pacing**: at 1/2 the client latency is one display period lower (p50 57 against 67 ms) at the same fps and the same
  missed periods; at 1/3 no gain (both 67, the p95 one period higher with it). The shim's line reports holds 0 in both UE runs, so the
  gain at 1/2 is not from the hold itself (open; the release lands differently relative to the submit, see the cost readings below).
- **The frame cost cannot judge a faster rate.** At 1/3 every game read p95 5-10 ms with "app cpu 0.5 ms" (the envelope's floor: the
  frame is composed within 0.5 ms of the release it is matched with, so release -> GPU done is the tail of GPU work already queued, 4-9
  ms), at 1/2 with queue pacing 41-47 ms (cpu 22-27, gpu 17-19: two frame intervals, the standing queue), with generation off 14-22 ms
  of CPU. Which release a UEVR game's frame belongs to depends on the rate and the pacing mode (xrWaitFrame right before xrEndFrame is
  the likely order; unverified, a per-frame trace would tell). Consequence tonight: Auto stepped Valor Mortis and Carma 1/3 -> 1/2 on
  "cost 7 ms under 80 % of 20 ms" and back 5 s later on repeats 10.7 %; Mortal Shell 2 and Lies of P reached 2/3 and came back to 1/2;
  Dead Space held 2/3 (49 fps). End of Abyss reached 1/2 in 27 s before the user quit; it renders near 90 fps without generation, where
  generation off is the right level: not a level Auto had.
- **Three step downs were the game's stalls, not the rate's** (user: "for some games like Callisto Protocol the gain is hard to see").
  Callisto held 1/2 (frame interval p50 20.0 ms, p95 21.5 against the 22 ms cap, repeats 0-1 per 2 s for 24 s) and was sent back to 1/3
  by a 4 s stall in which the shim released 6 then 0 frames per 2 s (184 and 75 repeats): a hitch of the game, read as 5.8 % repeats
  over 5 s. Lies of P at 2/3 the same (releases 38, 0, 0 per 2 s; 136, 200, 54 repeats). And every game that started at a remembered
  1/2 (End of Abyss, Lies of P, Callisto) was stepped down within 5 s by its loading burst (58-111 repeats while the shim's release
  counter had just reset). Valor Mortis and Carma at 1/2 were genuine: 16-43 repeats in every 2 s window with the game delivering 82-87
  frames, frame interval p50 23-26 ms against the 22 ms cap. So Callisto sat at 1/3, rendering 30 fps where it can render 69: little to
  gain over no generation. Fix in the controller: a step down needs 3 of the last 5 seconds with repeats over 5 % while the game
  delivers at least a quarter of the frames the level expects (shim releases); seconds in which it does not count neither way; 15 s of
  grace after a game start. Each false step down had also counted as a failed trial, doubling the dwell.
- **Auto redesigned (this commit): the trial is the measurement.** Levels 1/3, 1/2, 2/3, off; down on what the stream shows (repeats;
  with generation off, display periods without a frame); up by trying the next level after a dwell with no repeats, back within 5 s if
  it does not hold; a failed trial doubles the dwell, the third settles the game (remembered). docs/FRAME_GENERATION.md "Auto".
- **Pose match by display time fails almost everywhere**: the shim's "no pose for display time" counter reads 850-900 of 900 frames at 1/3
  for every game and at 1/2 for all but Valor Mortis (131 and 0 of 900). Not 1/3-specific as first thought; the fallback (the newest of the
  tied poses) is in force nearly always. Open: what VDXR stamps as SensorSampleTime against what the shim records per pose.
