# Plan: universal OpenXR (v3, 2026-11-20): game visibility masks, then a frame identity

**Status 2026-11-21:** G0 done and the encoder mask (M-E) shipped, both in 1.4.1 (`main` `8fb3381`: `docs/PLAN_1_4_FOUNDATION.md`, fork lab R1-R6
and F1-F3 there); **G1 answered in the field: the AC Black Flag mod's duplicate cycle releases nothing** (section 5, with the baseline Phase B is
judged against). **`main` (1.4.1, `d0c6435`) merged into this branch (`5da4312`)**: the merged shim, host, nvenc and GUI are identical to `main`, so
the lab runs the engine the field baseline ran; the branch adds only the identity lab, this plan and the catalogue. Next: step 2 (Phase A, G0 of
Part M). **2026-11-22: `main` = v1.4.2 (`e5ee359`, neural frame generation) merged as `d4b486e`**, engine, GUI and build scripts again identical
to `main`, IPC still 38. **2026-11-23: the owner's 1.5 branch is the universal engine's base: `1.5-simplify` (v1.5.0-pre.1, `7d0d55e`)
merged as `6f0461a`**: frame generation x2 / x3 with the neural or edge synthesis only; engine, GUI and build scripts identical to `1.5-simplify`,
IPC still 38, the identity lab unchanged (its rows use x2, "half"). Phase A runs on the 3080 Ti from there (run sheet in section 4). The
timing code 1.5 removed (x1.5 = `two_thirds` and `uncapped`: the content-clock planner with its lag controller and statistics, and the
two-thirds hold, about 250-300 lines of `tools/host/src/pipeline.rs`) stays at tag `v1.4.2` (`e5ee359`; `main` `cf8bc70` with its hotfix) if
this work needs it again.

Status: **Phase A done 2026-11-23 (Decision A = go, section 4); Phase B built (`685a07d`, arms 0-2 behind one lab switch) and its matrix run the same day (section 6): arm 0 = Phase A on every row, arm 1 fixes the new-picture rows, arm 2 also the shaking row; then the regressions and the timing A/B: no regression beyond two checks that assert 1.5's behaviour, the mod's numbers unchanged, OFXR's generated pictures kept. Decision B's table is in section 6: arm 2 meets every criterion except "no effect with the mode off". **Decision B (owner, 2026-11-23): arm 2, with the compatibility mode always on and its switch removed. With the spec as the arbiter, the
OpenXR letter holds for every app that follows it, and the mode does not engage for an app without runtime facts (section 6).** The follow-up batch the same evening: real OFXR with the mode on under arm 2 = the mode off (87.8 new frames/s, 0 dropped; arm 0: 71.4, 2,415 dropped); `e2e_baseline` arm 0 = arm 2; the regression set with the mode forced on: normal games unaffected, the mod unchanged. **Phase B: success in the lab; the field test is pending** (a personal bundle with arm 2 as the default, built 2026-11-23 for the owner's Black Flag session). Still owed: rewriting the two 1.5-only checks, the no-facts rule, removing the switch; then section 9's headset A/B. Noted for later: section 9b (the just-in-time release).** **Base: 1.5** (`1.5-simplify` `7d0d55e`, merged 2026-11-23; 1.4.2 on 2026-11-22, 1.4.1 on 2026-11-21, before that 1.4). Branch `universal-1.4` (worktree `/home/jeff/openxr-universal14`), cut
from `1.4` at `6c73d0b` with `universal-openxr-2` merged in (`469ea14`: the VDXR fork pipeline and the shim's texture-array views from `vdxr-fork`, the
fork lab, the identity lab). The merge was clean (`build.ps1`, `harness/lib.py`, `harness/run.py` and two docs auto-merged; `tools/wsl/check_cpp.sh` OK);
nothing is built on a builder yet. `vdxr-fork` and `universal-openxr-2` stay as they were (from 1.3.11); new work goes on `universal-1.4`. Builder roots: the
fork lab's dedicated ones (`tools/builder*.local` copied from `universal-openxr-2`), so the regular roots keep 1.4's VDXR. Rules: engine edits on this
branch only and only after the owner confirms at the start of each engine step (M1, Phase B); no push, release or merge without asking; every engine arm
behind one lab switch, default = 1.4's behaviour; the product keeps its defaults.

v3 (2026-11-20) changes two things: the base moves to 1.4, and **the next engine round is the calibrated mask (Part M)**, ahead of the identity work's
Phase B: first in the encoder (every game), in the game only if evidence shows titles the owner plays would use it. The identity sections (1-11) are v2's,
with their base updated.

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
   1.3.11; its rows apply unchanged, and R5 gains 1.4's FoV scenarios (`e2e_fov_client_box`, `e2e_fov_tool_mask`, `e2e_fov_tool_fit`). **Done on `1.4-foundation`, shipped as 1.4.1.**
2. **No engine change, one lab session:** the identity work's Phase A, the mask's E0 (offline encodes) and G0 (which of the owner's non-UEVR titles ask
   for a mask).
3. **The encoder mask, E1-E3** (Part M; engine, after the owner confirms at E1). The game mask only if G0 finds titles (M-G). **The encoder mask
   shipped in 1.4.1** (foundation F1-F3, then the mask band and rate fill; the headset on SWGR: 88.2 fps against 73.7 at Balanced); E0 is moot.
4. **G1, the field check** (fork lab plan section 3, 5090, AC Black Flag with the mod, one session): does the mod's duplicate cycle release an image?
   **Answered 2026-11-21: no** (section 5).
5. **Phase B (engine, after the owner confirms):** arm U1, the matrix again, regressions, timing A/B. Decision B.
6. **Headset A/B** (section 9) only if Phase B passes.

Base moved on 2026-11-20 (this branch). From 1.3.11 to 1.4 the engine changed only in `tools/host` (`vision.rs`, `main.rs`: the box message to the headset
app, `client_sharpen`) and the ALVR patches (`headset-view-message.patch` and the lab's `client-reserved-message*.patch`); the shim, the IPC (version 37 on
both) and VDXR did not, so nothing in Phase A's design changes. Run it on this branch.

Base moved again on 2026-11-21 (`main` = 1.4.1 merged, `5da4312`). What it changes here: the IPC is at version 38 (the foundation's E4, FoV
cleanup), so the next IPC field (Phase B's lab switch, or M-G's mask) takes 39, and the host and the shim are always built together (a host
without its shim leaves the app standalone; `shim_reached_host` fails such a run). VDXR stays at the pin `46deebb`, now the product's. The shim's
duplicate test did not change; its lines moved (section 3). The fork lab's builder roots (`tools/builder*.local`) are the ones 1.4.1 was built
and tested on. Phase A's design is unchanged.

## M. The calibrated mask (the next engine round): the encoder first, the game only with evidence

**Goal.** 1.4's line test gives a profile a per-eye visible outline (`mask`, kind `corner_fraction_v1`: four corner fractions per eye; `FovMask.Polygon` in
`tools/gui/UserFovProfiles.cs` makes it a convex octagon in tangent units; the per-eye edges `tan_left` / `tan_right` cut each eye's own strip). 1.4 only
stores it. Two places can use it:

- **The encoder (M-E, first).** The hidden corners are in every frame we encode, whatever the game. Spending almost no bits there works for UEVR games
  too and touches only our encoder: no game, VDXR, shim or IPC change.
- **The game (M-G, only with evidence).** Handed over as OpenXR's visibility mask it saves GPU only in games that draw it. The evidence of 2026-11-20
  (M-G) finds none among the owner's mods. The owner's rule: no engine change for the very few games that would use it.

Nothing in this part is Vision Pro only.

### M-E. The encoder mask (every game)

**Facts (code read 2026-11-20, this branch).**
- The encoder already builds a per-CTB (32 x 32) QP delta map, on by default (`docs/PERFORMANCE_KNOBS.md`: -3 in the foveal region, +6 at the edges):
  `nvh_build_qp_map` (`nvenc/hostlib/nvh.cpp:208-257`). For each CTB it inverts ALVR's foveation (`CalculateFoveationVars` copied verbatim, `foveF`)
  back to the eye's unfoveated position, measured from the eye's outer edge in both eyes (`:247`, the right eye mirrored in the frame): the mask's own
  outer / inner convention. The mask adds one point-in-polygon test per CTB, once per encoder creation; nothing per frame.
- Deltas are clamped to +-51 (`:254`): a hidden CTB at +51 costs close to nothing.
- The map is built from the session's foveation and the settings table (`nvh.cpp:139-162`); the mask can travel the same way (a settings key written by
  the session compile). The full-frame split profile turns the QP map off (`tools/host/src/main.rs:593-595`): no mask there unless black fill is built.
- Black fill (the hidden pixels made flat before NVENC) is the other half: flat blocks are nearly free and decode flat. It touches the compose pass (the
  frame path); build it only if E0 shows that +51 alone leaves bits worth taking.

**What it can buy (pixel shares, not bits).** The author's fit hides 38.6 % / 42.2 % of each eye's stream pixels and, after the foveation has squeezed
the edges, **25.9 % / 29.8 % of the encoded pixels** (the session of 2026-11-20 01:00: centre 0.603 x 0.548, shift 0.533 / -0.127, edge ratio 1.606 /
2.058; a linear edge model that reproduces the encoded size 1632 x 1824). At the owner's constant 275 Mbps the freed bits go to the visible area: about
1.35 x the bits per visible pixel if bits follow pixels, or the same picture at about 200 Mbps. The headset decodes the same frame size, so its decode
work barely changes: the gain is picture quality or Wi-Fi headroom. A slightly wrong fit shows blocky corners at the far edge, not black ones.

