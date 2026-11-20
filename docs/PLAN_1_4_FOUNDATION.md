# Plan: 1.4-foundation, the engine before the universal OpenXR attempt

Status: in progress, 2026-11-20. Owner's choices the same day: the mask through the QP map (ALVR's numbers stay 1.4's), and all four optional items
in (F2, D1, S2, E4). Progress per item: section 4. Branch `1.4-foundation` (worktree `/home/jeff/openxr-foundation`), cut from `1.4` = `main` = v1.4.0-pre.1
(`aa38ed3`), with `vdxr-fork` merged (`63e7880`, clean; `tools/wsl/check_cpp.sh` OK). Builder: the 3080 Ti, root `VisionALVR_vdxrfork` on its
D: drive (exFAT) behind the junction `%USERPROFILE%\VisionALVR_vdxrfork` (scp through it checked). Engine edits are allowed on this branch (owner,
2026-11-20). No push, release or merge without asking.

**Scope rule (owner, 2026-11-20): everything that improves the engine logic before the universal OpenXR attempt.** The universal attempt itself
(telling a duplicate from a new picture with VDXR's released-image signal: the identity lab, Phase A/B, U1, the field check G1;
`docs/PLAN_UNIVERSAL_OPENXR.md` on `universal-1.4`) is not in it. `universal-1.4` is rebased onto this branch once the foundation passes.

## 1. What goes in

| # | Item | Engine area | State | Test |
|---|---|---|---|---|
| **V1** | VDXR from our fork, pin `46deebb` (the five old patches as commits, upstream `6da20fe` merged) | fork, `stage15_vdxr.ps1` | merged here | fork lab R1-R6 (`docs/PLAN_VDXR_FORK_LAB.md`), R5 + 1.4's FoV scenarios |
| **V2** | The fork's dirty gate: no slice copy, no commit when the app released nothing | fork `06a8a9e` | in the pin | R3 (`e2e_probe_array_resubmit`) |
| **V3** | The per-frame facts VDXR -> shim (`ovrshim_Trace`), debug line `app frames (VDXR)` (diagnostic only, no decision uses it) | fork `46deebb`, `ovrshim/driver.cpp` | merged here | R2-R4 `vdxr_trace` checks |
| **S1** | Texture-array swapchains: an array view per image, the compose shaders sample `Texture2DArray` at the layer's slice (suspected broken before: a plain 2D view of an array texture) | `ovrshim/` | merged here | R1 (base build: expected to fail at swapchain creation), R2, R4 |
| **S2** | Array slices above 0 read in place by the shim instead of copied by VDXR (removes one eye-sized copy per frame for single-pass stereo apps) | fork `airframexr-slices` `dffe237`, shim receiving end already here | experiment | R8; kept only if the GPU time it saves is above the run-to-run spread (estimate 0.05-0.1 ms) |
| **F1** | Foveation from the mask: when the profile in use has a mask, the encoder gives every 32 x 32 block the lens never shows the highest QP (+51) | `nvenc/hostlib/nvh.cpp` `nvh_build_qp_map`, host, GUI compile | to build | QP map dump against a Python model; `bench-quality` mask off / on; regressions |
| **F2** | Hidden blocks made flat (black) right before NVENC, the same blocks as F1 (one `ClearView` with a list of rectangles after the foveation pass) | `nvenc/hostlib/nvh.cpp` | to build, lab switch, off | `bench-quality`: bits at the same QP and visible-area quality at 275 Mbps, F1 alone vs F1 + F2 |
| **F3** | The compile: mask mode = the encoding radio on Foveated and the profile in use has a mask; writes the per-eye octagon (tangents) for the host; one log line states the effective foveation | `tools/gui` (SessionCompiler, PipelineModel) | to build | `density_check.py`-style compile check, `e2e_session_compiled*` |
| **F4** | The UI: with a mask profile, the Encoding radios read "Foveated from the mask of <profile> (recommended)" / "No foveation (full frame + split encode)", the six numbers shown computed and read-only; the FOV card says the mask drives the encoding | `tools/gui` | to build | `ui_matrix.py`, uitest renders |
| **F5** | One writer for foveation on/off: `video.encode_profile` silently overrode the session's foveation (field 2026-10-12, 2026-10-17); the host logs the effective state and where it came from | host `vision.rs`, compile | to build | host unit test, `e2e_session_compiled*` |
| **G1** | Encoding gamma: a failed read of `session.json` silently fell back to 1.0 (field 2026-11-07: a session ran 1.00 instead of 1.5); read again, then keep the last good value, WARN in the general log | host `main.rs` `read_encoding_gamma` | to build | host unit test |
| **D1** | Debug logging on (the Debug tick, `--debug`: the host's and the shim's debug sessions) cost about 5 ms of encode p95 in the field (2026-10-17: 9.0 against 4.3 ms; `docs/PERFORMANCE_FINDINGS.md`). The universal attempt's lab and field reads need debug on, so find it first. Not the debug build flavour | host, shim | to measure | `e2e_baseline` against `optional/e2e_baseline_debug`, 3 interleaved pairs; then the writers one by one; fix only with a measured cause |
| **E4** | The field of view as one contract per streamer start: IPC 38 without `renderCeiling` (the slider's top is the stream's own size, as with the presets), the live `fov_override` command ignored and said, the lab flags `--fov-override` / `--fov-scale` / `--fov-ceiling` replaced by `--settings <file>` (an installed host's settings file) | IPC, shim, host, GUI, harness | built into the branch (`79f47fe`) | the nine FoV scenarios on `host_settings`, `e2e_fov_override_live` = the command is ignored, `streamer_events.py`, `pipeline_model_check.py` |

### F. Why the mask enters through the QP map, not through ALVR's foveation numbers

ALVR's foveation (`FFR.cpp`, the compress shader) is separable: one compression curve across x, one down y. The mask cuts the rectangle's
**corners**. Every column of the rectangle still has visible pixels in its middle rows (the octagon touches all four edges; the line test keeps
each corner fraction >= 0.4), so no column and no row can be compressed further without compressing visible pixels too. The six numbers
therefore stay what 1.4 computes (Vision Pro: Apple's rate map over the box, `RateMapFoveation.Milder`; other headsets: the session's). They
are now written by the compile in mask mode, never by hand.

The QP map is the encoder's own 2D foveation, one value per 32 x 32 block, built once per encoder creation, nothing per frame. It already inverts
the compress shader per block back to the eye's position from the outer edge (`nvh.cpp:247`), the mask's own convention. F1 adds: a block whose
whole footprint lies beyond one corner line of its eye's octagon gets +51 (the clamp); the -3 / +6 ramp elsewhere is unchanged. Per eye: the
left and right octagons are each eye's own (`tan_left` / `tan_right`, `mask.left` / `mask.right`), the right eye mirrored in the frame as the
map already does. Hidden blocks are judged with the whole footprint (a block on the line stays visible), at the calibrated line: the same rule as
the box edges, which 1.4 already cuts at the line test's lines.

What it can buy, from the plan of 2026-11-20 (Part M): the author's fit hides 38.6 % / 42.2 % of each eye's rectangle, 25.9 % / 29.8 % of the
encoded pixels after the foveation. At the owner's constant 275 Mbps the freed bits go to the visible area (about 1.35 x the bits per visible pixel
if bits follow pixels). F2 decides whether +51 alone leaves bits worth taking: a +51 block of game detail still costs something, a flat one almost
nothing. Risk: with a too tight fit the corners show blocky (F1) or black (F2) during fast head motion, like the box edges today.

