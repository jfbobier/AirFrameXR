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
