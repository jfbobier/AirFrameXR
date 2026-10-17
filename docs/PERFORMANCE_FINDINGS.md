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

1. **REMOVED 2026-10-17 (never engaged on any headset: virtualdesktop-openxr calls WaitToBeginFrame right before the submit, so there was nothing to hold; the setting, tick, IPC field and lab A/B scenarios are gone, IPC v30).** Just-in-time release (`ipc.h appPacingJit`, settings `video.game_pacing` jit | queue, default jit; `--game-pacing`, `VISIONALVR_GAME_PACING`).
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

### Second Auto session the same night (21:54-22:25): manual 1/3 "felt smoother", End of Abyss at generation off

User: manual 1/3 felt smoother than Auto on The Darken and Valor Mortis; End of Abyss "generates close to 90 fps", what did Auto decide.
The phase table (client_frames.csv, split at every level change):

| game, phase | duration | headset missed periods | client latency p50 ms |
|---|---|---|---|
| Valor Mortis 1/3 (Auto, after the trial) | 49 s | 2.0 % | 66.9 |
| Valor Mortis 1/2 trial | 6 s | 1.7 % (host repeats 12.5 %) | 66.9 |
| Valor Mortis 1/3 by hand | 127 s | 1.7 % | 66.9 |
| The Darken 1/3 (Auto) | 46 + 68 s | 1.9 / 1.8 % | 67.0 |
| The Darken 1/2 trials (two) | 6 s each | host repeats 18-23 % | 67.1 |
| The Darken 1/3 by hand | 117 s | 1.9 % | 67.1 |
| Callisto 1/2 trial | 27 s | 2.0 % | 57.1 |
| End of Abyss 2/3 | 26 s | 3.7 % | 67.0 |
| End of Abyss generation off (second trial, held) | 399 s | 14.0 % | 86.9 |

- **Manual 1/3 and Auto at 1/3 are the same stream** (missed periods 1.7-2.0 %, same latency). What the user felt is the trials: each
  one is 6 s of 12-23 % repeats, and Valor Mortis, Carma and The Darken each paid one or two per session without ever settling, because
  the failed-trial count lived in the session while the doubled dwell was remembered. Now the dwell carries the count (the third failure
  settles the game for good) and a young trial is judged on 3 s, so a wrong level costs 3-4 s.
- **Callisto held 1/2 for 24 s** (frame interval p50 20.0 ms, repeats 0) and was stepped down by a 0.4 s hitch (frame interval max 407 ms)
  that spread 63 repeats over 3 seconds: 3 of 5 failing. Hence 4 of 5 now.
- **End of Abyss: Auto chose generation off and the headset says that was wrong.** The host streamed every rendered frame (91.3 fps) and
  its own count saw no periods missing, but the headset repeated 14.0 % of its display periods for 399 s, with 87 ms of latency (a
  free-running game queues at the client), against 3.7 % and 67 ms at 2/3. The off level is now judged on the headset's per-frame
  statistics (frames displayed, periods repeated), which would have sent it back to 2/3 within 5 s. Whether 2/3 also *feels* better than
  off there is the user's call; the numbers say so.

### Third session the same night (22:43-23:19): Unity games, DLSS vectors, the 2/3 level at length, Zelda's menu

User followed the blind-spot list: Dead Space and Callisto at 2/3 by hand (just-in-time ticked), End of Abyss at 2/3 for 10 min, Unity
titles (Mouse: P.I. For Hire with the OptiScaler fork, Gunman Contracts native with DLSS, End of Abyss), Silas, and Zelda Breath of the Wild
through Cemu + BetterVR (Vulkan). Phase table (client_frames.csv):

