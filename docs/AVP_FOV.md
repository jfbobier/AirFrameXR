# Apple Vision Pro: declared vs visible field of view

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

The recommended row is what the Vision Pro profile carries (`fov` in `profiles\apple-vision-pro.profile.json`: the visible angles, a 3 degree margin and a render quality of 0.25). *Advanced
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