| Step | Engine | What | Pass |
|---|---|---|---|
| E0 | none | The bits the corners cost: the bench scene's encoded frames (`run.py bench-quality`, the owner's foveation) decoded, the hidden area made flat per eye (the octagons mapped into encoded coordinates by the model above), re-encoded with ffmpeg's `hevc_nvenc` at constant QP with and without; then at 275 Mbps, the visible area's PSNR / SSIM with and without | go if >= 15 % fewer bits at constant QP, or a visible-area gain above the run-to-run spread at 275 Mbps |
| E1 | nvenc, host, GUI compile | the mask in `nvh_build_qp_map` (+51 in fully hidden CTBs) from a settings key written by the session compile (the profile in use, if it has a mask); a lab switch first | `check_cpp`, `check_gui`; the QP map dump (`--dump-qpmap`) equals a Python model of the mask in encoded coordinates |
| E2 | lab | `bench-quality` mask off / on (bits, visible quality, encode time), the regression set (`e2e_baseline`, `e2e_fov_*`) | E0's numbers reproduced inside the engine; encode time unchanged |
| E3 | headset | the owner: the corners are not seen during head motion; mask off / on at 275 Mbps compared blind, or the same picture at a lower bitrate | **Decision E:** a switch in the FOV card (every headset), its default from E2 / E3 |

### M-G. The game mask (only with evidence)

**Evidence (2026-11-20).** A game can use the mask only if it enables `XR_KHR_visibility_mask` when it creates its instance: VDXR logs every requested
extension (`instance.cpp:233`) and refuses the call without it (`visibility_mask.cpp:57`).

| Path | Titles | Evidence | Uses a mask |
|---|---|---|---|
| UEVR | about 60 % of the owner's games (SWGR, Mortal Shell 2, ...) | source `praydog/UEVR` `4ee5c6b` (2026-08-29): never enables the extension (`src/mods/VR.cpp:279-302`); the Unreal HMD it fakes starts with every function a stub returning 0 and replaces about ten (`src/mods/vr/IXRTrackingSystemHook.cpp:383-386`, `:486-626`), so `HasHiddenAreaMesh` stays 0. VDXR log 2026-11-14 (SWGR): D3D12, depth, cylinder | **no** |
| R.E.A.L. VR (Luke Ross) | Dead Space | VDXR log 2026-11-14 (`private/logs/stgr-hdr-2026-11-14/OpenXR.log`): D3D11, win32 time conversion, depth | **no** |
| NoMoreFlat (`acbfvr` 1.0.4) | AC Black Flag | its `dxgi.dll` (`private/ghidra/mod`): no `XR_KHR_visibility_mask` string; `xrGetVisibilityMaskKHR` appears only in the statically linked loader's name table (next to Vulkan, MSFT and hand-tracking names); the extension names it holds: D3D11, D3D12, depth, cylinder, debug utils | **no** |
| OpenComposite (SteamVR games) | the owner's OpenComposite titles | the source we ship (OpenOVR `a27e7e6`): enables the extension whenever the runtime offers it (`DrvOpenXR/DrvOpenXR.cpp:220`) and answers SteamVR's `GetHiddenAreaMesh` from it (`DrvOpenXR/XrHMD.cpp:164-210`) | **yes, if the game draws SteamVR's hidden-area mesh**: per title |
| Native OpenXR games | per title | VDXR's requested-extension lines | per title |

Other R.E.A.L. VR and NoMoreFlat mods probably share the lists above (same frameworks); not checked.

