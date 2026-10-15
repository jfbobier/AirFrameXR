# Game motion vectors for frame generation (design note, 2026-10-14)

Status 2026-10-15: recon done, fork built and load-tested (see "Recon" below); consumer and host side not started. Follows docs/FRAME_GENERATION.md (optical-flow generator, lab-verified
2026-10-14) and the OFXR-Bridge analysis. Target games: Unreal Engine titles, native OpenXR and UEVR-injected (the user's main use).

## Why

The optical-flow generator guesses in-scene motion from the pixels after the fact. Every DLSS game already computes the exact
per-pixel geometry motion (the DLSS inputs: motion vectors at render resolution, depth, the jitter offset, a reset flag) and hands it
to NVIDIA's NGX library every frame. Tapping it gives us three things the flow cannot:

1. **Extrapolation instead of interpolation.** Warp the last rendered frame forward by one period (the ASW idea) and send it: no
   waiting for the next frame, so the display period the interpolating generator adds disappears.
2. **Depth-aware reprojection of every frame.** Positional, not only rotational: the picture matches the declared tracking sample in
   translation too. That is also the cleanest answer to the Vision Pro re-stamp question.
3. **No ghosting on hard edges.** The lab found the OFXR confidence drops to zero at any misaligned hard edge (the marker is lost in
   11 % of synthetic frames). Vectors cover geometry exactly. Particles and transparencies are not in the vectors, which is why DLSS
   Frame Generation itself still runs the optical-flow engine alongside; we keep ours for the same reason (hybrid: vectors where
   present, flow elsewhere).

## How OFXR-Bridge does it (read from its code)

An OFXR-aware fork of OptiScaler lives in the game as the DLSS proxy. At each DLSS evaluate call it publishes a struct in-process
(`DlssMotionVectorPublication`: colour output, motion-vector texture, depth, jitter, scale, reset, producer queue, D3D12 resources) to the
OpenXR layer, which synthesises in the game process in D3D12. Off by default, D3D12 games only, the midpoint is solved by three
fixed-point iterations on the vectors (`dlss_motion_vectors.hpp`, `provider_api.cpp`, `optiscaler_bootstrap` in `openxr_layer.cpp`).

## Why not embed OptiScaler

- **Licence.** OptiScaler is GPL-3.0; this project is MIT. A GPL plugin loaded through a narrow ABI and distributed separately is
  defensible; a fork tailored to this host and shipped in the same zip is the grey zone, and the future visionOS client should stay free
  of it.
- **Size.** Thousands of commits, three upscaler backends, GPU spoofing, an overlay, Streamline interposer handling, nightly releases.
  An NVIDIA-only, ini-less fork is a large codebase to keep alive for one tap point.
- **It replaces DLSS's loader.** We do not want to be between the game and DLSS; we only want to see the inputs.

OptiScaler's source stays the reference for the loading tricks and the parameter names (read, not copied).

## Where DLSS can be tapped

The chain in every DLSS game: the game links NVIDIA's NGX SDK statically; at init the SDK loads the driver's NGX core (`_nvngx.dll`
from the driver store, or `nvngx.dll` next to the executable if present and NVIDIA-signed, or unsigned with the `EnableSignatureOverride`
registry key); the core loads the DLSS snippet `nvngx_dlss.dll` next to the executable. Streamline titles (DLSS 3) go through
`sl.interposer.dll` and `sl.dlss.dll`, which call the same NGX core underneath. The game sets its inputs on an NGX parameter object
(`NVSDK_NGX_Parameter_MotionVectors`, `_Depth`, `_Color`, `_Output`, `_Jitter_Offset_X/Y`, `_MV_Scale_X/Y`, `_Reset`, the create-time
flags for inverted depth, jittered vectors, low-resolution vectors) and calls `NVSDK_NGX_D3D12_EvaluateFeature` (or the D3D11 variant)
with the command list, the feature handle and that parameter object. Three places to tap:

| Tap | How | Needs | Attach time | Verdict |
|---|---|---|---|---|
| A. Own `nvngx.dll` pass-through next to `nvngx_dlss.dll` | We implement the NGX core exports and the parameter object, forward everything to the real driver core, read the inputs at evaluate | A file in the game folder, the signature-override registry key (admin, machine-wide), conflicts with DLSS Swapper / OptiScaler users | Game start (before NGX init): fine for native titles | Fallback |
| B. Proxy DLL (`dxgi.dll`, `winmm.dll`, ...) that hooks `LoadLibrary` | OptiScaler's method | A file in the game folder; collides with ReShade and other proxies | Game start | No |
| C. In-process hook of the loaded NGX core at `xrCreateSession` | The shim is already in the game (it is the OpenXR runtime). Find `_nvngx.dll` / `nvngx.dll` among the loaded modules, inline-hook its exported `EvaluateFeature` (D3D12 and D3D11), read the inputs from the parameter object we are handed; also hook `LoadLibrary` for the rare game that loads NGX later | Nothing on disk, no registry; DLSS keeps running NVIDIA's own code | **Late**: works after injection | **Primary** |

C is the one that fits **UEVR**: UEVR injects into a running game whose DLSS was initialised long before, so anything that must be in
place at game start (A, B, or a hook at `xrCreateInstance`) is too late or impossible there, while the XR session is created after
injection. Native Unreal titles create the OpenXR instance before the RHI (so before the DLSS plugin initialises NGX); the late hook
still works there because the module is loaded by the time the session exists. The feature type is known from the parameters present
(colour + motion vectors + depth + output = super resolution; a frame-generation feature has other keys and is ignored). Streamline
titles are caught at the same level, since `sl.dlss.dll` ends in the NGX core.

Hooking a driver DLL in-process is what every overlay and mod does; anti-cheat titles are out of scope for a VR mod anyway. A is kept as
the fallback for a game where the inline hook cannot be placed (hot-patched export, packed core); it is the same code behind a different
entry.

## Design

**Shim side (new `ovrshim/ngxtap.cpp`, MIT).**
- At `xrCreateSession`, if `video.game_motion_vectors` is on (default on, GUI toggle "Use the game's DLSS motion vectors"): locate the
  NGX core module, resolve `NVSDK_NGX_D3D12_EvaluateFeature` and `NVSDK_NGX_D3D11_EvaluateFeature`, install inline trampolines
  (MinHook-sized code, our own; the shim already patches nothing else). Log INFO with the module path and version; WARN and continue
  without vectors if anything fails (the generator falls back to flow alone). Never block or alter the game's call.
- In the hook, per evaluate: read the parameter object (colour input size, motion vectors, depth, jitter, MV scale, reset, and the
  create-time flags cached from a `CreateFeature` hook when available). Record a copy of the motion-vector and depth textures into our
  own shared textures (a small ring per eye, `D3D12_HEAP_FLAG_SHARED` / keyed mutex on D3D11) on the game's queue, signal a shared
  fence, publish a descriptor through the existing IPC (new block: eye index inferred from the order of evaluates per frame and the
  view rectangles, render size, output size, jitter, scale, flags, fence value, frame counter). The game's own resources are never
  retained past the call.
- UEVR renders both eyes through Unreal's native stereo, so the DLSS plugin evaluates per view: two publications per frame, matched to
  the two projection views by order and size. If a game evaluates once on a side-by-side target, the publication says so and the host
  splits it.

**Host side (`nvenc/hostlib/framegen.cpp`).**
- A second motion source next to the flow: per eye, the vectors resampled into the composed frame's eye viewport (scale by output /
  render ratio, remove the jitter delta, flip to our convention), depth likewise. `nvh_fg_stats` reports which source each synthetic
  frame used.
