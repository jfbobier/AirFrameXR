# Relationship to upstream (how much is forked)

ALVR is fetched at a pinned upstream commit (`deps.lock.json`) and built with small, asserted patches, so an upstream change
fails loudly instead of drifting. VirtualDesktop-OpenXR is the exception since 1.3.11: we own both sides of its libOVR
boundary (VDXR above, OVRShim below), so it is a real fork, upstream plus our commits; upstream changes come in by merge, and
a conflict is the loud failure. Upstream is maintained but slow (two small commits between 2026-09-06 and 2026-10-08).
| Upstream | How it is used | Changes |
|---|---|---|
| **ALVR v20.14.1** (Rust) | `server_core`, protocol, sockets, audio, session crates linked into `alvr_host` as a workspace member; client_core in the test client | one small patch (`tools/patches/loopback-client.patch`): lets the test client bind its own loopback address; inactive unless `ALVR_BIND_IP` is set (it is compiled into `alvr_host.exe` through the shared `sockets` crate) |
| **ALVR encoder C++** (`VideoEncoderNVENC`, `NvEncoder*`, foveated encoding `FFR`, ~3,900 lines + NVIDIA's header) | vendored **unmodified** in `nvenc/upstream/` (checksums) | about 20 asserted build-time text replacements in `tools/host/build.rs`: 10-bit texture format, NVENC SDK 12.2 field names, a struct initializer the newer SDK broke, real error messages, split-frame encoding, QP delta map, reconstructed-frame output, accessors and a config dump |
| **VirtualDesktop-OpenXR** (the OpenXR runtime) | the fork `jfbobier/VirtualDesktop-OpenXR`, branch `airframexr`, built at the commit pinned in `deps.lock.json` (`tools/remote/stage15_vdxr.ps1`); `upstream_hash` there is the upstream commit last merged | our commits (`git log <upstream_hash>..<full_hash>`): per-eye visibility on quad/cylinder/cube layers in "Oculus runtime" mode, all 6 faces of cube swapchains, the runtime's display name, VDXR's own running start off (the game's `xrWaitFrame` waits for the shim's release), the cube-layer extension advertised (these five were build-time text patches until 1.3.10), no slice copy or commit when the app released nothing (texture arrays, MSAA), per-frame facts to OVRShim (`ovrshim_Trace`, `docs/ARCHITECTURE.md`) |
| **VDXR's OVRNull** (sample "null" LibOVR driver) | `ovrshim/` = OVRShim, the one real fork | `driver.cpp` grew several-fold, plus new files (IPC, layer shaders); the rest of OVRNull is used unchanged |

Everything else is new code: the host (`tools/host`, Rust), the encoder library and benchmark
(`nvenc/hostlib`, C++), the GUIs, the installer scripts, the mock client, the OpenXR probe and the harness.

## Taking upstream changes into the VDXR fork

Before each release, and whenever upstream moves:

1. **Look**: `tools/vdxr_upstream_check.sh` lists the commits on upstream `main` since the last merge (`upstream_hash` in `deps.lock.json`) and
   upstream's open pull requests.
2. **Take them in a checkout of the fork** (`jfbobier/VirtualDesktop-OpenXR`, branch `airframexr`): upstream `main` by `git merge upstream/main`, never
   a rebase (our commits keep their hashes, the pins of older releases stay valid); a conflict is resolved in the merge commit. A pull request upstream has
   not merged (third-party, MIT like the rest): `git cherry-pick -x` of its commits, the message naming the PR number and its author; its tests stay out
   if they need upstream's test harness. Each taken change is one commit or merge on a lab branch first, not on `airframexr`.
3. **Measure before pinning**: `AIRFRAMEXR_VDXR_COMMIT=<the new head> python3 harness/run.py build vdxr` (it deploys to `out\vdxr-shim`), then the
   VDXR scenarios interleaved against the pin (`run.py build vdxr` without the variable for the other arm): `e2e_openxr_probe`, `e2e_baseline`,
   `e2e_layers` (composition layers: alpha, cube, cylinder), `e2e_probe_array`, `e2e_probe_msaa`, `e2e_probe_array_resubmit`, each run with
   `AIRFRAMEXR_LAB_IDLE_GPU=10`, compared with `harness/ab_report.py` (no metric worse beyond noise, no check newly failing).
4. **Pin**: push the lab branch's commits onto `airframexr` (the owner's go: it is public), then in this repository `deps.lock.json`: `full_hash` = the
   new head, `upstream_hash` = the upstream commit merged (unchanged for a cherry-picked PR), and the note; one commit with the scenarios' result.
   `tools/remote/stage15_vdxr.ps1`'s header lists our commits: add the new one.

State (2026-11-20): upstream `main` has nothing after `6da20fe` (2026-08-16), merged on 2026-11-18 (`21b2ca0`). Open upstream: PR #53 (jfleezy23, 2026-10-11,
"Reuse alpha preprocessing shader and constant buffer"): VDXR recreates a compute shader and a constant buffer on every alpha correction of a
secondary layer (games with HUD quads: every frame); the PR creates them once. Our fork has the same code; a candidate for step 2.
