# Apple Vision Pro: declared vs visible field of view

## Calibrated values (2026-10-29), the current default

Two sweeps on the 5090 (`docs/FOV_CALIBRATION.md`: a dot drifts outward along 24 rays per eye, the wearer clicks when it is gone; data in [`fov_sweeps/`](fov_sweeps/)), one person, one fit:
eyes **following** the dot, and eyes **fixed** on the cross. The two windows differ a lot at the right eye's outer edge (35.5 deg following, 43 to 47 fixed), and a game is played with
the eyes mostly forward, so the rectangle has to cover **both**. One mirrored rectangle for both eyes (the host's override is mirrored): the smallest one that covers 99 % of the larger of
the two eyes' windows, over both gaze states. A first set that used the following sweep only (0.71 / 0.59 / 0.61 / 0.57, release 1.1.0) left a black strip at the right eye's outer edge in a game.

| | Outer | Inner | Upper | Lower |
|---|---|---|---|---|
| **Shipped default (99 % of the author's window)** | **0.97** | **0.71** | **0.69** | **0.63** |
| angle | 44.1 deg | 35.4 | 34.6 | 32.2 |
| The author's 100 % set (what the sweeps and the grid showed) | 1.00 | 0.71 | 0.73 | 0.66 |

**Release 1.2.0 ships the 99 % rectangle as the default** (the wizard applies it with the profile; 26 % fewer game pixels than an unrestricted game on the AirFrameXR client, a sliver of black at the far outer and lower corners): the author chose the compromise over the 100 % set for about 5 points more saving. The 100 % set, found on 2026-10-29 night (the ceiling client, which declares 1.045 / 0.800 / 0.759 / 0.871, so every value is inside what the headset shows): the lower edge from the third sweep
(0.61 -> 0.66, fit 3 below), the outer and upper edges from the grid read with the whole box shown (the 45-degree band visible on both sides; the picture visible up to the frame's
top at 37 degrees, so 0.73 keeps a degree of margin), the inner edge unchanged. About 13 % more game pixels than the 1.1.1 set (0.96 / 0.71 / 0.69 / 0.61). A trial at lower 0.70
read the same at the bottom as every frame before it (the band below the 30-degree line cut), so the sweep's 0.66 stands there.

Played by the author in a game on the Vision Pro (one session, one fit): the black strip was gone. Not measured: frame time, HUD corners in other games, other wearers.

The wearer's eyes differ (right eye 44 deg outward but 28 toward the nose, left eye 37 / 35 / 35 / 31): per-eye boxes would need about 13 % fewer pixels than the mirrored one, but the override cannot express them, only a client that declares a box per eye (`docs/FOV_PIPELINE_OPTIM.md`).

**Fit 3 (2026-10-29 night, following gaze only, Sense controller clicks, data in `fov_sweeps/fov_sweep_2026-10-29_jf_avp_fit3_following_run1.csv`):** 48 rays, no time-outs,
run under the ceiling client (declared 1.045 / 0.800 / 0.759 / 0.871, not clamping: every crossing is inside). Tight box per eye: left 0.77 / 0.70 / 0.63 / 0.66, right 0.86 / 0.53 / 0.56 / 0.63;
shared 0.86 / 0.70 / 0.63 / 0.66. Against fit 1's following pass it is the larger on every edge, so the union of the two following fits is tonight's box. Over all three of the author's
sweeps (fit 1 following and fixed, fit 3 following) the window's bounding box is 1.03 / 0.74 / 0.71 / 0.66 and the 99 % rectangle 0.97 / 0.71 / 0.69 / 0.63; the calibrated set above covers
99.2 % of it. **The one edge the new data moves is the lower one: 0.61 -> 0.66** (100 % of all data; 0.63 for 99 %), which matches what the wearer saw on the grid the same night (no
limit visible at the bottom). **Applied the same night** together with the grid's outer and upper readings: 1.00 / 0.71 / 0.73 / 0.66 (profile and the author's settings). The fixed-gaze
pass of fit 3 was not run.

Render quality 0 (no oversampling) is the default: the game renders at the floor size, so all the savings go to performance; the slider in the Advanced window raises it for supersampling.
How much that saves depends on the frame the headset declares (computed from tangent areas, not measured): against the stock app's declared 1.79 / 1.02 / 1.22 / 1.02 frustum the game
renders about 35 % of the pixels; against the AirFrameXR client's own visible limit (0.94 / 0.93 / 0.90 / 0.76, what the 5090's headset declares) about 68 %, and the earlier "recommended" row below
restricted nothing at all there. Do not read the old "about half the pixels" below as a saving on that client.

## Second wearer: Kevin (2026-10-29, calibration bundle, one run)

