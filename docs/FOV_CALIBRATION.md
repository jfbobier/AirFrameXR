# FoV calibration: plan

Status 2026-10-28: **phase 0 and the probe's sweep/outline modes are built** (branch `fov-calibration`; the probe compiles on the 5090, nothing has run on a headset
yet; the client branch exists with no changes). Phase 0 result (ellipse through the four measured crossings, `python3 harness/fov_calibration.py --ellipse-from-axes --curve`):
today's 0.94 / 0.93 / 0.90 / 0.76 cover 100 % of that ellipse and save 50.6 % of the pixels; the tight box also covers 100 % and saves 59.9 %; 99 % coverage
saves 62.0 %, 95 % saves 65.8 %. So the 3-degree margin costs about 9 points of savings *if the ellipse is the real V*; the sweep decides that. Goal: find the field-of-view restriction that shows the game on the
largest possible part of the Vision Pro's panel while rendering the fewest pixels that the panel will never show, and
measure it instead of guessing. Today's values (`docs/AVP_FOV.md`: four tangents in the streamer, FoV Scale 1.0 in the
headset app) come from one person tracing a grid with one eye; the author's estimate is that they are within about 5 %
of the optimum. This plan tests that estimate and replaces it with a measured curve.

The work happens in **two forks** (streamer host + probe, and the visionOS client), both branches named
`fov-calibration`. They are a calibration runtime, not the product: the engine rule in `CLAUDE.md` is lifted on those
branches, and only the final numbers (and whatever small change they need) come back to `main`.

### Run the sweep (phase 1)

On the 5090 with AirFrameXR connected, hands-as-controllers on in the client, FoV Scale 1.0, override off. Command prompt next to the exe (the probe renamed or not):

    set XR_PROBE_SCENE=fovgrid
    set XR_PROBE_FOVGRID=sweep
    set XR_PROBE_SWEEP_FIT=1
    set XR_PROBE_SWEEP_GAZE=following
    xr_probe.exe fovgrid.log 100000000 86400 fov_input.csv

(`fov_input.csv` only turns the input actions on; the sweep creates them itself.) Pinch once to start; a white cross shows for 2 s, then a dot
moves outward from 25 degrees at 1 degree/s in random ray order. Pinch when it disappears. After the 24 rays of the first eye the other eye follows (10 s
pause). One row per ray is appended to `fov_sweep.csv` (`XR_PROBE_SWEEP_CSV`). Repeat with `XR_PROBE_SWEEP_FIT=2,3` (headset off and on) and
`XR_PROBE_SWEEP_GAZE=fixed`. Confirmation: `XR_PROBE_FOVGRID=outline` with `XR_PROBE_OUTLINE=<csv eye,theta_deg,tan_r>` draws a polygon.
Row format and tangent convention: header of `tools/probe/xr_probe.cpp`.

## 1. The problem in one picture

Everything is in **tangent space** per eye (x = tan of the horizontal angle from the view axis, y = tan of the vertical
one). Three regions matter:

| Region | What it is | Where it comes from |
|---|---|---|
| **D** declared | the frustum the headset app declares to the streamer (left eye 1.79 / 1.02 / 1.22 / 1.02 outer / inner / up / down) | `ViewsConfig` from the client (`LayerRenderer.Drawable.views[i].tangents`, times FoV Scale) |
| **V** visible | the part of D a wearer can actually see through the lens: roughly an ellipse, about 0.85 / 0.84 / 0.81 / 0.68 at the axes | **unknown shape**, only its four axis crossings were measured, left eye, one fit |
| **R** rendered | the rectangle the game is told to render (the override's four tangents) | streamer `fov_override`, taken at the next game start |

Two numbers describe any candidate R:

- **coverage** = area(V ∩ R) / area(V): the share of what the wearer can see that carries game pixels. Below 100 % the
  wearer sees black at some edge or corner.
- **savings** = 1 − area(R) / area(D): the share of game pixels not rendered compared to an unrestricted game. Game
  pixels are proportional to tangent area because the shim scales the recommended size with the span
  (`ovrshim/driver.cpp`, `m_gameFov`); the stream, encode and decode are unchanged by the override.

Because the protocol can only carry a **rectangle** and V is an **ellipse**, 100 % coverage needs R = bounding box of
V, which costs about 4/π = 1.27 times area(V): the corners of R are never seen. Shrinking R trades coverage for savings
along a curve. The result of this plan is that curve (coverage on one axis, savings on the other), the point where
today's values sit on it, and the point we choose. Picking a point is a judgement (a little black in the far corners
versus a quarter of the render); the curve makes it an informed one.

