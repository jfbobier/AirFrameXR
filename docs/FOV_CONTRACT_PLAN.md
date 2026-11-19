# FoV contract: the streamer owns the field of view (plan)

Status 2026-11-19: **phase 0 built** on branch `fov-contract` (GUI only: `video.stream_density` bitrate / faster / sharper, the foveation following Apple's map inside the window, the FOV card's density readout and slider advice, the streamer's warning when the headset's box differs from the compiled one); lab-checked, not yet on a headset (section 6). Phases 1-3: not built; the engine items need the owner's go-ahead, on the same branch.
This replaces the design parts of `docs/FOV_PIPELINE_OPTIM.md` (S3 fit tick, S4 client ceiling, S6 declare channel, S9 render ceiling); its
measurements still hold. Numbers marked *model* come from `harness/fov_contract_model.py` (a Python reference of the launcher's sizing; `--selftest`
reproduces the 2026-11-14 SWGR compile line exactly), not from a headset. Primary target: Apple Vision Pro; the Quest uses the same design.

Owner decisions of 2026-11-19: the visionOS client's H / V FoV sliders go, the streamer manages the field of view; the client is rewritten for it; a new
FOV profile system is in scope; ALVR v21 is on hold (no value to migrate), and since we own both ends of the chain we deviate from stock ALVR v20
where that is cleaner (stock clients stay best-effort, `docs/progress.md` end-to-end ownership).

## 0. Summary

1. **The softness testers report is density.** In the SWGR session of 2026-11-14 (5090, Vision Pro, 275 Mbps, 100 Hz, quality FoV profile, headset sliders
   0.8 / 0.9) the picture reached the eye at **16.9 x 15.2 px/deg** in the centre, where Apple's compositor samples at **40**, and below the 22.6 x 24.9 the
   default resolution (3648 x 3200 over the whole frustum) would give. Cause: the stream is sized by a fixed 0.474 bits per encoded pixel, not by a density.
2. **One owner for the box.** Today the headset (sliders), the PC restriction and the PC's stream compile each hold a field of view, and the compile runs
   before the headset says which one it uses. In the new design the streamer compiles one **FOV contract** per start, sends it to the client, the client
   declares exactly that box and draws the stream over it, and the game renders exactly that box.
3. **Two modes instead of five switches** (the anchors the StrandXR authors use): **Faster** keeps the headset's default density over the trimmed window
   (fewer pixels), **Sharper** keeps the default pixel count over it (more density), capped at the headset's own centre density. The bitrate becomes a
   displayed result (bits per pixel, predicted encode and decode ms) with warnings, not the thing that sizes the stream.
4. Expected on the Vision Pro (*model*): Sharper over the quality window 31.4 x 33.4 px/deg at 12.6 encoded Mpx; over the performance window 37.9 x 39.8
   (Apple's cap) at 15.0 Mpx; Faster 22.6 x 24.7 at 6.8 Mpx. The 5090 and the Vision Pro have already carried 22.7 Mpx (2026-10-29: encode 7.55 ms p50,
   decode 6.9 ms p50).
5. Transport on v20: ALVR's existing `ServerControlPacket::Reserved(String)`, which stock clients ignore. Small patches on both ends; the shim and the IPC
   are untouched in phase 1.

## 1. What the chain does today (read in the code and the 2026-11-14 logs)

Unit used everywhere below: **centre px/deg = px / (tan left + tan right) x pi / 180, per axis.** Not px / degrees: over the Vision Pro's frustum
(about 60 + 45 degrees horizontally) that formula reads about 1.5 times the centre value, and it changes when the field of view is trimmed even when
the centre density does not.