- Synthesis: where vectors exist, a backward warp driven by them (midpoint solve as OFXR for interpolation, straight forward warp for
  extrapolation); where they do not (UI quads composed by the shim, transparencies flagged by a vector/colour disagreement), the flow
  path as today. Depth gives the occlusion order when two sources land on one pixel and a positional component for the re-stamp warp.
- Extrapolation mode (`frame_gen_mode: extrapolate`): the boundary after a rendered frame gets that frame warped forward by one period
  (vectors x 1, head rotation to the newest sample as today); the rendered frame itself is sent at its own boundary. No added period;
  disocclusions are filled by the flow fallback or left as the stretched background (what ASW does).

**Settings and telemetry.** `video.game_motion_vectors` (default on), the generator's `source` counters (vectors / flow / mixed), an
event when the tap attaches or fails, per-game overrides for vector conventions (`config/games/*.json`, matched by executable name:
sign, jittered, low-resolution, inverted depth) seeded from the Unreal DLSS plugin's defaults.

## Unreal specifics (the key target)

- The Unreal DLSS plugin hands NGX render-resolution vectors (low-resolution flag set, scale = render size), inverted infinite depth,
  jittered colour with the jitter offset in the parameters; the "velocity" buffer is converted to the NGX convention by the plugin
  before the call, so no engine-specific decoding is needed.
- UEVR: flat Unreal titles with UEVR injected; DLSS is the game's own (UEVR's stereo paths keep it running per eye). The hook must attach
  at session creation; the shim already runs there. UEVR titles are the first validation set: one D3D12 title with the plugin's defaults,
  then a D3D11 one.
- Unreal titles that ship DLSS through Streamline (newer engine versions) are caught at the NGX-core level as above.

## Plan