No foveation = full frame + split encode, no mask (the QP map is off in that profile, `main.rs:593-595`), as today.

## 2. What stays out, and why

| Item | Why not here |
|---|---|
| The identity lab, Phase A/B, U1, G1 field check, OFXR scenarios | the universal attempt itself (`universal-1.4`) |
| The game visibility mask (shim `GetFovStencil`, IPC 38) | evidence 2026-11-20: UEVR, R.E.A.L. VR and NoMoreFlat never enable `XR_KHR_visibility_mask`; owner's rule: no engine change for the very few games that would. G0 (VDXR's `OpenXR.log` per OpenComposite / native title) can reopen it |
| E6 per-eye boxes | v20 has one eye size and a mirrored foveation; needs an IPC change for a model-only 13 %; phase 2 of the FoV plan |
| HDR FP16 (`hdr-experimental`), JIT hold A2, frame generation items, decoder queue, duplicate timestamps (U3) | no measurement on the input they would fix, or headset-only evidence |
| The lab host stopping on `OpenSharedResource 0x80070057` after a 3-frame app | only the lab's non-daemon host stops; the installed host already reopens the slots and goes on |

## 3. Test plan (the full harness suite)

1. **Setup** `python3 harness/run.py setup` on the D: root (clones the pins, builds everything). exFAT records no owner: the builder's global git
   config has `safe.directory D:/VisionALVR_vdxrfork/*` (added 2026-11-20 for that root only).
