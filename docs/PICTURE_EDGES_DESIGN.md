# Picture edges: the recommended end-to-end design

Status 2026-11-05 (evening): **proposal; lab and oracle work done, product not built.** Review of 2026-11-05 folded in: step 0 (the vertical
orientation) comes first, the work order is 0, A, D, B, C (section 11), the verdict compares the told *size* (section 7), the margin costs
about 7.5 % per axis, the migration does not keep a default nobody chose (section 8). Replaces the model of `docs/END_TO_END_FOV_DESIGN.md` (sections 1 to 7); that document's
measurements stay the evidence: the rate map (section 8), the test-slider answers (section 9), the game behaviours and the lab work of
`docs/FOV_APERTURE_PLAN.md`, the stream geometry of `docs/FOV_PIPELINE_OPTIM.md`, the calibrations of `docs/AVP_FOV.md`. Steps marked **engine**
need the owner's approval before they are built (`CLAUDE.md`).

**Assumption:** the pipeline is ours end to end, the streamer and our own clients (visionOS from the fork's TestFlight, the Quest client submitted
to Meta). Stock ALVR apps are not designed for. A feature may require a client build; the minimum client build per feature goes in the release
notes and in one launcher line ("update the headset app").

## 1. Principles

1. **The PC owns the geometry.** Every box is decided on the PC before the connection and sent to the client at connection; the client declares
   what it is told, per eye. Nothing is tuned on the headset.
2. **Three boxes, one order.** Panel P (what the app can show), View V (what the stream covers and the wearer sees), Game box G (what the game
   renders). G inside V inside P, per eye. The stream covers V; black appears only where the game drew nothing.
3. **Defaults are safe for every wearer; the per-wearer gain comes from a two-minute fit on the PC keyboard.** No guessed box: the three wearers
   measured so far differ by 10 degrees or more on three edges, as much as the whole gain of a guess.
4. **Games that ignore the game box are detected and skipped** at their next start, automatically, visibly, reversibly.
5. **The user never reads "FOV".** Words: *picture edges* (V), *what the game renders* (G), *fit the picture to your eyes* (the tool). The settings
   keys and the code keep their names.

## 2. The boxes

| Box | Per eye | Where it comes from | Default | Who applies it |
|---|---|---|---|---|
| **P** panel | yes | the client at connection: Vision Pro = Apple's frustum (60 / 45 / 50 / 45 degrees outer / inner / up / down as logged, about 1 % with the IPD; up and down may be swapped, step 0), Quest = the runtime's lens window | | the client declares it when no V is sent |
| **V** picture edges | yes | the PC: P, or a profile (quick fit or sweep) plus the edge margin | **P** | streamer -> client message at connection; the client declares min(P, V); the stream is sized from V |
| **G** game renders | yes | the PC: V, or a smaller profile for performance (advanced) | **V** | the shim, at the game's next start (VDXR latches it); skipped for games on the ignore list |

Edge margin: one setting, default 2 degrees, added to a fitted window when it becomes V and G. Two degrees cost about 7.5 % of stream pixels per
axis on the author's fit (7.4 % horizontally, 7.8 % vertically: the tangent grows faster than the angle at 35 to 45 degrees), about 16 % in area; a view that is too tight on a bad fit day is the thing a wearer cannot fix in a game.

Quest: V is P by construction (the runtime already reports the lens window); the picture-edges row is greyed with that one reason. G and the ignore
list work the same on both headsets.

## 3. Why the view matters: the sharpness ceiling (computed, `harness/fov_stream_geometry.py`; Apple's full-rate density read from the rate map, 6261 logical px over 2.732 tan = about 2290 px per tangent unit)