Sweep `fov_sweeps/fov_sweep_2026-10-29_kevin_fit1_following_run1.csv` (`run_sweep.bat`: eyes **following** the dot, 2 deg/s, 50 ms latency compensation, both eyes,
24 rays each, no timeouts; his headset ran AirFrameXR 1.1.1 with the AirFrameXR client, which declared 0.946 / 0.936 / 0.906 / 0.765, override off during the sweep).
Measured window, following gaze: left eye outer 43.0 / inner 34.0 / up 32.4 / down 34.0 deg, right eye 39.9 / 30.8 / **24.8** / 37.9. The two eyes differ by 7.6 deg at the
top (the headset sits lower in front of the right eye, or the fit): the mirrored box takes the larger of the two per edge. `python3 harness/fov_calibration.py --sweep <csv>`:
the shared window's tight box is 0.93 / 0.67 / 0.69 / 0.80, its 99 % rectangle 0.92 / 0.65 / 0.64 / 0.79.

| Set (override tangents outer / inner / upper / lower) | degrees | game pixels | covers his window (left / right) | when |
|---|---|---|---|---|
| A tight: the whole following-gaze window | 0.93 / 0.67 / 0.69 / 0.77 (42.9 / 33.8 / 34.6 / 37.4) | 74 % | 100 / 99.1 % | the most saving; the following sweep underestimates the edges (below) |
| **B recommended: A plus 2 degrees per edge** | **0.95 / 0.72 / 0.74 / 0.77** (43.4 / 35.8 / 36.6 / 37.4) | **80 %** | 100 / 99.1 % | start here, render quality 0 |
| C safe: A scaled by the author's following-to-final ratios (1.35 / 1.20 / 1.13 / 1.07) | 0.95 / 0.81 / 0.78 / 0.77 (43.4 / 38.9 / 38.0 / 37.4) | 86 % | 100 / 99.1 % | if B shows black at an edge in a game |
| the author's calibrated set, for comparison | 0.96 / 0.71 / 0.69 / 0.61 | 68 % | 96.8 / **90.6** % | does not fit Kevin: a black band at the bottom of his right eye |

