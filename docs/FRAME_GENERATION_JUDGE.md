# Frame generation judge: generated frames against a rendered ground truth

`python3 harness/fg_judge.py` (host: `alvr_host --fg-judge <dir>`, `tools/host/src/fg_judge.rs`, `nvh_bench_fgj_*` in `nvenc/hostlib/bench.cpp`).
A lab measurement, not a verdict on how it looks: the eye and the brain see things PSNR cannot (judder over time, what a double image feels like in a headset).

## Method

The benchmark scene (Littlest Tokyo with its animation and scripted head, the same renderer as the quality benchmark and the OpenXR probe) is rendered by the
host's own D3D11 path at three moments, in the stream's frame format (10-bit, side by side):

* **A** and **B**: the pair of rendered frames a game at half the display rate delivers (pair gap 22.2 / 33.3 / 44.4 ms = 45 / 30 / 22.5 fps);
* **G**, the **ground truth**: the scene as it really is at the midpoint, for the head pose of the midpoint.

The streamer's own generator (`FrameGen`, flow at half scale, fast preset, grid 4: the shipped settings) ingests A then B and renders the midpoint with each
synthesis (classic, edge, edge-blend). Every result is scored against G on the luma:

| metric | what it says |
|---|---|
| PSNR whole frame, in the **moving region**, on the **boundary band** | the region is where A and B differ (|dY| > 6, dilated 6 px); the band is its outline (the part that is hard) |
| SSIM (8 x 8 windows) whole frame and in the moving region | structure, less blind to blur than PSNR |
| **edge excess** / **edge deficit** on the band | gradient energy the result has too much of (double images) or too little of (blur), as a share of the truth's |
| references | **hold** (repeat B) and **blend** (50/50 of A and B): a generator that does not beat these is no use |

Scenarios: `scene x1/8/24` (the scene animates at 1x, 8x, 24x speed, the head is still: its own animation is slow, ~3 % of the frame moves), `head` (scene frozen, the
scripted head moves: rotation reprojection and parallax) and `both x1/8/24`; `pan` (below) and `walk` (`--walks 0.5,1,2`: the camera strafes at these scene
metres per second, the scene and the head the generator is told stay still: a game's stick walk, parallax and newly uncovered background behind every near
object). Several start times along the animation (1.9 s apart). `--eye WxH` (default
1032 x 1104 per eye), `--starts N`, `--gaps`, `--speeds`. 63 cases take about 20 s on the 3080 Ti laptop. The report gives the means per scenario and rate and
the paired differences between the syntheses over all cases with their standard errors and win counts; crops of the moving region (truth, hold, the three
results and 4x difference images) are written as PNG montages.

## First run (2026-10-26, RTX 3080 Ti laptop, 1032 x 1104 per eye, 63 cases)

* **Edge against classic:** whole-frame PSNR +2.0 dB (edge better in 52 of 63 cases), SSIM in the moving region +0.013 (59 of 63), edge deficit (blur) lower in
  63 of 63. With the scene moving and the head still, classic scores 43.2 dB over the whole frame, below even repeating a frame (47.7) and far below edge (54.1):
  classic degrades the parts of the picture that did not move (it re-samples the whole frame at a sub-pixel offset). That is a measured reason for what the
  maintainer sees in games: edge looks better. Edge carries slightly more edge energy than the truth at the boundaries (+0.006): a little more double-edge risk.
* **Edge against edge-blend:** a statistical tie (PSNR on the boundary band -0.12 +- 0.08 dB, SSIM +0.004 +- 0.003). Edge-blend hedges where the motion is uncertain,
  which PSNR rewards; edge is slightly sharper (blur -0.004 +- 0.002). As indistinguishable by measurement as by eye.
* **Head moving:** the three are within 0.1 dB of each other (the rotation reprojection dominates; parallax and disocclusions are what limits all of them).
* **Large motion** (scene x24): holding B beats every synthesis at some rates. By design: a content shift beyond `motionLimit` (0.10 of the eye width) falls back to the still frame.

