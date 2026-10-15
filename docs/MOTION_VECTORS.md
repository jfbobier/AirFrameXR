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
- **Smoke test of the fork** (efe4f5f, built on the 5090, log-only ini): the sample app in D3D12 and D3D11 runs DLSS through the DLSS backend with the
  compiled defaults, and the provider reports at every evaluate `stream 1000000 (dx12|dx11) render 1280x710 output 2560x1421 mv true depth true
  flags 0x3` (IsHDR, MVLowRes), ~1000 evaluates/s, zero errors, no consumer registered yet. Built DLL kept at private/dist/VisionALVR-OptiScaler-efe4f5f.dll.
- **Harness target**: the DLSS sample app is a deterministic DLSS application for the lab (D3D11 and D3D12 by flag); the probe's own DLSS mode
  (direct NGX SDK, `NVIDIA/DLSS` clone, D3D11) remains the ground-truth path.

## Step 1: the tap in the shim and the host (2026-10-15)

Implemented (IPC version 26):
- **Shim consumer** `ovrshim/ngxtap.h` (MIT, header-only): finds the provider among the loaded modules by its export
  (`VisionALVR_SetConsumerV1`), checks the ABI version, registers. At every DLSS evaluate, inside the call: a copy of the motion vectors and the
  depth recorded on the game's own command list (D3D12: committed resources with `D3D12_HEAP_FLAG_SHARED` and simultaneous access, opened on
  the shim's D3D11 submission device through the NT handle; a transition barrier around the copy with the state DLSS requires of its inputs,
  `OVRSHIM_MV_STATE=<int>` overrides it like OptiScaler's `MVResourceBarrier`) or context (D3D11: shared textures on the game's device). Three
  copies per stream. Depth copies use the typeless twin of the depth format. At SubmitFrame the newest copy of each eye (streams ordered by
  first appearance: Unreal evaluates the left view first) is copied once more into the frame's slot companions, so the slot's GPU-done event
  covers vectors and picture together. The attach is retried every two seconds while the host has `gameMotionVectors` set; `OVRSHIM_MV_OFF=1`
  is the kill switch. Streams idle for two seconds are forgotten (level change).
- **IPC** (`ovrshim/ipc.h`): `HostToShim::gameMotionVectors` (settings `video.game_motion_vectors`, default on), `ShimToHost::motion[slot]`
  (`MotionSlot`: per eye the handles, formats, sizes, render/output sizes, subrect bases, jitter, MV scale, flags, reset, the stream's frame
  counter), `motionTap` state, `motionStreams`, `motionPublications`.
- **Host** (`nvenc/hostlib/nvh.cpp`, `framegen.cpp`): `nvh_fg_ingest` attaches the slot's vectors (and depth) to the frame it just took into
  the history (`FrameGen::IngestVectors`: own copies per history frame and eye, `FrameGenVectors` parameters); `nvh_fg_stats` gained an `mv`
  object (frames, eyes, depth, errors, size, format, flags, api, scale); the host logs the tap's state changes (`motion_tap` event);
  `VISIONALVR_FG_MV_DUMP=<dir>` writes the first vector ingests as `mv_<n>_eye<e>.ppm` (R/G = 128 + 4 x vector) with a `.txt` sidecar.
  The synthesis does not use the vectors yet (step 2).
- **Lab**: the probe stands in for the DLSS provider (`XR_PROBE_MV=synthetic`: it exports the provider ABI itself and publishes a known
  vector pattern and a cleared depth target per eye and frame on its immediate context, D3D11); simrig scenario `steady_mv` runs it with
  the generator on and fetches the dumps.

## Step 2: synthesis from the vectors (2026-10-15, lab)

In the generator's output pass (`framegen.cpp`), per eye, when the frame carries vectors:
- `PSVec`: per vector texel, the **residual backward flow** in full-resolution pixels: the NGX vector gives the pixel's position in the
  previous frame A (`uvA = uvB + mv * scale / size`); that direction is rotated into B's camera (the transpose of the frame-to-frame rotation
  the flow path already uses) and projected; the displacement from there to the pixel is the in-scene motion with the head rotation removed,
  the same quantity the optical flow estimates. Invalid (outside the view, NaN, over the clamp) is flagged.
- `PSDilate` (horizontal then vertical): the strongest valid residual within 1 % of the vectors' height. Exact vectors cover only the
  object's own pixels in B, nothing around it: a pixel the object moves over at the midpoint can lie outside its rectangle in B. A plain
  pull warp therefore thinned the lab's 5 px marker and biased it toward B (hf 0.05 -> 0.08 deg, frozen 2.3 -> 4.3 %). The dilated field
  offers the nearby mover as a candidate source, accepted when its own vector lands on the output pixel within a pixel (what a forward
  splat does); the pixel's own vector is the baseline. (A sparse 17-tap neighbourhood search was tried first: it missed thin movers and
  cost 1.8 ms.)