| Stage | SWGR 2026-11-14 | Centre px/deg | Finding |
|---|---|---|---|
| Headset sliders (client 1.3.11) | H 0.8 / V 0.9: declared box 1.43 / 0.82 / 1.10 / 0.92 | | The PC learns it only at connection. V could be 0.76 without cutting the window (lower edge 0.77 of 1.02). |
| Launcher compile (`PipelineModel.cs` `ChooseEye`) | 275 / (100 x 0.474) = 5.8 Mpx encoded, stream **2176 x 1760** per eye, computed over Apple's frustum 1.73 / 1.00 / 1.19 / 1.00 (the fallback of `PictureEdgesFoveation.View` when no saved box is read) | 13.9 x 14.0 as computed, **16.9 x 15.2** over the box the headset used | The reference bits per pixel (`profiles/apple-vision-pro.profile.json` `stream_budget`, 0.474) is the 2026-10 foveated session's, never measured as a floor. Sized for the wrong box: 10 % less density vertically than horizontally. |
| Foveation (`RateMapFoveation.Milder` with a window) | centre 0.798 x 0.818 (the window at full density), outside / 3.2 and / 4 | | Full density out to 50 degrees, where Apple's own map keeps 30 % or less beyond about 25 degrees. Computed for the wrong box: the window's outer ~3 and inner ~4 degrees fell in the compressed band. |
| Game render (`ovrshim/render_size.h`) | told 1966 x 1452 over 1.215 / 0.808 / 0.897 / 0.768, render quality 0.00 | 17.0 x 15.2 | "Spend the savings" can only reach the stream's own eye size (about 1.1 x here): the extra pixels are averaged into the stream's grid. It buys anti-aliasing, not detail. |
| Compose (`ovrshim/ReprojectPS.hlsl`) | game layer into the stream frame, nearly 1:1 at a sub-pixel offset | | One bilinear tap below a footprint of 1.05: may wash out 1-pixel detail (not tested; the lab checks the checkerboard unrestricted and at quality 1 only). |
| Visibility mask | VDXR maps `xrGetVisibilityMaskKHR` to `ovr_GetFovStencil` (`external/VDXR/virtualdesktop-openxr/visibility_mask.cpp`); our OVRNull stub returns an empty mesh (`external/VDXR/OVRNull/api.cpp:142`) | | Games render the rectangle's corners, about 21 % of the window (`docs/FOV_PIPELINE_OPTIM.md` section 2), at full cost. |
| NVENC | HEVC Main10 4:2:0, CBR, P3 low latency, spatial AQ, QP map, 3 forced strips on a 1536-row frame | | Strips against none never compared for quality; not a FoV item. |
| Client (`Shaders.metal`) | one pass: un-warp, reprojection, Catmull-Rom wherever it magnifies, bicubic chroma | | Already right. It magnified about 2.4 x in the centre. `maxRenderQuality` is set to 1, the dynamic `renderQuality` is not (`Renderer.swift:185`). |
| Compositor | Apple's rate map (profile `ratemap`): full rate = 2292 px per tangent unit | **40.0** | The ceiling worth targeting. The panel's pixel count (3660 x 3200) is not: a planar frame needs about 1.7 x that width to be 1:1 in the centre (Apple renders a 6261 x 5023 logical view into about 2496 x 2432). |

The shim already logs what each game really submits (sub-image size and FoV per layer, on change: `ovrshim/driver.cpp:1974`, e.g. AC Black Flag 2026-10-27:
2788 x 2976 over 0.94 / 0.81 / 0.90 / 0.76 = 27.8 x 31.3 px/deg).