The key simplification: **V does not depend on the stream or the game.** It is a property of the headset, the lens and
the wearer's fit. Measure V once, as a polygon, and the whole curve is geometry computed on the PC. No iterative
convergence from the current values is needed; they only serve as a reference point on the plot and as the first
candidate in the validation step.

## 2. Facts the plan rests on (read in the code, not tested unless said)

The five parameters and what each one does:

| # | Parameter | Lives in | Effect |
|---|---|---|---|
| 1-4 | `fov_outer / inner / upper / lower` (override tick) | streamer settings, host stdin `fov_override on o i u l`, IPC `fovScale[4]` to the shim | the game renders R, composited into the full D frame; black margin around; **taken at the next game start only** (VDXR latches the FoV at system creation) |
| 5 | FoV Scale | visionOS app (stock: 0.2-1.6 slider; fork: 0.7-1.1) | multiplies the tangents the client **declares** (D shrinks uniformly), so the stream's fixed eye resolution covers fewer degrees: denser pixels, same encode cost. The game follows the smaller D at its next start |
| (6) | `visibleFovLimit` | fork `airframexr-client`: left-eye tangents 0.94 / 0.93 / 0.90 / 0.76, right eye mirrored, `simd_min` with the drawable's tangents before declaring | the per-edge version of 5: the client declares the visible window itself, so D = R and the streamer needs no override |

So on the fork the five collapse into **one set of four tangents per eye**, owned by the client (the headset is the
thing being calibrated, and the values depend on the wearer). The streamer override stays as a manual fallback for
stock clients.

How the client puts the frame on the panel (`Shaders.metal`, `videoFrameVertexShaderCommon`): one quad per eye at
depth 500, its half-widths scaled by the **declared** tangents, projected with the drawable's **real** projection. A
panel pixel whose direction falls outside the declared window is black; inside, it samples the frame. Declaring less
than V leaves a visible gap; declaring more than V wastes stream pixels on directions nobody sees.

Channels available on the v20.14.1 protocol (no protocol break, both ends are our forks):

- **Client to host, live FoV:** `ClientControlPacket::ViewsConfig` can be re-sent at any time; `server_core` turns it
  into `ServerCoreEvent::ViewsConfig` and the host already stores it in `Shared::pending_views` and applies it in the
  main loop (`tools/host/src/main.rs`). Whether a mid-session change reaches a running `fovgrid.exe` is **untested**
  (the lab only covers the stdin path, `e2e_fov_override_live`); VDXR's latch means the probe must restart anyway when
  R changes. For the measurement phase it does not matter: the stream stays at the full D and the probe draws
  candidate rectangles inside it.
- **Client to host, data:** `ClientControlPacket::Reserved(String)` (JSON). The stock server parses it as
  `ReservedClientControlPacket` and, when the variant is unknown, logs the whole string at info level and continues
  (`connection.rs`, "Failed to parse reserved packet"). So even an unpatched server already gets the JSON into the
  host log; a two-line patch in our vendored `ALVR-v20.14.1/alvr/server_core` forwards it as an event instead.
  `client_core` needs a C entry point (`alvr_send_reserved_json`), a few lines next to
  `alvr_send_custom_interaction_profile`. `alvr_log` lines also reach the host log today (the fork uses them for
  `[floor]` and `[home]`).
- **Host to client:** `ServerControlPacket::Reserved(String)` exists; the v20 client ignores it (`Ok(_) => ()` in
  `client_core/src/connection.rs`). Patch: push a `ClientCoreEvent::Reserved(String)` and expose it through
  `alvr_poll_event`. `server_core` has no public sender for it; add `send_reserved(String)` on the server context
  (the `control_sender` is an `Arc<Mutex<..>>` in `connection.rs`, the same way `DecoderConfig` is sent).
- The visionOS client builds `client_core` from its `ALVR` submodule (`v20.14.1-4-ge3fd4480`, `build_and_repack.sh`),
  so the client-side patch lives in the fork's submodule pointer, not in a release of ALVR.

Inputs: on the fork, a hand pinch is sent as the controller trigger in hands-as-controllers mode (`1508b69`), and
`xr_probe` already has an action set reading `trigger/value`, `squeeze`, the buttons and the stick
(`tools/probe/xr_probe.cpp` line 224 onwards). A **pinch in the headset is therefore readable by the probe on the PC
with no new channel.**

Automatic signal worth testing: visionOS gives the client a **rasterization rate map** per eye
(`drawable.rasterizationRateMaps`, read in `Renderer.checkEyes`). Its density falls off toward the edges the lens does
not show well; the fall-off may trace V without a human. Hypothesis, to be checked against the human measurement in
phase 2.

## 3. Method

