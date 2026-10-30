# FoV client ceiling: plan

Status 2026-10-29 night: **shipped as visionOS client 1.2.0 (fork tag `v1.2.0`, branch `airframexr-client`, built by TestFlight run 16's content minus the debug line; not re-dispatched). The ceiling client ran on the author's Vision Pro.** `config\airframexr_views.json` on the 5090 shows the declared box **1.045 / 0.800 / 0.759 / 0.871** (left eye; right 0.802 / 1.048 / 0.761 / 0.873), the predicted 1.05 / 0.80 / 0.76 / 0.87 within 0.005: the circumscribing factor holds. The 1.1.1 override applied against it (92 / 89 / 91 / 70 % per edge), the game was told 0.959 / 0.709 / 0.689 / 0.609 exactly and rendered 3212 x 2552. Logs: `private/logs/5090-2026-10-29-fovgrid`. Client constants: fork branch `fov-calibration` `acf3de9`. Follows `docs/FOV_CALIBRATION.md` and the release 1.1.1 (`docs/AVP_FOV.md`).

## Problem

The AirFrameXR visionOS client declares to the streamer `min(the panel's own tangents, a fixed box)` per edge and nothing else
(`ALVRClient/Renderer.swift`, `GlobalSettings.visibleFovTangents`, left-eye constants **0.94 / 0.93 / 0.90 / 0.76**, mirrored for the right eye). There is no FoV Scale
on the headset any more (UI stripped, scale 1.0). After the circumscribing step the declared box is what the host log shows: **0.956 / 0.946 / 0.915 / 0.772**
(`config\airframexr_views.json` on the author's PC; the same caps appear in a second wearer's Advanced window: outer 0.95, lower 0.77).

Consequences:

- The streamer's override (`fov_outer` ... `fov_lower`) can only go **down** from the declared box ("never more than the headset's"). The box is a hard ceiling.
- Anything the wearer sees beyond the ceiling is black whatever the settings say, and only a new client build moves it.
- The constants come from the first traced estimate (2026-10-27, one person, left eye, +3 degrees), not from the sweeps.

Evidence (sweeps, eyes following the dot, `docs/fov_sweeps/`; a second wearer's file `fov_sweep_Kevin_visionpro_run1.csv` is kept out of the repo, name in the file):

| | outer | inner | upper | lower |
|---|---|---|---|---|
| Declared box today | 0.956 | 0.946 | 0.915 | 0.772 |
| Author, tight box, following gaze | 0.71 | 0.59 | 0.61 | 0.57 |
| Author, either gaze (following or fixed), 100 % | 1.03 | 0.74 | 0.71 | 0.62 |
| Second wearer, tight box, following gaze | 0.93 | 0.67 | 0.68 | **0.80** |
| Shipped 1.1.1 profile (`0.96 / 0.71 / 0.69 / 0.61`) covers | 100 % of the author's following window | | | 92.3 % of the second wearer's (lower edge short) |

The second wearer needs about 0.80 downward and the ceiling gives 0.77: unfixable on their side until the client changes. Two wearers, one fit each.

## Decision to keep

**Nothing calibration-related goes back on the headset.** No slider, no menu: the calibration lives on the PC (`airframexr.json`, the Advanced window's four fields, the
profile button). Only the **ceiling constants** change.

## Proposal

Replace the fixed box by a generous per-edge ceiling that covers the measured wearers, with margin; the per-person restriction stays in the four fields.

| | outer | inner | upper | lower |
|---|---|---|---|---|
| Declared box today | 0.956 | 0.946 | 0.915 | 0.772 |
| **Proposed ceiling (declared, after circumscribing)** | **1.05** | **0.80** | **0.76** | **0.87** |

How it was chosen: per edge the larger of the author's either-gaze 100 % box (1.03 / 0.74 / 0.71 / 0.62) and the second wearer's following box (0.93 / 0.67 / 0.68 / 0.80),
plus about 2 degrees. As client constants (before circumscribing, left eye outer / inner / up / down) they are to be derived from the declared values with the same factor
(about 1.017) and checked in `airframexr_views.json` after the build.

Effects (computed, not measured):

- Area of the declared box: 3.01 against 3.21 today, **about 6 % smaller**: the stream's fixed eye size covers slightly fewer degrees, so a little more pixel density. No loss.
- Game cost: unchanged. The override restricts what the game renders; a ceiling above the override restricts nothing by itself. A user who never ticks the override renders the
  ceiling-sized frame (about 6 % fewer pixels than today).
- Encode, decode, network: unchanged (stream size is a setting, not the box).

## Steps

1. Edit the constants in `ALVRClient/GlobalSettings.swift` (client branch `fov-calibration`; the file already carries the 1.1.1 set, commit `250f991`, to be replaced by the ceiling,
   not by the per-person set: the ceiling is not the restriction). Update the comment: ceiling, not visible window.
2. Build through the fork's CI / TestFlight (Mac; `.github/workflows/testflight.yml`, `docs/TESTFLIGHT.md`). Dispatch and push need the owner's approval.
3. Check on the headset: `airframexr_views.json` shows the new declared box; `fovgrid.exe` white border sits beyond the visible edge on all four sides (black outside, visible
   window inside); the 1.1.1 override fields accept values up to the new ceiling.
4. Second wearer: lower edge raised in the four fields (about 0.80 to 0.85), black strip at the bottom gone; slider at 0 % (the quality slider at its right end gives 100 % of an
   unrestricted game's pixels, no saving).
5. Write what was tested and not in `docs/progress.md`, update `docs/AVP_FOV.md` (a section on the ceiling) and `docs/releases/`.

## Interim for the second wearer

Until the new client: no way to raise the lower edge (FoV Scale is gone from the client). He can only restrict. Suggested starting values once the ceiling is out, from his
following-only box plus about 2 degrees (no fixed-gaze data): 0.96 / 0.74 / 0.74 / 0.85, not measured. The calibration bundle should run the fixed-gaze pass as well
(`run_sweep.bat` runs following only: the author's first set shipped too tight for exactly that reason).

## Risks and open questions

- Two wearers, one fit each: a third may exceed the ceiling (each exceed means a rebuild). Re-check the ceiling as sweep files arrive; widening an edge by 0.1 costs about 5 % of
  stream pixel density on that axis.
- The ceiling is never above the panel's own tangents (1.79 / 1.02 / 1.22 / 1.02); the proposed values are all below them.
- A per-wearer client setting (the "Calibrated" preset of the calibration plan, phase 2) would remove the ceiling question; it needs the host-to-client channel, which does not exist
  on the v20 protocol without patches.
- **Overlap**: another line of work, branch `fov-restriction-aware-engine` (FoV-aware stream, "S4 client" step in its notes), also touches what the client declares. Agree who edits
  `GlobalSettings.swift` / the declare path before changing it here. The longer-term idea in `docs/FOV_PIPELINE_OPTIM.md` (declare the rectangle itself and shrink the stream) would make
  the ceiling irrelevant; this plan is the small step that does not need it.

## Built on 2026-10-29 (the parts that do not need a Mac or a headset)

- Client: `ALVRClient/GlobalSettings.swift` on the fork branch `fov-calibration` now declares the ceiling **1.03 / 0.79 / 0.75 / 0.86** (the targets divided by
  the circumscribing factor 1.017; the host should see about 1.05 / 0.80 / 0.76 / 0.87), with the comment rewritten as a ceiling. Local commit `acf3de9`; the push to
  `kteque/alvr-visionos` and the TestFlight dispatch (`.github/workflows/testflight.yml`, `workflow_dispatch`, inputs `marketing_version`, `low_latency_streaming`) need
  the owner's go.
- Calibration bundle: `run_sweep.bat` now runs the **fixed-gaze pass after the following pass** into the same CSV (the shipped 1.1.0 set was too tight for want of it).
- Lab: `harness/scenarios/e2e_fov_declared_ceiling.json`: the mock headset declares the ceiling (`--views-tan 1.05,0.80,0.76,0.87`) and the override asks the 1.1.1
  rectangle: the restriction must be computed against the ceiling, the app told R exactly, margins black, picture inside. This is the field configuration after the client
  update, on the PC side.

## Tests to retry or extend when the new client is out

| Test | Why the ceiling touches it | What to expect |
|---|---|---|
| `e2e_fov_declared_ceiling` (new, PC only) | the exact field configuration | **PASS 2026-10-29** on the 3080 Ti (the host's existing override path against the wider box: R exact, 0.91 x 0.80 of the stream's eye size, margins black) |
| The Advanced window's "override saved for another headset" guard (`AppSettings.FovFits`, `fov_profile_check.py` second check) | it compares the **shape** of the declared box with the one the override was saved for (every edge in the same ratio within 5 %). The ceiling changes the shape (outer x 1.10, inner x 0.85, upper x 0.83, lower x 1.13 against today's 0.956 / 0.946 / 0.915 / 0.772), so after the client update every saved override reads as "made for another headset": the window shows the tick off while the host still applies the file's values. **Changed 2026-10-29**: the guard keys on the headset's name (`video.fov_for_headset`, saved with the override; same name = fits whatever the box) and keeps the shape rule only when a name is unknown; the uitest self-test gained "same name, new box shape" (`fov_profile_check.py` **PASS**) | the saved 1.1.1 override stays ticked and applied after the first connection with the new client |
| `fov_profile_check.py` first check (profile values) | unchanged (the profile holds the per-person rectangle, not the ceiling) | PASS as today |
| `harness/fov_stream_geometry.py` defaults and `docs/FOV_PIPELINE_OPTIM.md` numbers | the declared box D changes: R is 72 % of the ceiling box (3.02 tan2) instead of 70 % of today's (3.10); the fit at today's density becomes 3200 x 2528 per eye | update the default `DECLARED` and the two tables once `airframexr_views.json` shows the new box |
| The "Fit the stream" tick (branch `fov-restriction-aware-engine`) | it fits the stream to whatever box is declared: with the ceiling it gives about 6 % fewer pixels than today, **not** the 30 % of the rectangle itself; the 30 % needs the client to declare the per-wearer rectangle (the host-to-client declare channel, S6 of that plan) | expected, say so in the Advanced window's preview |
| Headset (hands-on) | `airframexr_views.json` shows about 1.05 / 0.80 / 0.76 / 0.87; `fovgrid.exe` outline beyond the visible edge on all four sides; the 1.1.1 fields accept values up to the ceiling; the second wearer raises the lower edge to about 0.80-0.85 and the black strip is gone | the write-up in `docs/progress.md` and `docs/AVP_FOV.md` |

## First night with the ceiling client (2026-10-29, 5090, 1.2.0-pre)

- The wearer ran `fovgrid.exe` with the 1.1.1 override **on**, so the grid showed the game's own rectangle (43.8 / 35.4 / 34.6 / 31.4 degrees), not the headset's
  window: the 40-degree green line plus part of the next 5-degree band on the outer sides, the 30-degree yellow line plus a partial band above and below. The wearer's
  report ("the limit right at the top; a square more to the left and right of the green edge; at the bottom no green and a full square missing") matches that geometry
  and says one useful thing: **the top edge of the rectangle is visible** (there is black above it), so the upper value can go above 0.69; the fixed-gaze sweep said
  0.71 (35.4 degrees). The bottom edge was not seen as a limit: 0.61 reaches the visible edge there. To measure the headset's window itself, run the grid with the
  override **off**; to check a candidate rectangle, `fovgrid.bat outer,inner,upper,lower` draws it on the grid.
- Pinch as a click (hands as controllers) reached the host "sometimes, unreliably" (the wearer). The host records input only as packet counters (41 600 input packets
  over the session, about 170 a second: hand tracking was streaming), not per press, so the lab cannot say how many pinches became trigger presses; the sweep
  (`run_sweep.bat`) depends on it. The fallback of `docs/FOV_CALIBRATION.md` (the sweep drawn by the client, pinch read from hand tracking) is the robust path.
- The guard: the author's config had no `fov_for` (it came from the personal zip), so nothing to migrate. For users whose 1.1.x config carries `fov_for` with the
  first client box and no headset name, `FovFits` now recognises that one transition (first box -> ceiling, both within 3 %) and keeps the override; a Quest's box
  still does not fit (`fov_profile_check.py`).

## Sweep of 2026-10-29 night (fit 3)

The run connected with the **ceiling client** (the probe saw 1.045 / 0.800 / 0.759 / 0.871), not the calibration build: TestFlight build 15 is the `calibration` branch, build 11 the
ceiling; the app that opened on the headset was still the ceiling one. The measurement was not clamped (every crossing inside the ceiling), so the result stands: shared tight box
0.86 / 0.70 / 0.63 / 0.66 (following gaze; `docs/AVP_FOV.md`, fit 3). The lower edge measured 0.66 against the shipped 0.61 and the ceiling's 0.87: the ceiling leaves room for it, and the profile moved to 0.66 the same night (`docs/AVP_FOV.md`).
The sweep only ran the following pass (the fixed-gaze pass of `run_sweep.bat` was not started). The first attempt of the evening failed because the probe was launched before the
streamer (the probe binds to the host at its start and goes headless after 20 s); the order is streamer, headset connected, then `run_sweep.bat`.

## The margins with a chroma key on (client 1.2.1)

Only while the streamer's chroma key (`video.passthrough`, the wizard's "transparent background") is on; with it off the margins are the opaque black described above and nothing below applies.

The restricted frame covers the declared box; what lies between it and the edge of the visible window is the margin. The rule is "the margin looks like the frame's own edge, but black if that edge is picture":

- The client clears the render target **transparent** and draws a ring around the frame (`videoFrameBorderVertexShader` in the fork's `Shaders.metal`): 4 thin quads, inner vertices on the frame quad's corners, outer ones far outside, both carrying the corner's texture coordinate (clamp to edge, no overdraw of the picture).
- Its fragment runs the same key mask on that edge texel and writes **black with the mask as alpha**. A game's edge is picture: mask 1, opaque black margin (the FOV restriction as before). The idle frame and the desktop mirror's background are the key colour (`00FF00` everywhere): mask 0, transparent margin, so the room shows to the edge of the window with no black frame around it.
- It is decided on the GPU from the decoded frame, so the margin changes on the same frame as the picture: no game-start signal, no host state, no ALVR change. `RealTimeConfig` (live `video.passthrough`) was considered and set aside: the v20 client drops it in `c_api.rs`, the server sends it once a second, and it races the video.
- Cost, by construction: one texture sample per ring pixel (the ring is the gap between the declared box and the window, about 1 % or less of the pixels at the 99 % rectangle), same render pass, no extra load or store. **Not measured**; the check owed is the command buffer's GPU time with and without the ring on the headset.
- Consequence for the streamer: nothing to do, as long as the idle colour fills the whole frame including its outermost texels (a border of another colour would make the margin opaque). A game whose edge pixels sit near the key colour gets a partly transparent margin (the ramp makes this unlikely; untested).

Status: built (TestFlight run 41041671109 on the `kteque` fork, version 1.2.1, branch `airframexr-client` `576577f`); **not tried on a headset**. To check: between games the room shows with no black frame; in a game the margin is black as in 1.2.0; the desktop mirror floats on the room with no black frame.