Phase 0 rows (*model*, `--phase0`, 275 Mbps, 100 Hz, today's client at 0.8 / 0.9, the quality window as the PC restriction):

| Case | Eye | Encoded, Mpx | Bits per pixel | Centre px/deg |
|---|---|---|---|---|
| As run 2026-11-14 (compiled over Apple's frustum) | 2176 x 1760 | 3776 x 1536 = 5.8 | 0.47 | 16.9 x 15.2 |
| Compiled over the declared box, today's rule | 1888 x 1696 | 3648 x 1568 = 5.7 | 0.48 | 14.6 x 14.7 |
| Declared box, foveation follows the map inside the window | 2592 x 2336 | 3648 x 1568 = 5.7 | 0.48 | 20.1 x 20.2 |
| Sharper (3648 x 3200 kept), map inside the window | 3648 x 3200 | 5120 x 2144 = 11.0 | 0.25 | 28.3 x 27.7 |
| Sharper, today's rule (window at full density) | 3648 x 3200 | 7040 x 2944 = 20.7 | 0.13 | 28.3 x 27.7 |

Fixing the box alone makes it softer (14.6): today's rule keeps the whole window at full density, so the budget goes to the periphery. The foveation
fix and the density anchor go together.

## 2. The design

### 2.1 Three pieces, each in one place

| Piece | Owner, where it lives | Content |
|---|---|---|
| **Headset capabilities** | measured from the device, on the PC per headset model (`config\headsets\<model>.json`, replaces `config\airframexr_views.json` as the source of the box) | physical frustum per eye (what the compositor renders, tangents), centre density and its fall-off per axis (Vision Pro: Apple's rate map, today in the profile; Quest: flat, from OpenXR's recommended image size over its FoV), refresh rates, the decoder's measured limit |
| **FOV profile** (the new profile system) | per wearer x headset model, on the PC (`config\fov_profiles\*.json`, migrated from `config\user_fov_profiles.json` and the `video.fov_*` keys) | the visible window per eye (four tangents each), an optional mask outline per eye from the calibration's rays, provenance (sweep file and date, line test, typed), the mode (Sharper / Faster) and an advanced density override in px/deg. "Headset default" = the physical frustum, no mask |
| **FOV contract** | compiled once per streamer start (`SessionCompiler`, the only place the geometry is computed), `config\fov_contract.json` + `session.json` | the box per eye (window clamped to the physical frustum), centre px/deg, stream eye size, foveation geometry, game render size (= the stream eye size: game box = stream box), mask mesh (phase 2), an id. Host, shim (through the host), clients and the UI read it; nothing else computes geometry |

### 2.2 Density rule

- **Faster**: the headset's default density over the window. Vision Pro: 3648 x 3200 over its frustum, 22.6 x 24.9 px/deg. Quest: its recommended size over its FoV.
- **Sharper**: the default pixel count over the window (density x frustum span / window span), capped at the headset's centre density (Vision Pro 40 px/deg).
- **Density override** (advanced): a px/deg value, same caps.
- Stream eye size = density x box span per axis, 32-aligned, both eyes the same size (ALVR side by side). Game render = the stream eye size.
- Foveation over the box from the headset's fall-off (Vision Pro: Apple's map at the 0.6 margin of `RateMapFoveation.Milder`, never stronger than ALVR's
  defaults); with the box = the window there is no black margin left to compress.
- Results shown, not chosen: encoded Mpx, bits per pixel at the session's bitrate and refresh, predicted encode ms (engine count from
  `config\airframexr_gpu.json`: 1.30 ms per Mpx + 2.1 ms measured on one engine at P3) and decode ms. Warnings: below a bits-per-pixel floor (start at
  0.15, to be judged by eye), above the GPU's or the decoder's measured capacity (a one-engine GPU should stay on Faster).

### 2.3 Session flow on v20

1. The launcher compiles the contract before the streamer starts, from the profile and the saved capabilities. Nothing depends on what the headset will say.
2. At connection the client declares its full physical frustum (as today). The host saves it as capabilities and logs a mismatch if it moved (IPD).
3. The host sends the contract as `ServerControlPacket::Reserved("{\"airframexr\":{\"fov_contract\":{...}}}")`, repeated by `server_core`'s real-time
   update thread like the existing `RealTimeConfig`, so a reconnect always gets it; the client acts on a new `id` only.
4. The client declares the contract's box per eye (clamped to its frustum, logged if clamped), draws the stream over it, black or passthrough outside.
5. The host receives that declaration: the shim's composition target becomes the box (existing `eyeFovTan` path), the game is told the box with a
   restriction scale of 1, the compose pass is an identity copy (the checkerboard already passes at 1:1 unrestricted), the stream frame has no black margin.
6. The host compares the declared box with the contract (0.005 tolerance) and emits `fov_contract` {honoured | mismatch, declared, contract}; the UI shows it.

A contract changes only at a streamer restart (the stream size is negotiated at connection; VDXR latches the FoV and the recommended size when a game
starts), as today. Between steps 2 and 4 a few frames composed for the physical frustum may be drawn over the box (v20 has no FoV in the video header):
at connection only, checked in the lab (section 5).

Contract JSON (the client needs `version`, `id`, `eyes`; the rest is for the host, the UI and the logs):

```json
{ "version": 1, "id": "6f1c2a", "headset": "Apple Vision Pro", "profile": "JF calibrated 2026-10-29", "mode": "sharper",
  "eyes": [ { "fov_tan": [1.22, 0.81, 0.90, 0.77] }, { "fov_tan": [0.81, 1.22, 0.90, 0.77] } ],
  "stream_eye": [3648, 3200], "density_ppd": [31.4, 33.4],
  "foveation": { "center_size": [0.50, 0.52], "center_shift": [0.0, 0.0], "edge_ratio": [2.4, 2.0] },
  "mask": null }
```