- `PSOut`: `vecSource` replaces `flowAt` where a vector exists; the flow stays for everything else (UI quads, no-vector games); the
  colour-disagreement confidence stays, the engine's cost term does not apply to vectors. `VISIONALVR_FG_USE_MV=0` ingests but ignores
  the vectors (A/B). Stats `mv.used_eyes`.

Lab (3080 Ti, 40 s each, the probe's exact vectors `XR_PROBE_MV=truth`, 50-70 fps app on the 100 Hz display, moving marker):

| scenario | source | glitches | hf rms (deg) | frozen % | marker visible % | synth ms |
|---|---|---|---|---|---|---|
| stutter_fg | flow | 9 / 0 | 0.050 / 0.050 | 2.3 | 88.8 / 91.2 | 0.22 |
| stutter_mv | vectors | 0 / 0 | 0.055 / 0.056 | 3.1 | 98.6 / 98.6 | 0.73 |
| stutter_turn_fg | flow | 0 / 0 | 0.078 / 0.088 | 3.1 | 23.4 / 23.8 | |
| stutter_turn_mv | vectors | 0 / 0 | 0.038 / 0.039 | 0.8 | 33.1 / 33.1 | 0.43 |

Both SMOOTH. With vectors: no glitches, the marker is found in nearly every frame, and with the head turning the smoothness doubles
and frozen periods drop to a quarter; head still, the smoothness is the same within noise. The probe's vectors are exact, so this is
the ceiling of the method on this scene; a game's vectors carry no transparency or particle motion (the flow fallback covers them).

## Step 3: the first real-game night, and the shared-copy fix (2026-10-15 / 16, RTX 5090)

The tap attached in every DLSS title the user ran (Lies of P, Mortal Shell II, Carma, Valor Mortis, The Sinking City 2 through
`dxgi.dll` next to the UEVR game's exe; Dead Space through `WINMM.dll` / `VERSION.dll`), saw the evaluates (one stream per eye, D3D12,
R16G16_FLOAT vectors at the output size, R32G8X24_TYPELESS depth at the render size, flags 0x49) and **copied nothing**: every copy failed
with E_INVALIDARG, so no stream was ever kept, `mv.frames` stayed 0 all night and the generator ran on optical flow in every game. The
"vectors" sessions the user judged artefact-free were flow. Two defects, both fixed on the branch:

1. **D3D11 opens a D3D12 shared texture only if it was created with `D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET`** (plus simultaneous access).
   The tap created the shared copy with simultaneous access alone, which `CreateCommittedResource` and `CreateSharedHandle` accept and
   `ID3D11Device1::OpenSharedResource1` rejects. Pinned with `tools/interop_test` on the 5090 (every flag combination; the render-target one
   is the only one that opens), then the whole path verified bit-exact there: upload a pattern into a UE-like source (RT | UAV flags, left in
   NON_PIXEL_SHADER_RESOURCE), copy on a D3D12 list into the shared RT | simultaneous-access texture, open it in D3D11, read it back. A render
   target cannot be a depth or typeless-depth format, so depth is now copied into a typed colour twin: D32 -> R32_FLOAT, D16 -> R16_UNORM,
   the packed depth-stencil formats as their depth plane (plane 0 via `CopyTextureRegion`, verified for D32_FLOAT_S8X24 -> R32_FLOAT) on
   D3D12; on D3D11 a cross-family copy is not allowed, so packed depth-stencil is skipped there (one log line). Typed copies also spare the
   host a typed view. The lab never exercised this: the probe's synthetic provider is D3D11 (`p.api = 11`), so `Copy12` met its first real
   D3D12 resource in a game.
2. A stream whose copies failed published nothing, so `Harvest` dropped it as idle and the next evaluate recreated it and logged the failure
   again: three lines per evaluate, 23 000 lines in Lies of P's shim log. A stream is now marked seen at every publication, copied or not;
   a failed source is logged once; the 900-frame summary line counts `sources not copied`.

The failure line now names the step (`shared copy failed at OpenSharedResource1 0x80070057`). What the next session must show, per DLSS game:
shim log `motion vectors: vectors WxH format 34 -> shared copy (format 34)` and `N DLSS stream(s) ... 0 sources not copied`, host `fg_perf`
/ stats `mv.frames` and `used_eyes` rising, and the headset verdict with the tick on versus off (first real A/B of vectors against flow).

Also learned that night: Luke Ross's mods (Dead Space, Cyberpunk 2077, Avatar) crash with the fork DLL under any proxy name, since the mod
hooks NGX itself for its own DLSS use; titles with anti-cheat reject the DLL. Vectors are therefore a UEVR (and native DLSS game) feature.

Next: the real-game vector verdict, then extrapolation mode and the depth-aware re-stamp.