| game, level | duration | headset missed periods | client latency p50 |
|---|---|---|---|
| Dead Space 2/3 | 121 s | 2.4 % | 67.0 ms |
| Callisto 2/3 | 67 s | 2.6 % | 67.0 ms |
| End of Abyss 2/3 | 608 s | 1.6 % | 67.0 ms (off the night before: 14 %, 87 ms) |
| Mouse 2/3 (Auto, two off trials inside) | 172 s | 4.3 % | 66.1 ms |
| Silas 2/3 -> 1/2 (Auto) | 152 s | 2.8 % | 66.2 ms |
| Gunman Contracts 1/2 -> 2/3 (Auto) | 185 s | 3.0 % | 66.3 ms |
| Zelda (Cemu) 2/3 -> 1/2 -> 1/3 (Auto) | 78 s | 7.0 % | 66.4 ms |
| Zelda (Cemu), generation off | 36 s | 28.2 % | 76.1 ms |

- **2/3 holds for 10 minutes** on End of Abyss with 1.6 % missed periods; the per-game memory now says "two_thirds" for it.
- **Just-in-time never engages, in any engine.** Dead Space (Frostbite), End of Abyss (Unity), Callisto (UE): holds 0, 0 and 2 in 8 000+
  releases, and the shim's "app cpu" sits on its 0.5 ms floor for all of them, UE and Unity alike. So the "frame submitted right after its
  release" pattern is not UEVR's: it is where virtualdesktop-openxr calls ovr_WaitToBeginFrame for every OpenXR app, right before the
  frame's submit. The shim never sees an app's frame start and the just-in-time rule has nothing to hold. The setting is inert; the gain
  measured at 1/2 the night before came from elsewhere. A real frame-start hold would have to live in the OpenXR layer (xrWaitFrame).
- **DLSS vectors**: Mouse (OptiScaler fork `dxgi.dll`, one DLAA stream 2560x1440, dx11): 3 274 evaluates, 0 copy failures, vectors used on
  three eyes in four (`used_eyes` 6 965 for 4 527 frames), the flow engine still ran for the eye without them (`flow_skipped` 0). No A/B
  without vectors in the logs, so no claim of improvement. Gunman Contracts: the fork attached but saw **0 DLSS streams and 0 evaluates**
  in 3 min: the game's DLSS does not pass through it (Unity's NGX plugin loads its own path, or DLSS was off in the game). Vectors did
  not work there.
- **Auto tried generation off on Mouse, Silas and Gunman** (55-64 fps at the 2/3 cap) and the headset sent it back each time within
  4-7 s (9-28 % periods repeated): the client-side verdict works, but each try is a visible 4-7 s hiccup for a game that obviously
  cannot hold 90. Gate added: the off trial needs the game to fill the 2/3 cap (at least 93 % of 66.7 releases per second) for the
  whole dwell.
- **Zelda's menu / world oscillation** (BetterVR, Vulkan): the mod renders the inventory and the scope as a single quad layer with no
  projection layer. Such frames go through the pipeline as **passthrough** (sent as they are, 60 per 2 s in the menu), but the generator
  kept the last world frame as its held frame, and every boundary without a new arrival re-sent it rotated to the head ("repeat"): the
  stream alternated menu quad, stale world, menu quad (`passthrough 100` and `repeats 100` per 2 s window, `same_ts 52`). Without
  generation the quad frames simply stream. Auto then read the repeats as the rate failing (2/3 -> 1/2 -> 1/3, "99.4 % repeats").
  Fix: a passthrough frame drops the held frame (nothing to repeat until the next projection frame).

## Quest 3 session 2026-10-16/17: the host paced at 241 Hz

First Quest 3 session on the frame-generation build (logs/debug/20261016-234945). User: "frame generation creates a lot of artifacts, and
even pacing issues", not solved by DLSS vectors off or the pacing setting; Dead Space without generation was perfect.