| V | Stream per eye | Centre density, px / tan | Share of the panel's density |
|---|---|---|---|
| P | 3552 x 3200 | 1300 x 1460 | 57 / 64 % |
| P | 4096 x 3584 (the cap) | 1500 x 1635 | 65 / 71 % |
| 0.80 x P (two testers: no black) | 3552 x 3200 | 1625 x 1824 | 71 / 80 % |
| the author's fit 0.97 / 0.71 / 0.69 / 0.63 | 3552 x 3200 | 2114 x 2424 | 92 / 106 % |

Detail beyond the panel's density is discarded by the compositor (the rate map caps the draw), so a fitted V reaches the panel's native sharpness
at the same encode cost, and anything above it is wasted. This sets the stream-size rule below.

## 4. Defaults (Vision Pro unless said; measured where said)

| Setting | Default | Backed by |
|---|---|---|
| Picture edges V | P (whole panel); the launcher proposes the two-minute fit once | the testers saw no black at P |
| Game renders G | V (no black ring); the launcher shows the saving a fit would give | 65 % fewer game pixels for the author's fit against P, computed |
| Edge margin | 2 degrees | judgement, to tune on fit data |
| Render quality | 0 | the author's measured choice |
| Stream size | the density rule (section 5): at P, the largest size the wizard's encode test fits (NVENC caps an eye at 4096 wide; 4096 x 3584 is the largest size measured so far, a tester's, not a limit) | 5090: 3.1 -> about 4 ms encode for +28 % encoded pixels at the cap |
| Encoding | foveated, parameters computed from Apple's rate map and V (section 5b), split 3 kept | 3.1 against 7.7 ms encode, 3x bits per pixel at the same bitrate, measured; the map says what the compositor discards |
| Ignore list | automatic, with a tick to make it manual | section 7 |
| Quest | V = P greyed, G = V, the Quest 3 starting point offered as a G profile | `docs/HMD_PROFILES.md` |

## 5. Stream size and foveation from V (settings only, `session.json`; the launcher's `PipelineModel` has the maths)

- **Density rule.** Target density = the panel's full-rate density (constant per headset model, from the rate map; a fraction of it as the
  advanced setting). Eye size = min(target x V span per axis, NVENC's 4096 x 8192 per eye, what the encode test fits), 32-aligned (at P the target asks 6256 x 5020: the width
  cap and the encode test decide). At P the cap and
  the encode test decide; as V shrinks the size stays until the target is reached, then shrinks with V (encode, decode and network saving).
  The resolution field becomes "density", shown as the equivalent size over the whole panel so the number keeps its meaning.
- **Foveation parameters computed from the rate map and V** at every change of V or stream size: section 5b.
- Full frame stays selectable; the card states its cost (the tester's 8192 x 3584 is 4x the encoded pixels of the foveated frame at the same
  centre density).
- A change of V needs a new connection (stream size and foveation are fixed at connection): the launcher closes the streamer as it does today.


## 5b. Foveation from the rate map: the maths (settings only; the oracle is `harness/fov_stream_geometry.py`, to extend)

**What Apple discards.** The compositor draws the frame through a rasterization rate map that is static and the same on every headset measured
(`docs/END_TO_END_FOV_DESIGN.md` sections 8 and 9). `rate(theta)`, per axis, is the density it keeps relative to the logical screen, 1.0 in the
centre, 0.06 at the outer edge. Detail the stream carries beyond `rate` is thrown away on the headset. The samples (left eye, degrees from the
axis, negative = outer or down):

| horizontal | -59 | -50 | -40 | -28 | -20 | -12 | -3 | +7 | +16 | +24 | +31 | +37 | +43 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rate | 0.06 | 0.09 | 0.15 | 0.30 | 0.70 | 0.96 | 1.00 | 0.99 | 0.94 | 0.43 | 0.23 | 0.17 | 0.13 |

| vertical | -43 | -34 | -23 | -16 | -9 | -2 | +6 | +13 | +20 | +26 | +32 | +41 | +48 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rate | 0.10 | 0.15 | 0.28 | 0.58 | 0.89 | 0.94 | 1.00 | 1.00 | 0.97 | 0.66 | 0.32 | 0.19 | 0.12 |

