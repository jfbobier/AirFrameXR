# Lab plan: the VDXR fork, texture-array swapchains, the per-frame facts (branch `vdxr-fork`)

Status: written 2026-11-18 while both builders were unreachable. Nothing here has run. Worktree `/home/jeff/openxr-vdxrfork`
(branch `vdxr-fork` from `1.3.11`), fork worktree `/home/jeff/vdxr-airframexr` (`jfbobier/VirtualDesktop-OpenXR`).

## What is under test

| # | Change | Where | Expected effect |
|---|---|---|---|
| 1 | VDXR from the fork, pin `46deebb`: the five old build-time patches as commits (byte-exact), upstream's two newer commits merged | fork `airframexr` | none (equivalence with what 1.3.10 shipped, which is fork commit `50eda31`) |
| 2 | Dirty gate: no slice copy or commit when the app released nothing | fork `airframexr` | a resubmitted frame costs no copy; a commit always means a new picture |
| 3 | Per-frame facts VDXR -> shim (`ovrshim_Trace`), debug line `app frames (VDXR)` | fork + `ovrshim/driver.cpp` | diagnostic only |
| 4 | Shim: every 2D swapchain image gets an array view, the compose shaders sample `Texture2DArray` at a slice (0 unless the layer flags say otherwise) | `ovrshim/` | single-slice textures: none. Texture-array swapchains (OpenXR `arraySize` 2, single-pass stereo): **suspected broken before** (the shim made a plain 2D view of an array texture, which D3D11 very likely refuses, so the swapchain creation failed); source reading only, never seen in a field log |
| 5 | Experiment: slices above 0 read in place by the shim instead of copied (lab switch `AIRFRAMEXR_VDXR_ARRAY_SLICES=1`) | fork `airframexr-slices` (`dffe237`), shim receiving end in `ovrshim/` | removes one eye-sized copy per frame for array swapchains: estimated 0.05-0.1 ms of GPU, probably under the noise |

Compile status: shim and probe clang-cl (both flavours), shaders `tools/wsl/check_hlsl.sh` (fxc, offsets pinned by `static_assert`),
VDXR `tools/wsl/check_vdxr.sh` (syntax with stubs). Nothing built, linked or run.

## 0. Before the first run

1. `harness/vdxr_fork_preflight.sh` (and `AIRFRAMEXR_PROFILE=5090 harness/vdxr_fork_preflight.sh`): reachable, free disk, what the root holds,
   the fork branches on GitHub. `airframexr-slices` must be pushed before step 5 (the builder fetches commits from GitHub).
2. Builder root. This worktree's `tools/builder.local` / `builder.5090.local` (gitignored) point at dedicated roots `VisionALVR_vdxrfork` /
   `vision-alvr-vdxrfork`, so the lab never moves the VDXR checkout other sessions build from. A new root needs
   `python3 harness/run.py setup` (clones ALVR and the fork, builds everything; budget about 25 GB; the 3080 Ti had 18.7 GB free on C: and no D:
   on 2026-11-13). If the disk does not allow it, use the existing root (`AIRFRAMEXR_BUILDER_ROOT=VisionALVR`) and tell the other session:
   `build vdxr` moves that root's `src\VDXR` to the fork; the 1.3.11 tree's old `stage15_vdxr.ps1` still builds there (its five patches report
   "already patched", HAS_CUBE_LAYERS is defined twice: warning C4005, no /WX) but then ships the fork's VDXR. Back to 1.3.11's VDXR:
   `bash tools/builder.sh run "git -C src\VDXR checkout -f 1a83fec8b5c565b14b06ffa8e1eb7e4768057573"`, then `build vdxr` from the 1.3.11 tree.

## 1. Builds

