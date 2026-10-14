# Frame generation (optical flow)

Branch `fg/optical-flow`, first version 2026-10-14. Off by default; experimental.

## What it does

Games that cannot hold the display rate (50-70 fps on a 90/100 Hz headset) look choppy: the headset repeats a frame every few
ticks, and in-scene motion (and everything the headset's own rotation reprojection cannot fix: translation, moving objects,
animation) stutters. With frame generation on:

1. The shim paces the game at **half the display rate** (IPC `appPeriodMul` 2: `WaitForVsync` releases the app every other compositor
   boundary; the predicted display time moves one period later, see below). A 60 fps game becomes a steady 50 fps (100 Hz) or 45 fps
   (90 Hz) game with a 20 ms budget per frame.
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

Rules of the per-boundary decision (`FgState`): the output schedule is a strict alternation, synthetic then rendered. A rendered frame
that arrives while the previous one is still due waits in the intake for one boundary (`fg_queued`); a boundary with nothing due waits up to
`VISIONALVR_FG_ARRIVAL_WAIT_MS` (4) past the boundary for the frame the app is about to publish (`fg_arrival_waits` / `fg_arrival_hits`) and
only then repeats the rendered frame. Both rules exist because an app released at a boundary publishes its frame near a boundary: without
them the schedule flipped between "just before" and "just after" (a frame dropped, then one frozen) at 6 % of the boundaries in the rig's
ideal-timing case. A boundary with no tracking sample newer than the last sent frame sends nothing (`fg_no_sample`); a frame without a
projection layer (quad-only loading screens) goes out as is; a frame still due when the encoder overran is dropped for the synthetic frame
between the two newest (`fg_dropped`). With the generator on the newest waiting frame always wins a pick (older ones superseded): the shim's
half-rate pacing runs open loop and the generator consumes one frame per two boundaries, so a standing queue would never drain otherwise.
When the GPU/driver has no flow engine the host logs a WARN, streams as without the feature and keeps the game at full rate.

## Telemetry

`encoder_stats.pipeline.frame_gen`: `synths`, `reals`, `repeats`, `holds` (boundaries filled by the generator), `no_sample`, `dropped`,
`queued`, `arrival_waits` / `arrival_hits` / `arrival_wait_ms_sum`, `sample_waits`, `pairs_skipped`, `errors`, `passthrough`, and `gen` = `nvh_fg_stats` (flow/synthesis GPU ms, flow size, engine info). Event `frame_gen`
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
- Always half rate when on, by design: no automatic engagement (the user's call: a game holding a steady 70-75 fps is often better left at full
  rate; the checkbox in the streamer window switches live).
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
