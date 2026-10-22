# Automated harness

Everything is driven from WSL: `python3 harness/run.py <command>`. Stdlib Python only.

| Command | What it does |
|---|---|
| `list` | scenarios in `harness/scenarios/*.json` |
| `build [host\|client\|shim\|probe\|gui\|package\|nvenc]` | (re)build on the builder (cargo / MSVC); default `client host` |
| `run <name>...` / `all` | run scenarios; exit 0 all PASS, 1 any FAIL, 2 any BLOCKED, 3 usage |
| `report [dir]` | re-evaluate saved artifacts with the current checks (no builder needed) |
| `bench-quality [quick\|full]` | benchmark phase 2 headless on the builder (`stage17_bench_quality.ps1`) + checks: recon used and matching the input's colour conversion, plausible PSNR, quality ordering (preset, foveation, resolution), proposals, duration |
| `test-zip` | the shipped zip unzipped into a fresh folder on the builder and run from it (`stage18_zip_test.ps1`): single top folder, no logs/user config shipped, logs created on first run, general log appended per run, one debug folder per debug run |
| `test-install` | runs the installer's registry/files logic against a throwaway `HKCU` key (never HKLM), see `docs/INSTALL.md` |

## GUI and desktop-configuration harnesses (0.5)

The GUI has to work on every desktop people have: 1080p laptops at 125-150 %, 1440p at 100-150 %, 4K at 150-200 %, ultrawide and portrait monitors,
HDR on or off. A headset session cannot tell you that, and one PC has one display, so these four harnesses cover the desktop side. All run from WSL
on the builder, stdlib Python only; each prints `CHECK PASS/FAIL` lines and exits 0 / 1 (2 = could not run).

| Harness | What it checks | Run | Needs |
|---|---|---|---|
| `ui_matrix.py` | every window, dialog and wizard page of the GUI at 3 resolutions x the Windows scales people use (1080p and 1440p at 100/125/150 %, 4K at 100/150/200 %): the window fits the work area (screen minus taskbar), no control is clipped or overlapped, no button / label / list header is cut off, lists do not need sideways scrolling. A PNG per case. | `python3 harness/ui_matrix.py` (`--matrix 1920x1080@150`, `--only streamer`) | the builder (it compiles `tools/gui/UiTest.cs` into `uitest.exe` and runs it in the interactive session) |
| `launcher_flow.py` | the launcher steps aside while a tool is open and comes back when every tool is closed, for each of the four tools; the root `VisionALVR.exe` stub starts `bin\launcher.exe` and passes its arguments on | `python3 harness/launcher_flow.py ["Streamer"]` | `build gui package` first |
| `mirror_check.py` | the desktop mirror: the input maths (14 Rust unit tests incl. every desktop shape) and the picture on the builder's GPU for 7 desktop shapes (1080p, 1440p, 4K, 16:10, 21:9, 32:9, portrait) x 4 display modes (SDR; HDR with Windows' SDR white at 80 / 200 / 480 nits): patch colours, where the screen quad lands and that it stays in view, the cursor dot's size at every resolution, the clear colour elsewhere; then the real desktop captured (BLOCKED, not FAIL, where the OS denies desktop duplication). Ends with the by-hand checklist for the headset. | `python3 harness/mirror_check.py` | the builder's GPU (`tools/probe/desktop_probe.cpp`) |
| `advice_check.py` | the Diagnose suggestions (`tools/gui/Advice.cs`, from `docs/PERFORMANCE_KNOBS.md`): every rule against synthetic wizard metrics and fixture debug logs / game records, a healthy setup that must stay quiet, the debug-logging button | `python3 harness/advice_check.py` | `ui_matrix.py` run once (it builds `uitest.exe`) |
| `readme_screens.py` | regenerates the README's screenshots (`docs/images/ui_*.png`) from the UI harness at 1080p / 125 % with demo data; look at them before committing | `python3 harness/readme_screens.py` | the builder |
| `firewall_check.py` | the firewall review (`Firewall.Analyze`) against real rules: throwaway rules `VT-<time>-*` for a dummy `alvr_host.exe` (no rule, one good rule, redundant protocol rules, stale rule, another copy, a blocking rule, a rule for a network that is not in use, a switched-off rule), then `tools/check_firewall_rules.bat` with scripted answers; the rules are removed afterwards | `python3 harness/firewall_check.py` | `ui_matrix.py` run once (it built `uitest.exe`); an elevated session on the builder |

