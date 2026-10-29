# FoV grid: what each headset actually shows

`xr_probe` has a scene, `XR_PROBE_SCENE=fovgrid`, for comparing how much of the field of view two headsets show.
It is head-locked and drawn in true angles from each eye's own view axis, so a line at 40 deg is 40 deg on any headset
whose declared FoV is right.

- black background; a line every 5 deg, horizontal and vertical
- the line colour tells the angle, in 10 deg steps: **10 red, 20 orange, 30 yellow, 40 green, 50 cyan, 60 blue,
  70 violet, 80 magenta**; the 5 deg lines in between are dim grey; 0 deg is white and thick (the crosshair)
- a white border at the edge of the FoV the runtime declared (the edge of the frame the game renders)

Why: the AVP declares about 106 x 96 deg and the Quest 3 about 94 x 99 deg (2026-10-26 observation: the AVP
looks "zoomed in" by about 30 % on intro screens). The declared numbers alone predict the opposite. If the declared FoV is
bigger than the window the headset really shows, the border is cut off and the picture looks zoomed in.

## Run it

It is an ordinary OpenXR app, so it runs like a game:

1. Since 1.2.0-pre the package ships it: `bench\fovgrid.exe` (this `xr_probe.exe` renamed: under that name it starts in the grid scene by itself
   and runs until the session ends) and `bench\fovgrid.bat`, which points it at the package's own OpenXR runtime; `fovgrid.bat 0.96,0.71,0.69,0.66`
   draws that rectangle (outer, inner, upper, lower tangents, the Advanced window's fields) as a white outline with a centre cross on black, to check a
   calibration: run it with the override **off** (with it on, the game's view is that rectangle and the outline falls on the picture's edge: black with a few pixels of line). Older packages: copy
   `xr_probe.exe` as `fovgrid.exe` anywhere on the PC, e.g. next to the games.
2. Start AirFrameXR, connect the headset, and double-click `fovgrid.bat` (or `fovgrid.exe`, or add it to Steam as a non-Steam game).
   Fresh settings: "Override the headset's field of view" unticked in the Advanced window (the game gets the headset's own), FoV scale in the visionOS app at 1.0.
3. Put the headset on, look straight ahead, head still. Repeat on the other headset. Close it from its console window.

It writes `fovgrid.log` next to itself. If the OpenXR runtime of this PC is not AirFrameXR, set
`XR_RUNTIME_JSON=<package>\bin\virtualdesktop-openxr.json` first (command prompt) and start it from there.
The plain `xr_probe.exe` does the same with `XR_PROBE_SCENE=fovgrid` and `xr_probe.exe fovgrid.log 100000 1200`.

## What to write down (per headset)

- Is the white border visible on all four sides? If a side is missing, it is cropped.
- The outermost line you can see **left, right, up, down** (colour, and whether it is a 5 deg grey one after it).
- Does the picture look the same in each eye (close one eye)?

`fovgrid.log` has `view fov tan` per eye: the declared tangents to compare with what you saw.

## Reading it

Left eye, declared half-angles in degrees (outer, inner, up, down) from `views_config` in the host events:

| | outer | inner | up | down |
|---|---|---|---|---|
| Quest 3 | 54.0 | 40.0 | 44.0 | 55.0 |
| AVP (RealityKit) | 60.8 | 45.6 | 50.6 | 45.6 |

- Border visible and the outermost line matches the table: the declared FoV is shown completely. The zoom is not a
  FoV mismatch (look at how the game places its screens).
- Border missing on the AVP, last visible line smaller than the table: the AVP crops the frame. The zoom factor is
  tan(declared) / tan(visible), per axis.
- Border visible on the Quest with black beyond it: the Quest shows the frame smaller than its panel (margin), which
  also makes the same content look smaller there.
