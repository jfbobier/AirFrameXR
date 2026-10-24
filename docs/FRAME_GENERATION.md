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
When the GPU/driver has no flow engine the host logs a WARN, streams as without the feature and keeps the game at full rate. **The regime switch, and the hold (2026-10-23, `video.frame_gen_hold_age`, default off after one headset night).** With one sample in hand every output's stamp
advances by exactly one of the headset's samples per boundary, and a sample late by less than a period costs nothing (the one in hand covers
its boundary, the late one is stamped at the next). A sample late by more than a period, or lost, left a boundary with a single unused
sample, which was stamped: the stamp jumped two samples and every later output was one period younger, until a skipped boundary followed by
a bunch rebuilt the one in hand and moved them back. On the Vision Pro that was the total latency switching between 67 and 77 ms in 10-20 s
cycles, each switch a pose-age jump shown as a hitch while the head turns. `regime_switches` counts these switches (the number
of unused samples left behind the stamped one changing between one and none). With the hold on (`VISIONALVR_FG_HOLD_AGE=1` or the setting true) such a boundary waits for the next sample like any other and otherwise repeats once (`hold_skips`); more than 5 of those in a second step
the hold aside until they thin out (`hold_bypassed`). The rig argued against it as a default: its uplink tail (up to 14 ms, over a period) makes such
late samples frequent, the hold skipped 3.4 boundaries a second and the head path went TREMBLE (`late_turn_fg`, one sample in 150 delivered two
and a half periods late, hold on: 149-208 hold skips, 22-57 bypassed, 114 regime switches left, head TREMBLE once and CLEAN once, motion JUDDER; hold off: 60-75 skipped boundaries, 1278 regime switches a minute, CLEAN and SMOOTH). The Vision Pro's Wi-Fi is
like that: the night of 2026-10-23 (Gears, 4 h 39 at 1/2, hold on) counted 5.5 regime switches a second, hold skips 2.4 a second = 97 % of all
skipped boundaries (2.6 % of boundaries against 0.6-2.4 % with the old rule on earlier nights), the bypass engaging about once a second. So the
67/77 ms reading is not a slow cycle but a sample late by more than a period about three times a second, a Wi-Fi fact the pipeline can only
trade between a repeat and a pose-age jump; the old rule (jump) stays the default, the counters stay.

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

### Game profiles (2026-10-17): the engine records, the launcher proposes

The in-engine controller ("Auto": a level ladder with trials, a settle rule, an off level, a per-game memory) was removed on 2026-10-17 after
two headset nights. It worked where the stream's verdict was clean, but every trial was a visible hiccup, its memory was keyed by executable
only (the Vision Pro's verdicts polluted the Quest's, half the pixels), and the "off" level it was built to reach lost to 2/3 on both
headsets (the plain path repeats 9-14 % of the display periods at display rate). The level is now **fixed per run** as far as the engine is
concerned; the launcher owns the judgement.

- **The host records.** At every game end (runs of 20 s or more) one entry in `config/games.json`, keyed by executable, then the headset's
  name, then the level (`third` | `half` | `two_thirds` | `off`): duration, display Hz, rendered and streamed fps, the headset's repeated
  display periods as the **median 10-second window** of the run (`headset_repeated_typ`, what the game is like most of the time) and its
  90th percentile (`headset_repeated_p90`, the heavy parts), the rendered fps of the slowest tenth of the windows (`rendered_fps_low`), the
  host's repeat share, the skipped-boundary share, the run count. A level
  changed during the run restarts the record from that moment, so a record belongs to one level. Log line `game run recorded:` and event
  `game_run`. The host decides nothing.