**G0 (no engine change).** For each OpenComposite and native OpenXR title the owner plays: one start through AirFrameXR, then VDXR's log
(`%LOCALAPPDATA%\VirtualDesktop-OpenXR\OpenXR.log`; it keeps only the last app, so read it after each start): `Requested extension:
XR_KHR_visibility_mask`, and `Application uses visibility mask` (logged once when the game asks, `visibility_mask.cpp:78`). **If no title the owner
plays asks, M-G is closed**: no shim, IPC or fork change for it. If some do: the design below behind a lab switch, measured on such a title (game fps and
GPU frame time, Phase B's decision rule) before any toggle.

**Design, only if G0 opens it.** The session compile writes the per-eye polygon (`video.game_mask`, `polygon_tan_v1`, view-space tangents) -> host ->
IPC version 39 (38 is 1.4.1's E4) -> the shim's `GetFovStencil` (one `IDriver` method in the fork; OVRNull's stub returns an empty mesh today, `OVRNull/api.cpp:142`) ->
VDXR's `xrGetVisibilityMaskKHR` (`visibility_mask.cpp:39`: SteamVR coordinates 0..1 across the FovPort, y down; 16-bit indices; line loop with every
second index). Latched per game start, like the box. Lab: `e2e_game_mask` (`xr_probe` draws the hidden mesh in a key colour, the stream shows it only
outside each eye's octagon). Risks: what a game leaves in hidden pixels is undefined (temporal upscalers can smear at the edge); OpenComposite's
conversion conventions.

### M-Risks

- A mask that is too tight hides what the wearer sees: blocky corners on the encoder side, undefined pixels on the game side. The calibration rule (push
  a line out until it disappears, Shift back until it is just visible) puts each line at the edge; another wearer or fit needs its own profile.
- The mask follows the profile in use; a profile without a mask changes nothing.

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
- The shim today (`ovrshim/driver.cpp:1559-1612`): duplicate = the projection layer's `RenderPose` equals the previous submit's (`memcmp`). Windows of 40
  submits; a pattern = 1-2 duplicates per real frame and a 40-70 % share, confirmed by 2 windows; then, with the compatibility mode on, a duplicate is
  dropped and a real frame refills the free-wait budget (`:952-975`). Quad layers are not looked at.
- ~~Unknown~~ answered 2026-11-21 (section 5): the AC Black Flag mod releases no image on its duplicate cycle (the 2026-10-28 probe hooked
  wait/begin/end only, 0 acquire/release events; the fork's facts settled it in the field).

## 4. Phase A: is the signal exact? (no engine change)

**Run sheet (3080 Ti, this branch at 1.5; prepared 2026-11-22, run on the owner's "go" 2026-11-23):**
1. The 3080 Ti answers (`ssh`), no AirFrameXR streamer runs there (the lab blocks while one does), nothing else loads its GPU.
2. Root: **`VisionALVR_universal` on C:** (owner 2026-11-23: not the D: drive; C: had 200 GB free, D: unplugged, so the old
   `VisionALVR_vdxrfork` junction points nowhere). Set in this worktree's `tools/builder.local`; the folder was empty, so `python3 harness/run.py
   setup` first (pinned ALVR + VDXR clones and every build; its last step, `package`, needs the neural files and fails until step 3).
3. `python3 harness/run.py build vdxr shim nvenc neural host client probe` (host and shim together after IPC 38; `neural` needs the CUDA
   toolkit 13.5, which the 3080 Ti has: the first run of the DLL's sm_86 code on an RTX 30. The host falls back to the edge synthesis by
   itself and says why at debug level, so check the `frame generation neural` line in a frame generation row's run log).
4. `AIRFRAMEXR_LAB_IDLE_GPU=10 python3 harness/run.py identity <rows>`: the 13 rows without the three `e2e_ofxr_tokyo_*` (they load the real
   OFXR layer from `D:\_ofxr\lab`); compare the table with the U0 column below; Decision A.
5. Optional: the real OFXR rows only with the layer rebuilt on C: and the three scenarios' path changed (recipe below); the manual sharpen
   row (admin).
G0 of Part M is a 5090 field check (VDXR's log per title), not part of this run.

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
| `id_mod_late_stale` | the mod's late shape (5b), mode off, frame generation off: from submit 1000, 14 % of the duplicate cycles submit new poses with the old images | about 5 % of all submits an old picture under a new pose | = app | **FAIL** `identity_stamp_matches_picture` (U0 stamps by the pose: about 7 % of the frames after the change shown under another pose); the control before the change clean |
| `id_mod_late_samepose_new` | mode on: from submit 1000, 6 % of the duplicate cycles draw a new picture under the same poses | about 2 % same poses, new picture | = app | **FAIL** `identity_new_pictures_shown` (U0 drops them; the aggregate `identity_no_new_picture_dropped` cannot see 2 % and passes); stamp clean before and after (the check's false-alarm control) |
| `id_mod_late_oneeye` | mode on: from submit 1000, 10 % of the real frames release the right eye only | about 3 % one eye | = app | PASS (drops, control); the stamp error after the change is reported, not checked: no arm can stamp both eyes right on ALVR v20 |

The real OFXR rows need the layer rebuilt (their scenarios load it from `D:\_ofxr\lab`; the D: drive is not used since 2026-11-23, so build
it on C: and change the three scenarios' path):
clone `djules75/OFXR-Bridge` at `382065f`, FidelityFX v1.1.4 optical flow, DLSS headers, CMake (recipe in the 2026-11-13 notes).

One manual row, admin needed (VDXR reads `HKLM\SOFTWARE\Virtual Desktop, Inc.\OpenXR`): sharpen on, then `id_mod_dup`, then off.
`reg add "HKLM\SOFTWARE\Virtual Desktop, Inc.\OpenXR" /v sharpen /t REG_DWORD /d 50 /f`, run, `... /d 0 /f`. Expected: the signal unchanged.

**Decision A.** `identity_signal_matches_app` passes on every modsim row (and the manual one): H1a holds, go on. A row where it fails: stop. The fork is
ours: fix the count there (one more fork commit, the fork lab's R2-R6 again) before any engine arm. The U0 table is the baseline Phase B is judged against.

**Phase A result (2026-11-23, 3080 Ti, root `VisionALVR_universal` on C:, this branch at `c4e813f` = 1.5 + the identity lab, debug flavour,
`AIRFRAMEXR_LAB_IDLE_GPU=10`; 13 rows in 38 min, results `harness/results/20261123T0848*` to `T0924*`): Decision A = go.** VDXR's "nothing
released" equals the app's truth on every row, frame for frame in count:

| row | status | submits | truth | VDXR / app no-release | shim seen / dropped | stamp error before / after | as expected |
|---|---|---|---|---|---|---|---|
| `e2e_modsim_ofxr_fix_off` / `_on` | PASS / PASS | | | | 0/0, 860/821 | | yes (as 2026-11-13) |
| `id_normal_game` | PASS | 1800 | all new | 0/0 | 0/0 | | yes |
| `id_mod_dup`, `_array`, `_msaa` | PASS x3 | 1721 | 50 % true duplicates | 860/860 each | 860/821 | | yes |
| `id_mod_dup_rerelease` | PASS | 1721 | 50 % re-released | 0/0 | 860/821 | | yes (poses alone drop them) |
| `id_mod_dup_quad` | FAIL `identity_no_new_picture_dropped` | 1721 | 50 % same poses, new picture | 0/0 | 860/821 | | yes (U0 drops them) |
| `id_ofxr_mimic` | FAIL `identity_no_new_picture_dropped` | 1721 | 50 % same poses, new picture | 0/0 | 860/821 | | yes (U0 drops them) |
| `id_pause` | PASS | 1800 | 50 % paused repeats | 901/901 | 901/0 | | yes (waits 45.1/s, no pattern) |
| `id_mod_late_stale` | FAIL `identity_stamp_matches_picture` | 2700 | 4 % old picture under a new pose | 1350/1350 | 0/0 (mode off) | 0.0 / 5.1 % (p95 2.1, max 11.8 mrad) | yes: **the shaking reproduced** |
| `id_mod_late_samepose_new` | FAIL `identity_new_pictures_shown` (re-evaluated) | 1721 | 1 % same poses, new picture | 839/839 | 860/821 | 0.0 / 0.0 % | yes: **50 drawn, 0 reached the client** |
| `id_mod_late_oneeye` | PASS (report) | 1721 | 2 % one eye | 860/860 | 860/821 | 0.0 / 8.1 % (max 17.5 mrad) | yes |

Notes: the window of the counts is the first 900 host frames' VDXR line (1721 submits with the mode on), so most late shapes fall after it; the
stamp analysis uses the whole stream. `id_mod_late_samepose_new` first PASSED: `identity_no_new_picture_dropped` compares totals (dropped /
seen <= true-duplicate share + 0.02), and new pictures at 2 % of the same-pose submits fit its allowance; the decoded ids are exact
(`identity_new_pictures_shown`, added the same day, re-evaluated on the saved run: 0 of 50). The neural frame generation ran on the RTX 30
for the first time (rows with frame generation: synthesis neural, 0 failures, 7.8 ms per run on 880 x 800 fields against about 1.8 ms on the
5090). Not run: the three real OFXR rows (D: is gone) and the manual sharpen row (admin).

## 5. G1: the field check decides U1's reach

AC Black Flag with the mod on the 5090 with the fork build, debug logging on, one session; read the shim's `app frames (VDXR)` line:

- "resubmitted with no new picture" near half the frames: the duplicate cycle releases nothing. U1 handles the real mod (row `id_mod_dup`). Go on.
- near zero: it releases an image (row `id_mod_dup_rerelease`). U1 would stop dropping its duplicates and undo the Black Flag fix. Stop Phase B and
  rethink: telling a re-released copy from a new picture needs the picture itself (a GPU checksum per submit), a different cost class; ask before.
- in between: read it per window (the cutscene lock changes the cadence) before deciding.

**Result (2026-11-21): nothing released, go on.** The owner on the 5090, 1.4.1 (`main` `8fb3381`, VDXR fork `46deebb`), debug on, two sessions of
about 9 minutes: the compatibility mode on (17:08), then off (17:18). Logs `private/logs/ac-black-flag-2026-11-21/` (game, shim, client CSVs, the
settings in force, VDXR's log); every number below comes from `python3 harness/shim_identity_windows.py <shim log> --flip 12000` (per 900-frame
window) and the run's `game_run` event. Conditions: Vision Pro at 100 Hz, profile AVP optimized trade-off (the game renders 1920 x 2368 per eye, the
encode mask hides 22 % of the blocks), 275 Mbps, frame generation on at "third" (release grid 33.3 Hz, period mul 3), and the game's own limiter at
45 fps (`Documents\Assassin's Creed Black Flag Resynced\ACBlackFlag.ini`: `framerate_limiter=1`, `framerate_target=45`; it was off in the
2026-10-25 copy of that file).

- **The duplicate cycle releases nothing.** In every play window before the mod's change of rhythm, in both modes, the submits with no image
  released are exactly the submits with the previous submit's render poses (mode on: 900 of 1800 in each of 3 windows; mode off: 450 of 900 in each of
  10), and every other submit releases both eye slices (1800 and 900 slices). So U1 (same poses AND nothing released) takes U0's decision on every
  frame of this game: `id_mod_dup` is the field's shape, `id_mod_dup_rerelease` does not apply to it.
- **The change of rhythm is at app submit 12,000** in both runs (4.5 min in at 45 submits/s, 6 min in at 33.3/s: the count, as in October's engine
  frame 12,000), and its shape depends on the mode:
  - mode on: same poses and nothing released still agree to within 1 per window; new pictures stay at 22.4/s; but 120-177 of every 899 new
    pictures (15.9 %) release ONE eye with a new pose, so that eye shows the picture of the frame before. The mod's doing; U0 and U1 both keep them.
  - mode off: new pictures fall from 16.7 to 14.0/s; 52.7 % same poses but 58.1 % nothing released: 34-50 of every 900 submits (5.4 %) carry a
    new pose and no new picture (the last picture under a fresh pose). U0 and U1 keep them; only a release-only test would drop them (U2, cut),
    nothing is dropped with the mode off anyway, and with the mode on this shape does not occur (0-3 per window).
- No single-pose freeze like the October probe's 6-7 s stretches (one would add about 200 same-pose submits to a window). The headset: periods
  skipped 0.2-1.2 % per minute in play, no gap over 3.25 periods outside loading screens, both modes, both sides of the change (October, lock 33:
  hitches of 60-90 ms about once a second after it).

**The baseline (U0 = 1.4.1) that Phase B and section 9 are judged against** (medians of the play windows; windows with a quad loading screen and
the window that spans submit 12,000 left out):

| | mode on, before 12,000 | mode on, after | mode off, before | mode off, after |
|---|---|---|---|---|
| app submits /s | 45.2 | 44.1 | 33.3 | 33.4 |
| new pictures /s (an image released) | 22.6 | 22.4 | 16.7 | 14.0 |
| nothing released / same poses / dropped | 50.0 / 50.0 / 50.0 % | 49.2 / 49.1 / 49.1 % | 50.0 / 50.0 / 0 % | 58.1 / 52.7 / 0 % |
| one eye released (of new pictures) | 0 | 15.9 % | 0 | 0 |
| new pose, nothing released (of submits) | 0 | 0.1 % | 0 | 5.4 % |
| releases late | 98 % | 88-92 % | 60-67 % | 63-66 % |
| xrWaitFrame return -> xrEndFrame p50 | 24.0 ms (9-14 already after the loading screen at submit ~8,900) | 12.7 ms | 14.5 ms | 0.15 ms |

Whole runs (`game_run`, mode on / off): latency p50 65.9 / 57.3 ms, p95 67.1 / 67.1; encode p50 2.7 / 3.5 ms; GPU p50 41 / 31 %, 218 / 183 W;
stream 98.0 / 96.6 fps; host repeats 32 % / 0.6 %.

Two more sessions the same evening (same save, the game's limiter still 45; `run3-181252`, `run4-182103` in the same folder): run 3 with the mode
off and frame generation at "half" (grid 50 Hz), run 4 with the mode on and frame generation off (from 14 s in). Before submit 12,000 the identity
result holds in both, to the frame (run 3: 450 of 900 in 9 play windows; run 4: 900 of 1800 in 5), so **G1 holds in all four configurations**. Both
make 22.6 new pictures/s: the mode costs pictures only where our grid is under twice the game's picture rate (mode off gives grid / 2: "third" at
100 Hz = 16.7, "half" = 25, above the limiter's 22.6). Run 3 after 12,000 (8 windows): 21.9 new pictures/s, nothing released 52.3 % against same
poses 51.2 %, one eye released 6.5 % of new pictures, a new pose with nothing released 0.9 %: the two late shapes mixed (free-running gave one-eye
releases in run 1, paced waits gave pose-only submits in run 2). Run 4 ended before 12,000 (about 10,800 submits).

| run | mode | frame generation | new pictures /s | generator inputs that are duplicates | host repeats | headset repeated | latency p50 / p95 ms | encode p50 ms | GPU p50 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | on | third | 22.6 | about 0 | 32 % | 0.5 % | 65.9 / 67.1 | 2.7 | 41 % |
| 2 | off | third | 16.7 | >= 51 % | 0.6 % | 0.6 % | 57.3 / 67.1 | 3.5 | 31 % |
| 3 | off | half | 22.6 | >= 39 % | 33 % | 0.9 % | 65.9 / 67.0 | 2.7 | 42 % |
| 4 | on | off | 22.6 | - | 0 | 77 % | 79.1 / 88.8 | 7.8 | 34 % |

Duplicates at the generator = its ingests minus the new pictures the shim saw (a lower bound). With the mode off every duplicate becomes a generator
input, and the frames it generates between two identical pictures do not move; with the mode on none reaches it. For the U1 regression the mode
must be on (U1 acts inside the compatibility tick): runs 1 and 4 are its references, run 4 the simpler one (no generator).

What it changes:
- **Before the change (and with the game's limiter at 45) U1 gains nothing on this game:** there its case rests on OFXR (row
  `id_ofxr_mimic`, the 2026-11-13 lab: 88 -> 72 new frames/s) and Black Flag is a regression check (the same dropped count per window as U0, to
  within 1). **Uncapped and after the change it does gain** (section 5b: today's mode drops new pictures that share the previous pose).
- **A/B windows stay on one side of submit 12,000** (the mod's shape changes there): pairs all before or all after, never across; the shim logs
  the count every 900 frames.
- **The same conditions for any comparison** (frame generation "third", the game's limiter at 45, the save): they set the new-picture rate (mode
  off: the 33.3 Hz grid / 2; mode on: the limiter's 45 / 2, the mod drawing one eye per engine frame; hence 98 % late releases with the GPU 41 % busy).
- Two field shapes the matrix lacks, both "keep" in U0 and U1, so they matter only to H3 (no regression): one eye released per submit (mode on,
  after the change) and a new pose with nothing released (mode off, after). Rows for them are cheap (`xr_modsim`, no engine change); add them to
  Phase A only if the owner wants H3 to cover the field's late shapes.

Against October (`private/logs/ac-black-flag-2026-10-27*`, `-dupfix-1`, `-freewait-1`; 1.1.x, the game at 2788 x 2976 to 3552 x 3200 per eye,
frame generation half or uncapped with lock 33, the game's limiter off in its 2026-10-25 settings copy and 62.5 submits/s on 10-28, above 45): the same 50 % same-pose pairs and the same change at 12,000; GPU p50
61-93 % (324-420 W) -> 31-41 % (183-218 W); encode p50 7.7-18.6 -> 2.7-3.5 ms; latency p50 67-77 -> 57-66 ms, p95 77-87 -> 67 ms; new pictures
25/s (no fix, grid 50 Hz) and 31.9/s (fix + lock 33) -> 16.7 and 22.6/s, a drop set by the settings above (grid, the game's limiter), not by the
engine. Not a one-variable comparison: engine, render size, frame generation mode and the game's settings all changed.

## 5b. The shaking after the change (2026-11-21, runs 5-6: the game's limiter off, frame generation off)

The owner: with the game's limiter off and no frame generation, in both modes, the picture starts shaking after about 2 minutes (the behaviour the
universal work is for), and shakes less with the limiter on. Logs `run5-183218` (mode off), `run6-183618` (mode on), same folder; the game's
settings file says `framerate_limiter=0` (target 100).

**The 2 minutes are the mod's change at submit 12,000**: 2 min 11 s in run 5 (94.5 submits/s), 1 min 51 s in run 6 (108/s); with the limiter
(33-45 submits/s) it comes after 4.4-6 min. Before it both runs are exact again (nothing released = same poses, 12 and 5 windows). After it:

| | run 5, mode off | run 6, mode on |
|---|---|---|
| submits /s before -> after | 94.5 -> 97.9 | 108 -> 129.7 (above the display rate: the free waits let it spin) |
| new pictures /s before -> after | 47.3 -> 39.6 | 54.0 -> 63.9 |
| after: nothing released / same poses | 59.6 / 52.7 % | 51.6 / 54.6 % |
| **old picture under a new pose** (nothing released, new pose; lower bound) | **6.9 % of submits, about 6.8/s** | masked (>= 0) |
| **new picture under the previous pose** (same poses, an image released; lower bound) | masked (>= 0) | **3.1 % of submits, >= 4/s, all dropped by the mode** |
| one eye released (of new pictures) | 0.8 % | 3.8 % (2.4/s) |
| whole run: GPU p50, encode p50, latency p50 / p95, headset ticks without a new frame | 72 %, 16.4 ms, 77.0 / 77.2 ms, 2-12 % | 83 %, 8.2 ms, 65.5 / 77.2 ms, 37-50 % |

Delivery does not change at the change (headset ticks, frame gaps): the shaking is not a cadence problem. **What makes it (inferred from the counts
and the code, not seen per frame):** the shim stamps a submit with the tracking sample of its layer's render pose (`FrameClientTimestamp`), and the
headset reprojects the picture to that pose. An old picture under a new pose is put where the new pose looks: for one frame the world moves by the
head's motion between the two poses (about 0.5-2 degrees at 50-100 degrees/s over 10-20 ms), then back with the next real picture. Still head:
nothing; turning head: shaking. This is the 2026-10-12 trembling's mechanism (the shim composed frame F-1's picture under pose F), made by the mod
this time. The mirror case, a new picture under the previous pose, is dropped by today's mode; when the next submit releases nothing, VDXR re-sends
that picture under the next pose: an old picture under a new pose again. A one-eye submit puts one eye's picture under a pose a frame newer than it.

**Less with the limiter:** mismatched submits per second after the change, limiter at 45 (runs 1-3, frame generation on): 1.8/s (run 2), 3.6/s
one-eye only (run 1), 1.8/s (run 3); uncapped: 7.1/s (run 5), >= 6.4/s (run 6). Consistent with the owner's "less"; not a clean A/B (frame
generation was on in runs 1-3, and run 4, limiter on with it off, ended before the change).

**Proposed (owner to decide; nothing built):**
- **Phase A, three more `xr_modsim` shapes** that start after N submits as the mod's do (`MODSIM_CHANGE_AT`): a share of submits with a new
  pose and nothing released, a share of duplicate cycles that release a new picture, one eye released. Plus a truth check per outgoing frame: its
  stamp is the stamp of the submit that released its picture (`identity_stamp_matches_picture`); U0 fails it on the first shape by the count of
  such submits. No engine change. **Built 2026-11-22 (owner: add them to tomorrow's Phase A):** `MODSIM_CHANGE_AT`, `MODSIM_STALE_POSE`,
  `MODSIM_SAMEPOSE_NEW`, `MODSIM_ONE_EYE`, DRAW / CHANGE rows and an `eyes` column; `lib.analyse_stamp_truth` (the decoded picture id of every
  received frame, the yaw it was rendered with against the client's yaw for the frame's timestamp, the constant offset removed, before / after
  the change); rows `id_mod_late_stale`, `id_mod_late_samepose_new`, `id_mod_late_oneeye` (section 4). Tested: `xr_modsim` clang-cl check (a
  planted error caught); the analysis and the checks on synthetic runs (U0 stale fails at 6.7 % after / 0 before, a fixed stamp passes at 0, one
  eye reported, the offset recovered exactly, decoder start loss, idle and unknown ids, old 8-column CSVs). Not run on a builder.
- **Phase B, an arm next to U1 (`lab_identity 2`): a picture keeps its own stamp.** A submit whose projection slices released nothing leaves
  with the previous submit's timestamp (a true repeat, which the headset shows as one: the 1.3.10 strobe test), whatever its poses; with the mode
  on it is dropped like a duplicate. U1's rule for same poses stays, so a new picture under the previous pose is kept. No pattern state and no free
  wait, so U2's objections (a paused app spinning on free waits, the budget) do not apply, and a paused game gets its last picture at its own pose,
  which is right. One-eye submits stay as today: ALVR v20 carries one pose per frame, so no stamp is right for both eyes.
- **Field counters** in the shim's statistics (debug only): same poses with an image released, a new pose with nothing released, one eye
  released, and the angle between a stale submit's pose and its picture's pose (p50 / p95 / max: the shake's size in degrees). They make the lower
  bounds above exact. Shim change: ask first.
- Section 8 defers U3 because "it changes statistics, not the picture". That does not hold for these submits, where the stamp decides where the
  picture lands. The arm above is the narrow part of U3 the field asks for.

## 6. Phase B: arm U1 (engine, this branch only, after the owner confirms)

Change (shim only, VDXR unchanged; about 30 lines next to `driver.cpp:1559-1612`):
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

**As built (`685a07d`, owner: go).** One switch, three arms: host `--lab-identity N`, stdin `lab_identity N`, the lab's `AIRFRAMEXR_LAB_IDENTITY=N`
(IPC 39 `labIdentity`; no GUI, no config file). 0 = 1.5 exactly; 1 = U1 as above; 2 = U1, and a submit whose slices released nothing leaves with the
previous frame's timestamp, whatever its poses (section 5b's "a picture keeps its own stamp"). Note: arm 2 re-stamps with the mode off too, so it is not
confined to the compatibility tick. The id check is not built: `ovr_EndFrame` does not carry the frame index (OVRNull `api.cpp:393-400`), so the
counter is "submits that took several facts" next to "submits without facts". Stats line: `identity: arm, submits, same poses with an image released,
new poses with nothing released, re-stamped, without runtime facts, with several`.

**Phase B result, the matrix (2026-11-23, 3080 Ti, same root and flavour as Phase A, shim + host lib + host built together at `685a07d`; 13 rows per arm,
36 min each; results arm 0 `harness/results/20261123T0948*`-`T1022*`, arm 1 `T1024*`-`T1058*`, arm 2 `T1101*`-`T1136*`):**

| row | arm 0 (= 1.5) | arm 1 (U1) | arm 2 (U1 + own stamp) |
|---|---|---|---|
| `e2e_modsim_ofxr_fix_off` | PASS | PASS | PASS |
| `e2e_modsim_ofxr_fix_on` | PASS (860 seen / 821 dropped) | FAIL by design: nothing dropped (see below) | FAIL by design, same |
| `id_normal_game` | PASS | PASS | PASS |
| `id_mod_dup`, `_array`, `_msaa` | PASS x3, 860/821 | PASS x3, 860/821 | PASS x3, 860/821 (the 39 kept duplicates re-stamped) |
| `id_mod_dup_quad` | FAIL: 50 % of the submits (a new quad) dropped | **PASS**: 0 dropped | **PASS** |
| `id_mod_dup_rerelease` | PASS | FAIL `identity_true_dups_dropped` (the known blind spot) | FAIL, same |
| `id_ofxr_mimic` | FAIL: the generated pictures dropped | **PASS** (one submit took 2 facts) | **PASS** |
| `id_pause` | PASS, 901 seen / 0 dropped | PASS | PASS (901 re-stamped, one submit took 2 facts) |
| `id_mod_late_stale` (mode off) | FAIL: stamp error 0.0 / 5.6 % (p95 2.9, max 16.8 mrad) | FAIL: 0.0 / 5.3 % | **PASS: 0.0 / 0.0 %** (1350 re-stamped) |
| `id_mod_late_samepose_new` | FAIL: 0 of 50 new pictures shown | **PASS**: 50 of 50 | **PASS**: 50 of 50 |
| `id_mod_late_oneeye` (report) | PASS, 8.2 % after | PASS, 7.6 % | PASS, 8.0 % (as expected: no arm fixes one eye) |

- **Arm 0 = Phase A**: the same decision on every row and the same counts (truth, VDXR / app no-release, seen / dropped, free waits); the stamp error
  after the change 5.6 % against 5.1 %. The switch's default changes nothing.
- **Pairing**: submits without facts 0 on every row of every arm; one submit with two facts in all (arm 1 `id_ofxr_mimic`, arm 2 `id_pause`), so
  arrival order holds.
- **`e2e_modsim_ofxr_fix_on` asserts 1.5's drop** (`fix_on_dups_dropped`, `fix_on_free_waits`, `fix_on_second_wait_free`, <= 100 duplicate
  timestamps). Under U1 OFXR's second submit released a new image: it is kept, both pictures go out with one timestamp (1457, as with the mode off), the
  app runs as with the mode off: real frames 44.22 / 44.25 fps (arm 1 / 2) with the mode on against 44.21 / 44.24 off, so Decision B's "OFXR with the
  mode on within 2 fps of off" holds (arm 0: 48.8 on, 44.2 off, because the generated pictures are dropped). If an arm ships, this row's checks become
  "as `_fix_off`".
- **A measurement fix on the way**: the first arm 0 table failed the three late rows "before the change" (2.5-5.1 %, up to 455 mrad). Cause: the ALVR
  server turns the tracking space once when the client's `PlayspaceSync` arrives (`server_core/src/connection.rs:1178`, `rotation_recentering_mode`
  Yaw); pictures that reach the client before it carry another offset (25 frames at 0, then 174 mrad for the whole run). Phase A never had a picture
  that early; arm 0 did: a race, not the arm. `analyse_stamp_truth` now leaves out the stream's first `checks.identity.stamp.warmup_s` (2 s, the late
  shapes start near 11 s); re-evaluated, every saved run (Phase A, the arm 2 smoke run, arms 0-2) reads 0.0 % before the change.

**Phase B result, regressions and timing (2026-11-23, same build; regressions arm 2 `harness/results/20261123T1141*`-`T1221*`, arm 1
`T1224*`-`T1303*`; timing `T1306*`-`T1353*`):**

- **Regressions (the R5 set and the `e2e_modsim_*` rows, 15 per arm): 14 of 15 pass under arm 1 and under arm 2.** The 15th,
  `e2e_modsim_static_pose_fix_on`, fails `fix_on_idle_nothing_dropped` under both, by design. The row runs the mod's loop (every real frame submitted
  twice) with a still head and asserts 1.5's outcome, nothing dropped: 1.5's pose-only test calls every submit "same", finds no pattern and stays out.
  With the facts, the real frames (an image released) and the duplicate cycles (nothing released) are told apart, so both arms find the mod's
  pattern and drop the true duplicates as with a moving head (860 seen, 821 dropped, 820 free waits, under both arms). Like
  `e2e_modsim_ofxr_fix_on`, the check encodes 1.5's blind spot; if an arm ships it becomes "as `e2e_modsim_fix_on`".
- Arm 1's `e2e_openxr_probe` failed `pacing_latency_steady` on its first run (last third minus first third -7.8 ms, limit 4): latency per third
  32.6 / 24.7 / 22.3 ms while the GPU went from P0 to P8. Arm 2's passing run has the same step a third later (32.4 / 32.8 / 24.3). With the mode
  off, arm 1 only counts. Rerun (`T135642Z`): PASS, -1.7 ms. A start-up race, not the arm.
- **The mod's numbers** (`e2e_modsim_fix_on`): arm 2 860 seen / 821 dropped / 820 free waits, second wait 0.1 ms p50, 45.0 real fps; arm 1
  859 / 820 / 819, 0.1 ms, 45.05 fps; 1.4.1 (`openxr-foundation` `20261120T182139Z`) 860 / 821 / 820, 0.1 ms, 44.77 fps: unchanged.
- **Pairing**: "without runtime facts" 0 in every run that prints the stats line (the regression rows `e2e_layers`, `e2e_fov_restrict_none`,
  `e2e_fov_restrict_quality_full` and `e2e_fov_client_box` print none). "With several" is 1 in 6 runs (matrix: arm 1 `id_ofxr_mimic`, arm 2 `id_pause` and `e2e_modsim_ofxr_fix_off`;
  regressions: arm 2 `e2e_openxr_probe`, arm 1 `e2e_modsim_fix_on` and `e2e_modsim_control_ts`) and 0 in the other runs, including all 18
  timing runs and the probe rerun. One submit per run took two facts and none took none, so one fact had no submit of its own; the facts are
  summed, so that submit counts as new if either frame released. Cause not found: on the sync path VDXR calls `ovr_EndFrame` after every frame's
  facts (fork `frame.cpp:504-563`, a dummy layer when the frame has none).

**Timing A/B.** 3 pairs, interleaved, the order rotated per pair (0-1-2, 1-2-0, 2-0-1), on `id_mod_dup` and `id_ofxr_mimic`. Whole-run values (the
display analysis skips its warm-up ticks), not the 10-40 s window. Arm 0 has no steady window on `id_ofxr_mimic`, so the repeat and skip
figures are whole-run for every arm, and pose age is missing there. A 1 ms floor for the ms metrics (the plan names none). Medians of the 3 pairs;
**bold** = "changes" by the rule against arm 0:

| metric | `id_mod_dup` arm 0 / 1 / 2 | `id_ofxr_mimic` arm 0 / 1 / 2 |
|---|---|---|
| app frame rate (waits/s) | 90.1 / 90.1 / 90.0 | 97.6 / **88.3** / **88.5** |
| client fps | 45.9 / 45.9 / 45.9 | 38.2 / **55.8** / **55.8** |
| unique timestamps/s | 45.9 / 45.9 / 45.4 | 37.7 / 36.3 / 36.4 |
| repeated ticks % | 48.9 / 48.8 / 48.8 | 57.5 / **37.7** / **37.9** |
| skipped % | 60.0 / 60.0 / 59.9 | 90.6 / **64.5** / **64.4** |
| encode ms p50 / p95 | 8.4 / 23.7, 8.3 / 24.0, 8.4 / 23.9 | 8.0 / 9.3, 7.9 / 9.1, 8.1 / 9.2 |
| arrival minus timestamp ms p50 / p95 | 26.4 / 54.7, 26.7 / 57.6, 26.2 / 58.4 | 21.5 / 55.9, 27.3 / 53.0, 28.2 / 53.9 |
| pose age ms p50 / p95 | 38.3 / 49.9, 38.4 / 49.9, 38.3 / 49.9 | -, 44.6 / 56.1, 44.6 / 56.1 |

- `id_mod_dup`: nothing changes. The arms cost the mod's row nothing, including arm 2's 39 re-stamped kept duplicates.
- `id_ofxr_mimic`: the four changes are the fix itself. Arm 0 drops OFXR's generated pictures and frees their waits (hence 97.6 waits/s); arms 1 and 2
  keep them, so 55.8 pictures/s reach the client instead of 38.2. Encode time does not move. Arrival minus timestamp p50 rises by 4-7 ms in 3 of 3
  pairs, but stays under 3 x arm 0's spread (2.6 ms), so it is not a change by the rule. The kept generated pictures carry their frame's
  timestamp (36.4 unique timestamps/s for 55.8 pictures/s), so they arrive one frame later than their stamp, as with the mode off. That reading is
  not measured on this row with the mode off.
- Not run that day: `e2e_ofxr_tokyo_on_compat` (real OFXR; its layer was built on the 3080 Ti's D: drive, which is no longer used, so it needed a
  rebuild on C:). Run in the follow-up batch below.

**Follow-up batch: real OFXR, `e2e_baseline`, the mode forced on (2026-11-23 evening, same build; `harness/results/20261123T1428*`-`T1713*`).**
The owner asked for this batch after reading Phase B's results.

- **The layer.** OFXR is rebuilt on C: by `tools/remote/ofxr_lab_build.ps1`: djules75 V551 `382065f`, the same pin as 2026-11-13, by its own CI recipe,
  into `C:\_ofxr\lab`.
  - Smoke run `T142153Z` (arm 0, `e2e_ofxr_tokyo_on`) matches 2026-11-13: 88.1 new frames/s against 87.9, app 42.1 fps against 42.2.
- **Real OFXR, arm 0 against arm 2:** 3 interleaved pairs per scenario, the order flipped in the middle pair, the 10-40 s window (`harness/ofxr_report.py`).
  - Medians of the 3 pairs; **bold** = "changes" by section 6's rule (1 ms floor for ms).
  - "Mode" is the compatibility mode (`drop_duplicate_frames`); "frame gen." is OFXR's own frame generation.
  - All 18 runs pass.

| metric | frame gen. off, mode off: arm 0 / 2 | frame gen. on, mode off: arm 0 / 2 | frame gen. on, mode on: arm 0 / 2 |
|---|---|---|---|
| app fps (log) | 44.5 / 44.5 | 42.4 / 42.5 | 42.7 / 42.5 |
| new frames/s | 44.5 / 44.5 | 87.8 / 87.8 | 71.4 / **87.8** |
| repeated ticks % | 50.6 / 50.6 | 2.5 / 2.4 | 20.7 / **2.4** |
| dropped by the mode | 0 / 0 | 0 / 0 | 2,415 / **0** |
| client fps | 39.8 / 39.8 | 70.5 / 70.4 | 59.0 / **70.5** |
| encode ms p50 / p95 | 8.2 / 9.3, 8.3 / 9.4 | 8.3 / 9.3, 8.3 / 9.3 | 8.2 / 9.3, 8.2 / 9.3 |
| arrival ms p50 / p95 | 16.0 / 52.6, 15.7 / 51.8 | 50.5 / 66.9, 51.4 / 66.7 | 47.8 / 63.0, 50.9 / **66.5** |
| pose age ms p50 / p95 | - | 67.2 / 83.7, 67.1 / 83.5 | 61.0 / 78.9, **67.1** / 83.2 |

  - **With the mode off, arm 2 is arm 0** on every metric.
  - **With the mode on, arm 2 keeps every OFXR picture and reads like the mode off.**
  - Arm 0 drops a third of OFXR's submits. The shim's log shows why: 2,741 of 6,916 submits had the previous frame's render poses with a new image
    released. That is OFXR's generated frame, and the poses-only test calls it a duplicate. The ratio lands inside the mode's 40-70 % pattern band.
    Arm 2 sees 181-192 submits per run that released nothing: OFXR's own resubmits, 3.5 %, under the band, so nothing is dropped.
  - Arm 2's later arrival and pose age with the mode on are the mode-off values. Arm 0 reads lower only because it sends fewer frames.
  - The ~35 ms between OFXR off and on (arrival p50 16 → 51 ms) is OFXR holding a rendered frame to generate the one between, not our chain.
- **`e2e_baseline`, arm 0 against arm 2, mode off:** 3 pairs.
  - App fps 88.9 / 85.0 / 89.0 against 88.6 / 86.0 / 88.7 (median difference -0.3); client fps +0.7; encode -0.3 ms; arrival p50 +0.7 ms.
  - Nothing changes. The 2 fps gap of the single runs was noise: the middle pair reads 85-86 under both arms.
  - Arm 0's first run failed `pacing_latency_steady`: last third minus first third +5.8 ms, limit 4. The other five read -1.6 to +1.1.
- **The regression set under arm 2 with the mode forced on** (`AIRFRAMEXR_LAB_DROP_DUP=1`, every scenario that does not set the key itself): 12 of 15
  pass.
  - **Normal games:** every app frame is published (`app_timing.csv` rows = `shim_frames.csv` rows; `e2e_baseline` 900 / 899, as arm 0 with the
    mode off). The rows that print the shim's stats line read dropped 0 and free waits 0.
  - **The mod's rows are unchanged:**
    - `fix_on`: 860 seen, 821 dropped, 820 free waits;
    - `dup_stops`: 862 seen, 823 dropped;
    - `triple`: 1,694 seen, 1,642 dropped, 2 per real frame;
    - `fix_off`: keeps its explicit off; 1,350 seen, 0 dropped, 1,350 re-stamped.
  - `e2e_modsim_static_pose_fix_on` fails `fix_on_idle_nothing_dropped`, by design (above).
  - `e2e_openxr_probe` (-7.0 ms) and `e2e_baseline` (+6.1 ms) fail `pacing_latency_steady`. With arm 0's failure above, this check fails on this rig
    whatever the arm.
    - **Reruns** (`T1716*`-`T1731*`): `e2e_openxr_probe` with the mode forced on passed 2 of 2 (-0.8 and -0.5 ms).
    - **5 interleaved pairs of `e2e_baseline`, arm 2, mode forced on against off** (`T1734*`-`T1802*`):
      - on fails 1 of 5 (+8.6 ms; the others -1.3 to +1.5);
      - off fails 2 of 5 (+6.5 and -5.2 ms; the others -0.2 to +1.9);
      - client fps and arrival p50 the same.
    - **Over the day, `e2e_baseline` failed this check in 6 of 20 runs:** arm 0 with the mode off 1 of 4, arm 2 off 2 of 8, arm 2 forced on 3 of 8.
      - This is the rig's flake, not the arm or the mode.
      - The check (a third of a 900-frame run against another) is too short for this rig's latency steps. It is a harness item, not a Phase B
        one.

**Decision B, where it stands (the owner decides):**

| criterion | arm 1 (U1) | arm 2 (U1 + own stamp) |
|---|---|---|
| every matrix row passes except the re-release row | no: `id_mod_late_stale` fails (the shaking row; section 5b added it after this rule was written) | yes |
| no regression in R5 and `e2e_modsim_*` | yes, apart from the 1.5-only check above (the probe passed on rerun) | yes, apart from the 1.5-only check above |
| OFXR with the mode on within 2 fps of off | yes (44.22 / 44.21, lab mimic; real OFXR not run) | yes (44.25 / 44.24, lab mimic; real OFXR app 42.5 / 42.5 fps, new frames 87.8 / 87.8 per s) |
| pairing counters 0 | without facts 0; several 1 in 3 runs | without facts 0; several 1 in 3 runs |
| the mod's numbers unchanged | yes | yes |
| ships inside the compatibility tick, no effect with the mode off | yes (with the mode off it only counts) | **no**: it re-stamps with the mode off, and that is how it fixes the shaking row |

`e2e_modsim_ofxr_fix_on` (in the matrix) and `e2e_modsim_static_pose_fix_on` (in the regressions) are the two checks that assert 1.5's
behaviour. Both arms fail them for the right reason, and they are rewritten if an arm ships. Arm 2 is the only arm that meets the matrix
criterion. Before section 9's headset A/B, three things were owed:
- ~~the owner accepts arm 2's mode-off re-stamp, or keeps arm 1~~. **Owner's direction, 2026-11-23: arm 2 is the future. The aim is the
  compatibility mode always on, with its switch removed.**
  - Arm 2 does not replace the mode. The mod's second-wait pacing and duplicate drop still need it: with the mode off under arm 2, the mod runs
    at 22.5 real fps against 45 with it on.
  - What arm 2 removes is the mode's harm to OFXR, the reason it had a switch.
  - With no switch there is no "mode off", so the last row of the table stops applying.
  - The regression set with the mode forced on (follow-up batch above) is the evidence that normal games are unaffected.
  - **The rule, in the spec's terms (owner, 2026-11-23: the spec is the arbiter; OpenXR 1.1 reference pages):** literal for every app that
    follows the spec; the compatibility behaviour only where the app's own frames contradict the spec's frame model.
    - **Mode off is the letter.**
      - Every `xrWaitFrame` is paced ("the runtime **should** block until the beginning of the next frame interval").
      - Every ended frame is composed with the last released image.
      - Every frame is stamped from the pose the app declared ("the runtime will map the view and FOV to the system display appropriately").
    - **The mode breaks no "must".**
      - The free wait bends one "should" (the blocking).
      - It keeps the "musts": the display time is one interval later ("**must** be monotonically increasing"), and the wait comes after the
        previous frame has begun.
      - The duplicate drop is a case the spec does not cover: the same image under the same poses, which the headset shows as the same picture.
    - **Arm 2's re-stamp departs from the declared pose only when an app puts new poses on an image it did not release again.**
      - That was 0 times in every app that follows the spec; the mod did it after submit 12,000.
      - With the same poses it agrees with the declared pose.
    - **The mod departs first.** It sends each picture under two display times, where the spec says "Every stage in the engine pipeline
      **should** use the exact same display time for one particular application-generated frame".
    - **Measured for apps that follow the spec, arm 2 with the mode on:**
      - drops 0 and free waits 0 in the normal games;
      - drops 0 for OFXR, and re-stamps only its own 3.5 % resubmits, with no measured effect.
  - **An app without runtime facts (owner, 2026-11-23: agreed).** That is stock VDXR or a libOVR game, where every arm fell back to the
    poses-only test that dropped OFXR's frames.
    - Decision: no compatibility behaviour without facts. No evidence means the letter. The mode does not engage for those apps; this is not a
      setting.
  - **Decision B (owner, 2026-11-23): ship arm 2 with the mode always on; the switch is removed; no compatibility behaviour without facts.**
    - The 1.5 mode as it is (poses only) is the one the data contradicts. The data supports arm 2's detection.
    - Owed before shipping:
      - the two 1.5-only checks rewritten;
      - the no-facts rule built and run in the lab;
      - section 9's headset A/B.
  - **Not covered by the lab:**
    - an app that double-submits on purpose: the free waits would let its loop run faster, up to its own limiter;
    - the headset.
  - Native libOVR games are experimental only (owner, 2026-11-23): a starting point for people who want to tinker with old titles, not a
    supported target. The no-facts rule only needs to be safe for them, not tuned.
  - The rule applies per submit: a submit without facts counts as a new picture. That way a layer VDXR rejects in an OpenXR game cannot feed the
    pattern windows.
  - Section 9 and the field logs are where "unless data says otherwise" is tested. The field log needs one line when the mode engages and
    disengages: an engine edit, to confirm with the owner.
  - Removing the switch is part of shipping arm 2: engine, GUI and `docs/EXPERIMENTAL_SWITCHES.md`. The scenarios that set the mode off are
    rewritten then; examples are `e2e_modsim_fix_off`, `e2e_ofxr_tokyo_off` and `e2e_ofxr_tokyo_on`.
- the two checks are rewritten (still owed);
- ~~the real-OFXR row is rebuilt on C: and run~~: done in the follow-up batch above.

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

Client 1.4.0 on the Vision Pro, the user on the 5090: OFXR on a real title, then AC Black Flag with the mod (its counts against the section 5
baseline, under its conditions, every window on one side of submit 12,000). Arm switched live with the stdin command
every 3 windows of 10 s, balanced order, start windows dropped. Read: ticks fresh %, `same_ts`, latency, the `identity:` line, and the user's blind call
on two or three head motions. One night.

**First field session, not the A/B (2026-11-23 evening).** The setup:
- The personal bundle `AirFrameXR-1.5.0-bundle-personal-universal-arm2.zip`: `9ee1bd5`, arm 2 by default, sha256 `5d48cb7e…`.
- On the 5090 at `D:\_VR\AirFrameXR`, with the mode on and the release flavour.
- Logs in `private/logs/universal-arm2-2026-11-23/`.

- **AC Black Flag** (16 min 34 s; the game at 22.6 new pictures/s, i.e. its limiter at 45 as in section 5):
  - Frame generation was off for 12 min: a 22.6 fps stream, 77 % of headset ticks without a new frame, as in section 5's run 4.
  - Then x3: a 99.6 fps stream, 0.5-1.3 % of ticks without a new frame.
  - The owner: "transformed". It worked well natively and "the behavior is exactly right at start now".
  - The mod's change still kicks in. The owner places it after about 2 minutes. At 45 submits/s the mod's submit 12,000 falls about 4.5 min in;
    about 2 min matches it only uncapped (about 100 submits/s, section 5b).
  - Debug was off, so no identity counts. Expected after the change, from section 5's run 1: one-eye releases, 15.9 % of new pictures. No arm
    fixes these, since ALVR v20 carries one pose per frame.
- **Dead Space** (12 min 46 s, then 2 min 51 s with debug on): the owner felt it slower than usual. Nothing from the new engine behaviour shows:
  - 0 dropped, 0 free waits and 1 re-stamp in 16,200 submits.
  - 13.5 % of its new pictures carry the previous frame's poses: under the 40 % trigger, so 1.5 behaves the same.
  - Rendered 96.8 fps (slowest tenth 94.7), headset repeats 2.8 %: as 1.4 / 1.4.1 on 2026-11-20 (96.3-97.9 fps, 2.1-3.1 %, same 1920 x 2368 per
    eye at 100 Hz).
  - What does show is the GPU at its limit: 97 % busy, 543 W, the power cap reported in 163 of 181 samples, after 26 min of PBZ.
  - The game's own latency stage grows from about 38 ms to 45-60 ms over the session (total p50 107 ms).
  - There is no earlier Dead Space debug run to compare the latency with.
  - Frame generation x2 was on for most of the first session: 50 fps by design.

**Arm 3: each eye by its own pose (owner, 2026-11-23 night: "it's a universal design", built the same night, `27fb7ff`).**
- **The problem.** Black Flag's mod uses alternate-eye rendering (AER), as Luke Ross's mods do: one eye's picture is a frame older than the
  other's. OpenXR gives each projection view its own pose ("the runtime will map the view and FOV to the system display appropriately"). The shim
  stamped the frame with the left eye's pose and composed both eyes with a FoV change only. The older eye was therefore misplaced by the head's
  turn over one engine frame: about 1 degree at 100 degrees/s and 100 fps, twice that at Black Flag's 45.
- **Arm 3 = arm 2, plus each eye mapped by its own pose.**
  - Each eye of the projection layers is drawn rotated from its render orientation to the frame's orientation, through the existing per-eye
    homography (`EyeHomography(..., qRel)`, which was given the identity before).
  - The frame's orientation is the head orientation of the newer eye: the later of the two tracking samples the eyes' poses match.
  - The frame is stamped with that eye's sample.
  - Quads are drawn from the mapped orientations, and the host's frame generation gets them as the frame's render orientations.
  - The headset then reprojects one consistent frame. Rotations compose, so this equals a pose per eye on the wire, with no protocol or client
    change and with stock clients unaffected. Position (parallax) is not corrected (no depth).
- **When it acts.** Only when the two eyes' head orientations differ by more than 1e-4 rad (about 0.006 degree). Every normal game is below that,
  and there the frame is the arm 2 frame bit for bit. A layer drawn for one eye only is left alone.
- **The switch and its counts.**
  - It is the default arm; `lab_identity 2` gives tonight's first bundle for an A/B.
  - Counted in the identity line and in the game-exit line of the release run log: "eyes mapped by their own poses in N submits (right eye
    newer, angle mean, max)".
  - A Black Flag count of 0 after submit 12,000 would mean the mod declares the new pose for its stale eye, as `MODSIM_ONE_EYE` models it.
    Then no runtime can place that eye right.
- **Field, the same night (bundle sha256 `899d463c…`).** Logs in `private/logs/universal-arm3-2026-11-23/`. The owner: "seems the
  implementation is working fine, no visible regression"; arm 3 stays as the implementation to improve.
  - **Normal games: arm 3 is the identity.**
    - Dead Space: 1 mapped submit in 53,676 (3.9 degrees, at start).
    - Mortal Shell 2: 96.4 fps.
    - Gears: no problem seen.
  - **AC Black Flag: 0 mapped submits in about 62,000 (two debug sessions).** The mod declares the same head orientation for both eyes in every
    submit, so its alternate-eye rendering never shows in its poses.
  - **Uncapped, mode off, before submit 12,000: clean.**
    - 47.7 new pictures/s.
    - Every frame stamped by its own pose (matched by display time, 0 fallbacks).
    - The client's timestamp steps a steady 2 samples (20 ms).
  - **The mod's change at submit 12,000 (2 min 6 s in at 95 submits/s).**
    - Its xrWaitFrame return to xrEndFrame falls from 12 ms to 0.15 ms: it now submits a picture rendered before the wait.
    - New pictures fall to 40.3/s with the mode off; old pictures under a new pose come at about 6/s (re-stamped).
    - The picture stream gets holes: steps of 5 or more samples (50 ms or more) rise from 0-1 to 30-50 per 10 s, and the client's receive gap
      p90 goes from 20-30 to 40-50 ms.
    - Timestamps never go back (0 of 14,490), so this is a broken cadence, not frames out of order: the owner's "flickering".
    - With the mode on (second session) after the change: 58 new pictures/s at 117 submits/s.
  - **A 33 s stretch before the change (second session, about 1-1.5 min in): every real submit released one eye**, with both eyes declared
    at the new pose. That is true alternate-eye rendering at the submit level, with the stale eye under the new pose. Poses cannot tell it.
  - **Next, arm 3's own gap: an eye that brought no new image keeps the pose of the submit that brought it.** This is arm 2's rule per eye;
    arm 3's map then rotates that eye.
    - The shim can tell per eye without VDXR's count: in Black Flag each eye has its own swapchain (0 copies into a second one), and VDXR
      commits a swapchain to the shim when the app releases it.
    - To check in the fork's source first: when VDXR commits.
    - Then an AER row in `xr_modsim` with a per-eye picture-position check.
- **Arm 4, built the same night (owner: "build the proposal of arm 2 rule's per eye", `8e5b4fb`, the default).** Arm 4 = arm 3, and an eye
  whose picture did not change keeps the pose it came with.
  - VDXR commits an eye's OVR swapchain only when the app released a new image into it (fork `d3d11_native.cpp` 560-582: it commits until the
    committed index matches the last released one).
  - So an eye of the first projection layer whose compose slot did not move since the previous submit shows an earlier submit's picture. Its
    source orientation is then the one declared with that submit, not the one declared now.
  - Arm 3's map then rotates it to the newer eye, and the frame is stamped with the newer eye's sample.
  - Both eyes unchanged is arm 2's whole-frame case (re-stamped). Static-image swapchains are left alone. Normal games release both eyes every
    frame, or share one swapchain, so nothing changes for them.
  - Counted as "one eye kept its picture's pose" in the identity line and the game-exit line. `lab_identity 3` gives arm 3 for an A/B.
- **Arm 4 in the field (2026-11-23/24 night, bundle sha256 `f9b11c67…`, logs `private/logs/universal-arm4-2026-11-24/`).** The owner: no
  regression seen, "let's base on it, it's even more universal". The games: Black Flag (debug), Dead Space, Mortal Shell 2, Titanfall 2 VR.
  - **Normal games:**
    - Dead Space: 1 mapped / 1 kept submit in 25,166, at start; 79.6 fps rendered, 97.6 streamed.
    - Mortal Shell 2: 91.9 / 91.7 fps.
    - Titanfall 2 VR: 94.6 / 94.3 fps, 1.4-2.2 % of ticks without a new frame.
    - No warnings other than Titanfall's start-up stall (5 s, its loading).
  - **Black Flag (mode on, 44,494 submits):** 0 kept or mapped before submit 12,000. After it:
    - 1,430 submits where one eye kept its picture's pose, and 1,393 rotated: the right eye newer in 95 %, mean 0.35 degree, max 6.0.
    - 761 re-stamped.
    - Delivery about the same across the change: 56.2 then 53.8 new pictures/s, gaps of 3 periods or more 16-94 per minute.
    - The mod's xrWaitFrame return to xrEndFrame falls from 12 ms to 0.14 ms, as before.
    - The owner: the 12,000 issue still kicks in.
  - **What arm 4 cannot fix.** On a one-eye submit the stale eye's picture comes from another moment: animation, world motion and the camera's
    position. Only its rotation is corrected. And since the change the mod submits a picture rendered before its wait, so it is shown one frame
    late.
  - **Gap: no counters for most mods.** Black Flag, Mortal Shell 2 and Titanfall 2 VR write no game-exit line: the shim's destructor does not run
    when they quit. Without debug their counters are lost.
- **Next, proposed:**
  1. One counter line per minute in the run log, only when a count moved (kept, mapped, re-stamped, dropped, free waits). A shim log change, so
     every mod session shows what the engine did.
  2. Per-eye columns in the shim's per-frame CSV (released per eye, kept, both eyes' orientations), then one Black Box session of Black Flag past
     12,000. It shows whether the late eye of a one-eye submit comes in the very next submit (a race: holding that frame for the late eye
     would give a consistent pair) or the mod alternates eyes (true AER: nothing more to do on our side).
  3. Aside, not the engine: the launcher threw `ObjectDisposedException` ('LauncherForm') at 19:59:30 on 2026-11-23, right after the Advanced
     toggles.
