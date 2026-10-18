# VisionALVR-OptiScaler

**A technical fork of [OptiScaler](https://github.com/optiscaler/OptiScaler), made for one job: handing a game's DLSS motion vectors to
[VisionALVR](https://github.com/jfbobier/vision-alvr) from inside the rendering pipeline, so its frame generation moves the scene with the
game's own vectors instead of measuring the motion afterwards.**

This is not a new upscaler mod and not an OptiScaler release. Everything that makes OptiScaler what it is, the upscaler swapping, the frame
generation bridges, the menus, the years of per-game knowledge, is the work of the OptiScaler team. If you use OptiScaler for what OptiScaler
does, go to **[the real project](https://github.com/optiscaler/OptiScaler)**, read its wiki, and if you want to support anyone, support
**them**: [GitHub Sponsors](https://github.com/sponsors/cdozdil?frequency=one-time) · [Buy Me a Coffee](https://buymeacoffee.com/nitec).
Nothing here asks for money and nobody here deserves OptiScaler's credit.

## What this fork changes

Branch `visionalvr`, GPL-3.0 unchanged, upstream source otherwise intact:

- `OptiScaler/visionalvr/VaProvider.{h,cpp}`: a small C ABI (`VisionALVR_ProviderVersion`, `VisionALVR_SetConsumerV1`) that calls a
  registered consumer, synchronously, at every DLSS Super Resolution evaluate (D3D11 and D3D12) with the game's resources and parameters:
  motion vectors, depth, colour, jitter, scale, flags. No allocation, no copy, no blocking; without a consumer it costs a few parameter reads.
- Two hook lines in `upscalers/dlss/DLSSFeature_Dx11.cpp` and `DLSSFeature_Dx12.cpp`, right before the native evaluate. DLSS itself keeps
  running NVIDIA's own code through OptiScaler's DLSS backend; the picture the game produces is unchanged.
- Compiled defaults so that no `OptiScaler.ini` is needed: DLSS backend on every API, overlay menu off, DXGI spoofing off. An ini next to the
  game still overrides them, exactly as upstream.

The consumer lives in VisionALVR's OpenXR runtime, in the same process as the game. Without VisionALVR this DLL behaves like OptiScaler
with the defaults above.

## Install (VisionALVR users)

1. Download `VisionALVR-OptiScaler-<commit>.dll` from this repository's **Releases** page.
2. Copy it next to the game's real executable (Unreal titles: `<Game>\Binaries\Win64\`, where `*-Win64-Shipping.exe` lives) and rename it
   to one of OptiScaler's proxy names: `dxgi.dll` is the usual choice; `winmm.dll`, `version.dll`, `dbghelp.dll`, `winhttp.dll` or
   `wininet.dll` when another mod already owns `dxgi.dll`. One file, no registry change, no ini.
3. The game must run DLSS Super Resolution (any quality mode, DLAA included) for vectors to exist. In VisionALVR's streamer window tick
   **DLSS motion vectors**; the shim log then reads `motion vectors: attached to ...dxgi.dll`, then `vectors WxH ... -> shared copy` and
   `N DLSS stream(s) ... 0 sources not copied`. "attached" alone means the DLL is loaded, not that vectors flow. Delete the file to uninstall.

A game folder that already holds stock OptiScaler works with VisionALVR as before, without the vectors (frame generation then uses the GPU's
optical flow); replace the DLL with this one to get them. OptiScaler's own frame generation must stay off: two generators would fight.

Known incompatibilities: Luke Ross's R.E.A.L. VR mods hook NGX for their own DLSS use and crash with this DLL under any proxy name; titles
with kernel anti-cheat reject it (as they do upstream OptiScaler); Unity's own DLSS plugin does not go through it. Flat-to-VR mods that do
not touch DLSS (UEVR) and native DLSS titles work.

## Build

Visual Studio 2022, `git clone --recursive -b visionalvr`, `msbuild OptiScaler.sln /p:Configuration=Release /p:Platform=x64`; the DLL lands
in `x64\Release\a\OptiScaler.dll`. The source of every released DLL is the tagged commit it is named after. For everything else, OptiScaler's
own documentation applies: [README](https://github.com/optiscaler/OptiScaler#readme), [wiki](https://github.com/optiscaler/OptiScaler/wiki),
[Config.md](https://github.com/optiscaler/OptiScaler/blob/master/Config.md). Licence: GPL-3.0, as upstream.
