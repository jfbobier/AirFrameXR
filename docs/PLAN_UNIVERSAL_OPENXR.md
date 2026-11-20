# Plan: universal OpenXR (v3, 2026-11-20): game visibility masks, then a frame identity

Status: plan, nothing run. **Base: 1.4** (`main`, pre-release v1.4.0-pre.1). Branch `universal-1.4` (worktree `/home/jeff/openxr-universal14`), cut
from `1.4` at `6c73d0b` with `universal-openxr-2` merged in (`469ea14`: the VDXR fork pipeline and the shim's texture-array views from `vdxr-fork`, the
fork lab, the identity lab). The merge was clean (`build.ps1`, `harness/lib.py`, `harness/run.py` and two docs auto-merged; `tools/wsl/check_cpp.sh` OK);
nothing is built on a builder yet. `vdxr-fork` and `universal-openxr-2` stay as they were (from 1.3.11); new work goes on `universal-1.4`. Builder roots: the
fork lab's dedicated ones (`tools/builder*.local` copied from `universal-openxr-2`), so the regular roots keep 1.4's VDXR. Rules: engine edits on this
branch only and only after the owner confirms at the start of each engine step (M1, Phase B); no push, release or merge without asking; every engine arm
behind one lab switch, default = 1.4's behaviour; the product keeps its defaults.

v3 (2026-11-20) changes two things: the base moves to 1.4, and **the next engine round is the game visibility mask (Part M)**, ahead of the identity
work's Phase B. The identity sections (1-11) are v2's, with their base updated.

What changed from v1, and why:

| v1 | v2 | Why |
|---|---|---|
| identity = swapchain commit counts (the parked patch) | identity = VDXR's own "an image was released since the previous frame", sent to the shim before that frame's submit (fork trace channel) | a commit is not a new picture on VDXR's copy paths (array slice > 0, MSAA) and with upscale/sharpen on, where VDXR committed every frame; the fork's dirty gate fixes the copy paths, the trace channel counts before all of them (section 3) |
| H1 "accepted outright" by the lab | H1a (the signal is exact) tested first with no engine change; H1b (the decision) waits for the field check | nothing proved that the real mod's duplicate cycle releases nothing; the lab mimic was built on that assumption |
| U2: no-commit = repeat, free wait, no pattern state | cut | removes the safeguards the field-tested fix relies on (budget refilled only by real frames, the 40-70 % pattern); a paused app would get every wait free; no false positive of the pattern was ever measured |
| U3: re-stamp every picture, this week | deferred (section 8) | statistics-only gain, the most expensive arm (old tree, IPC bump, headset night); a cheaper first step exists |
| arm by environment variable | arm by host stdin command over the IPC (as `dropDuplicateFrames`) | the headset A/B switches arms live; an env var reaches a Steam game only through a user-wide setting and a Steam restart |
| matrix of call shapes, no truth | the same shapes in `xr_modsim` with the app's truth per submit, checked automatically (`run.py identity`) | a row now says pass/fail per arm instead of a number to interpret |

## 0. Gates and order

1. **G0, the foundation:** the VDXR fork lab R1-R6 (`docs/PLAN_VDXR_FORK_LAB.md`) on this branch's build (1.4 + the fork). The fork lab was written on
   1.3.11; its rows apply unchanged, and R5 gains 1.4's FoV scenarios (`e2e_fov_client_box`, `e2e_fov_tool_mask`, `e2e_fov_tool_fit`).
2. **Phase A (identity signal) and M0 (which games ask for a mask):** no engine change, the same B1 build, one lab session.
3. **The mask round, M1-M4** (Part M; engine, after the owner confirms at M1).
4. **G1, the field check** (fork lab plan section 3, 5090, AC Black Flag with the mod, one session): does the mod's duplicate cycle release an image?
5. **Phase B (engine, after the owner confirms):** arm U1, the matrix again, regressions, timing A/B. Decision B.
6. **Headset A/B** (section 9) only if Phase B passes.