They go in the headset profile (`profiles/apple-vision-pro.profile.json`, key `ratemap`), one table per axis, so the launcher computes from
them; a new visionOS or a new headset model means new samples, nothing else.

**What ALVR's foveation does** (`CompressAxisAlignedPixelShader.hlsl`, three parameters per axis: centre size `cs`, shift `sh`, edge ratio
`er`). The band `[(1 - cs)(1 + sh) / 2, that + cs]` of the eye, in source uv from the outer edge, is kept at full density. Outside it the
source position is a quadratic of the encoded coordinate, so the **local compression ramps linearly along the encoded axis from 1 at the band
to `2 er - 1` at the frame edge**, averaging `er` over the edge region. The encoded eye size on an axis is `(cs + (1 - cs) / er)` times the
source size, 32-aligned by ALVR's own rounding (`harness/lib.py` `fov_vars`, the exact port).

**The rule.** Let `d0` be the stream's pixels per tangent unit in the band and `r(theta)` the local compression at angle `theta`. The stream's
density at `theta` is `d0 / r`; the panel's is `2290 rate(theta)` px per tangent unit. "No more loss in the periphery than in the centre" is
`d0 / r >= (d0 / 2290) 2290 rate`, that is

    r(theta) <= max(1, m / rate(theta))   at every theta inside V,   m <= 1 (margin; 1 = exactly what the compositor discards, 0.8 keeps 25 % more)

With a fitted V the band is at the panel's density, so this is literally zero loss; at Panel the periphery keeps the centre's deficit and
nothing worse.

**The automatic parameters, per axis, from V (per eye, the stricter eye) and the map:**

1. Band = the interval where `rate >= 0.5`, clipped to V (about -24..+23 degrees horizontally, -18..+29 vertically); `cs` = its width in
   source uv, `sh = 2 start / (1 - cs) - 1` (`fov_stream_geometry.py` `band_params`).
2. `er = (1 / rate(stricter edge of V) + 1) / 2`: the local ratio at the frame edge, `2 er - 1`, equals what the map allows there on the
   side that allows less (ALVR has one ratio for both ends of an axis). Clamped to [1, 10].
3. ALVR's alignment, the encoded size, the strips (section 5b, split). The exact variant (**the recommended default**, `zero_loss` in the
   oracle): per axis, over band contours 0.30 to 0.95, the largest `er` (bisection) whose worst over-compression after ALVR's alignment is at
   most 1, keeping the contour with the fewest encoded pixels. Rules 1-2 alone leave 1.1-1.2x of loss; the exact variant costs 2 % more
   pixels at Panel and is smaller on a fitted view.

**Orientation (read from ALVR's shaders, 2026-11-05):** the compress pass draws a full-screen quad with uv.y = 0 at the **top**
(`QuadVertexShader.hlsl`: `position.y = -(uv.y - 0.5) * 2`), so `center_shift_y > 0` moves the band **down**, and a band from -18 to +29 degrees
needs a negative shift. The first prototype of this table had the vertical axis upside down: its shifts had the wrong sign and its "today"
column described the lower field where the loss is in the upper one. The table below is the oracle's (`python3 harness/fov_stream_geometry.py --ratemap`).

**Open (2026-11-05, `harness/swift_headset/README.md`):** the client's own code may declare the frustum upside down (with a standard
projection matrix, `_extractFrustumTangents` + `Renderer.swift` send the bottom tangent as `up`; the client's defaults say 45 up / 50
down). If a headset's raw `computeProjection` confirms it, the panel is 45 up / 50 down, the rate map's vertical table is mirrored, and
every vertical number below flips sign (up becomes down); the horizontal ones stand. Settle that before step D's vertical parameters.

