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
| Streamer, launcher | restriction fields; stream fit tick | FOV card: headset view (Panel .. Calibrated) and the restriction as two separate rows; "Restrict to the headset view"; the yellow ring warning; stream size from the headset view (the existing pipeline model); per-game list |
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
| 5 | Panel end from Apple's rate map, if `[ratemap]` shows the lens window | step 3 | | one wearer per light seal size |

## 7. Not decided

- The slider between Panel and Calibrated, or two choices only (a slider is proposed: a wearer between two fits can stop short of the edge).
- Stream size when the headset view changes: keep the pixel count (sharper) or keep the density (cheaper); today's `stream_fit_window` tick is
  the switch, measure encode and sharpness on the 5090 before choosing a default.
- Whether the restriction fields stay visible once "Restrict to the headset view" exists (proposal: yes, under the card's Advanced part, for
  performance below the view).
- The Quest: whether a smaller headset view than the runtime's is ever useful (sharpness at the edge's expense); no request so far.