`fov_tan` is ALVR's order per eye (left, right, up, down tangents, all positive): for the left eye left = outer. The foveation values in the example are
placeholders.

### 2.4 Stock clients and the Quest

- **Stock clients** (best-effort, no design effort): they ignore `Reserved` and keep declaring their own frustum. The host then tells the game the
  contract's box through the existing PC-side restriction (scale = box / declared, black margin in the stream): the game GPU saving stays, the density gain
  does not. No extra code: it is today's mechanism fed from the contract instead of the `video.fov_*` fields.
- **Quest** (our fork, `external/alvr-quest`): the same `client_core` patch; the client declares the contract's box clamped to `xrLocateViews`
  (`client_openxr/src/stream.rs:549`, `interaction::get_head_data`); the projection layer already uses the declared view parameters (`stream.rs:465`,
  `474`). Capabilities: the recommended image size over its FoV, a flat fall-off, ALVR's foveation defaults. The PC-side calibration (`calibrate.exe`,
  `fovgrid.exe`) already runs through the chain on any OpenXR headset.

## 3. Expected numbers (*model*, `--table`, Vision Pro, 275 Mbps, 100 Hz, the client declares the window)

| Case | Eye | Encoded, Mpx | Bits per pixel | Centre px/deg |
|---|---|---|---|---|
| Faster, quality window (1.22 / 0.81 / 0.90 / 0.77) | 2624 x 2368 | 3712 x 1824 = 6.8 | 0.41 | 22.6 x 24.7 |
| Sharper, quality window | 3648 x 3200 | 5184 x 2432 = 12.6 | 0.22 | 31.4 x 33.4 |
| Faster, performance window (0.97 / 0.71 / 0.69 / 0.63) | 2176 x 1856 | 3328 x 1632 = 5.4 | 0.51 | 22.6 x 24.5 |
| Sharper, performance window (capped at Apple's 40) | 3648 x 3008 | 5632 x 2656 = 15.0 | 0.18 | 37.9 x 39.8 |

Game GPU (the game renders the stream eye size): Sharper renders the same 11.7 Mpx per eye as an unrestricted game at 3648 x 3200 (the whole saving
goes into density), Faster 6.2 over the quality window (about 45 % saved) and 4.0 over the performance window (about 65 %). Not measured: whether 0.18-0.22 bits per pixel holds up by eye (the only related A/B, 2026-10-13, saw no gain from
0.39 over 0.12 at equal centre density, one session), and the encode and decode times at these sizes (proportional estimates from the 2026-10-29 run).

## 4. Work items

Rule: **engine** = needs the owner's go-ahead and lives on the `fov-contract` branch. Everything else follows the usual rules.

### Engine (`fov-contract` branch)

| # | Item | Where | Gate |
|---|---|---|---|
| E1 | The host reads `config\fov_contract.json`, logs it once (box per eye, px/deg, stream eye, encoded size, foveation, game size), takes the game's box from it instead of the `video.fov_*` fields (scale 1 with our clients, the PC restriction with stock ones) | `tools/host/src/vision.rs`, `main.rs` | `e2e_fov_contract` (section 5) |
| E2 | `server_core`: a `set_reserved_message(Option<String>)` on `ServerCoreContext`, sent by the real-time update thread (`server_core/src/connection.rs`, the `RealTimeConfig` loop) while streaming | new `tools/patches/server-reserved-message.patch` (today the only ALVR patch is `loopback-client.patch`) | same; a stock client still connects and streams |
| E3 | `fov_contract` event (honoured / mismatch) for the GUI; the run record's `pipeline` block gains the contract and the density per stage | host | `streamer_events.py`, `performance_check.py` |
| E4 | Cleanup after phase 1: the render quality ceiling (IPC v36 `renderCeiling`), the host's `fov_override` stdin command and `--fov-override` / `--fov-scale` lab flags once nothing uses them | host, shim, IPC | the FoV regressions rewritten on the contract |
| E5 | Phase 2: the visibility mask. The contract's outline per eye (tangent space, up to 48 vertices) over the IPC; the shim implements `ovr_GetFovStencil` (hidden area, visible area, border line), a stage patch removing OVRNull's stub like stage11 | shim, IPC, `stage11_ovrshim.ps1` | xr_probe queries `xrGetVisibilityMaskKHR` and checks the mesh; GPU time saved in one Unreal and one Unity game, else dropped |
| E6 | Phase 2: per-eye boxes. v20 has one eye size for both eyes and mirrors the foveation, so the two boxes share the span per axis and differ by offset | compiler, host | the calibration's 13 % saving against the mirrored box, recomputed with equal spans |

### UI (`tools/gui`, no engine)

| # | Item | Gate |
|---|---|---|
| U1 | **Contract compiler** (replaces `PictureEdgesFoveation.Apply` / `ChooseEye`, `RenderPlan`): capabilities + profile + mode + bitrate / refresh + GPU engines -> contract + `session.json`; the density rule of 2.2; foveation over the box from the fall-off | `contract_check.py`: the C# equals `harness/fov_contract_model.py` over a grid (modes, both windows, headset default, 90 / 100 Hz, 150-300 Mbps, 1 / 3 engines) |
| U1a | Phase 0, on today's client: `Milder` follows the map inside the restriction window (free outside), Sharper / Faster anchored on the golden resolution, the compile warns when the headset's declared box differs from the compiled one and offers the restart | the `--phase0` rows reproduced by the C#; `milder_check.py`, `session_compile_check.py` |
| U2 | **FOV profile store**: per headset model x wearer, per-eye windows, mask outline from the sweeps (`FovFit` keeps the rays), migration of `user_fov_profiles.json` and `video.fov_*` | `fov_store_check.py` extended: migration, per-eye, the "Headset default" entry |
| U3 | **One FOV card**: profile, Sharper / Faster, the density strip (game, stream, encoded centre and edge, headset), encoded Mpx, bits per pixel, predicted ms, warnings; after connection "contract honoured" from E3 | `ui_matrix.py` rows, `launcher_flow.py` |
| U4 | **Game statistics**: the density strip per run, the game's real submitted size from the shim's layer line | `performance_check.py` |
| U5 | **Resolution chart in fovgrid** (`tools/probe/xr_probe.cpp`): bar groups labelled in px/deg at 0, 15 and 30 degrees; the smallest readable one is what survives encoding and every resample. The in-headset check, both headsets | lab: the decoded stream's patch contrast per group (mock client); headset: read by eye |

Settings that go (the contract replaces them): `video.fov_override`, `fov_outer`, `fov_inner`, `fov_upper`, `fov_lower`, `fov_for`, `fov_for_headset`,
`fov_render_quality`, `render_spend_pct`, `render_allow_above`, `render_reference_eye`, `stream_fit_window`, `stream_fit_eye`, `stream_reference_eye`,
`stream_reference_fov`, `stream_keep_panel_size`, `stream_size_manual` (a typed size becomes the density override). `foveation_manual` stays as the
hand-edit escape. `docs/EXPERIMENTAL_SWITCHES.md` and `docs/PERFORMANCE_KNOBS.md` follow.

### visionOS client (rewrite of the FoV part, branch off 1.3.11)

| # | Item | Where |
|---|---|---|
| V1 | Remove the H / V sliders and their state | `FovScale.swift`, `GlobalSettings.swift` (`fovScaleH`, `fovScaleV`, `fovCustom`), `Entry/Entry.swift`, `Renderer.swift` 800-862 |
| V2 | `client_core`: surface `ServerControlPacket::Reserved(String)` as an event (today `Ok(_) => ()` in `client_core/src/connection.rs`) and through the C API | `patches/client_core-reserved-event.patch` (same mechanism as the two existing patches); the same change in `external/alvr-quest` |
| V3 | Contract handler: parse, keep the latest `id`, declare the box per eye after the first (physical) declaration, clamped to the compositor's frustum (the canted-to-orthogonal conversion stays), draw over it; read-only status "Field of view: managed by the streamer (profile, 31 px/deg)" | `EventHandler.swift`, `Renderer.swift` |
| V4 | `renderQuality` set to `maxRenderQuality` explicitly; one log line with the drawable's physical size | `Renderer.swift:185`, `ALVRClientApp.swift` |
| V5 | Later, measured with the chart: an edge-adaptive upscale (AMD FSR 1 EASU / RCAS, MIT) in the existing single pass, since the client magnifies the stream | `Shaders.metal` |

### Quest client (`external/alvr-quest`)

| # | Item |
|---|---|
| Q1 | The V2 change in `client_core` and the Rust event |
| Q2 | Declare the contract's box clamped to `xrLocateViews` (`interaction::get_head_data`, `stream.rs:549`) |
| Q3 | Capabilities: the recommended image size over its FoV; Quest 3 starting window 51 / 40 / 43 / 54 degrees as the first profile |

## 5. Lab tests to add

| Test | Checks | Needs |
|---|---|---|
| `contract_check.py` | U1 against `harness/fov_contract_model.py` over the grid: box, eye size, foveation, encoded size, density, warnings | `uitest.exe` |
| `e2e_fov_contract` | the mock headset (`tools/mock_client`, a new flag: re-declare what the contract says) gets the contract, re-declares it; the host logs `honoured`; the app's tangents and recommended size equal the contract; the decoded stream has content to its edges (no black margin); the probe's checkerboard passes at 1:1; counter, gray patch, pose round trip; the frames right after the re-declaration | builder |
| `e2e_fov_contract_stock` | the mock without the patch (ignores `Reserved`): it streams, the host falls back to the PC restriction, the margins are black | builder |
| `e2e_fov_restrict_checker_floor` | today's restriction at render quality 0: does the checkerboard survive the near-1:1 compose? (phase 0 evidence for the sub-pixel blur, settled by the contract's identity compose either way) | builder |
| `e2e_fov_mask` (phase 2) | `xrGetVisibilityMaskKHR` returns the outline's mesh for both eyes, three mask types | builder |
| chart readback (U5) | contrast per px/deg group in the decoded stream, Faster against Sharper | builder |