The cause was the host's, not the headset's. The display-rate estimator (`vision::RateEstimator`, `--no-follow-rate` to disable) infers the
client's display period from the spacing of its tracking samples, built for the Vision Pro, which sends one sample per displayed frame (it
is how the host follows the AVP's 90 / 100 Hz switch). The Quest 3 tracks at 270 Hz (gap p50 3.67 ms) whatever its display rate. The
estimator dropped gaps under 4 ms and took the median of the rest: 4.1 ms, "client display rate 241.2 Hz: host clock follows it", three
minutes into the session. From there every boundary, budget and pattern ran at 241 Hz: 139-242 frames per second streamed to a 90 Hz
headset (Valor Mortis 1/3: 30 030 streamed in 3m35s), Auto's budgets read 8.3 ms at 1/2 and 6.1 ms at 2/3, "the game does not hold a third
of the display rate", the headset repeated 7-16 % of its periods and received two or three frames per period: the artifacts. With
generation off the host streams the game's own frames and the clock does not matter, hence the clean Dead Space.

Fix: a cadence is only followed when it is within 3 % of one of the refresh rates the headset declared at the handshake (ALVR's client
capabilities, `supported_refresh_rates`: Vision Pro 100 / 96 / 90, Quest 3 72 / 80 / 90 / 120; a generic list of display rates is the
fallback before the handshake has been seen); otherwise one line, `client tracking cadence 270.0 Hz is none of the headset's refresh rates
(72 / 80 / 90 / 120): several samples per frame, host clock stays on the nominal 90.0 Hz`, and the nominal grid stays. The estimator's floor is 2 ms so the Quest's true cadence is what gets judged. Not a headset exception: a sanity check
on an inference. The Quest findings of that night are void; the headset needs a new session on this build before anything is said about
its artifacts. Separately, the eye-height override (made for the Vision Pro's unreliable floor estimate) displaced the user in Valor Mortis
on the Quest, which reports its floor correctly: the launcher now greys the knob out and hands the host 0 (the headset's floor) for any
headset that is not an Apple one; the saved value stays for the Vision Pro.

## 2026-10-17: the Auto controller removed, game profiles in the launcher, the frame cost gone

Quest 3 session on the corrected build (logs/debug/20261017-003318): the display-rate guard held (`client tracking cadence 270.1 Hz is
none of the headset's refresh rates`), the stream stayed at 86-89 fps. Auto still lost to the user's hand: Valor Mortis started "at third,
settled" from the Vision Pro's memory and never tried 1/2, which it holds on the Quest (39 fps by hand); Carma's inherited 80 s dwell outlived
the run; Dead Space reached "off" and the Quest repeated 10 % of its periods there, as the Vision Pro had on End of Abyss. Decision (user):
the engine does not change the level during a run, ever; it records one honest line per run (games.json: headset, level, fps, the headset's
repeated share as the median and 90th-percentile 10-second window) and the launcher proposes from the records ("Recommended" tick, greyed
slider, nothing proposed without a record). The shim's frame-cost readout (release to GPU done, CPU / GPU envelopes, IPC fields, headroom
line, launcher text) is gone with it (IPC v31): it never measured a capability. Recenter: a generic host command, sent by the launcher at
game start for non-Apple headsets (Valor Mortis started facing the room's forward on the Quest).

## 2026-10-17, 02:30: the phase margin (3 -> 7 ms), found through the debug tick

The user felt Valor Mortis at 1/3 smoother with debug logging on than off, and said so. The counters agreed with neither feeling until the
client data was compared: with debug on every frame left the host 4.6 ms later in its display period (encode p95 9.0 ms against 4.3, send
after tick 8.6 ms against 3.9), arrived later at the headset, waited 3 ms longer in its decoder queue (p50 10.8 against 7.3-8.5) and almost
never missed its tick (0.5 % repeated periods against 1.2-2.3 % in the earlier runs), at the same measured latency (67 ms p50: the headset
displays at its next tick either way). The phase lock had been aiming the earliest arrivals 3 ms before the render tick with an arrival
spread of 8 ms, so a share of the frames landed after the tick and were shown a tick late: that is the "lower performance" of the evening.
`VISIONALVR_PHASE_MARGIN_MS=7` with debug off reproduced the cure by hand ("visual frame generation aspect was MUCH BETTER"); the default is
now 7 ms. Open: why debug mode costs 5 ms per frame (the CSV writers are buffered; the NVML sampler or the event writer are the suspects).
