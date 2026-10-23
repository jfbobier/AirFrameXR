# VisionALVR: install, use, logs (0.5)

VisionALVR is **portable**, like ALVR and UEVR: one folder you unzip wherever you want. Nothing goes to Program Files; the only
system changes are the OpenXR runtime registration and one firewall rule, both undone by the launcher's OpenXR runtime button ("Switch back").
Requirements: Windows 10/11, an NVIDIA GPU with a current driver (RTX 40 series or newer recommended), the ALVR app (20.14.x) on the Vision Pro, same LAN.

## The folder

| Path | What it is |
|---|---|
| `VisionALVR.exe` | **the only file in the root**: a tiny stub (no window of its own) that starts `bin\launcher.exe` |
| `bin\launcher.exe` | the launcher: a card per tool, the OpenXR runtime switch, the OpenXR layers and firewall reviews, Help; steps aside while a tool is open |
| `bin\*.exe.config` | one per GUI exe: WinForms' own per-monitor DPI mode (a window follows its monitor's scale) |
| `bin\streamer.exe` | the streamer window: keep it open while you play (starts the streamer, shows status, display / frame generation / mirror switches) |
| `bin\configure.exe` | the setup wizard (pair the headset, tests, settings); `--diagnose` runs the same checks on your settings as they are |
| `bin\performance.exe` | the games you played: per-session numbers, the DLSS motion-vector DLL of each game, export of the logs |
| `bin\alvr_host.exe` | the streamer proper (ALVR server_core + NVENC); started by streamer.exe, no window |
| `bin\virtualdesktop-openxr.dll`, `.json`, `LibOVRRT64_1.dll` | the OpenXR runtime games load (VDXR + VisionALVR's OVR shim; VDXR loads the shim from its own folder) |
| `bin\register_openxr_runtime.ps1` | what the launcher's runtime switch runs (UAC prompt): make this folder the Windows OpenXR runtime / put the previous one back |
| `DLSSMotionVectors\dxgi.dll` | the DLSS motion-vector extractor (GPL-3.0); copied next to a game's exe from the Logs window, never loaded from here |
| `bench\`, `profiles\` | the benchmark scene ("Littlest Tokyo" by Glen Fox, CC-BY-4.0) and the scene app; community headset profiles |
| `docs\` | `README.txt`, `THIRD_PARTY_NOTICES.md`, `licenses\` (ALVR, VirtualDesktop-OpenXR, the Rust crates, the scene) |
| `config\session.default.json` | shipped ALVR settings; `config\session.json` (yours), `config\visionalvr.json` and `config\games\<exe>.json` are created on first use |
| `logs\` | created on first use (see below) |

Build it with `python3 harness/run.py build gui package` (on the builder: `out\dist\VisionALVR` and `out\dist\VisionALVR-0.1-alpha.zip`;
the zip holds exactly the files above under one `VisionALVR\` folder, never logs or user config).
`python3 harness/run.py test-zip` unzips it into a fresh folder on the builder and runs from there (logs created on first run,
general log appended by every run, one `logs\debug\<time>\` per debug run, config seeded, benchmark finds its scene).

## Steps
configure.exe is a setup wizard: it checks the PC (NVIDIA GPU with a driver that supports NVENC API 12.2+), finds and pairs the headset (a paired or fixed-IP headset is listed at once; or add its IP), connects to read what the headset supports, then offers one recommended way to configure (the matching **community profile**, else **manual configuration** pre-filled from what the headset reports) and, under *Other ways to set up*, the rest: an imported profile, or **an existing ALVR configuration** (`config\session.json` and `visionalvr.json` used as they are; *Import my working ALVR session.json* asks for your ALVR folder, finds the file and keeps a backup of the current one). It then tests the encoder, the network, a short scene with your head movement and the split encoding, and shows the verdict with the top suggestions (every measurement is under *Details*). The launcher's *Advanced (full ALVR settings)* opens `configure.exe --advanced`, which has the same import.

**The headset's address is not fixed.** VisionALVR dials the address saved at pairing first, and ALVR re-finds a paired headset by its name on the network on its own (mDNS for the Vision Pro, UDP broadcasts for Quest). If it still does not connect (new address that was not picked up, another headset, a reinstalled app), click **Find headset...** in the VisionALVR window: it stops the streamer, lists what the network shows (a headset at a new address is selected for you), pairs the one you pick and starts the streamer again. A fixed address can also be typed there for an already paired headset.

**Quest:** the Meta store version of the ALVR app is 20.14.0; the protocol id only depends on the major version (20), and ALVR's own diff from 20.14.0 to 20.14.1 touches only audio-device handling and the dashboard (no client/server packet or stream change), so it connects to this streamer (not yet tried on a real Quest 3 at the time of writing). It appears in the list as untrusted until you pick it once.

1. Unzip. 2. Double-click `VisionALVR.exe`, click **Switch to VisionALVR** in the OpenXR runtime row (administrator prompt; moved the folder? click it again from the new place; the same button switches back). 3. **Setup wizard**:
open the ALVR app on the Vision Pro, select it, **Pair**; check the settings; turn on the ALVR app's chroma key (the idle frame, "colour when no
game runs", defaults to #107C10, the colour that app keys). 4. **Streamer**, then start an OpenXR game. Close SteamVR and ALVR's own launcher first (same ports).

**Passthrough:** `video.passthrough` is enabled by default with an RGB chroma key on the idle colour (#107C10, threshold 50, feathering 0.2), in the default session and in every shipped profile. The Vision Pro app has its own chroma-key setting and ignores it; the Quest app (and other ALVR clients with passthrough) use it, so the idle green frame is transparent there without touching the ALVR dashboard.

**A plain green screen on connecting is normal**: it is the idle frame while no game runs; turn on **Enable Chroma Keyed Passthrough** in the ALVR app and it turns transparent: the idle colour is #107C10, the key colour the App Store app uses and cannot change (an ALVR app with a Chroma Key Color picker: set #107C10 there, or change the idle colour in the streamer to match). Network advice (a Wi-Fi 6 access point in dumb-AP mode on 5 GHz with a 160 MHz channel for the first Vision Pro, PC on Ethernet, fixed IP by MAC): see the
README's "Recommended network setup".

VisionALVR.exe needs an NVIDIA GPU whose driver supports NVENC API 12.2 or newer (any current driver) and refuses to start otherwise. Older RTX/GTX cards work, with one NVENC chip, so no split encoding and a lower resolution/bitrate ceiling.

## Settings: what is used
`configure.exe` edits only what VisionALVR uses; everything else stays in `config\session.json` for hand editing.
- ALVR (session.json): resolution per eye, refresh rate, constant bitrate, foveated encoding + its geometry, game audio + mute
  PC speakers, stream protocol (UDP/TCP), packet size, haptics strength. ALVR's controller section IS used (we deliver ALVR's own
  Quest/Touch emulation: button mapping, haptics curve, controller offsets), as is its network section.
- VisionALVR (visionalvr.json): encode profile (`foveated` or `full-split`), idle colour, debug default, display gamma. The NVENC
- **Frame queue**: the host sends every frame the game finishes, in order, and keeps a one-frame buffer (about one display period of latency). This costs a little latency, and it never throws a frame away when the game's frame times are uneven, which keeps motion consistent in demanding games. (There is no low-latency mode: dropping the newer or older frame of a pair lost 15-30% of the frames in GPU-bound games.)
  encoder parameters are fixed (`tools/host/src/nvidia_profile.rs`).
- Not used: SteamVR/OpenVR options, color correction / sharpening (ALVR compositor features we bypass), wired/ADB.

## Benchmarks (configure.exe, optional)
- **Encoder test** (no headset): 300 frames of each encode profile at the configured resolution and bitrate: p50/p95 ms and
  whether it fits the frame time.
- **Network test** (headset paired, ALVR app open; it shows noise): random frames (16-pixel random blocks + fine noise, so every
  frame costs the full bitrate and rate control can reach targets from ~100 to 500 Mbps) at each bitrate step, 2 s settle + N s
  measured with ALVR's own client statistics: delivered Mbps, headset fps, packets lost/s, total latency split into encode /
  network / decode. Recommendation = highest step that kept the fps, lost nothing and did not raise the network latency, minus
  10%. A GPU keep-alive load runs during the test (an idle GPU downclocks NVENC).

- **Quality benchmark** (no headset, ~75 s; `alvr_host --benchmark-quality`, configure.exe's wizard comes with S3): the shipped
  scene `bench\littlest_tokyo.vab` ("Littlest Tokyo" by Glen Fox, CC-BY-4.0, inside a textured room) through ALVR's foveation and
  NVENC; NVENC's reconstructed frame is compared on the headset's display grid with the native render. ~30 configurations
  (preset, resolution, foveation, AQ, split): encode time, bitrate, slices, PSNR; three proposals in `config\benchmark_quality.json`.

## Logs
| File | When | Content |
|---|---|---|
| `logs\VisionALVR.log` | always (appended; > 10 MB rotates to .1) | one line per fact from every component (`[host]`, `[shim:<game>.exe]`, `[gui]`, `[configure]`): start/stop, GPU, paired headset, headset connected/disconnected, negotiated stream, game start (exe, size, FoV, gamma, IPC) and end (duration, rendered/streamed fps), settings saved, benchmark results, every warning and error |
| `logs\alvr.log` | debug off | ALVR server_core's own log for the last run |
| `logs\debug\<yyyymmdd-hhmmss>\` | debug on (one folder per streamer run) | `host_events.jsonl` (every event: encoder_stats every 2 s incl. ALVR's client statistics, status every 10 s, ...), `alvr.log`, `gpu.csv` (NVML 1 Hz: utilization of GPU/encoder/decoder, clocks, power, temperature, P-state, VRAM, clock event reasons), `shim_<game>_<pid>.log` (layer layouts, pacing counters, swapchains) |

Report a problem with `logs\VisionALVR.log` + the matching `logs\debug\<time>\` folder. PresentMon is not used: OpenXR games do
not present through DXGI here, the host measures the game's real frame rate itself (`app_fps`, `app_frame_interval_ms`).

## Tested vs untested (harness, laptop builder, mock client)
Tested: the portable folder end to end (`e2e_installed_layout`: host with `--install-dir` and debug, the app against only the
folder's manifest, the general log's key lines and the debug folder's files), the registration script against a throwaway HKCU
key (`test-install`: register, re-register, unregister, foreign runtime, moved folder, taking over ClearXR / Program Files
installs), the network benchmark (`e2e_benchmark_network`), the GUIs build and render (screenshots), the encoder test.
**Not tested:** the real HKLM write and firewall rule, clicking through the GUIs with a real headset, the GPU refusal on a real
older GPU (the builder's RTX 30 laptop was used with the override).