**Results (computed, `fov_stream_geometry.py --ratemap`; today = 0.45 x 0.40, shift 0.4 / 0.1, ratios 4 / 5; worst = local compression over what
the map allows, 1.00 = no loss):**

| V | Stream per eye | Today: encoded, worst | Rules 1-2: geometry | encoded, worst | **Zero loss: geometry** | **encoded, worst** | Ideal warp (below) |
|---|---|---|---|---|---|---|---|
| Panel | 3552 x 3200 | 4224 x 1664, 7.03 Mpx; **upper field 1.6x at +29 degrees**, 1.3-1.45x up to +44; none in the lower field or horizontally, but full density wasted from 23 to 34 degrees | cs 0.317 x 0.398, shift 0.380 / **-0.028**, er 4.35 / 4.67 (band -24..23 / -18..29) | 3392 x 1696, 5.75 Mpx (-18 %); 1.10x at 28, 1.19x at +37 | cs 0.304 x 0.360, shift 0.376 / -0.028, er 3.81 / 3.78 | **3456 x 1696, 5.86 Mpx (-17 %); 1.00** | 2816 x 1504, 4.24 Mpx (-40 %) |
| Panel | 4096 x 3584 | 4864 x 1856, 9.03 Mpx; same | same | 3904 x 1888, 7.37 Mpx; same | cs 0.331 x 0.360, shift 0.386 / -0.028, er 4.22 / 3.78 | 4032 x 1920, 7.74 Mpx (-14 %); 1.00 | 3264 x 1696, 5.54 Mpx |
| 0.80 x Panel | 3552 x 3200 | 4224 x 1664; 1.16x at 29, **2.7x at +26** | cs 0.396 x 0.497, shift 0.427 / -0.084, er 3.65 / 3.56 | 4032 x 2048, 8.26 Mpx; 1.11x, 1.09x | cs 0.347 x 0.450, shift 0.403 / -0.082, er 2.96 / 3.17 | 4032 x 2016, 8.13 Mpx; 1.00 | 3328 x 1792, 5.96 Mpx |
| Author's fit | 3552 x 3200 | 4224 x 1664; 1.6x at -23, **4.6x at +23** | cs 0.516 x 0.660, shift 0.292 / -0.374, er 3.19 / 2.27 | 4800 x 2592, 12.4 Mpx; 1.08x, 1.00 | cs 0.452 x 0.567, shift 0.273 / -0.305, er 2.70 / 2.14 | **4672 x 2464, 11.5 Mpx; 1.00** | 4096 x 2176, 8.9 Mpx |

What this says: at Panel the computed parameters are both **smaller and better** than today's (17 % fewer encoded pixels with no loss, where
today over-compresses the **upper** field 1.6x). On a fitted view the periphery that can be thrown away is small, so zero-loss foveation needs **milder**
ratios than today (2.7 / 2.1, not 4 / 5): today's parameters would visibly cut a fitted view's upper field (4.6x). The encoded frame grows with a fit; that
is the price of the sharpness, and the density rule of section 5 and `m` are the two levers when the encode test says it does not fit.

**Split encoding (kept, forced 3 strips).** The strips are horizontal bands of the encoded frame; their seams fall at one and two thirds of
the encoded height. The full-density band is about 75 % of the encoded height, so with 3 strips the seams cross the band whatever the
parameters (about -5 and +17 degrees from the axis at Panel), and with 2 strips the single seam would sit on the axis: 3 is the right count,
the axis stays mid-strip. The rule only asks the encoded height to stay 32-aligned (ALVR does it; HEVC CTBs are 32). Owed: seam visibility
at 300 Mbps with the judge and the eyes, the `slices_per_frame` reading at the new heights (forced mode splits at any height, the 2112-row rule
is for auto mode only).