- **The launcher proposes.** A game is only known once it streams, so the proposal is applied at game start (the first second runs at the
  slider's level, the record restarts at the switch); the slider is the default for a game without a record, and the user's choice with the
  tick off. The "Recommended" tick (`video.frame_gen_recommended`, on by default) reads games.json when a game starts on the connected headset: the fastest level whose last run was clean (typical window: the headset repeated under 5 % of its periods AND the
  host repeated under 5 % of its outputs, run of 30 s or more) is set on the
  slider, which is greyed while the tick is on; if that run filled its cap (rendered at least 95 % of the level's fps) and the next level
  has no run yet, the next level is proposed once, and its run then speaks for itself. No clean level: the one with the fewest repeats.
  No run at all for the game on this headset: nothing is proposed and the slider stays live (1/2 on a fresh install). Unticked, the slider
  is yours at its current value. The proposal is made on the typical window, not the worst: hiccups are a fact of life in VR and a heavy
  corridor must not cost the rest of the game a level (user, 2026-10-17). No pop-up: when the game ends the launcher's "Streaming" box turns
  into "Streaming (last session stats)" (average and slowest-tenth fps, repeated periods typical and heavy parts, host repeats, skipped) with
  the line "Frame generation recommendation: ... next time"; the live numbers come back with the next game.
- **Recenter.** A generic host command `recenter` turns the published tracking space so the head's forward at that moment is forward and
  its position the origin (height as before). The launcher sends it at every game start (verified on the Quest 3 with Valor Mortis and
  Carma, 2026-10-17; the Vision Pro's own centring at connection agrees with it) and has a "Recenter" button.

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

`encoder_stats.pipeline.frame_gen`: `synths`, `reals`, `repeats`, `holds` (boundaries filled by the generator), `no_sample`, `in_hand`, `hold_age`, `hold_skips`, `hold_bypassed`, `ts_jumps`, `dropped`,
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

## Synthesis: edge (default) and classic (0.6.1)

Two syntheses draw a generated frame from the same optical flow (`video.frame_gen_synthesis`, Advanced settings):

- **edge** (default since 0.6.1), ideas from VectorXR (MPL-2.0, github.com/DienerTech/vectorxr), re-implemented: per flow block our own luma
  match cost (sampled at the sub-texel vector; a vector must beat no motion, which zeroes the engine's noise vectors on flat content) and a
  3x3 cost-weighted vector median; per output pixel the vector solved where the content comes from (two fixed-point steps), at motion
  boundaries the neighbouring vector both frames agree on, doubtful motion shrunk toward the real frame (a little local judder instead of a
  double image), the previous frame blended in by the time weight, gated by its colour agreement only as far as the vector is doubted.
  `edge-blend` is the same without that gate. Lab (5090, steady_fg / stutter_fg, marker position error): classic 0.024-0.031 deg, edge
  0.030-0.036, edge-blend 0.017-0.022, all CLEAN + SMOOTH; headset (Valor Mortis, 2026-10-24): edge "looked great". Cost (5090, 7104x3200,
  stutter_fg, all outputs): synthesis 0.24 ms average against 0.33 for classic, flow 2.17 ms either way (the two extra passes after the
  flow are within its noise), no added latency.
- **classic**: the 0.4-0.6 recipe below, kept as the predictable fallback.

The game's DLSS motion vectors were an alternative motion source from 0.4 to 0.6; they were removed in 0.6.1 (docs/MOTION_VECTORS.md).

## Borrowed from OFXR-Bridge (LGPL-3.0, github.com/tig3rmast3r/OFXR-Bridge): the classic synthesis

The synthesis: rotation-compensated flow (there analytically subtracted, here by aligning A into B's camera before the engine),
midpoint sampling at ±flow/2, confidence = saturate(1 - 6 max|dRGB|) x (1 - cost/255), crossfade fallback, no hole filling; the
never-block rule (the generator runs in the encoder thread off the game's path; errors fall back to the plain frame). Not taken: its
D3D12 synthesis path and D3D11 interop (our frames are already composed side by side on the encoder's device), the FidelityFX backend
(NVIDIA only), the DLSS motion-vector provider.

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
  scene (OFXR-Bridge copies them verbatim instead). A frame with no projection layer at all (a loading screen or a menu made of quads:
  Zelda BotW through BetterVR) is passed through as it is and drops the generator's held frame, so nothing stale is repeated between such
  frames (2026-10-16: the inventory alternated with the last world frame before this).
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

Why both repeat counts (2026-10-17, Valor Mortis on the Vision Pro at 100 Hz): at 1/2 the game rendered 39 fps against a 50 fps cap, the host
re-sent its last frame on 14 % of the outputs (`host_repeats_typ`) while the headset itself repeated only 0.4 % of its periods (it was shown
a frame every period, with frozen content). A proposal made on the headset's count alone said 1/2; the user's eyes and the host's count said
1/3 (0.2 % host repeats, 0.5 % headset). Both are in the record and both must be under 5 % for a level to count as clean.