- **Tested so far:** clang-cl check from WSL, then the 5090 build. No lab row yet: the 3080 Ti was offline. Owed:
  - an AER shape in `xr_modsim` (the stale eye keeps its old pose) with a per-eye picture-position check;
  - the regression set under arm 3.

## 9b. Noted for later: the just-in-time game release (owner, 2026-11-23)

The owner asked to test it again, now that the chain is understood end to end. It is not a gate of this plan. It is an engine step of its own, after
Decision B, and the owner's go comes first.

- **What it is.** The shim holds the game's release until the previous frame is predicted done on the GPU, minus the app's CPU time and a 1 ms lead.
  - The prediction is the last GPU completion plus the frames pending times the GPU time per frame.
  - The app's CPU time is release to submit.
  - The frame the game starts then reaches the GPU as the GPU frees up: one frame in flight, and no idle GPU. With the GPU idle, nothing is held.
  - Built 2026-10-16 (`ac8004a`, IPC v28 `appPacingJit`); removed 2026-10-17 (`e7a8d25`, IPC v30).
- **Why it held nothing then.** It recorded 0 holds in 8,000+ releases. The cause was in VDXR, not the shim: its running start woke the game's
  `xrWaitFrame` a period minus 2 ms after the previous release, whatever the shim did (`docs/PERFORMANCE_FINDINGS.md`, 2026-10-23).
  - VDXR patch 5 turned that early wake off, and the fork keeps it off: `session.cpp:525` at `46deebb`.
  - `ovrshim_vdxr_running_start` = 1 restores the early wake for an A/B.
