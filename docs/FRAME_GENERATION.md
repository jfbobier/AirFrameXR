# Frame generation (optical flow)

Branch `fg/optical-flow`, first version 2026-10-14. Off by default; experimental.

## What it does

Games that cannot hold the display rate (50-70 fps on a 90/100 Hz headset) look choppy: the headset repeats a frame every few
ticks, and in-scene motion (and everything the headset's own rotation reprojection cannot fix: translation, moving objects,
animation) stutters. With frame generation on:

1. The shim paces the game at **a fraction of the display rate** (IPC `appPeriodMul` m: `WaitForVsync` releases the app every m-th
   compositor boundary; the predicted display time moves accordingly, see below). At the default **1/2** a 60 fps game becomes a steady
   50 fps (100 Hz) or 45 fps (90 Hz) game with a 20 ms budget per frame; at **1/3** a 40 fps game becomes a steady 33 fps game with a 30 ms
   budget and two generated frames per rendered one (see "Rates" below).
2. For every rendered frame B the host keeps a copy, computes the **optical flow** between the previous rendered frame A, rotated into
   B's camera first, and B (NVIDIA Optical Flow API: the flow engine of Turing-and-later GPUs, through the driver's `nvofapi64.dll`,
   on the frame downscaled to 50 %, 4x4 vector grid, cost output), and sends the **interpolated frame S(A, B)**: each pixel samples A
   and B at the two ends of its flow vector, weighted for the midpoint, with a confidence from the colour disagreement at the two ends
   and the engine's cost, falling back to the crossfade of the rotation-aligned A and B where the flow is doubtful (ghosting rather
   than holes). This is OFXR-Bridge's synthesis (`frame_synthesis.hlsl`), ported to one D3D11 pass per eye in `nvenc/hostlib/framegen.cpp`.
3. At the next boundary the host sends **B itself**. A boundary with nothing new re-sends B. So the headset receives one picture per
   display period: S, B, S, C, S, D ... each a period apart.