## Stick turns: the `pan` scenario (2026-11-20, RTX 5090)

`--pans 60,120,...` (deg/s; `fg_judge.py --pans`): the camera turns about the world's up axis while the scene stays frozen and the head the generator is told
does not move: what a game's stick turn is to the generator (field 2026-11-20, Hellraiser at 1.5x: trailing on the stick, none on head turns). `--views` renders
with an install's box, `--env AIRFRAMEXR_FG_MOTION_LIMIT=...` (and the other two knobs) changes the generator's tuning for the run.

Run on the owner's stream (1920 x 2368 per eye, box 0.92 / 0.58 / 0.90 / 0.77, 3 start times; `harness/results/20261120T210125Z_fg_judge`), whole-frame PSNR of
edge at a 24.4 ms pair gap (a 41 fps game): head turn 27.7 dB; stick 60 deg/s 27.3, 120 22.7, 180 20.2, 270 17.8, 360 16.1. The double-image energy on the
boundary band (edge excess) goes 0.12 (head) -> 0.18 / 0.32 / 0.45 / 0.62 / 0.77; blur rises with it. The score follows the step between the two frames
(about 3 deg per gap already costs 5 dB: 120 deg/s at 24 ms equals 180 deg/s at 16.7 ms); classic, edge and edge-blend all fail the same way (edge-blend
+0.3 dB). **Not the motion limit**: `AIRFRAMEXR_FG_MOTION_LIMIT=0.25` gives the same numbers (only 270 / 360 deg/s at 33 ms move, by 0.1 / 1 dB). The pictures
(`pan_x180_g24_pictures.png`) show torn, doubled vertical edges and ghosted texture; the head turn's are clean. A head turn never reaches the optical flow
(A is turned into B's camera first); a stick turn reaches it whole, and the flow as configured (half scale, fast preset, grid 4, no hints) does not follow it.

### Stick turns: the flow engine's settings, and the rotation oracle (2026-11-20, RTX 5090)

The 5090's optical-flow engine supports external hints in optical-flow mode (hint grids 1 2 4 8, output grids 1 2 4, ROI; `fgj_start` prints it).
The 3080 Ti was offline: not checked.

**Settings sweep** (lab-only `AIRFRAMEXR_FG_JUDGE_PERF` / `_FLOW_SCALE` / `_GRID`; 11 settings, 3 starts, gaps 16.7 / 24.4 ms, pans 60-270 deg/s): no setting fixes
stick turns. At 24.4 ms the shipped one (fast, half scale, grid 4: 1.05 ms) scores 22.7 / 20.2 / 17.8 dB at 120 / 180 / 270 deg/s; the best (slow or fast preset at
full scale, grid 4) 23.2 / 20.5 / 17.9 for 2.9-7.6 ms; finer grids (2, 1) are worse and dearer (1.4-19 ms). Head and scene cases do not move. The shipped
settings stay.

**Rotation oracle** (`AIRFRAMEXR_FG_JUDGE_PAN_ORACLE=1`: the generator is told the camera's turn as if it were the head's, the upper bound of compensating a
global rotation before the flow): the double images go away: edge excess at 24.4 ms 0.32 / 0.45 / 0.62 -> 0.14 / 0.15 / 0.17 (a head turn: 0.12), and the pictures
(`20261120T212442Z_fg_judge/pan_x180_g24_pictures.png`) are clean but for a black strip at the eye's edge, the part of the view neither rotated frame covers
(about half the turn per gap, ~50 px at 180 deg/s). Whole-frame PSNR falls (20.2 -> 19.1 at 180 deg/s) because of that strip alone: PSNR is the wrong score for this
case, the edge excess and the pictures are the right ones. So the flow is not what fails on a stick turn: the generator needs the turn removed first, as it does
for the head. The strip needs a fill (the other frame, or the edge clamped).

### The camera turn (lab switch `AIRFRAMEXR_FG_CAM_TURN`, 2026-11-20, RTX 5090)

`--pan-warm 3`: three frames before A, so each pan rate also runs as a sequence (`pan` sustained, `panstart` still then turning at A, `panstop` turning
then still, `panrev` turning then the other way): the prediction's cases. Edge synthesis, whole-frame PSNR off -> on (double-image energy), 3 starts,
the owner's eye and box (`20261120T223155Z_fg_judge` off, `20261120T223501Z_fg_judge` on):

| 24.4 ms (41 fps) | 60 deg/s | 120 | 180 | 270 |
|---|---|---|---|---|
| sustained | 27.1 -> 35.8 (0.18 -> 0.11) | 22.8 -> 33.4 (0.32 -> 0.13) | 20.2 -> 31.5 (0.45 -> 0.14) | 17.7 -> 29.3 (0.62 -> 0.17) |
| start | 27.3 -> 35.9 | 22.8 -> 33.5 | 20.2 -> 31.3 | 17.7 -> 29.1 |
| reverse | 26.6 -> 35.0 | 22.1 -> 32.9 | 19.7 -> 31.0 | 17.2 -> 28.9 |
| stop | ~97 -> ~100 (a still pair) | | | |

At 33.3 ms (30 fps) the same shape (180 deg/s 18.3 -> 30.0, reverse 17.9 -> 29.5). Head turns 27.7 -> 36.0 and `both` 27.7 -> 35.8 (the edge fill);
scene x1 / x8 unchanged (56.2 / 42.6). The measured turn of a pair equals the truth within 0.01 deg. Lessons on the way: the unpack pass's alpha is
data, so the generator sets its own blend state (the benchmark scene left alpha blending bound and broke every edge result once); a prediction
from the last pair's turn alone missed on most real pairs (2 / 3 display periods apart): the rate times the pair's own time step fixed it.

## The game's depth: what perfect depth buys the edge synthesis (2026-11-22, RTX 5090, fg-testing)

The question: is forwarding a game's depth layer (XR_KHR_composition_layer_depth, which VDXR already hands the shim as `ovrLayerType_EyeFovDepth` and
the shim drops) worth building for frame generation? Measured with the best depth there is, the scene's own depth buffer, before any shim work.
`--env AIRFRAMEXR_FG_JUDGE_DEPTH=1` adds a fourth generator, `edge_depth`: the edge synthesis with each frame's depth (`FrameGen::Ingest` takes NDC depth
and LibOVR's P22 / P23, what VDXR fills from the layer's near / far; the multisampled depth is resolved to each pixel's nearest sample). Its rule is
`AIRFRAMEXR_FG_DEPTH_MODE` (framegen.h `depthMode`; knobs `_TOL` 0.06, `_OCC` 0.08, `_MARGIN` 0.03); the report pairs it with `edge` on the same cases.
Depth off, the output pass compiles to the same bytecode as before (the depth path sits behind a shader macro).

