# End-to-end FOV design

Status 2026-11-04: **design.** What exists today is marked *shipped*; the rest is *planned* and the steps that touch the engine (`ovrshim/`, `tools/host/`,
the IPC, ALVR's server side) need the owner's approval before they are built. Supersedes the "one control" idea of `docs/FOV_APERTURE_PLAN.md` (which
merged the two settings below into one); that plan's measurements, protocol notes and lab work stay valid. Background: `docs/AVP_FOV.md` (calibration),
`docs/FOV_PIPELINE_OPTIM.md` (stream geometry), `docs/EXPERIMENTAL_SWITCHES.md` (the settings as they are).

## 1. Two settings, two purposes

| | **Headset view** | **Performance restriction** |
|---|---|---|
| Question it answers | What does the headset show, and what does the stream cover? | How much does the game render? |
| Purpose | Picture: put the stream's pixels where the wearer can see | Speed: fewer game pixels, less game GPU time |
| Who applies it | the headset app (declares the box), the streamer (sizes the stream) | the shim (the FOV and render size the game is told) |
| Works for every game | yes (it is applied after the game) | **no**: only games that honour the FOV and the render size they are told |
| Cost when set too small | black where the wearer could see | black where the wearer could see, and nothing saved if the game ignores it |

**One rule ties them:** the game is never told more than the headset view (whatever lies outside it is never shown), and the stream never covers
more than the headset view. Inside the headset view, the restriction is a performance choice. The box the game is told is always inside the
headset view, which is always inside the panel.

## 2. The headset view per headset

### Meta Quest: no setting

The Quest's OpenXR runtime reports the lens window itself (`xrLocateViews`; Quest 3 measured by the stock client at about 54 / 40 / 44 / 55 degrees
outer / inner / up / down, constant across sessions). The client declares it, the compositor reprojects the frame over it (`client_openxr`
`stream.rs`: the projection layer carries the frame's FOV). Nothing to calibrate, nothing to set. *Shipped* (Quest client 1.3).

**UI rule (decided 2026-11-04): no headset fine tuning for Meta Quest users.** A smaller headset view could only cut picture the wearer sees, and
there is no oversized box to correct. When the last headset seen is a Quest (the name in `config\airframexr_views.json`, as `video.fov_for_headset`
already uses; Quest 3, Quest Pro, Quest 2, stock or AirFrameXR client):

- the **Headset view** row (Panel .. Calibrated) is shown **greyed**, with one line: "The Quest reports what its lenses show: nothing to tune."
  Greyed rather than hidden, so the card looks the same for everyone and says why;
- **Calibrate now** (the 30-minute sweep) is not proposed in the FOV card, the profile manager or the setup wizard's Field of view step;
- the **performance restriction** stays fully available (the Quest 3 starting point 51 / 40 / 43 / 54 degrees is a restriction profile, about 10 %
  fewer game pixels), and so does **Check with fovgrid** (it checks a restriction rectangle on any headset);
- no headset seen yet: everything enabled, as today. A Vision Pro seen later enables the row again.

### Apple Vision Pro: a setting, because visionOS does not tell us what the wearer sees

What visionOS gives: the frustum the compositor wants rendered, from `LayerRenderer.Drawable` at run time (`computeProjection`), about
**1.79 / 1.02 / 1.22 / 1.02** tangents (60.8 / 45.6 / 50.6 / 45.6 degrees, left eye; varies slightly with the IPD). These are **not marketing figures**:
they come from the API on every connection. They are, however, larger than what a given wearer sees:

- They cover every eye position the light seals allow, plus a margin for reprojection (the lens design estimate is about 60 degrees temporal,
  50 nasal, 45 up and down; Hypervision via K. Guttag).
- The window a wearer actually sees depends on the fit (light seal size, inserts, eye relief): measured on two wearers with the sweep, the
  author's either-gaze window is 1.03 / 0.74 / 0.71 / 0.62 and a second wearer's lower edge reaches 0.80 (`docs/FOV_CLIENT_CEILING.md`).
- No API reports the lens window. The render-context stencil mask (`drawMaskOnStencilAttachment`, visionOS 26) covers the whole texture in full
  immersion. The rasterization rate map might encode it: the client 1.3.2 logs it (`[ratemap]` lines) to find out.

So the Vision Pro needs a headset view setting with two ends:

| Headset view | Box | Picture | When |
|---|---|---|---|
| **Panel** (default) | Apple's frustum | whole panel for every wearer; the stream's pixels spread over the largest box (the stock app's sharpness) | no calibration, or the wearer wants the edges |
| **Calibrated** | the wearer's calibration (`config\user_fov_profiles.json`, e.g. the standard 0.97 / 0.71 / 0.69 / 0.63) | about 1.7x sharper per axis than Panel at the same stream size (area 35 %), or the same sharpness with about 65 % fewer stream pixels (computed) | after a calibration that fits this wearer |
| In between (slider) | per edge between the two, in angle | | to trade a little edge for sharpness without a full calibration |

History: clients 1.2.0 to 1.3.1 declared a fixed box (1.03 / 0.79 / 0.75 / 0.86) for everyone, a calibration of two wearers baked into the app.
It cut the view of wearers who see more (a tester's black bars, 2026-11-04). Client 1.3.2 (TestFlight build 38) declares Apple's frustum: the
"Panel" end, *shipped*. The "Calibrated" end needs the PC to tell the client which box to declare (section 5).

## 3. The parameters

| Parameter | Where | Layer | Default | Applied | What it does | State |
|---|---|---|---|---|---|---|
| Panel box | the headset app, from the runtime | headset view | | every connection | the largest box; declared to the streamer in `ViewsConfig`, kept in `config\airframexr_views.json` | shipped |
| Calibration profiles | `config\user_fov_profiles.json` (launcher FOV card, `configure.exe --fov`, `calibrate.exe`, `fovgrid`) | headset view | Vision Pro standard / Quest 3 starting point | when chosen | the measured window per wearer; copied into the settings, the engine never reads this file | shipped (today copied into the restriction fields) |
| `video.headset_view` | `airframexr.json`, FOV card | headset view | `0` (Panel) | streamer restart (new connection) | 0..100: 0 = Panel, 100 = the chosen calibration, per edge in angle; ignored on headsets without the channel (Quest, stock apps) | **planned** |
| `video.headset_view_tan` | `airframexr.json` | headset view | unset | with the above | the calibrated box (outer, inner, upper, lower tangents, left eye) the 100 % end uses | **planned** |
| Stream eye size | `session.json` (`openvr_config.eye_resolution_*`, `transcoding_view_resolution`) | stream | profile | streamer restart | the stream's pixels per eye, spread over the headset view | shipped |
| `video.stream_fit_window`, `stream_reference_eye`, `stream_reference_fov` | `airframexr.json`, Advanced | stream | off | streamer restart | size the stream to the headset view at the reference density (fewer pixels when the view shrinks) instead of keeping the pixel count (sharper) | shipped |
| `video.fov_override` | `airframexr.json`, FOV card | restriction | off | the next game start | on: the game is told the four tangents below instead of the headset view | shipped |
| `video.fov_outer` / `_inner` / `_upper` / `_lower` | `airframexr.json` | restriction | the headset view's own | next game start | the box the game is told; never above the headset view, at least a fifth of it | shipped |
| `video.fov_render_quality` | `airframexr.json` | restriction | 0 | next game start | the game's render size between the stream's density over the restricted box (0) and the stream's eye size (1): spend the saving on supersampling | shipped |
| `video.fov_for`, `video.fov_for_headset` | `airframexr.json` | restriction | | | which headset the four tangents were made for (a Quest's box sets a Vision Pro's values aside) | shipped |
| `video.fov_unrestricted_games` | `airframexr.json`, Game statistics / FOV card | restriction | empty | that game's next start | executables the restriction is not applied to (the game is told the headset view) | **planned** |
| Game verdict | run record (`games.rs` `pipeline` block), Game statistics | measurement | | after 2 s of a game | what the game submitted (size, FOV per eye) against what it was told: honours / fixed size / own frustum | **planned** |

## 4. What happens, case by case

### 4.1 Restriction off

The game is told the headset view, exactly, and the recommended size is the stream's eye size (render quality hidden). The stream covers the headset
view; black appears only where the game drew nothing.

- Quest: the lens window. No black.
- Vision Pro, Panel: Apple's frustum, the whole panel, the stock app's picture. No black. (Client 1.3.2.)
- Vision Pro, Calibrated: the wearer's window, sharper; beyond it black, by the wearer's choice (that is what the calibration measured as not seen).
- Before 1.3.2 the Vision Pro showed black bars here with no setting to fix it (the fixed ceiling): that was the bug.

### 4.2 Restriction on, the game honours it

The game is told the restriction box and renders fewer pixels (UEVR, most native OpenXR games: 65 % fewer for the standard rectangle against the
Panel view, computed). The stream still covers the headset view; between the restriction box and the edge of the view it is black.

- If the restriction box equals the headset view (the common case once the view is calibrated), there is no black ring and nothing is lost: the
  FOV card proposes this ("Restrict to the headset view").
- If the restriction is smaller than the view, the ring is black inside what the wearer sees: the FOV card warns (yellow) with the share of the
  view that goes black.

### 4.3 Restriction on, the game does not honour it

The shim measures what the game actually submits (planned, section 3 "Game verdict"); today only the lab can tell
(`e2e_fov_game_*`, `docs/FOV_APERTURE_PLAN.md` section 2).

| Verdict | What the game does | Effect of the restriction | What we do |
|---|---|---|---|
| **Honours** | renders the told FOV at about the recommended size | saves GPU | nothing |
| **Fixed size** (mods that force a fixed, often square 1:1 render) | renders the told FOV at its own fixed size | **no GPU saved**; the view shrinks; its fixed pixels are packed into the smaller box, denser than the stream carries (60 % of its detail reaches the stream with the standard rectangle under a Panel view) | report it; offer **"Don't restrict this game"** (adds the executable to `video.fov_unrestricted_games`); at its next start the game is told the headset view: the view comes back at no GPU cost |
| **Own frustum** | ignores the told FOV and submits its own | no GPU saved; the composition maps what it submitted, so its picture already reaches the edges of the view (nothing of ours crops it inside the view) | report it; same offer, which here only makes the setting honest |
| Lies (renders its own FOV but submits the told one) | | picture distorted | not detectable from data; the user's eyes, then "Don't restrict this game" by hand |

Why manual and not automatic: the game has already started when the verdict exists (VDXR reads the FOV once, at the game's start), so any change is for
the next start anyway; and the project prefers a toggle the user sees to a mode that switches itself (`docs/DEVELOPMENT_PHILOSOPHY.md`). An automatic
"skip the restriction for games that ignored it last time" can be added later if the per-game toggle proves tedious; it needs no new mechanism.

### 4.4 A calibration larger than what the headset app declared

A calibration measures the wearer, not the app, so it is never refused for being larger than the box the headset app declared at its last connection
(`config\airframexr_views.json`). Testers on the Vision Pro apps 1.2.0 to 1.3.1 hit this: the profile manager marked their values red ("more than the
headset shows (1.04, 46°)") and the launcher's FOV card hid those profiles, because the declared box was the old fixed ceiling, not their eyes.
*Changed 2026-11-04 (built, not run on a PC):* it is a yellow note that says games get at most the declared box per edge until the app declares more
(the streamer already caps each edge, `vision::fov_scale_from`), and the "share of the pixels" counts each edge at most the declared one. With the
1.3.2 app the declared box is the whole panel, so the note disappears after one connection. Only values above the panel itself would remain
meaningless, and the app's own box is then the panel.

### 4.5 Pixels outside the headset view

Pixels a game renders outside the headset view (a square mod over a frustum wider than the view) are discarded by the composition. With a calibrated
view they are outside what the wearer sees, so nothing visible is lost; with a Panel view they are outside the panel.

## 5. What the pieces must do

| Piece | Today | Planned |
|---|---|---|
| Vision Pro client | declares Apple's frustum (1.3.2) | declares `min(panel, box from the streamer)` when the streamer sends one; Apple's frustum otherwise (old streamers keep working) |
| Channel streamer -> client | none | ALVR v20 `ServerControlPacket::Reserved(String)`, ignored by stock clients: `{"airframexr":1,"headset_view":[o,i,u,d]}` at connection and on change. Engine (server side) + client (`client_core` event + Swift) |
| Quest client | declares the runtime's FOV | unchanged (may accept the same message later; no need today) |
| Streamer, launcher | restriction fields; stream fit tick | FOV card: headset view (Panel .. Calibrated, greyed for a Quest, section 2) and the restriction as two separate rows; "Restrict to the headset view"; the yellow ring warning; stream size from the headset view (the existing pipeline model); per-game list |
| Shim | told FOV and size; composes the submitted FOV | records what each game submitted (size, FOV per eye) once per run into the IPC: no per-frame cost |
| Host | forwards the restriction | run record `game_submitted` + verdict; applies `video.fov_unrestricted_games` at game start (scale 1 for that executable) |
| Game statistics | pipeline strip | the verdict per game and the "Don't restrict this game" button |

Migration of today's settings: an existing `fov_override` on with a calibration's values becomes headset view = Calibrated with those values and the
restriction on with the same box (picture and GPU as before, plus the sharper stream once the client declares it). Off stays off (Panel).

## 6. Order of work and tests

| Step | What | Needs | Lab | Headset |
|---|---|---|---|---|
| 0 | Client declares Apple's frustum; lab game behaviours | done (client build 38; lab not run) | `e2e_fov_game_honours` / `_fixed_size` / `_own_fov` / `_square_mod` + all `e2e_fov_*` | the tester's bars gone; `[ratemap]` lines read |
| 1 | Game verdict in the run record and Game statistics | engine approval | the four scenarios assert the verdict | one real game per verdict (UEVR; the Enshrouded mod) |
| 2 | "Don't restrict this game" | step 1 (GUI + host) | second start of a fixed-size probe is told the full view | the Enshrouded mod: view back, same fps |
| 3 | Headset view setting and the channel | engine approval + client build | mock client follows the message (`--views-tan` replaced); stream sized from the view | Calibrated sharper than Panel at equal stream size; no black at Panel |
| 4 | Card rework: two rows, "Restrict to the headset view", yellow ring warning | GUI only | `ui_matrix` rows | |
| 5 | ~~Panel end from Apple's rate map~~ **dropped**: the map does not mark the lens window (section 8) | | | |
| 5b | Foveated encoding shaped like Apple's rate map (section 8): an existing-parameters profile, no engine change | session settings | encode time and judge at iso centre quality | A/B by eye |

## 7. Not decided

- The slider between Panel and Calibrated, or two choices only (a slider is proposed: a wearer between two fits can stop short of the edge).
- Stream size when the headset view changes: keep the pixel count (sharper) or keep the density (cheaper); today's `stream_fit_window` tick is
  the switch, measure encode and sharpness on the 5090 before choosing a default.
- Whether the restriction fields stay visible once "Restrict to the headset view" exists (proposal: yes, under the card's Advanced part, for
  performance below the view).
- The Quest: whether a smaller headset view than the runtime's is ever useful (sharpness at the edge's expense); no request so far.

## 8. What Apple's rasterization rate map says (client 1.3.2 `[ratemap]`, one tester, 2026-11-04 23:46, `alvr.log`)

Six snapshots per eye, 20 s apart, **identical to two decimals**: a static map in that session (not following the gaze, as far as one session with
a still wearer can tell). The compositor's frustum was exactly **60 / 45 / 50 / 45 degrees** (tangents 1.732 / 1.000 / 1.192 / 1.000, left eye outer /
inner / up / down): round design values, the same box for every wearer. Logical screen 6261 x 5023 per eye, physically rasterized 2432 x 2368.

Rate (physical pixels per logical pixel, 1 = full) across the left eye:

| Horizontal, outer -> inner | -59 | -50 | -40 | -28 | -20 | -12 | -3 | +7 | +16 | +24 | +31 | +37 | +43 deg |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rate | 0.06 | 0.09 | 0.15 | 0.30 | 0.70 | 0.96 | 1.00 | 0.99 | 0.94 | 0.43 | 0.23 | 0.17 | 0.13 |

| Vertical, up -> down | +48 | +41 | +32 | +26 | +20 | +13 | +6 | -2 | -9 | -16 | -23 | -34 | -43 deg |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rate | 0.12 | 0.19 | 0.32 | 0.66 | 0.97 | 1.00 | 1.00 | 0.94 | 0.89 | 0.58 | 0.28 | 0.15 | 0.10 |

Rate at or above 0.5 from about 24 degrees outer to 23 inner, 29 up to 18 down; at or above 0.25: 31 / 30 / 36 / 25; at or above 0.15: 40 / 40 / 44 / 34.

What follows:

- **It is not the lens window.** The rate never reaches zero (0.06 at the outer edge) and does not depend on the wearer: it is a fixed falloff of
  resolution, not a visibility mask. Step 5 (an automatic Panel box from it) is dropped; the headset view stays Panel or the wearer's calibration.
- **It is a ceiling on what the stream can show.** The client draws the frame through this map (`Renderer.swift` passes the drawable's
  `rasterizationRateMap` to its render passes), so detail the stream carries beyond the rate is discarded on the headset. The tester's stream
  (4096 x 3584 per eye over the declared box 1.83 + 1.04, about 1430 px per tangent unit horizontally) is below the full-rate density in the
  centre (about 2290 px/tan) but above it in the periphery: the map keeps about 690 px/tan at 28 degrees outer (2x fewer than the stream carries),
  340 at 40 degrees (4x), 140 at the outer edge (10x). Much of the peripheral detail we encode, send and decode is discarded by the compositor.
- **So foveated encoding should be shaped on this map** (step 5b): the target is a profile that never compresses below the map (no visible loss)
  and compresses down to it elsewhere. ALVR's foveation has another shape (a full-density band, then a fixed 4 to 5 times compression;
  `docs/FOV_PIPELINE_OPTIM.md`), so the comparison is per region, not "more" or "less": `harness/fov_stream_geometry.py` (ALVR's maths ported)
  is the tool to fit the existing `foveated_encoding` settings (centre size, shift, edge ratio) under the map, then measure encode time in the lab
  and judge by eye. The tester streams full frame (no foveation) at 8192 x 3584, the case where the gain would be largest; not assumed until measured.
- Caveats: one session, one wearer, one visionOS version; a dynamic (gaze-driven) map in other conditions would change the last point (foveation
  would then have to stay wide). The `[ratemap]` lines are temporary: remove them from the client in its next build.


## 9. Field data from the test slider (client branch `calibration-fov-slider`, TestFlight build 39)

The slider scales the compositor's frustum on every edge alike (0.70 to 1.00, steps of 0.05); the tester reports the lowest value without a
black edge, the edge that goes black first and the light seal. The app logs one `[fovtest]` line per connection (`logs\alvr.log`).

| Wearer (client id) | Light seal | Lowest value without black | First edge to go black | Frustum logged (left eye o / i / u / d) | IPD | visionOS | What it bounds (left eye tangents) |
|---|---|---|---|---|---|---|---|
| B (7488), 2026-11-05 | 25W | **0.80** | **bottom** | 1.758 / 1.011 / 1.208 / 1.013, then 1.742 / 1.004 / 1.198 / 1.005 | 65.6, then 66.6 mm | 27.2 (24N120) | lower edge between 0.76 and 0.81 (37 to 39 degrees); at 0.75 the other edges showed no black, so outer at most 1.31, inner 0.75, upper 0.90 |
| A (5299), 2026-11-04 | not reported | (no slider) the stock app at 0.80 showed no black; client 1.3.1's box (1.06 / 0.81 / 0.77 / 0.88 declared) did, outer and top | | 1.732 / 1.000 / 1.192 / 1.000 | | | outer above 1.06, upper above 0.77 |

What this adds (two wearers, not a population):

- **The frustum is not a fixed constant**: wearer B's varies by about 1 % with the IPD the headset measured (two connections 7 s apart,
  65.6 then 66.6 mm). Wearer A's was exactly 60 / 45 / 50 / 45 degrees.
- **The rate map is the same on both headsets**, to two decimals: a fixed map, not per wearer (section 8).
- **0.80 of the frustum shows no black for both wearers**, which would be about 25 % more stream pixels per degree on each axis than 1.00 at
  the same stream size. For wearer B the bottom edge is the limit and the other three edges have room to spare: a uniform scale wastes that room,
  a per-edge box (the calibration) would not. Not a default yet: more answers first, with light seal sizes.