**Ideal warp (later, engine on both ends, our two shaders).** Encoded coordinate = the integral of `min(1, rate / m)` along the axis over V: a
1-D lookup per axis in the host's FFR shader and the client's decompress shader, negotiated at connection. Zero loss by construction and the
smallest frame the map allows: 4.24 Mpx at Panel against 5.75 with ALVR's three parameters, 8.9 against 12.4 on the author's fit. Gated by
a judge run and a headset look; the QP map follows the same contours (it reads the foveation variables).

## 6. The fit tool: "Fit the picture to your eyes" (probe + `calibrate.exe`, no engine)

What the wearer sees: the stream covers P (G off during the tool). The probe fills the candidate box with a mid blue, black outside, a thin lighter
line on the box edge so it reads where the panel is dim. No grid, no numbers in the headset. Not green (chroma key), not white (too bright).

| Key (PC keyboard, blind) | Does |
|---|---|
| Arrow | pushes that edge outward 0.5 degree, repeats while held |
| Shift + arrow | pulls it back |
| Tab | switches eye; the other eye goes black |
| Space | both eyes, the check view |
| Enter | keep it |
| Esc | leave without saving |

Protocol, written on the PC window: push until the black bar is gone, one step back to see it return, one step forward. One eye at a time (with
both eyes open the nose-side edges hide behind the other eye, and the eyes differ: the author's right eye 44 degrees outward against 37, Kevin's
8 degrees apart at the top). Eight short holds, under two minutes. Start box 0.80 x P so the bars are visible on the first frame.

Mechanics: `calibrate.exe` gains a Quick fit entry: starts the streamer minimized (as today), turns G off, starts the probe as a child in a
`fit` scene, takes the keys in its own window, writes the box to the probe's stdin on every change (the probe redraws it next frame), shows the
eight values live. Enter asks the light seal size once and saves a profile (method `quickfit`, per-eye tangents, P at measurement, seal, IPD if
known), then applies it as V and G with the margin. If P is not Apple's frustum (an old app), one line: update the headset app.

The 30-minute sweep stays in the profile manager only (it alone gives the polygon a hidden-area mask would need, section 10); it leaves the
wizard and the card. Remove it later if the mask is not pursued.

## 7. Games that ignore G (**engine**: shim + host; the rules are GUI)

The shim records, once at 2 s of a game and again on change, the size and FOV per eye of the layer the game submits, against what it was told
(no per-frame cost); the host writes them in the run record and the verdict:

| Verdict | Rule | Effect of G | Action |
|---|---|---|---|
| own frustum | submitted FOV differs from the told one by more than 0.01 | none | ignore list |
| fixed size | FOV matches, but the aspect ratio of the submitted size differs from the told **recommended size**'s by more than 5 % (a fixed, often square render packed into our box). Not the box's tangent aspect: the stream's density differs per axis (1300 against 1460 px/tan at P), so the told size of the author's fit is 2184 x 1927 (1.13) while its tangent box is 1.27, 12 % apart: every honouring game would be flagged | no GPU saved, view shrunk | ignore list |
| fixed size, second form | a previous run of the same executable under another G exists and the submitted size did not move with the box | same | ignore list |
| honours | aspect matches; the area may differ (a resolution scale) | saves GPU | nothing |
| lie (renders its own view, submits the told FOV) | not detectable | distorted picture | the user's eyes; manual entry |

Ignore list: `video.fov_unrestricted_games` (executables). A verdict of own frustum or fixed size adds the executable automatically; the game is
told V at its next start. Automatic because it is the deterministic consequence of a measurement, acts at a game start (never mid-session), is
logged, shown in Game statistics with the verdict and the numbers, and reversible (a tick per game; a global tick "add games by hand only").

## 8. The pieces (what each must do)