Paired difference `edge_depth - edge`, PSNR on the boundary band, 60 cases each (3 starts, 2 gaps, head / scene / both / walk 0.5, 1, 2 m/s):

| rule | 1032 x 1104 | owner's stream (1920 x 2368, box 0.92 / 0.58 / 0.90 / 0.77, gaps 24.4 / 33.3 ms) |
|---|---|---|
| mode 0: candidates classified by depth (one surface / newer frame only / older frame only), one-sided content drawn from its one frame, the nearer surface wins near-ties, also where the solved vector's ends are not one surface | -0.61 +- 0.08 dB, 6 / 60 wins | -0.65 +- 0.14, 2 / 60 |
| mode 0 without the one-sided picks (`_OCC=10`) | -0.44 | |
| mode 0 without the near-tie rule (`_MARGIN=0`) | -0.72 | |
| mode 0 without either (only the extra places where candidates are re-chosen) | -0.46 | |
| mode 1: the edge synthesis exactly, depth only breaking near-ties at its own motion boundaries | +0.04 +- 0.01, 40 / 60 (walk +0.04 .. +0.10) | +0.02 +- 0.004, 46 / 60 (walk +0.04 .. +0.08) |
| mode 2: mode 1 + the ghost gate (the older frame not blended in where its end is a nearer surface) | -0.16 +- 0.06 (animated scene -0.4 .. -1.2) | |

