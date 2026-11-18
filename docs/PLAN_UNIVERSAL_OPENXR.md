# Plan: a universal OpenXR frame identity (v2, 2026-11-19)

Status: plan, nothing run. Branch `universal-openxr-2` (worktree `/home/jeff/openxr-universal2`), cut from `vdxr-fork` (the VDXR fork and its per-frame
facts, `docs/PLAN_VDXR_FORK_LAB.md`). Supersedes the 2026-11-13 draft (worktree `/home/jeff/openxr-universal`, uncommitted there, left as it was) after the
review of 2026-11-18. Rules: engine edits on this branch only and only after the owner confirms at the start of Phase B; no push, release or merge without
asking; every engine arm behind one lab switch, default = today's behaviour; the product keeps its defaults.

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

1. **G0, the foundation:** the VDXR fork lab R1-R6 passes (`docs/PLAN_VDXR_FORK_LAB.md`). Phase A needs only its B1 build.
2. **Phase A (no engine change):** the identity matrix on the B1 build. Decision A: is VDXR's signal exact on every shape?
3. **G1, the field check** (fork lab plan section 3, 5090, AC Black Flag with the mod, one session): does the mod's duplicate cycle release an image?
4. **Phase B (engine, after the owner confirms):** arm U1 on this branch, the matrix again, regressions, timing A/B. Decision B.
5. **Headset A/B** (section 9) only if Phase B passes.

Before Phase B: `vdxr-fork` merged into `1.3.11`, then `1.3.11` merged here (from 1.3.10 to 1.3.11 the engine changed only in `tools/host/src/logs.rs` and
`watchdog.rs`, nothing on the frame path, so Phase A results carry over).

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

Build: the fork lab's B1 (VDXR at the pin, shim from `vdxr-fork`), then `python3 harness/run.py build probe` from this worktree (adds the `xr_modsim`
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
  Arm 0 must behave exactly like 1.3.11: run the matrix on 1.3.11's shim and on arm 0, same decisions on every row.

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

Client 1.3.11 on the Vision Pro, the user on the 5090: OFXR on a real title, then AC Black Flag with the mod. Arm switched live with the stdin command
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