| Piece | Change | Engine |
|---|---|---|
| visionOS client | takes `{"airframexr":1,"view":{"left":[o,i,u,d],"right":[o,i,u,d]}}` over `ServerControlPacket::Reserved(String)` at connection and on change, declares min(P, V) per eye through the existing view-params path; declares P when no message came. Removes the temporary `[ratemap]` and `[fovtest]` lines. On v21 the same JSON moves to `ext_str` at the handshake | client build |
| Quest client | accepts the same message when cheap; V = P in practice | client build, optional |
| Host | sends the message at connection and on change (`server_core` `send_reserved`); G per eye (IPC `fovScale` 4 -> 8 values, `vision::fov_scale_from` per eye); applies the ignore list at game start; run record `game_submitted` + verdict | **yes** |
| Shim | records what the game submitted per eye (size, FOV) once and on change into the IPC | **yes** |
| Probe | `fit` scene: blue box per eye from stdin | no (lab tool) |
| `calibrate.exe` | Quick fit entry (section 6) | no |
| Launcher card, wizard, manager | section 9 | no |
| Session settings | density rule, foveation re-anchoring, written by the launcher | no |

Migration of today's settings: `fov_override` on with a profile's values becomes V = G = that profile (per eye mirrored) with margin 0 (the values
were already the user's choice); off stays V = G = P. **Except** values equal to the shipped default 0.97 / 0.71 / 0.69 / 0.63 that the 1.2.0 wizard
applied by itself (`HmdProfiles.ApplyFovDefault`): nobody chose them, and as V they would bring back the black bars of wearers who see more;
they become G only (V = P), with a one-time launcher line proposing the fit.

## 9. UI and profiles

**The card "Picture and performance"** (replaces "FOV restriction and calibration"):

1. *Picture edges*: `Whole panel` | a profile from the list, with **Fit the picture to your eyes (2 min)** and one line from the model:
   "Picture: 57 % of the panel's sharpness; a fit typically gives about 90 %". Quest: greyed, "The Quest reports what its lenses show: nothing
   to fit."
2. *What the game renders*: `The picture edges` (default) | a smaller profile, with the saving in game pixels and, when smaller than V, a yellow
   line with the share of the picture that goes black.
3. *Games that ignore it*: the count, a link to Game statistics (verdict, numbers, the per-game tick), the global automatic/manual tick.
4. Fold *Advanced*: render quality, stream density, full frame, the edge ratio preset, the pipeline preview and its split warnings (as today).

**Wizard step 5 "Picture edges"**: `Whole panel` selected by default on the Vision Pro, the fit button, nothing else. The shipped author's
profile is never pre-selected.

**Profiles (`config\user_fov_profiles.json`, schema 2):** a profile is the wearer's window; V and G derive from it. New optional fields: per-eye
tangents (`tan_left`, `tan_right`; `tan` stays the mirrored union for old readers), window polygon per eye, `method` (quickfit | sweep |
slider | manual), `declared_at_measure` (P then), `light_seal`, `ipd_mm`, `client_build`, `visionos`. The active profile is remembered per
headset name. The shipped Vision Pro profile is relabelled "Author's fit (one wearer, 2026-10-29)" with a source the wizard never proposes, and
loses `client_fov_scale` (dead advice on our client). Import and export unchanged.

## 10. Later, only with a measurement

- **Hidden-area mask** from the sweep's polygon through `XR_KHR_visibility_mask` and the LibOVR stencil (**engine**, likely a VDXR patch: the
  extension is advertised with an empty mask today). The rectangle's corners outside the measured ellipse are about 21 % of G; Unity and Unreal
  native games skip masked pixels, UEVR does not. Default off until one Unreal game is measured.
- **A proposed community box** once five or more wearers across light seal sizes fit inside one box with margin: offered, never applied.
- **Per-frame or canted views** on v21 (`docs/ALVR_v21_MIGRATION_PLAN.md`).

## 11. Order of work and tests

Order: **0, A, D, B, C, E.** Step 0 decides the sign of every vertical number; A and D need neither the engine nor a client build, and D is the
one measured gain (-17 % encoded pixels at Panel with no loss). Until C exists the client declares P whatever the PC wants, so a fit (B) can
only be applied as G: the sharpness of section 3 waits for C and a client build.