- **What there is to win.** The rig on 2026-10-23 measured a GPU-bound game with a 21 ms GPU frame: it still took 42 ms from frame start to GPU done.
  - The cause is the shim's rule "at most one frame still completing on the GPU at the release", which amounts to double buffering.
  - The game reads its pose at `xrWaitFrame`, so a hold that works takes up to one GPU frame off pose-to-photon latency.
  - It does nothing for a game that holds the rate.
  - The risk is a misjudged hold: an idle GPU, which means lost fps.
- **What is new since October.**
  - The fork reports each frame's real start and end to the shim (`xrWaitFrame` return, `xrBeginFrame`, `xrEndFrame`), printed in the
    `app frames (VDXR)` line. On the 2026-11-23 OFXR off rows, `xrWaitFrame` return to `xrEndFrame` was 22.3 ms p50, the probe's 22 ms of CPU.
  - So whether the game started when the shim released it can be checked on every frame.
  - The lab reads new frames/s, repeated ticks, arrival and pose age in the 10-40 s window (`display_sim`, `harness/ofxr_report.py`).
- **Step 1, no engine change.**
  - On a GPU-bound row, as the 2026-10-23 rig did (`harness/simrig`, `ab_gate.py`), measure on the current engine:
    - release to `xrWaitFrame` return (about 0 with the fork);
    - frame start to GPU done, against the GPU time per frame.
  - If start to done is already about one GPU frame, there is nothing to win, and the note closes there.