Reading: the edge synthesis's blend of both frames already hides most boundary errors; every rule that replaces it with a hard per-pixel decision
(re-chosen vectors, one-frame-only drawing, the gate) loses more where it is wrong than it wins where it is right, and adds speckle along the edges
(the nearest-pixel depth flips at sub-pixel edges). The one part that helps, depth as the near-tie breaker, is worth a few hundredths of a dB: invisible.
What limits the walk instead is the strip each eye's border uncovers (content neither frame has there, as for a turn) and thin silhouettes at the flow's
block size (8 px at full resolution); depth is not the missing information for either. Not tried: depth-guided vector fill in wide disocclusions (a
background vector from beyond the 2 x 2 block neighbourhood) and depth-based extrapolation (no wait for the newer frame), both larger designs.
Verdict: depth forwarding (shim + IPC + host) is not worth building for frame generation's boundaries; the lab switch stays on fg-testing.

## A neural interpolator offline: RIFE 4.4 against the edge synthesis (2026-11-22, RTX 5090, fg-testing)

`--env AIRFRAMEXR_FG_JUDGE_DUMP=<dir on the builder>` writes every case's full frames A, B, the truth G and the edge synthesis's output with a
`cases.jsonl` index. `harness/rife/rife_eval.py` (venv from `harness/rife/setup_rife_5090.ps1`: PyTorch 2.11 cu128, RIFE 4.4 = vs-rife's model code at
3488617, MIT, weights `flownet_v4.4.pkl`, both downloaded, not in this repo) interpolates the midpoint per eye (batch 2, FP16) at RIFE scales 1, 0.5,
0.25 (scale < 1: the network at reduced resolution, the final warp at full resolution) and scores it with a numpy port of this judge's metrics (equal
to the judge's own edge numbers within 0.0000 dB on all 60 cases). RIFE gets the raw A and B: no head-rotation removal, unlike the edge synthesis.

60 cases (1032 x 1104, 3 starts, gaps 22.2 / 33.3 ms), band PSNR, RIFE minus edge:

| | scale 1 | scale 0.5 | scale 0.25 |
|---|---|---|---|
| all cases | +0.66 +- 0.29 dB (31 / 60) | +0.01 | -2.36 |
| walk 0.5 / 1 / 2 m/s | +1.3 / +3.4 / +4.8 | +0.1 / +2.1 / +3.4 | -3.3 / -1.4 / +0.6 |
| scene x1 / x8 / x24 | +1.0 / +0.7 / +0.3 | +0.1 / +0.7 / -0.3 | -2.1 / -0.1 / -0.1 |
| head, both | -1.0 .. -1.4 | -1.2 .. -1.8 | -3.9 .. -4.9 |

Double images (edge excess): RIFE lower in 55 / 60 cases at scale 1; blur (edge deficit): RIFE higher in 60 / 60 (softer). The walk crops show the
edge synthesis tearing outlines where RIFE keeps them whole. The head / both losses are expected without the rotation removal: the next test feeds
RIFE the older frame aligned into the newer one's camera, as the flow gets it. Time (PyTorch eager FP16, no TensorRT, both eyes): 1032 x 1104 per
eye 5.2 / 3.9 / 3.7 ms at scale 1 / 0.5 / 0.25; the owner's 1920 x 2368 per eye 21.0 / 12.7 / 11.5 ms (launch-bound at small scales; TensorRT
and CUDA graphs not tried). These run on the shader cores the game needs, unlike the optical-flow engine.

## What it does not tell

One scene, one GPU, one frame size; PSNR rewards blending and SSIM is a proxy; nothing here sees judder over time or the feel of a double image in a headset.
It is a lab tool for comparing encodes and syntheses on equal terms, not a replacement for looking.