**How `ui_matrix.py` emulates a display.** The laptop's own display scale is not changed. `UiTest.cs` builds each window, shows it off screen, and then scales
it by `target dpi / real dpi` (sizes, margins, fonts) and re-runs the window's own fitting code (`Ui.FitToWorkArea`, `Ui.AutoFitWindow`) against the pretend
work area, so the code under test is the shipped code. List columns follow `Ui.DpiOverride`. This finds layout defects reliably; it is not a substitute for
one look at a real 4K display, and moving a window between monitors of different scale is not covered.

**What these harnesses made the GUI do** (the rules for new GUI code): every window derives `DpiForm` (the designer pattern, so WinForms' own per-monitor
scaling applies: `bin\*.exe.config` + the `TargetFramework` attribute); controls created after the window loaded use `Ui.LateScale`; pixel constants use
`Ui.Px`; list columns are sized from their content (`Ui.FitColumns`); content-sized windows use `Ui.AutoFitWindow` (capped at the work area, the body
scrolls) and fixed-size ones `Ui.FitToWorkArea`; a long text line wraps at 760 design px.

**Builder wrapper limits.** `tools/builder.sh` refuses delete commands (`del`, `rd`, `Remove-Item`, `rmdir`...), and `&` chains only work inside `cmd /c`. The
harnesses therefore write each run to a fresh `runs\<id>` folder and never delete; old ones can be cleared by hand.