1. NGX tap in the shim + IPC publication + a debug view (the vectors rendered as colour next to the frame in the host's debug flavour,
   `host --fg-debug-mv`), validated on one UEVR D3D12 title on the 5090. Toggle wired in the GUI.
2. Vector-driven synthesis replacing the flow where vectors exist; lab check with a probe scene that publishes synthetic vectors
   through the same IPC block (the probe is ours: it can hand exact vectors and depth, which gives the judge a ground truth).
3. Extrapolation mode; latency measured with the lab's arrival-to-send columns; the motion judge extended with the extrapolation's
   expected position.
4. D3D11 tap, Streamline title check, per-game override list.

Effort: several days to step 1 on one title; the pacing, stamping, synthesis pass, encode path and the lab's motion judge all stay.

## Open questions

- Which NGX core exports are hot-patchable on current drivers (export forwarding, guard pages); whether `_nvngx.dll` is loaded at all
  when the user has DLSS off in the game menu (Unreal loads it at startup to query support; other engines may not).
- Vector quality at 50 % render scale (DLSS Performance): the vectors are at render resolution, so the generator's motion field is
  coarser than the output; fine for warping, to be judged.
- Whether to keep the flow at all once vectors are present (particles, transparencies, the UI quads composed by the shim): the hybrid is
  the safe start.
- The future visionOS client could do the warp itself with the vectors and depth streamed as a side channel (the OFXR layer and ASW
  both warp on the display side); the tap and the IPC are the same either way.

## Recon (2026-10-15 night, RTX 5090)

Decision taken with the user: no own NGX tap; a **fork of OptiScaler** (GPL-3.0 kept, separate repository
[VisionALVR-OptiScaler](https://github.com/jfbobier/VisionALVR-OptiScaler), branch `visionalvr`) publishes the DLSS inputs to the shim
in-process, the way OFXR-Bridge's fork does. OFXR's own fork is not public (GitHub code search finds the provider export only in OFXR-Bridge),
so the provider is ours, modelled on OFXR's ABI.

Facts established:
- **Build**: one Visual Studio 2022 project (v143), prebuilt libraries committed, submodules for FidelityFX, XeSS, nvapi, spdlog; `msbuild
  OptiScaler.sln /p:Configuration=Release /p:Platform=x64` works out of the box on the 5090; the DLL (26 MB) lands in `x64\Release\a\`.
- **Loading**: OptiScaler is not named `nvngx.dll`; it is dropped next to the game's executable under a proxy name (`dxgi.dll`, `winmm.dll`,
  `version.dll`, `dbghelp.dll`, `winhttp.dll`, `wininet.dll`) and intercepts the NGX SDK's own lookup of `nvngx.dll` in the app folder, serving
  itself and satisfying the signature check from a signed file. **No registry key.** The real NGX core is loaded from the driver store.
- **Load test**: NVIDIA's DLSS sample app (`ngx_dlss_demo.exe` from the DLSS SDK release) with the DLL as `dxgi.dll` and a pinned ini: D3D11 and
  D3D12 both run DLSS Super Resolution through OptiScaler's DLSS backend (`DLSSFeatureDx11/Dx12`, handle 1000000), ~800 evaluates/s, zero errors.
- **D3D11 answer**: OptiScaler's DLSS backend is native D3D11 (no D3D12 interop), so D3D11 titles publish D3D11 resources directly.
- **Tap point**: `DLSSFeatureDx12::EvaluateInternal` and `DLSSFeatureDx11::EvaluateInternal`, right before the native evaluate: the game's
  parameter object with colour, motion vectors, depth, output, exposure, jitter, MV scale, reset, subrects, plus the feature's flags and sizes,
  and the game's command list / device context.
- **Fork changes** (two commits): `OptiScaler/visionalvr/VaProvider.{h,cpp}` (C ABI `VisionALVR_ProviderVersion`, `VisionALVR_SetConsumerV1`;
  the consumer is called synchronously at every evaluate with a `VisionALVR_PublicationV1`; no allocation, no copy, no blocking; a structured
  exception in the consumer removes it instead of taking the game down), two hook lines, compiled defaults (DLSS backend on D3D11/D3D12/Vulkan,
  overlay off, DXGI spoofing off) so no ini is needed, `VISIONALVR.md`. No stripping yet: make it work, then make it lean.
- **Harness target**: the DLSS sample app is a deterministic DLSS application for the lab (D3D11 and D3D12 by flag); the probe's own DLSS mode
  (direct NGX SDK, `NVIDIA/DLSS` clone, D3D11) remains the ground-truth path.

Next (in order): the shim consumer (find the module by export, register, copy motion vectors and depth into shared textures on the game's
command list / context inside the callback, publish the sidecar over the existing IPC), the host's generator input and shader variant, the
lab scenario with the sample app, then the probe DLSS mode and the PSNR judge.