2. **Fork lab** in its order: B0 (base runtime `out\vdxr-shim-base`: VDXR `50eda31` + the shim of `aa38ed3`), B1 (this branch), R1-R6, R8.
3. **The features** F1-F5, G1 built, then their own checks (above), then `bench-quality` mask off / on and F2 off / on.
4. **Full suite** on the final build: `python3 harness/run.py all` (every top-level scenario), the optional swapchain / foveation / split
   scenarios by name, `run.py pacing`, the GUI checks (`ui_matrix.py`, `fov_store_check.py`, `fov_profile_check.py`, `density_check.py`,
   `pipeline_model_check.py`; rebuild `uitest.exe` first), `harness/stability/run_stability.py run all`.
5. Known before this branch (judged against, not fixed by it): `e2e_openxr_probe` 3 pacing checks (same on 2026-11-14), `e2e_baseline` pacing
   flaps on the 3080 Ti, `e2e_mirror_desktop` BLOCKED without a duplicable display, `fov_store_check` `over_declared_red`, `tray_check --expiry`
   setup. `e2e_fov_client_box_early` runs for the first time.
6. Results in `docs/progress.md` (legend), numbers in `docs/PERFORMANCE_FINDINGS.md`. The headset checks (corners not seen in head motion, mask
   off / on blind at 275 Mbps) are the owner's, after the lab.

## 4. Progress (legend: `docs/progress.md`)