- **Step 2, engine, after the owner's go.** Bring the hold back from `ac8004a` behind a lab switch. Run 3 interleaved pairs with the hold on and off, on a
  GPU-bound row, a row that holds the rate, and `id_mod_dup`; the mod's free waits must stay as they are.
  - Read: app fps, new frames/s, repeated ticks, frame start to GPU done, pose age, and the holds count.
  - Section 6's rule decides. Fps must stay within 2.
  - Then the headset, on a GPU-bound title.

## 10. Risks and open points

- The async id race (section 3): measured by the U1 pairing counters; if it is frequent, pair by order only.
- A layer VDXR rejects before resolving (validation error) sends no facts for that frame: counted as "submits without facts", the pose-only test applies.
- Depth swapchains count as released slices: an app that re-renders only depth would count as new. Harmless (kept, as today).
- The rows run at 90 Hz only; add a 100 Hz row if Phase B passes (the Gears case lives there).
- `XR_FRAME_DISCARDED` and two `xrLocateViews` at one time are not in the matrix yet (the second matters to U3, not to identity).
- One mutex lock in the shim per trace call (about 5 per frame, microseconds), on the app's thread.
- The lab mimic is ours: the matrix proves the shim against these shapes, not against every game. G1 and the headset are the reality checks.

## 11. Assets

