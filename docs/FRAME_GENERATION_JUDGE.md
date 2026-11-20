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
scripted head moves: rotation reprojection and parallax) and `both x1/8/24`. Several start times along the animation (1.9 s apart). `--eye WxH` (default
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

## What it does not tell

One scene, one GPU, one frame size; PSNR rewards blending and SSIM is a proxy; nothing here sees judder over time or the feel of a double image in a headset.
It is a lab tool for comparing encodes and syntheses on equal terms, not a replacement for looking.