## 6. Phases and gates

| Phase | Content | Gate | Measured on |
|---|---|---|---|
| 0 (no engine) | U1a, U3 on today's client, `e2e_fov_restrict_checker_floor`, `harness/fov_contract_model.py`. **Built 2026-11-19** (not the checker scenario): `harness/density_check.py` 6 of 6 (the real SessionCompiler through `uitest.exe` equals the model: stream size, foveation, density, for sliders 0.80 / 0.90 and 0.80 / 0.76 in the three modes), UI renders 0 failures (`advanced-switches`, the seven `fov-panel-*` cases, the new `fov-panel-sharper`) | the `--phase0` rows in C#; UI matrix 0 FAIL | lab; then SWGR on the 5090 + Vision Pro: as run against Sharper, by eye, encode / decode ms from the existing logs |
| 1 (engine branch + clients) | E1-E3, U1-U3, V1-V4, Q1-Q2 | `e2e_fov_contract` and `_stock` PASS; the client's CI compile | the same SWGR A/B on the new client; Quest 3: one game, honoured line |
| 2 | E5 mask, E6 per-eye boxes, U4, U5, V5 | GPU saving measured in two games for the mask; the chart on both headsets | headset |
| 3 | E4 cleanup | the FoV regressions rewritten on the contract | lab |