| Item | State | How it was checked |
|---|---|---|
| Builder | 3080 Ti, root on D: (exFAT): `safe.directory D:/VisionALVR_vdxrfork/*` in the builder's global git config (exFAT records no owner, git refused every repo there); the host build now uploads `third_party/nvof` (a fresh root failed `framegen.cpp`) | setup RESULT OK, every build ok |
| V1-V3, S1 | built (B1 = `612995a`: VDXR at the pin, shim, probe, host, mirror, GUI, package); B0 kept in `out\vdxr-shim-base` (VDXR `50eda31` + the shim of `aa38ed3`) | fork lab: section 3 step 2 |
| R2, R4 | **tested-pass** (B1): `e2e_probe_array` (fork + array views: each eye its own image, the facts 2 slices / 2 released / 1 copied per frame), `e2e_probe_msaa` | `harness/results/20261120T101145Z_*`, `*101834Z_*` |
| R3 | **tested-fail, one check** (B1): `e2e_probe_array_resubmit`'s dirty-gate facts are exactly the plan's (3600 app frames, 1800 resubmitted with no new picture, copied 1800 / skipped as unchanged 1800 slices), but `pose_roundtrip` fails (p95 0.82 rad) and the app runs at 25.8 fps with the shim's vsync falling back to local timeouts (1778 of 3600 waits): the duplicate-cycle pacing problem of the AC Black Flag mod, which the scenario reproduces without the compatibility mode. **Not the fork's**: the base runtime fails it the same way (p95 0.80 rad against 0.81 on the branch, idle GPU, 2026-11-20 12:44-12:52) | `*101510Z_e2e_probe_array_resubmit`, controls `*124*Z_e2e_probe_array_resubmit` |
| R1 (control) | **the suspected bug is refuted**: on the base runtime (1.4's shim, VDXR `50eda31`) `e2e_probe_array` ran to its end (1800 frames, 88.9 fps), each eye its own image; the only failing check is `vdxr_trace_present` (the base VDXR has no facts channel). D3D11 accepted the old shim's plain 2D view of the array texture, so S1 matters for slice selection and the in-place path (S2), not as a crash fix | `harness/results/20261120T100814Z_e2e_probe_array` (3080 Ti) |
| F1 | built, lab-checked on the 3080 Ti (B1) | the C++ map against the Python oracle offline (g++ on the extracted code, 60 random foveations and masks, 693,036 blocks, 0 differ); `e2e_qpmap_mask` (first run, shallow 0.2 cuts): the dumped map equals the oracle (0 of 9150 blocks differ), engine 120 hidden blocks = oracle 120, NVENC in delta mode, the hidden blocks still carry the picture with the fill off (mean luma 84); it failed only the scenario's own 3 % floor (1.3 %), the scenario now cuts 0.4 of each edge (9.7 % expected) |
| F2 | built, lab-checked (B1), off by default | `e2e_qpmap_mask_fill`: the hidden blocks decode black (mean 0.2, p99 2), the visible ones keep the scene (max 255); same 3 % floor note as F1 |
| F3 / F5 | built in B1 | `harness/mask_compile_check.py` 5/5 with the real SessionCompiler (the author's fit: 38.7 % / 42.6 % of the stream eyes hidden, the foveation of 2026-11-20 reproduced: centre 0.603 x 0.548, shift 0.533 / -0.127, edge 1.606 / 2.058, encoded 1632 x 1824) |
| F4 | built in B1 | not yet rendered (uitest renders in the suite) |
| G1 | built in B1 | compile only (the host's unit tests run on the builder) |
| E4 | merged (`79f47fe`), not built yet | check_cpp, check_gui, cargo check; `fov_stream_geometry.py --selftest` PASS |
| Lab conditions | from 11:25 to 12:22 another program kept the builder's GPU 80-98 % busy (88 W) before and during the runs (`gpu.csv`'s first sample): the R3 control, R5 and nearly all of R6 / D1 ran under it and were redone. The harness now waits for an idle GPU on request (`AIRFRAMEXR_LAB_IDLE_GPU=10`, `steps.gpu_idle_gate`) and `ab_report.py` shows each run's start load (`--idle` leaves busy starts out). Also fixed: `run.py build vdxr` did not refresh the app runtime folder (the first R8 measured the pin in both arms); the views oracles did not read 1.4.0-pre.2's `views_placeholder` (e2e_layers failed `layers_analysis`) | `harness/ab_report.py`, `lib.gpu_idle_gate` |
| R5 | **tested-pass** (B1, idle GPU): `e2e_layers` 47/47, `e2e_modsim_control_ts`, `e2e_modsim_fix_off` (base 2/2, branch 1/1); `e2e_modsim_fix_on` flaky in **both** arms (`modsim_spacing_before_switch`: base 21.1 then pass, branch 3.05 against <= 3) | `harness/results/20261120T12*_e2e_modsim_*`, `*125*Z_e2e_layers` |
| R6 | **no difference**: 3 interleaved pairs each, runtime base (1.4) against the branch, same host (B1), idle GPU: every metric within noise (`e2e_openxr_probe` encode p95 9.13 / 9.18 ms, app 88.9 / 89.0 fps; `e2e_baseline` encode p95 11.03 / 10.93, client 85.2 / 86.3 fps). The pacing checks that fail (`pacing_latency_steady`, at times `pacing_no_stall`) fail in both arms, as on this laptop on 2026-11-13 | `private/logs/foundation-lab-2026-11-20/redo_manifest.csv` -> `harness/ab_report.py` |
| D1 | **not reproduced in the lab**: debug on against off, 3 pairs, idle GPU: encode p95 10.99 / 10.93 ms, app 87.9 / 87.9 fps, pace p95 12.84 / 12.74 ms: no cost measurable, so no fix (the plan's rule). The field's 5 ms (2026-10-17) came with a real headset and game; the debug-only writers the lab does not exercise (client CSVs fed by a real headset, NVML at a game's load) stay the suspects | same manifest, arm `d1_debug_logging` |
| Frame generation checks (added 2026-11-20, users saw generation slow down with 1.4) | **tested** (B1): `analyse_fg` + `checks.fg`; `e2e_fg_latency` passes them (89.9 of 90 fps); the preset sweep `optional/e2e_fg_size_*`: 1.3 sizing 89.9 fps, Performance 89.6, Balanced **79.6**, Sharp **48.3** (half rate, 90 Hz, the laptop's encoder the first limit; the generator 113 / 115 / 171 / 150 GPU ms per second); the generator code is unchanged since 1.3.10 | `docs/PERFORMANCE_FINDINGS.md` |
| Masked frame generation, step 1 (the output pass draws the visible polygon) | **tested-pass, kept** (B2 host): 3 pairs, output pass 0.74 -> 0.54 ms, generator 172 -> 153 ms/s, stream 77.2 -> 80.4 fps | `private/logs/foundation-lab-2026-11-20/fgmask_manifest.csv` |
| Masked frame generation, step 2 (optical-flow ROIs) | **tested, no gain, not merged**: 93 % coverage as computed, no engine error, but the search slower (3.79 -> 3.95 ms) | `fgroi_manifest.csv`, branch `1.4-foundation-fgroi` |
| Calibration tool: Xbox controller (added 2026-11-20) | **tested-pass** in the lab through scripted pad events (`e2e_fov_tool_pad`, `_restrict`: edges and corners exact), keyboard tool scenarios still pass, UI layouts pass; **not tested** with a real controller | `pad_batch.log` |
| Lab: shim against host | a host built without its shim after E4 (IPC 38) left the app standalone; `shim_reached_host` now fails such a run | `lib.py` |
| S2 / R8 | **no gain, not merged**: the in-place slices engage (0 copies, 1 slice per frame, each eye its own image) and pass every check, but copy against in place, 3 pairs, idle GPU: compose 1.94 / 1.94 ms, encode p95 9.10 / 9.13, app 88.9 / 88.8 fps. The probe is not GPU-bound at 1760 x 1600: a GPU-bound game might gain, not measured. `airframexr-slices` stays an experiment; the pin stays 46deebb | `private/logs/foundation-lab-2026-11-20/r8_manifest.csv` |