### Phase 0: quantify the "5 %" with what exists (no code, one evening)

Fit an ellipse through the four measured axis crossings of `docs/AVP_FOV.md` (tight row, left eye), treat it as V, and
compute coverage and savings for the recommended tangents 0.94 / 0.93 / 0.90 / 0.76. This gives the first point on
the curve and tells whether the rest of the plan can move it by more than the measurement noise. Script:
`harness/fov_calibration.py --ellipse-from-axes` (phase 3 tool, its first function). If the current values already
sit within a few percent of the knee of the curve, stop here and say so in `docs/AVP_FOV.md`.

### Phase 1: measure V properly (the only part that needs a human)

Tool: a new `fovgrid.exe` mode, `XR_PROBE_FOVGRID=sweep`, in `tools/probe/xr_probe.cpp`:

1. Head-locked, black, one eye at a time (the other eye black; the wearer may close it). A white fixation cross at
   the centre.
2. A short bright arc moves outward along a ray at azimuth θ, 1 degree per second, starting at 25 degrees. The wearer
   pinches (trigger) when it disappears. The probe logs θ, the tangent at the pinch minus the arc's own travel during
   the pinch latency (about 50 ms, under 0.1 degree at that speed), and the frame number. Then the next ray.
3. 24 rays per eye (every 15 degrees), both eyes, about 6 minutes per eye. The wearer does **not** move the head.
4. Two gaze protocols, both recorded: **fixed** (eyes on the cross; peripheral detection) and **following** (eyes may
   look toward the arc; the pupil moves and the lens shows a different window). Games are played with the eyes
   roaming, so **following** is the one the restriction must satisfy; fixed is kept to see how much they differ.
5. Three fits: take the headset off and on between runs (light seal, IPD setting unchanged). The per-ray spread across
   fits is the margin, instead of a fixed 3 degrees.

Output: `fov_sweep_<date>.csv` (eye, fit, gaze protocol, θ, tangent) written next to the exe by the probe, as
`fovgrid.log` is today. Nothing in the streamer changes for this phase: the stream stays at the full D, the override is
off, FoV Scale is 1.0.

Confirmation: a second mode, `XR_PROBE_FOVGRID=outline`, draws the measured polygon as a thin line. The wearer should
see it just at the edge all around. If a segment is clearly inside or outside, that ray is redone.

Fallback if the streamed path is unusable (pinch latency, or the hands-as-controllers trigger does not arrive): the
same sweep drawn by the client itself in Metal, with the pinch read from hand tracking directly, the CSV sent to the
host as `Reserved` JSON. More Swift, no stream, same output.

### Phase 2: the channel and the automatic proxy (forks)

Client fork, branch `fov-calibration` off `airframexr-client`:

- `client_core` patch (submodule): `alvr_send_reserved_json(const char*)`; `ServerControlPacket::Reserved` becomes
  `ClientCoreEvent::Reserved` and an `AlvrEvent` with a string buffer.
- On connect, and whenever the drawable changes, the client sends one JSON report:
  `{"cal":"drawable", "eye":0, "tangents":[l,r,u,d], "colorTexture":[w,h], "vrr":{"screen":[w,h], "physical":[w,h],
  "rowDensity":[...], "colDensity":[...]}}`: the rasterization rate map sampled along the horizontal and vertical axes
  through the centre, 64 samples each, as physical pixels per screen pixel.
- Host command `{"cal":"declare", "left":[l,r,u,d], "right":[l,r,u,d]}` makes the client re-declare its views
  (`sendViewParams`) with those tangents, clamped to the drawable's own, and store them as the **Calibrated** preset.
  Menu: Measured (today's constants) / Calibrated / Off (declare the drawable's tangents).

Streamer fork, branch `fov-calibration` off `main`:

- `server_core` patch: `ServerCoreEvent::Reserved(String)` for unknown reserved JSON; `send_reserved(String)`.
- Host: stdin `cal_declare l r u d l r u d` forwards to the client; every `Reserved` event is appended to
  `logs/fov_calibration.jsonl`. No other engine change.

Proxy test: plot the rasterization-rate density curves against the human polygon from phase 1 (same axes, same eye).
If the density knee lands within the fit spread of the human edge on all four axes, the proxy can replace the sweep
for other wearers (it is per headset, not per wearer, so it probably **cannot** capture fit; this is what the test
says). If not, the sweep stays the method and the proxy is dropped; say so in this file.

### Phase 3: the curve and the choice (PC only)

`harness/fov_calibration.py`:

1. Reads the sweep CSV, keeps the **following** protocol, per eye takes the per-ray minimum across fits (the tightest
   window seen) as V and the per-ray spread as the uncertainty band.
