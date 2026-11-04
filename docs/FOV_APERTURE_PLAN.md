# FOV end to end: the aperture plan

**Superseded in its model by `docs/END_TO_END_FOV_DESIGN.md` (2026-11-04):** the headset view and the performance restriction stay two separate
settings instead of one "aperture"; the game behaviours, the measurements, the `Reserved(String)` channel and the lab work below still apply.

Status 2026-11-04: **plan, nothing of the aperture built.** Built today: the visionOS client 1.3.2 (TestFlight build 38: the client declares the
compositor's own frustum, the fixed ceiling is gone) and the lab's game behaviours (`xr_probe` options and four `e2e_fov_game_*` scenarios, compile
and syntax checked, **not run**: both builders offline). Follows `docs/FOV_CLIENT_CEILING.md` (the ceiling, now removed), `docs/FOV_PIPELINE_OPTIM.md`
(the stream shrink, "S6") and `docs/AVP_FOV.md` (the calibration).

## 1. Why rethink it

Today there are two knobs that do different things and users read them as one:

| Knob | Where | What it changes | What it buys |
|---|---|---|---|
| The box the headset app **declares** | visionOS client (1.2.0-1.3.1: a fixed ceiling 1.03 / 0.79 / 0.75 / 0.86; 1.3.2: the compositor's own 1.79 / 1.02 / 1.22 / 1.02) | what the stream frame covers; the client maps the frame over it | **sharpness**: the stream's fixed pixels over a smaller area (the ceiling was about 1.4x per axis over the stock box) |
| The streamer's **FOV restriction** | PC (`video.fov_override` + four tangents, the FOV card) | what the game is told (`ovrshim` `m_gameFov`, `render_size.h`) | **game GPU time only**, and only if the game honours it |

Two field facts broke the model:

1. **The ceiling cut the panel for wearers who see more** (a tester's black bars with the restriction off on 1.3.1, none on the stock app at FoV 1).
   No visionOS API reports the lens window: the render-context stencil mask (`drawMaskOnStencilAttachment`, visionOS 26) "stores the full
   texture" in full immersion. So the only safe automatic box is the compositor's frustum; anything tighter needs a measurement of the wearer.
2. **Many games ignore the restriction** (the user's report: mods that force a fixed, often square 1:1 render size). For them the restriction saves
   nothing and only removes picture. The pipeline cannot know beforehand; it can measure what the game submitted.

## 2. What happens to each kind of game today (computed, `analyse_fov_restrict` metrics; left eye, Vision Pro frustum, stream 1760 x 1600 per eye, rectangle 0.97 / 0.71 / 0.69 / 0.63)

The composition maps whatever frustum the game **submits** into the stream (`EyeHomography(m_eyeFov, layer fov)`, `ovrshim/driver.cpp`): nothing the
game rendered inside the declared box is cropped by us. What changes is what the game is told.

| Game | Render saved vs unrestricted | Its pixels that reach the stream | Headset view with picture | Game detail the stream can carry (per axis) | Lab scenario |
|---|---|---|---|---|---|
| Honours FOV and size (UEVR: size = recommended x its scale, FOV from `xrLocateViews`; most native OpenXR) | **65 %** | 100 % | 35 % (the rectangle, by choice) | 100 % | `e2e_fov_game_honours` |
| **Honours FOV, fixed size** (fixed-resolution mods) | **0 %** | 100 % | 35 % | **60 %** (its pixels packed into the rectangle, the stream still spread over the whole box) | `e2e_fov_game_fixed_size` |
| Ignores FOV, submits its own frustum honestly | 0 % | 100 % | 100 % | 100 % | `e2e_fov_game_own_fov` |
| Square 1:1 mod (2048 x 2048, tangent 1.3 on every edge), no restriction | -49 % (renders more) | 77 % (the rest is outside the headset's view) | 83 % (black beyond 1.3 on the outer edge) | 80 / 91 % | `e2e_fov_game_square_mod` |
| Ignores FOV but submits the told one (a lie) | 0 % | distorted | | | not testable from data; by eye only |

So for a fixed-size game the restriction is pure loss today: no GPU saved, two thirds of the view black, and 40 % of the detail the game drew
thrown away because the stream still covers the whole box. That last loss is the one the aperture recovers.

## 3. The model: one box from the headset to the game

Boxes, per eye, as tangents outer / inner / up / down:

- **P**: the panel box the client reports (the compositor's frustum; on a Quest the runtime's `xrLocateViews`, already the visible window).
- **C**: the wearer's calibration (PC profile, `config\user_fov_profiles.json`, copied into `airframexr.json`).
- **A, the aperture**: the box the client declares, the stream covers and the game is told. Set by one slider, 0 % = P, 100 % = C, interpolated
  per edge in angle (not tangent) so the steps look even. Never above P, never below C.
- **Gs**: what the game actually submitted (measured by the shim, section 5).

Rules:

1. **The game is told A, exactly.** No separate restriction inside the streamed box: telling the game less than what is streamed only paints black
   inside pixels we encode and send. The restriction tick and the aperture become one control (migration: restriction on = 100 %, off = 0 %).
2. **The stream covers A.** The client declares A, so the stream's pixels sit only where the wearer looks. Stream size is a choice, kept as today's
   setting: same pixels (sharper as A shrinks) or same density (fewer pixels, encode and decode save; the full-frame profile on the 5090 measured
   -30 % encoded pixels for the old box, the Vision Pro frustum to C is -65 % at equal density, computed).
3. **What the game rendered and the wearer can see is never cropped.** For a game that does not honour A (Gs differs, section 5) the aperture for
   that game is widened to min(P, Gs) at its next start (per-game, manual: section 6).
4. **The headset always ends at P.** Black appears only where the game drew nothing.

What this gives per game type: honours = the same GPU saving as today plus the sharpness of the smaller stream box; fixed size = no GPU change
either way, but A = C now gives its full detail (density 1.0 instead of 0.6), and "full view for this game" gives the whole panel at lower density;
own frustum = shown as rendered up to P.

## 4. The aperture channel (S6, needs the engine)

The v20 protocol has a free slot: `ServerControlPacket::Reserved(String)`. Stock `client_core` ignores it (`connection.rs`: `Ok(_) => ()`), so a stock
client keeps working; `ReservedBuffer` is taken by `RealTimeConfig` and stays untouched.

- **Streamer (engine, ask first):** at connection, before the client's first `ViewsConfig` matters, send `Reserved("{\"airframexr\":1,\"aperture\":[o,i,u,d]}")`
  (left-eye tangents; the client mirrors). Re-send when the slider moves; a game takes it at its next start (VDXR latches the FOV at system creation).
- **Client (ours, TestFlight only):** the vendored `client_core` maps `Reserved(String)` to a new event; the Swift side stores the box and
  re-declares `min(panel, aperture)` with `sendViewParams` (same path as an IPD change). Without a message (old streamer) it declares P, as 1.3.2.
- **Stream size** is fixed at connection (`StreamConfig`), before `ViewsConfig`: the PC computes it from A (it knows P from
  `config\airframexr_views.json` of the last session and C from the settings) with the existing pipeline model (`tools/gui/PipelineModel.cs`).
  A slider move that changes the stream size needs a reconnection (the streamer already closes on settings that need a restart).
- **Quest:** same message, same rule; P is already tight there, so the slider mostly trades a little area for sharpness.

## 5. Knowing what the game did (needs the engine)

The shim sees every projection layer: `Viewport.Size` and `Fov` per eye (`ComposeLayers`). Today the run record's `game_render` is only the size
we *recommended*. Add, once per game run (no per-frame cost: first layer after 2 s, then on change):

- `game_submitted`: size and four tangents per eye, from the shim through the IPC to the host's run record (`games.rs`, the `pipeline` block).
- A verdict: **honours** (size within 5 % of recommended and FOV within 0.01 of told), **fixed size** (FOV honoured, size not), **own frustum**
  (FOV differs), with the numbers of section 2 (saving, pixels used, view covered, detail carried).
- Game statistics shows the verdict per game; the general log writes one line.

## 6. Per-game choice (GUI, no new engine mode)

Manual, as the philosophy asks (a toggle, not an auto mode): when a game's verdict is fixed size or own frustum, Game statistics and the FOV card
offer **"Full view for this game"**, saved by executable (`airframexr.json`, `video.fov_full_view_games`). At that game's next start the streamer
sends A = min(P, Gs) instead of the slider's box. Open question: the stream size for that game (reconnect at game start, or keep the session's
stream size and accept the density change); measure the reconnection time first.

## 7. Lab

Built 2026-11-04 (not run):

- `xr_probe`: `XR_PROBE_RENDER_SIZE=WxH` (ignores the recommended size), `XR_PROBE_RENDER_FOV=outer,inner,up,down` (renders and submits its own
  frustum; the runtime's values are logged first). `tools/wsl/check_cpp.sh` OK.
- `harness/lib.py` `analyse_fov_restrict`: margins, content and checkerboard now follow the box and size the game rendered; new report block `game`
  (rendered, size_ignored, fov_ignored, render_saving, render_used, view_covered, stream_px_per_game_px) and checks `render_saving` [min, max] and
  `edges_reached` (where the game was told to stop short but rendered past, its picture reaches the stream's edge). Existing `e2e_fov_*` keep
  their meaning (a probe without the new options renders exactly as told).
- Scenarios `e2e_fov_game_honours`, `e2e_fov_game_fixed_size`, `e2e_fov_game_own_fov`, `e2e_fov_game_square_mod`: they pin today's behaviour.

Owed with the engine phases: the verdict checked against the probe's options (S5), the aperture message received by the mock client and the
declared box following it (`--views-tan` replaced by the message), the stream size computed from A, "full view for this game" on the second start.
First run on a builder: the four new scenarios plus every existing `e2e_fov_*` (regressions from the analysis change).

## 8. Order of work

| Step | What | Needs | Test |
|---|---|---|---|
| 0 | Client declares P (1.3.2, build 38); lab behaviours | done | headset: the tester's bars gone; `[ratemap]` lines read |
| 1 | Run the lab: four new scenarios + all `e2e_fov_*` | a builder | PASS, numbers of section 2 measured |
| 2 | Shim `game_submitted` + verdict in the run record and Game statistics | engine approval | lab: verdict per probe option; one real game per type |
| 3 | Aperture: slider in the FOV card, `Reserved` message, client declares it, game told A, stream sized from A | engine approval + client build | lab: mock client follows the message; headset: A = C sharper than 1.3.1 at equal stream size, no bars at 0 % |
| 4 | "Full view for this game" | step 2 + 3 | lab: second start widens A; headset with the Enshrouded mod |
| 5 | If the `[ratemap]` probe shows Apple's map falling to zero outside the lens window: offer it as the 0 % end instead of P | the probe's log | one wearer per light seal size |

## 9. Not decided

- Interpolation per edge in angle or in tangent (angle proposed).
- Whether the restriction fields stay as an "advanced" override once the aperture exists (proposal: no; one control).
- Stream-size policy per aperture (same pixels or same density) as a user choice or a fixed rule; measure encode and sharpness on the 5090 first.
- The Enshrouded mod's actual behaviour (fixed size? own frustum?): step 2 answers it from the run record.