| Step | What | Needs | Lab | Headset |
|---|---|---|---|---|
| 0 | **Vertical orientation.** With a standard projection matrix the client declares the frustum upside down (`_extractFrustumTangents` returns left, right, bottom, top; `Renderer.swift` sends the third as up); its own defaults say 45 up / 50 down where our docs say 50 / 45. Log the raw `computeProjection` matrix once (one `[proj]` line per eye with the 16 values) in the next client build; if confirmed, fix the client (z, w order), re-read the `[ratemap]` vertical table mirrored and flip the vertical numbers of section 5b | client build (one log line, then a one-line fix) | `python3 harness/swift_headset_check.py` (the declaration check FAILS today and must PASS with the fix; `--projection-y-flipped` covers the other reading); then the Swift headset against the streamer: `views_config` in the host log equals the frustum | the `[proj]` line; fovgrid with asymmetric up / down marks, read by the wearer: the 0-degree line at eye level |
| A | Wizard default `Whole panel`; author's profile relabelled, never proposed; `client_fov_scale` removed; card and wizard wording; migration of section 8 (the shipped default is not a choice) | GUI | `ui_matrix`, `fov_store_check`, `fov_profile_check` (expects A: FAIL until built) | a fresh install shows no black |
| D | Density rule + rate-map foveation (section 5b), the zero-loss geometry: `PipelineModel` port of the oracle's `--ratemap` mode (`ratemap` in the profile and the oracle: done 2026-11-05). Vertical parameters only after step 0 | settings | `fov_stream_geometry.py --selftest`; `pipeline_model_check` (its grid has Apple's frustum, the fit and 4096 x 3584); `e2e_fov_avp_panel`, `e2e_fov_avp_panel_ratemap` (+ `_rule`) against `e2e_openxr_foveated` (layout, decoded counter and patch, encode ms); `e2e_fov_avp_fit_ratemap` (the vertical orientation of the band); the same with the Swift headset: the client's own foveation variables (`swift.client_foveation`, Float32 maths) equal the encoded frame the host sent (the host's FFR.cpp mixes float and double: a 32-pixel disagreement would misplace the client's decompression); `bench-quality --eye` at the encoded sizes; `slices_per_frame` = 3 at the new heights | A/B on the 5090 at Panel: today's 4 / 5 against the zero-loss geometry: encode ms, the judge, the eyes on the field where today over-compresses (the upper one, or the lower one if step 0 flips it); then a fitted V after C |
| B | Quick fit: probe `fit` scene + `calibrate.exe` entry + profile schema 2; applied as G until C | probe + GUI | a scenario that drives the probe's stdin and reads the blue box's edges in the decoded stream (`analyse_fov_restrict` margins); `ui_matrix` rows | two wearers fit themselves; the fit matches their sweep within the margin |
| C | Channel per eye (V to the client), G per eye, verdict (told **size** aspect, section 7), ignore list | **engine** + client build | the Swift headset follows the message with the client's real code (the Rust mock's `--views-tan` stand-in retired); `e2e_fov_game_*` assert the verdict and the skip at the second start; all `e2e_fov_*` as regressions | a fitted V visibly sharper than P at equal stream size; the Enshrouded mod lands on the ignore list by itself |
| E | Mask, community box | **engine** / data | | after one Unreal measurement; after five wearers |

Lab infrastructure owed for the steps above: the Swift headset against the streamer (`harness/swift_headset/README.md`: from WSL if the
builder can reach the laptop, else built on the builder with Swift for Windows) and its `harness/run.py` wiring (`client.kind: "swift"`);
retire `e2e_fov_declared_ceiling` (the 1.2.0-1.3.1 fixed box no client sends any more).

What the lab cannot judge stays owed to the headset and is written in `docs/progress.md` with the legend.