- **B0, the base runtime** (1.3.10's VDXR + 1.3.11's shim, kept in `out\vdxr-shim-base` for the controls and the A/B):
  1. here: `AIRFRAMEXR_VDXR_COMMIT=50eda3182fb05af9356615db13dad23dbbd6f867 python3 harness/run.py build vdxr` (prints `vdxr: 50eda31... (AirFrameXR fork)`);
  2. a clean 1.3.11 tree: `git -C /home/jeff/openxr worktree add /home/jeff/openxr-1311base 0d7ef3102b8b`, copy this worktree's
     `tools/builder*.local` into it, link `external`, then `python3 harness/run.py build shim` there;
  3. `bash tools/builder.sh run "robocopy out\vdxr-shim out\vdxr-shim-base /E /NFL /NDL /NJH /NJS"`.
- **B1, the branch**: `python3 harness/run.py build vdxr shim probe` here (VDXR at the pin; check the `vdxr: 46deebb...` line). The host and
  client do not change (no IPC change).

## 2. Runs, in order

| Run | Runtime | Scenario(s) | Expected |
|---|---|---|---|
| R1 control | base (`AIRFRAMEXR_APP_RUNTIME=out\vdxr-shim-base`) | `e2e_probe_array` | **FAIL at swapchain creation** if the D3D11 hypothesis holds: `app.log` "FAIL xrCreateSwapchain", the shim's general log "swapchain creation failed (... array 2 ...)". A pass means D3D11 accepted the 2D view and change 4 only matters for slice selection |
| R2 | new | `e2e_probe_array` | PASS: each eye its own image (`eye_colors`), counter and gray patch, `vdxr_trace` 2 slices / 2 released / 1 copied per frame |
| R3 | new | `e2e_probe_array_resubmit` | PASS: half the frames resubmits, copied 0.5 and skipped-as-unchanged 0.5 per frame (the dirty gate), both eyes intact |
| R4 | new | `e2e_probe_msaa` | PASS: 2 slices copied (resolved) per frame, both eyes intact |
| R5 regression | new | `e2e_openxr_probe`, `e2e_layers`, `e2e_modsim_fix_on`, `e2e_modsim_fix_off`, `e2e_modsim_control_ts`, `e2e_baseline`, `e2e_fov_restrict_none` and `e2e_fov_restrict_quality_full` (the probe's checkerboard through the changed compose shader: 1:1 and averaged) | as on 1.3.11 (last full run: `memory harness-full-run-2026-10-27`) |
| R6 A/B | base vs new, 3 interleaved pairs | `e2e_openxr_probe`, `e2e_baseline` | pacing and latency within noise (the rules of `docs/PLAN_UNIVERSAL_OPENXR.md` section 5) |
| R7 only on a regression | stock upstream: `AIRFRAMEXR_VDXR_UNPATCHED=1 python3 harness/run.py build vdxr` | the failing scenario | tells the fork from upstream |
| R8 experiment | `AIRFRAMEXR_VDXR_COMMIT=dffe237c879bebb45972e0f3bcc61141e9cbb2b0 python3 harness/run.py build vdxr` | `e2e_probe_array_inplace` (in `scenarios/optional/`), then 3 interleaved pairs against `e2e_probe_array` | PASS with 1 slice and 0 copies per frame; the GPU-time difference (`gpu.csv`, the shim's compose+GPU) decides whether the in-place path is worth keeping |

After R8: `python3 harness/run.py build vdxr` (back to the pin).

## 3. One field check (5090, after R2-R6 pass)

AC Black Flag with the mod, debug logging on, one session: the `app frames (VDXR)` line answers finding 2 of the universal-plan review.
"resubmitted with no new picture" near half the frames: the mod releases nothing on its duplicate cycle (a release-based duplicate test
can tell its duplicates from new pictures). Near zero: it releases an image each cycle, and that test cannot.

## 4. Merge into 1.3.11 when

R2-R6 pass and R1 is recorded. The experiment (5, R8) does not block: it stays on `airframexr-slices` until its number is known.
Record: `docs/progress.md` (what ran, legend), the numbers of R6/R8 in `docs/PERFORMANCE_FINDINGS.md`, the R1 verdict in
`docs/OPENXR_APP_BEHAVIORS.md` (worktree `openxr-universal`).