**Run 2 (same evening, 23:38, `fov_sweeps/fov_sweep_2026-10-29_kevin_fit2_following_run2.csv`) contradicts run 1 and changes the proposal.** Every ray reads wider:
+7.0 deg on the left eye (+1.6 to +12.5), +10.0 on the right (one timeout), and **12 of 24 rays on the left eye and 14 of 24 on the right end at or beyond the edge of the rendered
frame** (the client's declared box, 43.4 / 43.1 / 42.2 / 37.4 deg on the axes): the dot left the frame before it left his view, so those rays measure the frame, not the lens,
and the uncensored rays sit 1-6 deg inside it on the left eye. Read literally, run 2 says Kevin sees the whole box the AirFrameXR client declares on more than half of the
directions, and its 99 % rectangle (1.00 / 0.89 / 0.91 / 0.82) clamps to the declared box on three edges: nothing left to restrict. A 7-10 degree change between two runs three
hours apart is a change of fit (eye relief, light seal) or of criterion (clicking when the dot dims against when it is gone), not measurement noise; the author's single fit cannot
say which. Consequence for the proposal: the safe setting for Kevin on this client is **override off** (set B of run 1 saves 20 % of the game's pixels at the price of black edges
if run 2 is the true fit); and his window cannot be measured on this client at all where it is censored. Next: one sweep with a client that declares the full frustum (the stock
ALVR visionOS app, 1.79 / 1.02 / 1.22 / 1.02, or a build with the visible limit off), eyes **fixed** on the cross and then following, the headset put on once and left alone,
the same click criterion (the dot gone) in both; that run gives the rectangle. Until then the client's baked-in limit is the thing cutting his view, not the override.

Two things the run says beyond the numbers. (1) The lower edge is **capped by the client, not by the override**: Kevin's right eye sees 37.9 deg downward and the
AirFrameXR client declares 37.4 (the author's limit 0.765), so the frame's bottom edge is visible to him in the right eye whatever the override says, and the 99.1 % right-eye
coverage above is that cap. A visible limit baked into the client fits one wearer; the fix is the rectangle owned by the PC per wearer (docs/FOV_PIPELINE_OPTIM.md, step S6), or
a looser client constant (lower 0.80 or more). (2) The author's sweeps showed the following protocol under-reads the outer edge badly (35.5 deg following against 43-47 fixed on the
right eye), which is why the margin of set B and the fixed-gaze run exist: a second run with the eyes on the cross (`XR_PROBE_SWEEP_GAZE=fixed`, six minutes) would replace B by a
measured rectangle. Check as always with `fovgrid.exe` (the white border at the visible edge) or an intro screen, and raise the edge that shows black.

The tables below are the first estimate (2026-10-27, grid traced by eye, left eye), kept for history; the calibrated row replaces its "Recommended" row.

Measured 2026-10-27 with the stock ALVR visionOS app (RealityKit renderer, FoV scale 1.0) and `fovgrid.exe`
(`docs/FOV_GRID.md`). One person, one fit of the headset, **left eye only**: a first estimate, not a calibration.

## What the AVP declares

The app tells the host the render views' field of view (`fovgrid.log`, `views_config` host events). Tangents of the
half-angles, left eye (the right eye is mirrored, outer and inner swap):

| Edge | tangent | angle |
|---|---|---|
| Outer (away from the nose) | 1.787 | 60.8 deg |
| Inner (nose side) | 1.020 | 45.6 deg |
| Up | 1.219 | 50.6 deg |
| Down | 1.020 | 45.6 deg |

Total 106 x 96 deg, recommended size 3552 x 3200 per eye at 90 Hz. The Quest 3 declares 94 x 99 deg
(54.0 / 40.0 / 44.0 up / 55.0 down) and shows it all.

## What is visible

The grid was shown head-locked on the AVP and the edge of the visible part drawn on its picture
([`images/avp_fov_left_measured.png`](images/avp_fov_left_measured.png), stroke about 30 px = 2 deg wide; the centre of
the stroke is used):

| Edge | visible | declared | visible / declared (tangent) |
|---|---|---|---|
| Outer | 40.3 deg | 60.8 deg | 48 % |
| Inner | 40.0 deg | 45.6 deg | 82 % |
| Up | 39.0 deg | 50.6 deg | 66 % |
| Down | 34.4 deg | 45.6 deg | 67 % |

The AVP shows about 1.69 x 1.49 (tangent width x height) of the frame; the Quest 3 declares 2.22 x 2.39. Horizontally
the Quest shows 1.31x more of the game (the "about 30 % more real estate" seen on intro screens), vertically 1.6x.
Full-frame content (intro screens, HUDs placed at the frame's edge) is cropped on the AVP.

Why the crop (lens and eye position, or the app's mapping of the frame) is not known: this test cannot tell the two apart.

## Recommended FoV restriction

Advanced picture and FOV settings, "Field of view override" ticked: the four fields are the **tangents** the game is
told (X.XX; the window shows the angle next to each), outer = the side away from the nose, inner = towards it
(`fov_outer`, `fov_inner`, `fov_upper`, `fov_lower` in the settings). The headset's own values are 1.79 / 1.02 /
1.22 / 1.02.

| | Outer | Inner | Upper | Lower |
|---|---|---|---|---|
| Tight (exactly the visible part) | 0.85 (40.3 deg) | 0.84 (40.0) | 0.81 (39.0) | 0.68 (34.4) |
| **Recommended (visible + 3 deg)** | **0.94 (43.3 deg)** | **0.93 (43.0)** | **0.90 (42.0)** | **0.76 (37.4)** |
| Safer (visible + 5 deg) | 1.01 (45.3 deg) | 1.00 (45.0) | 0.97 (44.0) | 0.82 (39.4) |

As percentages of the headset's tangents (the older fields of the 1.0.1 build): recommended 53 / 91 / 74 / 75,
tight 48 / 82 / 66 / 67, safer 57 / 98 / 79 / 80.

3 deg of margin covers head and eye position changes, headset fit and the width of the drawn stroke. Start with the
recommended row and check the corners for black.

Effect, recommended row: the game renders 66 % x 74 % of the declared span, about half the pixels (game GPU only: the
stream, encode and decode are unchanged). Full-frame UI then fits the visible window. A new value is taken at the
next game start.

The recommended row is what the Vision Pro profile carries (`fov` in `profiles\apple-vision-pro.profile.json`: the visible angles, a 3 degree margin and a render quality of 0.25: the first estimate, replaced by the calibrated profile above, render quality 0). *Advanced
picture and FOV settings* shows a button, "Use the Apple Vision Pro's measured values", that fills the fields and the slider from it (see [HMD_PROFILES.md](HMD_PROFILES.md)). The
button is a convenience, not a default: nothing is restricted until it is pressed.

## Also set FoV Scale to 0.8 in the visionOS app

The Vision Pro profile recommends it ("0.8 client slider recommended (manually)": a value circulating in the community, not measured here; you set it yourself in the app). The stock app multiplies the tangents it declares by its **FoV Scale** (`RealityKitClientSystem.swift`,
applied when the immersive space is entered), so the stream then covers less angle: every pixel in view is 1.25 times denser at the same encode, decode and bitrate cost. Reconnect once
afterwards so the Advanced window's greyed values refresh. The fields above stay the same (they are absolute; the inner value is clamped to the headset's own, 0.82), and at 0.8 the
game's floor becomes 2778 x 2966 (8.2 Mpx, 72 % of an unrestricted game's cost), which is why the profile suggests a render quality of 25 %. The edge nearest the nose is the limit: at 0.8
it declares 39.2 degrees against the 40.0 measured visible, so check that corner with `fovgrid.exe`. A scale is uniform; going further needs a per-edge scale in the client.

## Check it

Tick the override, enter the values, start a new `fovgrid.exe`: the white border should now touch the edge of what you see on all four
sides (the declared field of view is then the visible one). Black beyond a side means that edge can be raised; a
missing border means it needs more margin.

## Limits

- Left eye only; the right eye is assumed to mirror it. Draw it the same way to confirm.
- One person's fit: the visible window moves with how the headset sits (light seal, glasses inserts, IPD).
- Not tested on a game; the restriction feature itself is the one of `fov-restrict` (see `docs/FOV_GRID.md`).
