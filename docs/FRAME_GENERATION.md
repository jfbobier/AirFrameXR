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

Cost: one display period of added latency for the rendered frames (an interpolated picture is shown before each of them), plus the
generation itself (flow ~2-4 ms expected on an RTX 5090 at 3552x1600 input, synthesis ~1 ms at the full 7104x3200 frame, to be measured).

## Where it lives

| Part | File | Role |
|---|---|---|
| Pacing | `ovrshim/driver.cpp` `WaitForVsync`, `GetPredictedDisplayTime` | release every `appPeriodMul` boundaries, display time + (mul-1) periods |
| Render orientation | `ovrshim/driver.cpp` `FrameRenderOrientation`, `ovrshim/ipc.h` `frameRenderQuat` (IPC v25) | the per-eye orientation each frame was rendered with |
| Generator | `nvenc/hostlib/framegen.{h,cpp}` | NVOF session, flow-input prep pass, synthesis/rotation pass, output texture |
| Host glue | `nvenc/hostlib/nvh.cpp` `nvh_fg_*` | own foveation pass on the output, encode tail shared with the slot path, stats JSON |
| Decision per boundary | `tools/host/src/pipeline.rs` `FgState`, `fg_plan_app`, `fg_plan_hold` | synth / real / repeat, sample stamping, counters |
| Sample ring | `tools/host/src/main.rs` `Shared::recent_samples`, `push_recent_sample` | the last 48 tracking samples with their head orientation |
| Settings | `config/visionalvr.json` `video.frame_gen` (+ `frame_gen_flow_scale`, `frame_gen_perf`), `--frame-gen on|off`, `VISIONALVR_FRAME_GEN=1/0` | CLI > env > settings; GUI checkbox "Frame generation" in Settings |
| Headers | `third_party/nvof/` | `nvOpticalFlowCommon.h`, `nvOpticalFlowD3D11.h` (MIT notice; nothing of NVIDIA's is redistributed) |

Rules of the per-boundary decision (`FgState`): a rendered frame arriving while the previous one is still due sends the previous one and
skips the synthetic frame for that pair (the app ran faster than half rate: two frames, two boundaries); a boundary with no sample newer
than the last sent frame sends nothing (`fg_no_sample`); a frame without a projection layer (quad-only loading screens) goes out as is.
When the GPU/driver has no flow engine the host logs a WARN, streams as without the feature and keeps the game at full rate.

## Telemetry

`encoder_stats.pipeline.frame_gen`: `synths`, `reals`, `repeats`, `holds` (boundaries filled by the generator), `no_sample`, `dropped`,
`pairs_skipped`, `errors`, `passthrough`, and `gen` = `nvh_fg_stats` (flow/synthesis GPU ms, flow size, engine info). Event `frame_gen`
at start (and at every encoder rebuild). Frame fates (debug flavour, `host_pipeline_fates.csv`): `I` ingested (1 = pair), `G` synthetic
sent, `R` rendered frame sent, `Q` rendered frame re-sent; `host_frames.csv` slot column -2 (synthetic) / -3 (re-stamped rendered).

## Lab (headset-less)

`harness/simrig`: `python3 harness/simrig/run_sim.py build` then `run stutter,stutter_fg` (head still, app frame time drawn from 50-70 fps,
moving marker) and `run stutter_turn,stutter_turn_fg` (head turning 45 deg/s). The probe's `XR_PROBE_MOVING=25:0.25` draws a rectangle
swinging in world yaw below the ring; the viewer measures it (`moving.csv`) and `judge.py` reports `motion`: hf rms of its world angle
after a local quadratic fit, frozen ticks (repeated frames), step regularity; verdict SMOOTH / JUDDER next to the usual CLEAN / TREMBLE of
the head-rotation path. `steady_fg` is the generator's null case (ideal timing, must be CLEAN + SMOOTH). Generated frames are always
rendered and encoded for real in the rig (the viewer shows the encoder's input).

Live harness: variant setting `{"frame_gen": true}`; scenario key `frame_gen` in `harness/scenarios/*.json`.

## Borrowed from OFXR-Bridge (MIT, github.com/tig3rmast3r/OFXR-Bridge)

The synthesis: rotation-compensated flow (there analytically subtracted, here by aligning A into B's camera before the engine),
midpoint sampling at ±flow/2, confidence = saturate(1 - 6 max|dRGB|) x (1 - cost/255), crossfade fallback, no hole filling; the
never-block rule (the generator runs in the encoder thread off the game's path; errors fall back to the plain frame). Not taken: its
D3D12 synthesis path and D3D11 interop (our frames are already composed side by side on the encoder's device), the FidelityFX backend
(NVIDIA only), the DLSS motion-vector provider (a later option: game motion vectors would replace the flow where DLSS runs).

## Known limits and open questions (2026-10-14)

- **Vision Pro re-stamp question.** Every output is stamped with a sample it was not rendered with and rotated to it. The Quest 3 showed such
  frames cleanly; the Vision Pro trembled slightly with host-side re-stamping on 2026-10-13 (mechanism unknown, parked). Frame generation
  re-stamps *every* frame, so the staleness is uniform rather than alternating; whether the Vision Pro is happy with that is the first
  thing the headset test decides.
- Interpolation only (t = 0.5): `--frame-gen-t` exists for experiments; extrapolation (t > 1) would remove the added period at the cost of
  disocclusion artefacts.
- Always half rate when on: no automatic engagement yet (a game that could run at the display rate is held at half rate). An "auto" mode
  (engage when the app's frame-time envelope exceeds the period) is the next step once the headset verdict is in.
- Quad/cylinder layers are composed into the frame by the shim before the generator sees them, so HUD quads are interpolated like the
  scene (OFXR-Bridge copies them verbatim instead).
- The flow runs on gamma-encoded colour at 50 %; `frame_gen_flow_scale` and `frame_gen_perf` (fast | medium | slow) are the two knobs.
