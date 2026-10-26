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

## What it does not tell

One scene, one GPU, one frame size; PSNR rewards blending and SSIM is a proxy; nothing here sees judder over time or the feel of a double image in a headset.
It is a lab tool for comparing encodes and syntheses on equal terms, not a replacement for looking.