Base moved on 2026-11-20 (this branch). From 1.3.11 to 1.4 the engine changed only in `tools/host` (`vision.rs`, `main.rs`: the box message to the headset
app, `client_sharpen`) and the ALVR patches (`headset-view-message.patch` and the lab's `client-reserved-message*.patch`); the shim, the IPC (version 37 on
both) and VDXR did not, so nothing in Phase A's design changes. Run it on this branch.

## M. Game visibility masks (the next engine round)

**Goal.** 1.4's line test gives a profile a per-eye visible outline (`mask`, kind `corner_fraction_v1`: four corner fractions per eye; `FovMask.Polygon` in
`tools/gui/UserFovProfiles.cs` makes it a convex octagon in tangent units). 1.4 only stores it: the game renders the whole rectangle. Handing the outline to
the game as OpenXR's visibility mask (`XR_KHR_visibility_mask`) lets an engine that uses it skip the hidden pixels. The per-eye edges (`tan_left` /
`tan_right`, stored but never told, because a per-eye box broke the parallax in End of Abyss) go into the same mask: each eye also hides the strip it does
not see, while the box and projection the game is told stay the same for both eyes. Nothing in this round is Vision Pro only: the line test and the mask
work the same on every headset.

### M1. Facts (code read 2026-11-20; fork `airframexr` 46deebb, shim and IPC on this branch)

- VDXR advertises `XR_KHR_visibility_mask` (`instance.cpp:577`) and answers `xrGetVisibilityMaskKHR` (`visibility_mask.cpp:39`) for both stereo views with
  `ovr_GetFovStencil` (`:108`, `:130`) on the eye's FovPort, the box the game is told (`:105`): hidden triangles, visible triangles, or the line loop (it
  keeps every second index, `:99`). It reads the vertices as SteamVR hidden-area-mesh coordinates (0..1 across the FovPort, y down) and converts them to
  OpenXR's view plane (`convertSteamVRToOpenXRHiddenMesh`, `:169`); indices are 16-bit. It logs `Application uses visibility mask` once per process
  (`:78`) in its own `OpenXR.log` (`framework/entry.cpp:98`).
- Our shim answers that call with OVRNull's stub (`OVRNull/api.cpp:142` in the fork): 0 vertices, 0 indices. Every game that asks gets an empty mask
  today, as in every release so far.