- `xr_modsim` shapes: `MODSIM_ARRAY`, `MODSIM_MSAA`, `MODSIM_DUP_RELEASE`, `MODSIM_QUAD`, `MODSIM_PAUSE_S`, `MODSIM_OFXR`, `MODSIM_STROBE`, and the
  late ones from `MODSIM_CHANGE_AT`: `MODSIM_STALE_POSE`, `MODSIM_SAMEPOSE_NEW`, `MODSIM_ONE_EYE`; truth columns in `modsim.csv` (rel, new_pic,
  same_pose, eyes; DRAW rows: each picture's render yaw). `lib.analyse_stamp_truth` + the `identity.stamp` checks; `run.py identity` prints
  stale %, one-eye % and the stamp error before / after. `xr_modsim` is in `tools/wsl/check_cpp.sh` since 2026-11-22.
- `harness/lib.py` `analyse_identity` and the `identity` checks; `run.py identity` (table: truth classes, VDXR vs app, shim seen/dropped, free waits, wait rate).
- `harness/shim_identity_windows.py`: the same comparison on a field shim log (per 900-frame window: nothing released, same poses, dropped, one eye
  released, new pose with nothing released; `--flip N` splits the summary at an app submit count). Checked on the 2026-11-21 logs only.
- `docs/OPENXR_APP_BEHAVIORS.md` (catalogue), `harness/ofxr_report.py`, the OFXR scenarios (2026-11-13, now in `scenarios/identity/`).
- Parked: `private/parked/dup_image_identity_2026-11-13.patch` (commit counting; superseded by the trace channel, kept for reference).
- Tested so far: `xr_modsim` clang-cl syntax check (and a planted error caught); the analysis and checks on synthetic runs (OFXR-like, mod duplicates,
  signal mismatch, no VDXR line, re-released duplicates, paced and spinning pause, free waits, old CSV). Nothing built or run on a builder.