## 7. Decisions

Taken (owner, 2026-11-19): the streamer owns the field of view; the client sliders go; the client is rewritten for it; a new profile system; ALVR v21 on hold,
v20 extended where we own both ends; the Quest follows the same design where it costs little.

Open, with a recommendation:

1. **Transport**: `Reserved(String)` JSON (recommended: stock clients skip it, one small patch per end) or a typed `ServerControlPacket` variant (cleaner to
   read, but a stock client fails to decode it).
2. **Per-eye boxes**: phase 2 (recommended), one mirrored box first.
3. **The PC-side restriction for stock clients**: keep (recommended, no extra code).
4. **The mask**: phase 2, kept only if two games show a measured GPU saving.
5. **The bits-per-pixel warning floor**: start at 0.15, set from the phase-0 A/B.

## 8. Risks and unknowns

- The bits-per-pixel floor is not measured; Sharper's value depends on it (phase-0 A/B).
- The re-declaration at connection (step 4) goes through the host's views-change path, which the FoV plan of 2026-10-29 listed as untested
  (`pending_views`); `e2e_fov_contract` checks the frames around it.
- A tight window crops HUDs placed at a game's frame edge (known from the restriction; untested beyond the author's games).
- `renderQuality`'s default on visionOS is unknown; the logged rate map integrates to about 2456 x 2352 physical, consistent with quality 1 (V4 removes the doubt).
- The foveation's vertical orientation (`RateMapFoveation.VerticalFlipped`, default false) was meant to be settled by a `[proj]` log line that only existed on
  the archived 1.4 line; it matters once the foveation follows the map inside the box.