**Headset address not fixed** (`python3 harness/find_headset_e2e.py [auto] [button] [unpaired]`): VisionALVR.exe against the mock headset on the PC, started from a fresh copy of `out\dist\VisionALVR`. `auto`: the session pairs the headset at a wrong, unroutable address and the streamer must still connect (ALVR re-finds a trusted headset by name from its announcements). `button`: same start, `VisionALVR.exe --test --find-auto` opens the *Find headset* dialog, which must list the headset at its new address, pair it and restart the streamer. `unpaired`: nothing paired, the dialog finds and pairs it. The headset's announcement is sent by `announce.ps1` (a unicast datagram from 127.0.0.2 to the streamer): a broadcast from the same machine is not delivered, and a datagram to the PC's own LAN address is dropped by the Windows Firewall until `alvr_host.exe` is allowed in that folder (what the wizard's firewall line checks).

Results: `harness/results/<UTC>_<scenario>/` (`result.json`, `analysis.json`, host/client reports, `client.hevc`,
`frames.csv`, logs); `harness/results/latest` points at the newest. A scenario is BLOCKED (not FAIL) when the
infrastructure is missing (builder unreachable, binary or source absent, impossible topology).

## Audio harnesses (2026-10-21)

The game audio has three layers that fail in different ways (Windows' device state, ALVR's capture, the network to the headset), so it is tested from the
system's side with WASAPI, not from the host's counters. All run from WSL on the builder (stdlib Python plus small C# probes compiled on the builder, `harness/probes/`);
each prints `CHECK PASS/FAIL` lines. The Core Audio calls only work in the interactive desktop session, so the helpers run as limited scheduled tasks like the OpenXR app.

| Harness | What it checks | Run | Needs |
|---|---|---|---|
| `audio_chain_check.py` | the whole chain driven by the streamer: `streamer.exe --test` starts the host in a fresh portable copy, the mock headset connects (it plays the received audio into the PC speakers; the host excludes its process, a real headset is another machine) and `xr_probe` renders Littlest Tokyo with its music. Default mode = process loopback: the default output is left alone, the cable carries nothing, the host log says process loopback, the headset's own process loopback hears the music. `--device` = the cable path: the cable is the default for all three roles, carries the music, the speakers are muted at the endpoint. Both: the streamer exits when its window closes, defaults and mute are restored. With a microphone in a quiet room: the client's output is silent while muted and about 10 times louder unmuted (skipped when the muted reading is above rms 0.0015) | `python3 harness/audio_chain_check.py [--device]` | `run.py build client probe gui package`; VB-CABLE on the builder |
| `audio_route_check.py` | the cable path in detail: defaults / mute / 100 % / 2 ch 48 kHz, audio.mute_pc off, a default stolen and the cable muted while running (healed in 7 s), a named device, no cable (`VISIONALVR_FAKE_NO_CABLE=1`), the format fixed from 44.1 kHz, crash recovery | `python3 harness/audio_route_check.py` | `run.py build gui package` |
| `process_loopback_probe.py` | Windows process loopback from plain .NET (`probes/proc_loopback.cs`): include a process, include another (silent), exclude; at the output muted / 10 % / 100 % and at 44.1 / 96 kHz device formats | `python3 harness/process_loopback_probe.py` | builder only |
| `elevated_audio_check.py` | an elevated (high integrity) tone player is heard in include and exclude mode by a normal-token process (UEVR and mods run elevated) | `python3 harness/elevated_audio_check.py` | someone accepts the UAC prompt on the builder |

What the lab taught: a muted output's own loopback is not silent (use the microphone or the endpoint state to prove mute); processes started from the SSH session
live in another logon session and process loopback does not hear them (start the mock headset with `lib.start_limited`); the builder wrapper silently ignores `rmdir` / `del`
(use `robocopy /MIR` for a clean copy); the VB 16ch cable refuses a second loopback client. The scene music: `tools/bench_scene/sakura_breeze.mp3`
(credits in `THIRD_PARTY_NOTICES.md`), played by `xr_probe` when it renders a scene (`XR_PROBE_SOUND=0` silences it, `XR_PROBE_SOUND=<file>` picks another).

## Stability harness (2026-10-22, docs/STABILITY_PLAN.md)

`python3 harness/stability/run_stability.py list | run <name>[,<name>|all] [--seconds N] [--host-env K=V ...] | judge <results dir> <scenario>`: fault injection and soak
scenarios on the real chain (host + shim + probe + mock client on the builder), judged by `harness/stability/judge.py` (`--selftest`). Scenarios are `stability/scenarios.json`
(a base derived from `e2e_fg_latency`, an override, criteria); results go to `harness/results/stability/<UTC>_<name>/` (frames.csv, client_stdout.txt with the injected
faults, host_stdout.txt, mem.csv, ballast.log, soak_plan.json, result.json). Run the **debug** flavour (`run.py build nvenc vdxr host shim probe ballast`).

| Scenario | Fault | Pass means |
|---|---|---|
| `tracking_blackout_frame` / `_fgoff` | the pose uplink silent for 5 s, resumed only after 30 frames arrive (visionOS-like) | frames keep flowing, recovery under 2 s (the `_fgoff` control passes without the fix too) |
| `tracking_blackout_timed` | 0.5 / 5 / 30 s silences that end by themselves | no stall beyond the silence |
| `host_freeze_30s` / `_fgoff` | the whole host process suspended for 30 s (NtSuspendProcess) | back within the freeze + 15 s, the watchdog reports it |
| `client_freeze_5s` / `_30s` | the mock headset frozen (the host drops the connection at 30 s) | back within a few seconds of resuming |
| `reconnect_storm` | 15 client sessions of 8-25 s against one daemon host | every session streams, first frame within 6 s |
| `memory_baseline_fullres` | none: what the chain holds at the Vision Pro's size | no growth, memory events present |
| `vram_cap_loose`, `vram_squeeze`, `ram_squeeze` | the ballast leaves the chain 4 GB / 1.8 GB of VRAM, 2.3 GB of RAM (squeezes released at +95 s) | alive at the end |
| `mv_clean`, `mv_loss`, `mv_churn`, `mv_garbage` | probe vector faults (`XR_PROBE_MV_FAULT`: drop, blackout, churn, resize, recreate, garbage, null) | vectors used, no generator errors, no stall, no growth |
| `soak_tokyo` | `--seconds N` (default 900) of seeded plausible events: re-centre, eye height, gamma, generation off/on and rate, game vectors, debug logging, blackouts, vector faults, host and client freezes | every stall inside an injected freeze, host and probe memory flat |

Machinery: `memory_limit` in a scenario (`vram_cap_mb`, `ram_cap_mb`, `at_s`, `release_s`) starts `tools/stability/vram_ballast.cpp` (built by `run.py build ballast`) so the machine
behaves as if the chain had only that much (the dev PCs have far more than many users); `faults` (`suspend` of a process for N s), `stdin_feed` (host commands at given times),
`reconnect_storm`, `sample_memory` (`memsample.ps1`: RAM, handles, dedicated VRAM per process once a second, `mem.csv`). Mock client flags: `--track-blackout start:dur[:frame]`,
`--track-after-frame`, `--track-frames-needed N`. `--host-env VISIONALVR_FG_DARK_MS=0` turns the dark-tracking fix off (the freeze scenarios then fail: that is how the harness is
known to see the bug).

## Topology
Note: a client on a loopback address makes `server_core` treat it as *wired* and force the **TCP** stream protocol, so loopback
scenarios do not exercise ALVR's UDP stream (the real AVP uses UDP).


**Default (loopback):** host and client both run on the builder, so the full 250 Mbps stream and a real GPU host can be used.
The client is given its own loopback address with `ALVR_BIND_IP=127.0.0.2` (bind + `SO_REUSEADDR`) and the host reaches it by
manual IP. This needs the env-gated patch `tools/patches/loopback-client.patch` on the builder's ALVR checkout (applied
automatically by `python3 harness/run.py build`); without the variable the client behaves exactly like upstream.
Packet loss and latency across a real network are **out of scope** (the headset's Wi-Fi handles that); the thresholds
only guard against loopback regressions.

## Cross-network topology (optional scenarios in `scenarios/optional/`, not part of `all`)
- Host and client **cannot share a machine**: both bind UDP 9943 (`os error 10048`). Discovery broadcasts do not
  cross subnets, so the host is given the client by **pinned hostname + manual IP** (session `client_connections`).
- In the original setup the dev PC's firewall blocked inbound connections from the builder, so these scenarios run
  **host = `alvr_host.exe` from WSL via interop (dev PC), client = builder**. The host needs no GPU while it replays a
  pre-encoded file. Live NVENC on the builder with a client elsewhere needs inbound UDP to the builder.
- Some Windows policies (e.g. Windows Information Protection) block Windows processes from writing to `\\wsl.localhost\...`, so the host works in a
  Windows-local scratch dir (`%LOCALAPPDATA%\Temp\openxr_harness\<run>`, deleted after) and the harness copies the
  report back. Everything else (results, cache, ffmpeg inputs) stays on the WSL filesystem.

## Checks (per scenario, `checks` block)
Protocol: host/client connected, negotiated config (resolution, full range, foveation), codec, timestamp
monotonic/duplicates, pose-match (client timestamp echo), arrival-minus-timestamp p95, fps, delivery ratio (UDP loss).
Stream: ffprobe profile/pix_fmt/size/range. Decode: Windows `ffmpeg.exe -hwaccel d3d11va` on the AMD iGPU (software
ffmpeg fallback), frame count vs received, and **content integrity** (per-frame md5 of every received frame against the
source stream decoded the same way; tolerance = `max_unknown_frame_ratio`, to allow loss-damaged frames).

## Scenarios
- `client_no_host`: negative control (client alone must report "never connected").
- `e2e_loopback_hevc10`: the main regression scenario (250 Mbps, 10-bit HEVC, 7104x3200, 90 fps, loopback on the builder).
- `e2e_live_nvenc` (3552x1600) / `e2e_live_nvenc_fullres` (7104x3200): `alvr_host --live` encodes a stamped pattern with NVENC; the
  harness reads the frame counter back from the decoded client stream and checks it against the host's per-frame log.
- `e2e_openxr_probe`: **the real chain.** An OpenXR app (`tools/probe/xr_probe.cpp`, D3D11) runs against VDXR with our OVRShim as its
  OVR runtime; the shim composes the app's layers into a side-by-side texture shared with `alvr_host --shim`, which encodes it with
  NVENC and streams it to the mock client. Checks: the app's per-frame counter and an sRGB gray patch come out of the decoded
  stream, the head pose the app received equals the yaw the client sent for that frame's timestamp, plus all stream checks.
- `e2e_openxr_input`: controllers: scripted Touch input and poses from the client must equal what the app reads; the app's haptics must reach the client.
- `e2e_green_idle` / `e2e_app_lifecycle`: pure green when no app runs; green -> app -> green when an app starts and exits.
- `e2e_openxr_foveated`: the user's real settings (3552x3200 per eye, foveation on): encoded size must equal ALVR's layout (independent Python
  port of `FFR.cpp`) and the stamp is read back at the positions that port derives from the compress shader.
- `e2e_daemon_reconnect`: one `--daemon` host process serves a headset session, a disconnect, and a second session.
- `e2e_avp_late_views`: the user's session with the mock behaving like the stock visionOS client in its immersive space
  (`--views-after-first-frame`: ViewsConfig only after the first decoded frame). Also checks game-audio capture started, pose-matched
  frame timestamps and the async submit mode.
- `e2e_installed_layout`: the portable folder `out\dist\VisionALVR` copied as a user unzips it; host with `--install-dir --debug`, app against
  only its manifest; checks the general log's key lines and the debug session files.
- `e2e_benchmark_network`: `--benchmark-network 100,200` to the mock client: steps must reach their target bitrate at >= 80 fps.
- `e2e_heavy_app_async` (and `optional/e2e_heavy_app_sync`, `OVRSHIM_SYNC_SUBMIT=1`): xr_probe with a synthetic CPU + GPU load per
  frame (`XR_PROBE_CPU_MS`, `XR_PROBE_GPU_COPIES`) that only fits 90 Hz when the app's CPU and GPU work overlap; laptop: 93.5 vs 65.4 fps.
- optional: `e2e_openxr_foveated_split` (needs a GPU with >= 2 NVENC engines, e.g. the 5090) and `e2e_wslhost_hevc10_30mbps` (host in WSL, client on the builder over the real network) and
  `net_capacity_250mbps` (records what that network can carry).

## Adding a scenario
Copy a JSON file; set `source` (a `.hevc` on the builder, e.g. from `nvenc/` stage6), `host`/`client` args, `checks`.
Sources are fetched into `harness/cache/` once. Regenerate streams with `python3 harness/run.py build nvenc`.

## Cross-network note
Only inbound UDP 5000 is known to be open on the WSL machine. ALVR's stream port is configurable (session `connection.stream_port`), but the
server also opens a TCP control connection to the client on the hardcoded port 9943, which the laptop firewall blocked. A real
WSL-client chain needs one open inbound TCP port plus a small patch making the control port configurable (like `loopback-client.patch`).

## GPU power state matters for timing
With a light synthetic load the laptop GPU drops to P8 (NVENC clock 555 MHz vs 1560 MHz), making encodes ~3x slower. Live scenarios therefore
pass `host.gpu_keepalive: 90` (a background GPU load standing in for the compositor/game). `gpu_sample: true` records `nvidia-smi`
(p-state, clocks, encoder/decoder utilization, power) to `gpu.csv` for every run. Concurrent NVDEC decode does not slow NVENC.

## OpenXR chain details (`e2e_openxr_probe`)
- **Components:** `ovrshim/` (fork of VDXR OVRNull: `driver.cpp` = `HostDriver`, `ipc.h`/`ipc_win.h` = shared-memory contract,
  modified `ReprojectPS.hlsl`/`constantsbuffer.h`), built by `build shim` into `out\vdxr-shim` together with the unmodified VDXR dll.
  The host side is `alvr_host --shim` (`nvh_ipc_*` in `nvenc/hostlib`).
- **Process model:** host, client and app are separate processes. The host and the app are both started as non-elevated scheduled
  tasks in the interactive session (`topology.host_launch: "interactive"`): the OpenXR loader ignores `XR_RUNTIME_JSON` for elevated
  processes, and D3D shared textures do not cross Windows sessions. The IPC objects use the `Global\` namespace (fallback: local).
- **Frame timestamp contract:** the shim remembers the client tracking timestamp of the head pose the app last read and attaches it to the
  frame it composes; the host sends the frame with that timestamp (what the client echoes back).

## Pacing checks
`checks.pacing` (scenarios `e2e_baseline`, `e2e_openxr_probe`): client arrival spacing of the app's frames after `warmup_frames` (`client_p99_ms`, `max_late_frac` = share of gaps over 1.5 periods), and the host's own `pacing` report (`vsync_p99_ms`, `vsync_max_ms`, `intake_p99_ms`). The counter check pairs received frames with host rows in order (the host may skip frame numbers) and needs a constant counter offset.


## Follow-the-frames / follow-the-pose / SteamVR baseline (2026-10-11)
- `python3 harness/run.py pacing <scenario>` then `python3 harness/frames.py <results dir>`: per-frame join of the whole chain (needs a debug build, see docs/DEVELOPMENT.md); scenarios in `scenarios/pacing/exp/` (`ftf_*`, `replay_*`).
- Replay of recorded real head traces (`harness/fixtures/headtraces/*.csv`, cut with `tools/make_headtrace.py`) with `client.replay` + `client.real_views` in a scenario; the hop table in frames.py shows every pose hop.
- `python3 harness/steamvr_baseline.py <trace> --seconds 60`: stock ALVR + SteamVR + the probe + the mock client replaying the trace (needs stock ALVR and SteamVR installed; paths at the top of the script);
  `python3 harness/pose_compare.py <dir>` and `python3 harness/stamp_check.py <dir>` analyse it (and our own replay runs) the same way.
- `python3 harness/steamvr_hmd.py`: the same on a real headset. `python3 harness/live.py run harness/live_variants.json --only probe_light_pred100` runs the probe through our chain on the real headset.

## simrig
`harness/simrig/`: make_profile.py (timing profiles from a real session), judge.py (dots.csv -> verdict, `--selftest`), run_sim.py (scenarios on the PC). See docs/DEVELOPMENT.md.