2. For each coverage target from 90 % to 100 % in 0.5 % steps, finds the rectangle R of minimum area with
   area(V ∩ R) / area(V) at least the target (four parameters, V is convex: a coarse grid over the four tangents then
   a local refinement is enough). Right eye solved separately; if the two eyes differ by less than the uncertainty
   band, one mirrored set is reported, otherwise two.
3. Plots savings against coverage with the uncertainty band, marks today's values (phase 0 point), the bounding box
   (100 %) and the knee. Writes `docs/images/avp_fov_pareto.png` and a table of candidate tangents.
4. Prints the chosen tangents as the client's **Calibrated** preset and, for stock clients, as the streamer override
   (`fov_override on ...`) plus the equivalent uniform FoV Scale.

Decision rule proposed: the knee of the curve, defined as the highest coverage whose marginal cost is still below
1 % of area(D) per 1 % of coverage. The wearer can override it after step 4 of the validation.

### Phase 4: validation on the headset (human, minutes)

1. The host sends the chosen tangents (`cal_declare`), the client re-declares, `fovgrid.exe` restarts (VDXR latch):
   the white border should now sit at the visible edge on all four sides, black only in the far corners.
2. Candidates one step up and one step down the curve, the same way, with the wearer saying which corner black is
   acceptable. This is the manual input the plan asks for: a yes/no per candidate, no numbers.
3. One real game (the UEVR baseline of `fov-restriction` notes) with the chosen preset: check HUD corners and the
   intro screen; write what was seen in `docs/progress.md`.
4. Numbers go to `docs/AVP_FOV.md` (replacing the single-fit table), the profile
   (`profiles/apple-vision-pro.profile.json`, `fov` block gains the polygon as `visible_deg_by_azimuth`), and the
   client's Measured constants.

## 4. Success criteria

- A measured V per eye as a 24-point polygon with a fit-to-fit spread, both gaze protocols, from one wearer; a second
  wearer if one is available (fit dependence is the open question the margin exists for).
- The coverage/savings curve with today's values marked. The plan succeeds whether the answer is "today's values are
  at the knee" or "move them": either is a measurement.
- A chosen preset validated by step 1 and 3 of phase 4, shipped as the client's default and as the profile's values.
- Everything else (the channel, the proxy) is instrumentation; it is removed from the product branches if the curve
  does not need it.

## 5. Risks and open questions

- **Gaze.** V with roaming eyes is smaller than with fixed eyes and more variable; it may make the curve flatter than
  expected (more black in corners unavoidable). Measuring both protocols settles it.
- **Fit.** One wearer's V may not be another's. The client-side preset with a Calibrated entry is the answer; the
  sweep is six minutes per eye.
- **Rectangle only.** The v20 header carries no per-frame FoV and the quad is rectangular (`airframexr-client` notes,
  2026-10-27). The corner pixels inside R but outside V are the price. A later engine change could return V as the
  OpenXR visibility mask (VDXR advertises `XR_KHR_visibility_mask`, our stencil is empty; Unreal and Unity skip masked
  pixels) and recover most of that price without touching the protocol. **Engine, out of scope here, ask first.**
- **Stream density.** With the client declaring R, the stream's eye resolution covers fewer degrees: pixels per degree
  rise by area(D)/area(R). Encode and decode cost do not change; the game saves. Whether the extra density is wanted or
  the stream resolution should drop instead is a separate measurement (`docs/PERFORMANCE_KNOBS.md`).
- **Live re-declaration.** Untested whether a mid-session `ViewsConfig` reaches the shim's next game start cleanly
  (host `pending_views` path). Phase 4 step 1 is where it shows; fallback is a reconnect.
- **Pinch as trigger.** Needs hands-as-controllers on in the client and the probe started with its input file
  (`inputF`). If the trigger does not arrive, phase 1's fallback (sweep drawn by the client) applies.
- **The proxy.** The rasterization rate map describes what the compositor bothers to render sharply, not what the
  lens shows a given eye. Expect it to be close but not equal; it is a check, not the method, until proven.

## 6. Order of work

1. Phase 0 script and number (one evening; decides whether the rest is worth it).
2. Probe sweep + outline modes, one measurement session, both eyes, three fits (the only hardware-dependent step).
3. Phase 3 script, curve, chosen preset.
4. Client preset (Calibrated) and the validation session. The channel (phase 2) is only built if the sweep CSV has to
   come from the client (fallback) or the proxy test is wanted; otherwise the JSON channel is a documented option.

Branches: `fov-calibration` in both forks; nothing merged to `main` until phase 4 step 3 is written up with what was
tested and what was not, following `docs/progress.md`'s legend.