- Eye sizes above 4096 per eye (`PipelineModel.MaxEyeWidth`, applied before foveation) are not reached with a window; a "Sharper over the headset default"
  at full density would be, and stays capped.

## 9. Phase 0 on the headset (2026-11-19, the owner, 5090 + Vision Pro, SWGR, 275 Mbps, 100 Hz, release flavour)

Logs: `private/logs/fov-contract-phase0-2026-11-19/`. Headset sliders about H 0.80 / V 0.75 (declared box 1.41 / 0.81 / 0.91 / 0.76), quality restriction
1.22 / 0.81 / 0.90 / 0.76. The owner's verdict: **less blurry in both new modes; the old sizing was faster.** Only two runs of the night are long enough; the
old sizing's runs tonight (20 s and 23 s, game trouble) are not, so its row is the 7-minute SWGR run of 2026-11-14 (sliders 0.8 / 0.9, same restriction).

| Sizing | Stream per eye | Centre px/deg | Encoded, Mpx (bits per pixel) | Game per eye (Mpx, both eyes) | Game fps, run | Headset repeated |
|---|---|---|---|---|---|---|
| Old (bitrate), 2026-11-14 | 2176 x 1760 | 16.9 x 15.2 | 5.8 (0.47) | 1966 x 1452 (5.7) | 89.1 over 7 min, slowest tenth 76.3 | 3.0 % |
| Old (bitrate), tonight | 1984 x 1504 | 15.5 x 15.7 | 5.8 (0.48) | 1808 x 1496 (5.4) | 56.7 / 65.4 over 20 s / 23 s: not usable | |
| Faster | 2880 x 2368 | 22.5 x 24.7 | 7.3 (0.37) | 2626 x 2356 (12.4) | 75.9 over 2 min, slowest tenth 74.5 | 21.4 % |
| Sharper | 3648 x 3200 | 28.5 x 33.4 | 12.6 (0.22) | 3324 x 3182 (21.2) | 52.1 over 2.4 min, slowest tenth 51.0 | 46.8 % |

What it says:
- The density reaches the eye: 1.5 x (Faster) and about 2 x (Sharper) the old centre px/deg were seen as less blurry. Sharper's 0.22 bits per pixel drew no complaint (one session).
- **The cost is the game, not the stream.** Every run streamed what the game rendered (rendered = streamed fps, no host drops). Phase 0 keeps the game at the
  stream's density over the window, so a denser stream is a bigger game render: 5.4-5.7 Mpx (old), 12.4 (Faster), 21.2 (Sharper, about 90 % of an unrestricted
  3648 x 3200 game). Sharper's 52 fps is the 100 Hz pacing's half-rate state (two thirds of the frames shown for two periods); its real capacity is somewhere
  between 50 and about 65 fps.
- Phase 1 (the client declaring the window) does **not** buy game fps: the game already renders only the window. It trims the stream (the outer edge 1.41 -> 1.22)
  and removes the slider setup.
- The levers for the game's cost: a density between Faster and Sharper (the Settings resolution scales both modes: Sharper at 3200 x 2800 is about
  25 x 29 px/deg and a 16 Mpx game, *model*), frame generation at half rate with Sharper (its slowest tenth, 51 fps, is at the edge of 50), and game-side
  foveated rendering (variable-rate shading following Apple's map: the game shades the window's periphery at full density where the compositor keeps 30 %
  or less; candidate to reuse: OpenXR Toolkit's NVIDIA VRS foveation, an API layer like OFXR), not measured.
- Bug found: every connection the headset's first declaration is 1.73 / 1.00 / 1.00 / 1.19 (vertical flipped, not circumscribed) for about 0.2 s before the
  real box (the host's first "70 / 81 / 90 / 65 %" line, also on 2026-11-14). The streamer's box warning fired on it (22:09:29); it must compare the settled
  declaration.