4. **Every output is stamped with the newest tracking sample** (newer than the last sent frame) and **rotated to that sample's head
   orientation** (the parked re-stamp warp, one rotation per eye inside the synthesis pass). The headset's reprojection then has only
   the sample-to-display motion left to do; head turns stay smooth while the in-between pictures carry the scene motion. The flow
   itself never sees head rotation (A is aligned into B's camera before the engine runs), so fast turns do not produce flow artefacts.
   Translation (parallax) is not compensated, like the headset's reprojection.

Cost: one display period of added latency for the rendered frames (an interpolated picture is shown before each of them), one more when the
app publishes before the previous rendered frame has gone out (the arrival waits for the next boundary rather than displacing it), plus the
generation itself (3080 Ti at 2048x1024: flow 3.3 ms, synthesis 0.2 ms; the RTX 5090 at the foveated size to be measured).

## Where it lives

| Part | File | Role |
|---|---|---|
| Pacing | `ovrshim/driver.cpp` `WaitForVsync`, `GetPredictedDisplayTime` | release every `appPeriodMul` boundaries, display time + (mul-1) periods |
| Render orientation | `ovrshim/driver.cpp` `FrameRenderOrientation`, `ovrshim/ipc.h` `frameRenderQuat` (IPC v25) | the per-eye orientation each frame was rendered with |
| Generator | `nvenc/hostlib/framegen.{h,cpp}` | NVOF session, flow-input prep pass, synthesis/rotation pass, output texture |
| Host glue | `nvenc/hostlib/nvh.cpp` `nvh_fg_*` | own foveation pass on the output, encode tail shared with the slot path, stats JSON |
| Decision per boundary | `tools/host/src/pipeline.rs` `FgState`, `fg_plan_app`, `fg_plan_hold` | synth / real / repeat, sample stamping, counters |
| Sample ring | `tools/host/src/main.rs` `Shared::recent_samples`, `push_recent_sample` | the last 48 tracking samples with their head orientation |
| Settings | `config/visionalvr.json` `video.frame_gen` (+ `frame_gen_flow_scale`, `frame_gen_perf`), `--frame-gen on|off`, `VISIONALVR_FRAME_GEN=1/0`; live: GUI command `frame_gen on|off` | CLI > env > settings; checkbox "Frame generation" in the streamer window (applies at once: generator created or dropped under the encoder lock, shim pacing follows) and in Settings |
| Headers | `third_party/nvof/` | `nvOpticalFlowCommon.h`, `nvOpticalFlowD3D11.h` (MIT notice; nothing of NVIDIA's is redistributed) |

Rules of the per-boundary decision (`FgState`): the output schedule is a fixed pattern per game period, the m - 1 synthetic frames at
t = 1/m ... (m-1)/m then the rendered frame (`due`); at 1/2 that is the strict alternation synthetic, rendered. A rendered frame
that arrives while the previous one is still due waits in the intake for one boundary (`fg_queued`); a boundary with nothing due waits up to
`VISIONALVR_FG_ARRIVAL_WAIT_MS` (4) past the boundary for the frame the app is about to publish (`fg_arrival_waits` / `fg_arrival_hits`) and
only then repeats the rendered frame. Both rules exist because an app released at a boundary publishes its frame near a boundary: without
them the schedule flipped between "just before" and "just after" (a frame dropped, then one frozen) at 6 % of the boundaries in the rig's
ideal-timing case. Every output is stamped with a tracking sample newer than the last sent frame, and when two are waiting the older of
the two is taken so that one stays in hand for the next boundary (`fg_in_hand`; the Vision Pro sends one sample per display period and
Wi-Fi delivers them bunched, a quarter of the gaps over 15 ms, so a boundary that takes the newest sample as it lands leaves the next
boundary waiting for a late one: 7-14 % skipped boundaries on 2026-10-16, each a frozen frame). A boundary that still finds none sends
nothing and what it owed stays owed (`fg_no_sample`, the pattern slides one boundary); a frame without a
projection layer (quad-only loading screens) goes out as is; a frame still due when the encoder overran is dropped for the synthetic frame
between the two newest (`fg_dropped`). With the generator on the newest waiting frame always wins a pick (older ones superseded): the shim's
half-rate pacing runs open loop and the generator consumes one frame per two boundaries, so a standing queue would never drain otherwise.
When the GPU/driver has no flow engine the host logs a WARN, streams as without the feature and keeps the game at full rate.

## Rates (2026-10-16): 1/3, 1/2, 2/3

The launcher offers the game's rate as a fraction of the display rate, one slider with three positions (`video.frame_gen_rate`):

| rate | setting | the game | the host | for games that render at |
|---|---|---|---|---|
| 1/3 | `third` | released every 3rd boundary (33 fps at 100 Hz, 30 at 90) | S(A, B, 1/3), S(A, B, 2/3), B | 34-49 fps |
| 1/2 | `half` (default) | every 2nd boundary (50 / 45 fps) | S(A, B, 1/2), B | 50-66 fps, and the safe choice |
| 2/3 | `two_thirds` | free-running, held to two thirds of the display rate by the shim's minimum release spacing (67 / 60 fps) | the content-clock planner of `docs/FRAME_GENERATION_UNCAPPED.md`: every output interpolated at the fraction its time falls at | 67 fps and above |

Why these three and not a free ratio: on the boundary grid only 1/m patterns are regular (a 2/3 pattern with boundary-locked releases
alternates 10 and 20 ms frame times, which engines with frame-time smoothing turn into judder), so 2/3 is the free-running planner with a
cap rather than a pattern. Why not "uncapped" in the launcher: the two headset nights of 2026-10-15/16 showed that the free-running game's
render queue (2 to 3 frames when GPU-bound) sets the content lag, and that the user preferred the locked cadences on latency; `uncapped`
stays available from the config and the command line, with `video.frame_gen_cap_fps` and `video.frame_gen_lag_ms`, for experiments.
Choosing: the 10-second `frame generation perf` line reports the game's `app fps`; pick the largest fraction the game sustains with margin.

### Auto (branch fg-performance, 2026-10-16): the level by trial, judged by what the stream shows

The "Auto" tick (`video.frame_gen_auto`, live `frame_gen_auto on|off`, off by default) lets the host pick the **level**: 1/3, 1/2, 2/3 or
generation off (a game that holds the display rate is better without it: End of Abyss on the 5090). One step at a time:

Judged second by second over the last 5 s; a second in which the game delivers less than a quarter of the rendered frames the level expects
(the shim's releases: a loading screen, a hitch) counts neither way, and a game start gets 15 s of grace: the headset night of 2026-10-16
had three games stepped down by their own 2-4 s stalls and by their loading at game start, not by the rate; a 0.4 s hitch of Callisto at 1/2
spread over 3 seconds, hence 4 of 5. With generation off the host's own sent count sees nothing wrong with a game at 91 fps (every frame
streamed) while the headset repeats 14 % of its periods (End of Abyss, 399 s), so that level is judged on the headset's statistics.

| condition | action |
|---|---|
| 4 of the last 5 s with repeats above 5 % of the outputs while the game delivers (at 2/3 also a frame cost 15 % over the budget: the content clock there turns a slow game into lag, not repeats) | step down (off -> 2/3 -> 1/2 -> 1/3); at 1/3 a line says there is nothing slower |
| generation off and 4 of the last 5 s with the headset repeating more than 8 % of its display periods (its own per-frame statistics; the host's sent count is the fallback) | back to 2/3 |
| every second quiet (repeats under 2 %, the game delivering) for the dwell (20 s at first) | **trial** of the next level (1/3 -> 1/2 -> 2/3 -> off); while the trial is young (15 s) the verdict above is taken on 3 s, all failing, so a wrong level costs 3-4 s of repeats, not 6 |

A failed trial doubles the dwell (40, 80 ... 320 s); the third failure **settles** the game at the level that held: no more trials, steps down
only. The failures are carried across sessions by the remembered dwell (headset 2026-10-16: Valor Mortis, Carma and The Darken each paid one
failed 1/2 trial per session, 6 s of 12-23 % repeats, without ever settling; now the third one settles). Dwell and settled are remembered per game in `config/games.json` with the level ("off" included), the frame cost and whether vectors
were found, keyed by the executable; written at every change and at game end; a known game starts at its remembered level (`auto_start`).
Ticking Auto off and on re-arms the trials, and a rate chosen by hand brings generation back. While Auto has generation off the launcher's
tick stays as the user set it and the line under the slider says so; generation comes back at game end so the next game starts as set.
Every decision is a log line (`frame generation auto: third -> half (no repeats for 20 s, frame cost p95 8.1 ms: trying the next rate; back
if it does not hold)`, `... half -> third (3 of the last 5 s with repeats over 5 % (mean 10.7 %) ...; the trial did not hold, next one after
40 s without repeats)`), an `fg_auto` event (with `failing_s` and `stalled_s`) and a `frame generation config (auto)` line.

Why trials and not the frame cost (the first design, 2026-10-16 afternoon: "decide from what the shim measures, never by trying"): the headset
night of 2026-10-16 showed the cost cannot judge a faster rate. At 1/3 every UEVR game read a p95 cost of 5-10 ms with "cpu 0.5 ms" (the frame
is submitted right after its release, so release -> GPU done is the tail of GPU work already queued), and the cost rule stepped Valor Mortis
and Carma up to 1/2, which they do not hold (repeats 10.7 % within 5 s, back to 1/3). At 1/2 the same games read 41-47 ms (two frame intervals:
the queue), with generation off 14-22 ms of CPU. The cost stays as a readout (the line under the slider, the 10-second `frame generation
headroom` log line) and as the 2/3 step-down signal; the launcher no longer prints "1/2 would fit" from it.

Lab: `auto_light` (2 ms CPU, 4 GPU copies, from 1/3): trials to 1/2, 2/3 and off over the first 60 s, then held. `auto_burst` (cost 24 / 2 ms
alternating every 30 s, from 1/2) and `auto_ramp` (cost 12 -> 24 -> 12 ms over 40 s) as before: the step down is unchanged, the step up is the
trial after the dwell.

Lab for the rates (3080 Ti simrig, 2026-10-16, 40 s each, marker on the wall clock; `third`, `third_slow`, `third_mv`, `stutter_third`,
`two_thirds`, `jitter_two_thirds`, `stutter_fg` as the half-rate regression):
- 1/2 unchanged: stutter_fg SMOOTH, frozen 1.3 %, step cv 0.18-0.20.
- 2/3: an 80-100 fps app held to 67 fps, SMOOTH, frozen 2.4 %, step cv 0.34; the ~75 fps jittery app SMOOTH, frozen 3.7 %, step cv 0.36-0.38.
- 1/3: the pattern runs as designed (two synthetic outputs then the rendered frame, superseded frames 8-10 per 2200 once the sample-miss
  rule below was in), but the lab's 5 px marker is a hard case at 30 ms game steps: it moves 14 px per pair, past what the optical flow's
  8 px blocks resolve, so the synthetic frames under-move it and the rendered frame catches up (steps small, small, big). With the probe's
  exact vectors the steps are uniform (4.0 / 4.0 / 4.0 px) except where a boundary had no fresh tracking sample. The 1/3 rate is where the
  per-frame motion is largest, which is the case the vectors exist for; a 1/3 verdict with flow is a thin-object verdict, not a planner one.
- The sample misses. The lab's misses depended on the random phase between the host's boundary grid and the simulated headset's sample
  delivery (1 % to 13 % of boundaries between runs), and a rule was tried that dropped the synthetic output of a missed boundary instead
  of delaying it (delaying slid the pattern, the next arrival queued and a newer one superseded it: 7 % of the game's frames at 1/3 in
  the lab). On the headset (2026-10-16) the misses were 7-14 % of the boundaries in every mode, the headset's samples arriving bunched
  over Wi-Fi, and the drop rule turned each miss into a frozen frame followed by a double step: a regression in every rate against the
  night before. Both are gone with the one-sample-in-hand stamping above (the miss needs a sample a whole period late) and the delayed
  output is back (a rare miss then costs one repeat, not a jump). The `frame generation samples` log line every 10 s and the game summary
  carry the skipped boundaries, the waits and the headset's sample spacing, so a bad Wi-Fi night is read from the log, not guessed.

## Telemetry

`encoder_stats.pipeline.frame_gen`: `synths`, `reals`, `repeats`, `holds` (boundaries filled by the generator), `no_sample`, `in_hand`, `dropped`,
`queued`, `arrival_waits` / `arrival_hits` / `arrival_wait_ms_sum`, `sample_waits` / `sample_wait_ms_sum`, `pairs_skipped`, `errors`, `passthrough`, and `gen` = `nvh_fg_stats` (flow/synthesis GPU ms, flow size, engine info).
General log every 10 s with generation on: `frame generation samples: N outputs, S boundaries skipped for want of a tracking sample (x %), W waited for one (mean m ms), H stamped
with one in hand; tracking spacing p95 / max (gaps over 15 ms)`; the `game ended` line carries the game's skipped boundaries. Event `frame_gen`
at start (and at every encoder rebuild). Frame fates (debug flavour, `host_pipeline_fates.csv`): `I` ingested (1 = pair), `G` synthetic
sent, `R` rendered frame sent, `Q` rendered frame re-sent; `host_frames.csv` slot column -2 (synthetic) / -3 (re-stamped rendered).

## Lab (headset-less)

`harness/simrig`: `python3 harness/simrig/run_sim.py build` then `run stutter,stutter_fg` (head still, app frame time drawn from 50-70 fps,
moving marker) and `run stutter_turn,stutter_turn_fg` (head turning 45 deg/s); `steady` (generator off, ideal timing) is the generator-off
reference (the rig's own ~6 % empty boundaries read as frozen periods there). The probe's `XR_PROBE_MOVING=25:0.25` draws a rectangle
swinging in world yaw 7 deg below the ring; the viewer measures it (`moving.csv`) and `judge.py` reports `motion`, measured per distinct shown
frame against the frame's timestamp (the rig's viewer wakes up late, so per-tick measurements were luck): hf rms of its world angle after a
local quadratic fit in time, frozen display periods (a new frame whose marker did not move, or a timestamp gap the host did not fill: frames the
viewer missed are cross-checked against `host_frames.csv` and counted apart), step regularity; verdict SMOOTH (hf <= 0.10 deg, frozen <= 5 %) /
JUDDER next to the usual CLEAN / TREMBLE of the head-rotation path. The marker's measurement also doubles as a ghosting probe: a synthetic frame
whose marker is doubled or lost to the crossfade fallback shows no single rectangle (`visible_pct`). `steady_fg` is the generator's null case (ideal timing, must be CLEAN + SMOOTH). Generated frames are always
rendered and encoded for real in the rig (the viewer shows the encoder's input).

Live harness: variant setting `{"frame_gen": true}`; scenario key `frame_gen` in `harness/scenarios/*.json`.

## Borrowed from OFXR-Bridge (MIT, github.com/tig3rmast3r/OFXR-Bridge)

The synthesis: rotation-compensated flow (there analytically subtracted, here by aligning A into B's camera before the engine),
midpoint sampling at ±flow/2, confidence = saturate(1 - 6 max|dRGB|) x (1 - cost/255), crossfade fallback, no hole filling; the
never-block rule (the generator runs in the encoder thread off the game's path; errors fall back to the plain frame). Not taken: its
D3D12 synthesis path and D3D11 interop (our frames are already composed side by side on the encoder's device), the FidelityFX backend
(NVIDIA only), the DLSS motion-vector provider (a later option: game motion vectors would replace the flow where DLSS runs).

## Known limits and open questions (2026-10-14)

- **Vision Pro re-stamp question: answered, fine.** Every output is stamped with a sample it was not rendered with and rotated to it; the
  Vision Pro trembled with the alternating host-side re-stamping of 2026-10-13, but with every frame treated the same (uniform staleness)
  the first headset session (2026-10-14, Callisto Protocol, 5090) was visually good, no tremble, 1.1 % missed display periods against 38 %
  without the generator, total latency 57 ms against 86 ms (the half-rate budget removes the GPU-bound game's standing queue). Generator cost
  on the 5090 at the foveated size: flow 2.3 ms, synthesis 0.13 ms.
- Interpolation only (t = 0.5): `--frame-gen-t` exists for experiments; extrapolation (t > 1) would remove the added period at the cost of
  disocclusion artefacts.
- Rates 1/3, 1/2, 2/3 and an Auto level that includes generation off (2026-10-16): a game holding a steady 70-75 fps is often better left at
  full rate; the checkbox and the Auto tick in the streamer window switch live.
- Quad/cylinder layers are composed into the frame by the shim before the generator sees them, so HUD quads are interpolated like the
  scene (OFXR-Bridge copies them verbatim instead).
- The flow runs on gamma-encoded colour at 50 %; `frame_gen_flow_scale` and `frame_gen_perf` (fast | medium | slow) are the two knobs.
- **Small fast high-contrast objects ghost.** The confidence saturate(1 - 6 max|dRGB|) is zero at any misaligned hard edge, so such an object
  falls back to the crossfade (two half-bright copies). The rig's marker (a white rectangle on black, ~9 px per app frame at 1024 px per eye)
  is lost in 11 % of the synthetic frames, 33 % at its top speed; game content is softer, but HUD elements and particles will show this.
  A softer confidence, or the engine's cost alone, is the first thing to try.
- **Sharpness.** Every output is a resample of the rendered frame at sub-pixel offsets (the rotation to the stamp sample; the flow displacement
  for synthetic frames), which the first headset session saw as a lower resolution. Since a1dfeb3 the colour fetches are Catmull-Rom (5 bilinear
  taps) and a rendered frame whose stamp orientation is within 0.02 deg of its render orientation is copied as is (`gen.copies`). If it still
  reads softer than the game, the next steps are a sharpening term in the output pass or sending rendered frames unwarped once the client can
  take their own stamp.
- **Game motion vectors** (DLSS inputs) would replace the flow where they exist, allow extrapolation (no added period) and depth-aware
  reprojection: design note docs/MOTION_VECTORS.md (in-process NGX tap at session creation, not an OptiScaler fork).