- OVRNull's API reaches the driver through `IDriver` (`api.cpp:78`, as `GetEyeFov`); `ovrshim/driver.cpp` is our `IDriver`. The change is one `IDriver`
  method in the fork (`GetFovStencil`; OVRNull's own driver keeps returning the empty mesh) and its implementation in the shim.
- VDXR already raises `XrEventDataVisibilityMaskChangedKHR` per eye when its `visibility_mask_scale` setting changes (`session.cpp:545-548`,
  `instance.cpp:456-472`): a ready path for a live change later. Round 1 latches the mask at game start, like the box.
- The IPC carries the box the game is told as per-edge fractions (`ovrshim/ipc.h:114-116`, version 37); the mask needs its own field and version 38.
- **UEVR never applies a mask** (its source read 2026-11-20, `praydog/UEVR` `4ee5c6b` of 2026-08-29; about 60 % of the owner's games, SWGR and Mortal
  Shell 2 among them). It cannot ask: it enables only the D3D11 / D3D12, depth-layer and cylinder-layer extensions (`src/mods/VR.cpp:279-302`), not
  `XR_KHR_visibility_mask`, and VDXR refuses the call without it (`visibility_mask.cpp:57`). And Unreal never draws one: the HMD UEVR fakes for the engine
  starts with every function a stub returning 0 (`src/mods/vr/IXRTrackingSystemHook.cpp:383-386`) and replaces about ten (`:486-626`: connection, resets,
  tracking, the view extension); `HasHiddenAreaMesh` and `HasVisibleAreaMesh` stay at 0. UEVR games gain nothing from round 1. Reaching them means
  changing UEVR (its fake HMD answering the hidden-area functions from the runtime's mask, for each Unreal version it supports), not our engine.
- The other games are open: native OpenXR titles (Unreal's own OpenXR plugin, Unity's occlusion mesh) and SteamVR titles through OpenComposite (its
  `GetHiddenAreaMesh`; source not read yet) may ask. M0 reads which of the owner's do.

### M2. What it can buy (a bound, not a measurement)

- The author's fit (shipped profile *AVP optimized trade-off*, told box 0.9215 / 0.5775 / 0.90 / 0.77): the visible octagon is 61.4 % (left eye) and
  57.8 % (right eye) of the told rectangle, so the mask hides **38.6 % and 42.2 %**: 35.2 % and 39.5 % inside each eye's own rectangle, plus its 5.2 % and
  4.5 % edge strip.
- A game skips only the pixel work inside the hidden area; geometry, shadow maps, compute and full-screen passes that ignore the mask still run. As a
  scale only: on SWGR's line (2026-11-19, `docs/FOV_CONTRACT_PLAN.md` section 9: 7.7 ms + 0.45 ms per game Mpx, both eyes) the fit's 9.1 Mpx is 11.8 ms
  (85 fps), and 10.2 ms (98 fps) if the whole per-Mpx part followed the mask. SWGR itself is a UEVR game and gains nothing (M1); the ceiling for a game
  that asks is measured in M3.
- A game at the cap shows nothing: Dead Space ran at 96.8 and 97.9 fps at 100 Hz with the quality rectangle and with the fit (2026-11-20,
  `docs/progress.md`). M3 uses a title below the cap and the telemetry flavour's frame times.
- The stream does not change in round 1: the encoder still gets the rectangle, with whatever the game leaves in the hidden pixels. Painting them black
  before NVENC is a frame-path change; measure the bits it would save before proposing it.

### M3. Design (round 1)

- **GUI (session compile):** with the switch on and a profile that has a mask, write `video.game_mask` = `{kind: "polygon_tan_v1", left: [[x, y], ...],
  right: [...]}`: each eye's `FovMask.Polygon` on its own rectangle (so the per-eye edges are in it), in view-space tangents (x right, y up, OpenXR's
  convention; the GUI turns outer / inner into left / right per eye, so the shim has no eye logic). No key otherwise.
- **Host:** read `video.game_mask` at start and put it in the IPC (up to 16 vertices per eye, a count, a serial); latched per game start.
- **Shim (`GetFovStencil`):** from the eye's polygon and the FovPort VDXR passes: visible area = the convex polygon as a triangle fan; hidden area = the
  told rectangle minus the polygon (each rectangle corner joined to the polygon vertices it faces); border line = the polygon's edges as index pairs;
  coordinates 0..1 across that FovPort, y down. No mask in the IPC: the empty mesh, exactly as 1.4.
- **Switch:** a lab switch first (host stdin `lab_game_mask 0|1` and the settings key, not in any window), default off. It becomes a toggle in the FOV card
  only after M3 shows a gain (project rule: no setting without a measurement behind it).
- **Margin:** none in round 1. The calibration rule puts each line where it is just visible; the headset check (M4) decides whether other wearings need
  one.

### M4. Steps

| Step | Engine | What | Pass |
|---|---|---|---|
| M0 | none | Which games ask. UEVR: answered from its source, no (M1). The rest: VDXR's `Application uses visibility mask` line per run for the owner's non-UEVR titles (native OpenXR, OpenComposite, the Dead Space and AC Black Flag mods), and OpenComposite's source for `GetHiddenAreaMesh`; `xr_probe` asks for the mask and logs the counts (lab tool) | a list of non-UEVR titles that ask; at least one below the cap, or the round stops here |
| M1 | fork (`IDriver` method), shim, IPC 38, host, GUI compile | the code above, behind the switch | `check_cpp`, the fork's compile, `check_gui` |
| M2 | lab | `e2e_game_mask`: `xr_probe` draws the hidden mesh in a key colour; the received stream has the key colour only outside each eye's octagon (both eyes, mirrored corners); the line loop matches the polygon; switch off = empty mesh (1.4). A Python model of the mesh (as `harness/fov_contract_model.py`) checked against the shim's output. Regressions: the fork lab's R5 plus `e2e_fov_client_box`, `e2e_fov_tool_mask`, `e2e_fov_override` | all pass |
| M3 | timing | a non-UEVR title from M0, telemetry flavour, 3 interleaved pairs (switch off / on) at the same spot: game fps, GPU frame time, repeated ticks | Phase B's rule: a change exceeds 3 x the larger spread and is >= 2 fps in 3 of 3 pairs |
| M4 | headset | the owner, the trade-off profile, the M3 title: anything visible at the corners during head motion (black, smearing, a temporal upscaler's edge) | nothing visible. **Decision M:** a toggle in the FOV card (every headset), default off until a second wearer has tried it |

### M5. Risks

- What a game writes in hidden pixels is undefined (black, stale, the near plane in depth). Screen-space effects and temporal upscalers (TAA, DLSS) can
  smear at the boundary; M4 looks for it.
- A game that asks before the mask is set gets the empty mesh. The host puts the mask in the IPC at its start, before any game.
- UEVR and OpenComposite may read the mesh with other conventions (winding, y direction): M2 gets an OpenComposite row if M0 finds a title.
- The fan needs a convex polygon. The corner-fraction octagon is convex; a free-form mask later needs a real triangulation.
- A wrong mask hides what the user sees: the switch is off by default, and nothing changes for a profile without a mask.

## 1. The question and the hypotheses

The engine and ALVR identify a frame by its tracking timestamp. OpenXR gives no frame ID. The damage measured on 2026-11-13: the compatibility mode
(`drop_duplicate_frames`) drops OFXR's generated frames because they carry the previous frame's poses (88 -> 72 new frames/s, n=3, spread <= 0.5).

- **H1a, the signal.** VDXR's per-frame count of swapchain slices with an image released since the previous frame (fork trace `FrameContent`) is zero
  exactly when the app released nothing, on every shape of the matrix, and reaches the shim before that frame's submit.
- **H1b, the decision.** A submit is a duplicate when it has the previous submit's exact render poses AND VDXR reports no image released on any layer.
  With everything else unchanged (the compatibility switch, the 40-70 % pattern, the free-wait budget), this keeps OFXR's generated frames and frames
  where only a quad changed, still drops the mod's duplicates, and costs no fps, encode time or latency.
- **H3, no regression on these shapes:** no matrix row crashes, deadlocks or gets worse in U1 than in U0. A finite matrix can only reject H3.
- **H4, the headset agrees** (section 9). The lab cannot prove it.
- H2 (every picture its own timestamp): deferred, section 8.

## 2. What the lab can and cannot say

| Question | Lab | How |
|---|---|---|
| Is VDXR's "nothing released" exact? | yes | `xr_modsim` writes its truth per submit (`modsim.csv`: `rel`, `new_pic`, `same_pose`); `identity_signal_matches_app` compares it with VDXR's count over the same frames |
| Does the shim keep new pictures and drop true duplicates? | yes | `identity_no_new_picture_dropped`, `identity_true_dups_dropped` against the truth classes |
| Does an app stay paced (no spin)? | yes | `identity_waits_paced`: the app's xrWaitFrame returns per second (inside the pause row's pause) |
| New frames shown, repeated ticks, encode, latency | yes | mock client `display_sim`, host logs, `harness/ofxr_report.py`; n=3 interleaved pairs |
| What the real mod does on its duplicate cycle | **no** | the field check (G1) |
| Whether the Vision Pro shows it right | **no** | headset A/B |

## 3. Facts the plan rests on (code read 2026-11-18/19; fork `airframexr` 46deebb, shim on `vdxr-fork`)

- VDXR never commits at `xrReleaseSwapchainImage`: it sets `dirty` (`swapchain.cpp:633`, the same code for every graphics API) and commits in `xrEndFrame`
  through one function for all APIs, `resolveSwapchainImage` (`d3d11_native.cpp:517`; D3D12, Vulkan and OpenGL apps share D3D11 textures through interop).
- Old VDXR committed every frame on its copy paths (`needCopy`: array slice > 0, or no direct OVR swapchain, e.g. MSAA). The fork's dirty gate (06a8a9e)
  skips copy and commit when nothing was released. The precompositor (registry `upscaling` / `sharpen`) still commits its own swapchain every frame.
- The fork counts `released` in `resolveSwapchainImage` before any copy, commit or precompositor step (`d3d11_native.cpp:532-533`), for every resolved
  slice of every layer: projection colour, projection depth (`frame.cpp:769`), quad and cylinder (`:895`), cube (`:986`). With upscaling on it is still
  called (with `skipCommit`), so the count holds there too.
- Order: `FrameContent` is sent at `frame.cpp:504`, after all layers are resolved and before the submit: before `ovr_EndFrame` on the app thread
  (`:563`), or before the layers go to the async submission thread (`:592`), VDXR's default (`session.cpp:348`).
- Frame ids: the trace and the synchronous submit use `m_frameBegun - 1`. The async thread submits with its own `ovrFrameId = m_frameCompleted`, read
  at the top of its loop (`frame.cpp:1129`); the app thread sets `m_frameCompleted` at `:600`, after handing over the layers at `:592`. If the async
  thread loops before the app thread reaches `:600`, it submits the next frame under the previous id: an upstream race, rare (a few instructions), never
  measured. So U1 pairs "the facts received since the previous submit", checks the id, and counts mismatches.
- The shim today (`ovrshim/driver.cpp:1570-1610`): duplicate = the projection layer's `RenderPose` equals the previous submit's (`memcmp`). Windows of 40
  submits; a pattern = 1-2 duplicates per real frame and a 40-70 % share, confirmed by 2 windows; then, with the compatibility mode on, a duplicate is
  dropped and a real frame refills the free-wait budget (`:945-970`). Quad layers are not looked at.
- Unknown: whether the AC Black Flag mod releases an image on its duplicate cycle (the 2026-10-28 probe hooked wait/begin/end only, 0 acquire/release
  events). G1 answers it.

## 4. Phase A: is the signal exact? (no engine change)

Build: the fork lab's B1 on this branch (VDXR at the pin, the shim of 1.4 with `vdxr-fork`'s array views), then `python3 harness/run.py build probe` from this worktree (adds the `xr_modsim`
shapes; `xr_probe` is unchanged, the CSV only gains columns, so the fork lab's runs are not affected). Builder root: the fork lab's
(`VisionALVR_vdxrfork`, on the 3080 Ti's D: behind the junction), one lab after the other.

Run `python3 harness/run.py identity` (all rows of `harness/scenarios/identity/`, a table at the end). Counts are deterministic: n=1 per row; repeat a row
only if it fails. Expected on this build, which is arm U0 (today's pose-only decision):

| Row | Shape (`xr_modsim`) | Truth | Signal | U0 decision (expected) |
|---|---|---|---|---|
| `id_normal_game` | one submit per wait | all new | 0 resubmits | PASS (nothing seen) |
| `id_mod_dup` | the mod as measured (`MODSIM_DUP=1`) | 50 % true duplicates | 50 % | PASS (dropped) |
| `id_mod_dup_array` | + `MODSIM_ARRAY=1` (copy path for slice 1) | 50 % | 50 %; slice 1 copied 0.5 / skipped 0.5 per frame | PASS |
| `id_mod_dup_msaa` | + `MODSIM_MSAA=4` (resolve copy, both eyes) | 50 % | 50 %; copied 1 / skipped 1 per frame | PASS |
| `id_mod_dup_rerelease` | duplicate cycle copies the last picture into a new image and releases it | 50 % true duplicates, all released | 0 % | PASS (poses alone) |
| `id_mod_dup_quad` | + a quad redrawn at every submit | 50 % same poses, new picture | 0 % | **FAIL**: drops them |
| `id_ofxr_mimic` | OFXR mimic (`MODSIM_OFXR=1`) | 50 % same poses, new picture | 0 % | **FAIL**: drops them |
| `id_pause` | normal game paused 10-30 s (last picture, its poses, no release) | about 50 % of the first 1800 submits true duplicates | same | PASS: waits paced (<= 100/s), no pattern (100 % share) |
| `e2e_modsim_ofxr_fix_on/off` | the 2026-11-13 OFXR mimic scenarios | | | as on 2026-11-13 |
| `e2e_ofxr_tokyo_off/on/on_compat` | the real OFXR layer (V551) on `xr_probe` | no app truth | read: OFXR's frames released? | `harness/ofxr_report.py` |

The real OFXR rows need the layer rebuilt into `D:\_ofxr\lab` (the 3080 Ti's D: is the new USB drive, so the scenarios' path works again):
clone `djules75/OFXR-Bridge` at `382065f`, FidelityFX v1.1.4 optical flow, DLSS headers, CMake (recipe in the 2026-11-13 notes).

One manual row, admin needed (VDXR reads `HKLM\SOFTWARE\Virtual Desktop, Inc.\OpenXR`): sharpen on, then `id_mod_dup`, then off.
`reg add "HKLM\SOFTWARE\Virtual Desktop, Inc.\OpenXR" /v sharpen /t REG_DWORD /d 50 /f`, run, `... /d 0 /f`. Expected: the signal unchanged.

**Decision A.** `identity_signal_matches_app` passes on every modsim row (and the manual one): H1a holds, go on. A row where it fails: stop. The fork is
ours: fix the count there (one more fork commit, the fork lab's R2-R6 again) before any engine arm. The U0 table is the baseline Phase B is judged against.

## 5. G1: the field check decides U1's reach

AC Black Flag with the mod on the 5090 with the fork build, debug logging on, one session; read the shim's `app frames (VDXR)` line:

- "resubmitted with no new picture" near half the frames: the duplicate cycle releases nothing. U1 handles the real mod (row `id_mod_dup`). Go on.
- near zero: it releases an image (row `id_mod_dup_rerelease`). U1 would stop dropping its duplicates and undo the Black Flag fix. Stop Phase B and
  rethink: telling a re-released copy from a new picture needs the picture itself (a GPU checksum per submit), a different cost class; ask before.
- in between: read it per window (the cutscene lock changes the cadence) before deciding.

## 6. Phase B: arm U1 (engine, this branch only, after the owner confirms)

Change (shim only, VDXR unchanged; about 30 lines next to `driver.cpp:1570-1610`):
- At submit, the facts are those received since the previous submit: `released` summed, the last id. Counters: submits without facts, facts whose id is
  not this submit's `frameIndex`. Both should stay 0; the pairing uses arrival order, the id is only checked.
- `same` becomes: same render poses AND the facts say nothing was released. Everything after it is unchanged: the 40-window pattern, the 0.25 s guard,
  the budget refilled only by real frames, the compatibility switch.
- No facts at all (stock VDXR, a libOVR game, the first frames): today's pose-only test, exactly.
- Stats line: `identity: arm N, same poses P (with an image released R, kept), submits without facts F, id mismatches M`.
- Lab switch: an IPC field next to `dropDuplicateFrames`, set by a host stdin command (`lab_identity 0|1`), default 0, not in any GUI or config file.
  Arm 0 must behave exactly like 1.4: run the matrix on 1.4's shim and on arm 0, same decisions on every row.

Runs: `run.py identity` on arm 1. Expected: every row PASS except `id_mod_dup_rerelease` (the known blind spot; it matters only if G1 found
re-releases). Then the fork lab's R5 regression set and the nine `e2e_modsim_*`. Then timing: 3 interleaved pairs (arm 0 / arm 1) on `id_mod_dup` and
`id_ofxr_mimic`, and on `e2e_ofxr_tokyo_on_compat`: app fps, new frames/s, unique timestamps/s, repeated ticks %, encode ms p50/p95, display latency.
Window 10-40 s, drop windows under 85 % fresh frames. An arm "changes" a metric only if the difference exceeds 3 x the larger spread and is >= 2 fps
(or 1 point for a percentage) in 3 of 3 pairs.

**Decision B.** H1b passes when: every matrix row passes except the re-release row; no regression in R5 and `e2e_modsim_*`; OFXR with the
compatibility mode on within 2 fps of OFXR with it off; pairing counters 0; the mod's numbers (`e2e_modsim_fix_on`: >= 95 % of duplicates dropped,
second wait free, >= 40 real fps) unchanged. It ships as a small change inside the compatibility tick (no effect when the mode is off) in a point release;
the owner decides.

## 7. U2: cut

Re-open only with a measured false positive of the pattern windows. If it comes back: identity over all layers (as U1), keep the budget, and rows
`id_pause` and `id_mod_dup_quad` must pass.

## 8. U3 (a timestamp per picture): deferred

It changes statistics, not the picture: the headset already shows same-timestamp frames (1.3.10 strobe test) and 1.3.10 counts display ticks.
The cheaper first step (`docs/PERFORMANCE_FINDINGS.md`, "Future work on the ALVR visionOS client"): read our visionOS client for where the device
anchor and the prediction horizon come from. If U3 comes back: one mode only (the shim-side stamp, the only one clean on both headsets in October), its
own switch on top of U0 (Decision A must not stop it), arrival jitter added to the metrics, the October calibration of the staleness proxy built from
`caaff65` on the D: drive, and the rig files left in `/home/jeff/openxr-universal` brought over then.

## 9. Headset A/B (only if Decision B passes)

Client 1.4.0 on the Vision Pro, the user on the 5090: OFXR on a real title, then AC Black Flag with the mod. Arm switched live with the stdin command
every 3 windows of 10 s, balanced order, start windows dropped. Read: ticks fresh %, `same_ts`, latency, the `identity:` line, and the user's blind call
on two or three head motions. One night.

## 10. Risks and open points

- The async id race (section 3): measured by the U1 pairing counters; if it is frequent, pair by order only.
- A layer VDXR rejects before resolving (validation error) sends no facts for that frame: counted as "submits without facts", the pose-only test applies.
- Depth swapchains count as released slices: an app that re-renders only depth would count as new. Harmless (kept, as today).
- The rows run at 90 Hz only; add a 100 Hz row if Phase B passes (the Gears case lives there).
- `XR_FRAME_DISCARDED` and two `xrLocateViews` at one time are not in the matrix yet (the second matters to U3, not to identity).
- One mutex lock in the shim per trace call (about 5 per frame, microseconds), on the app's thread.
- The lab mimic is ours: the matrix proves the shim against these shapes, not against every game. G1 and the headset are the reality checks.

## 11. Assets

- `xr_modsim` shapes: `MODSIM_ARRAY`, `MODSIM_MSAA`, `MODSIM_DUP_RELEASE`, `MODSIM_QUAD`, `MODSIM_PAUSE_S`, `MODSIM_OFXR`, `MODSIM_STROBE`; truth columns in `modsim.csv`.
- `harness/lib.py` `analyse_identity` and the `identity` checks; `run.py identity` (table: truth classes, VDXR vs app, shim seen/dropped, free waits, wait rate).
- `docs/OPENXR_APP_BEHAVIORS.md` (catalogue), `harness/ofxr_report.py`, the OFXR scenarios (2026-11-13, now in `scenarios/identity/`).
- Parked: `private/parked/dup_image_identity_2026-11-13.patch` (commit counting; superseded by the trace channel, kept for reference).
- Tested so far: `xr_modsim` clang-cl syntax check (and a planted error caught); the analysis and checks on synthetic runs (OFXR-like, mod duplicates,
  signal mismatch, no VDXR line, re-released duplicates, paced and spinning pause, free waits, old CSV). Nothing built or run on a builder.
