# Plan: 1.4-foundation, the engine before the universal OpenXR attempt

Status: proposal, 2026-11-20. Branch `1.4-foundation` (worktree `/home/jeff/openxr-foundation`), cut from `1.4` = `main` = v1.4.0-pre.1
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
| **D1** | The debug build costs about 5 ms per frame (`docs/PERFORMANCE_FINDINGS.md`; NVML sampler or event writer suspected). Every lab number of the universal attempt is taken on a debug build, so find it first | host | to investigate | `e2e_baseline` debug vs release, then the cause; fix only with a measured cause |

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
| E4 cleanup (`renderCeiling`, the `fov_override` stdin command, `--fov-override` / `--fov-scale`) | nine FoV regressions, `lib.py`, `density_check.py` and `streamer_events.py` use them: rewriting them now removes the baseline this branch is judged against. Next round |
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
