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
| `ui_matrix.py` | every window, dialog and wizard page of the GUI at 3 resolutions x the Windows scales people use (1080p and 1440p at 100/125/150 %, 4K at 100/150/200 %): the window fits the work area (screen minus taskbar), no control is clipped or overlapped, no button / label / list header is cut off, lists do not need sideways scrolling. A PNG per case. Warnings (a sideways scroll bar, a list wider than its page) are listed with the control that reaches furthest; every scrolling container is re-measured before its audit because the scale is emulated by resizing. 2026-10-27: 0 FAIL, 2 warnings (32 before), both at 4K / 200 % (the wizard's result page and the settings benchmark tab: the refitted window is narrower than their 780 px boxes). | `python3 harness/ui_matrix.py` (`--matrix 1920x1080@150`, `--only streamer`) | the builder (it compiles `tools/gui/UiTest.cs` into `uitest.exe` and runs it in the interactive session) |
| `launcher_flow.py` | the launcher steps aside while a tool is open and comes back when every tool is closed, for each of the four tools; the root `AirFrameXR.exe` stub starts `bin\launcher.exe` and passes its arguments on | `python3 harness/launcher_flow.py ["Streamer"]` | `build gui package` first |
| `mirror_check.py` | the desktop mirror: the input maths (14 Rust unit tests incl. every desktop shape) and the picture on the builder's GPU for 7 desktop shapes (1080p, 1440p, 4K, 16:10, 21:9, 32:9, portrait) x 4 display modes (SDR; HDR with Windows' SDR white at 80 / 200 / 480 nits): patch colours, where the screen quad lands and that it stays in view, the cursor dot's size at every resolution, the clear colour elsewhere; then the real desktop captured (BLOCKED, not FAIL, where the OS denies desktop duplication). Ends with the by-hand checklist for the headset. | `python3 harness/mirror_check.py` | the builder's GPU (`tools/probe/desktop_probe.cpp`) |
| `advice_check.py` | the Diagnose suggestions (`tools/gui/Advice.cs`, from `docs/PERFORMANCE_KNOBS.md`): every rule against synthetic wizard metrics and fixture debug logs / game records, a healthy setup that must stay quiet, the debug-logging button | `python3 harness/advice_check.py` | `ui_matrix.py` run once (it builds `uitest.exe`) |
| `readme_screens.py` | regenerates the README's screenshots (`docs/images/ui_*.png`) from the UI harness at 1080p / 125 % with demo data; look at them before committing | `python3 harness/readme_screens.py` | the builder |
| `fg_judge.py` | frame generation against a rendered ground truth on the benchmark scene: classic / edge / edge-blend generate the midpoint of two rendered frames, scored against the frame the scene really has there (PSNR, SSIM, boundary band, blur and double-image energy), holding and blending as references, scene / head / both motion, paired statistics, picture montages (docs/FRAME_GENERATION_JUDGE.md) | `python3 harness/fg_judge.py [--eye WxH] [--starts N] [--gaps ..] [--speeds ..]` | `run.py build host`; the benchmark scene on the builder |
| `fov_profile_check.py` | the Advanced window's profile button: the shipped Vision Pro profile's `fov` reproduces the row of `docs/AVP_FOV.md` (0.94 / 0.93 / 0.90 / 0.76, quality 0.5), and a click fills the four fields and the render quality slider, ticks the override and saves | `python3 harness/fov_profile_check.py` | `ui_matrix.py` first (builds `uitest.exe`) |
| `encoding_radios_check.py` | the Encoding radio buttons of the settings panel exclude each other (1.0 put them in different containers: both could be checked and the save stored full frame) | `python3 harness/encoding_radios_check.py` | `ui_matrix.py` first (builds `uitest.exe`) |
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

**Headset address not fixed** (`python3 harness/find_headset_e2e.py [auto] [button] [unpaired]`): AirFrameXR.exe against the mock headset on the PC, started from a fresh copy of `out\dist\AirFrameXR`. `auto`: the session pairs the headset at a wrong, unroutable address and the streamer must still connect (ALVR re-finds a trusted headset by name from its announcements). `button`: same start, `AirFrameXR.exe --test --find-auto` opens the *Find headset* dialog, which must list the headset at its new address, pair it and restart the streamer. `unpaired`: nothing paired, the dialog finds and pairs it. The headset's announcement is sent by `announce.ps1` (a unicast datagram from 127.0.0.2 to the streamer): a broadcast from the same machine is not delivered, and a datagram to the PC's own LAN address is dropped by the Windows Firewall until `alvr_host.exe` is allowed in that folder (what the wizard's firewall line checks).

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
| `audio_route_check.py` | the cable path in detail: defaults / mute / 100 % / 2 ch 48 kHz, audio.mute_pc off, a default stolen and the cable muted while running (healed in 7 s), a named device, no cable (`AIRFRAMEXR_FAKE_NO_CABLE=1`), the format fixed from 44.1 kHz, crash recovery | `python3 harness/audio_route_check.py` | `run.py build gui package` |
| `process_loopback_probe.py` | Windows process loopback from plain .NET (`probes/proc_loopback.cs`): include a process, include another (silent), exclude; at the output muted / 10 % / 100 % and at 44.1 / 96 kHz device formats | `python3 harness/process_loopback_probe.py` | builder only |
| `elevated_audio_check.py` | an elevated (high integrity) tone player is heard in include and exclude mode by a normal-token process (UEVR and mods run elevated) | `python3 harness/elevated_audio_check.py` | someone accepts the UAC prompt on the builder |

What the lab taught: a muted output's own loopback is not silent (use the microphone or the endpoint state to prove mute); processes started from the SSH session
live in another logon session and process loopback does not hear them (start the mock headset with `lib.start_limited`); the builder wrapper silently ignores `rmdir` / `del`
(use `robocopy /MIR` for a clean copy); the VB 16ch cable refuses a second loopback client. The scene music: `tools/bench_scene/sakura_breeze.mp3`
(credits in `THIRD_PARTY_NOTICES.md`), played by `xr_probe` when it renders a scene (`XR_PROBE_SOUND=0` silences it, `XR_PROBE_SOUND=<file>` picks another).

## Stability harness (2026-10-22, docs/permanent_record/STABILITY_PLAN.md)

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
| `gui_pipe_stall_30s` | the streamer window stops reading the host's stdout for 30 s (the host's stdout goes through a frozen `findstr`) | no stall over 2 s (a host thread blocked in `println!` froze the stream for 27 s before the event queue) |
| `memory_baseline_fullres` | none: what the chain holds at the Vision Pro's size | no growth, memory events present |
| `vram_cap_loose`, `vram_squeeze`, `ram_squeeze` | the ballast leaves the chain 4 GB / 1.8 GB of VRAM, 2.3 GB of RAM (squeezes released at +95 s) | alive at the end |
| `soak_tokyo` | `--seconds N` (default 900) of seeded plausible events: re-centre, eye height, gamma, generation off/on and rate, debug logging, blackouts, host and client freezes, audio events (default output muted / volume / switched to the cable and back, restored afterwards; `--no-audio` leaves them out) | every stall inside an injected freeze, the host sees the audio changes, host and probe memory flat |

**When to run it:** it is optional on a new build. The suite takes about 90 minutes and a soak hours, so run it after a major change (frame generation, the tracking / timestamp path, audio capture, the host's threading) and before a release that carries one, not on every commit.

Machinery: `memory_limit` in a scenario (`vram_cap_mb`, `ram_cap_mb`, `at_s`, `release_s`) starts `tools/stability/vram_ballast.cpp` (built by `run.py build ballast`) so the machine
behaves as if the chain had only that much (the dev PCs have far more than many users); `faults` (`suspend` of a process for N s), `stdin_feed` (host commands at given times),
`reconnect_storm`, `sample_memory` (`memsample.ps1`: RAM, handles, dedicated VRAM per process once a second, `mem.csv`). Mock client flags: `--track-blackout start:dur[:frame]`,
`--track-after-frame`, `--track-frames-needed N`. `--host-env AIRFRAMEXR_FG_DARK_MS=0` turns the dark-tracking fix off (the freeze scenarios then fail: that is how the harness is
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
- `e2e_fov_stream_r`: the stream frame shrunk to the calibrated Vision Pro window at today's pixel density (3168x2496 per eye instead of 3552x3200, foveation on; docs/FOV_PIPELINE_OPTIM.md): the checks of `e2e_openxr_foveated` at that size (negotiated size, encoded frame = ALVR's layout for it, counter and gray patch through the decoded stream, pose round trip, pacing, encode p95); its `encoder_stats` give the compose / foveation-pass / encode timings against the 3552x3200 run on the same build (the laptop's compose reading is clock-bound: P8). `python3 harness/fov_stream_geometry.py` (no builder) prints the sizes, the encoded frames and where ALVR's foveation puts its full-density band in degrees for the declared box and the rectangle, and the foveation parameters that keep the band when the frame shrinks; `--selftest` checks its band formula against the compress shader's own equations.
- `e2e_fov_restrict`, `e2e_fov_override`, `e2e_fov_override_live`, `e2e_fov_restrict_quality_half` / `_full` (render quality 0.5 / 1, `checks.fov_restrict.quality`), `e2e_fov_restrict_none`: the field of view the game is told (Advanced picture and FOV settings, shim `fovScale`). The probe app logs what UEVR reads (the four tangents per eye from `xrLocateViews`, the recommended size) and `checks.fov_restrict` compares them with the headset's views (`views_config` event) and the restriction (`scale` in percent for the lab flag `--fov-scale`, `override` in tangents for `--fov-override`), then samples the decoded stream: the trimmed margins black, the picture just inside them. `_live` sends the override as the streamer does (`fov_override on o i u l` on the host's stdin, `host.gui`; the host must log one `fov_restrict` event), `_none` is the 100 % control with all the probe's checks.
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
- `e2e_installed_layout`: the portable folder `out\dist\AirFrameXR` copied as a user unzips it; host with `--install-dir --debug`, app against
  only its manifest; checks the general log's key lines and the debug session files.
- `e2e_benchmark_network`: `--benchmark-network 100,200` to the mock client: steps must reach their target bitrate at >= 80 fps.
- `e2e_heavy_app_async` (and `optional/e2e_heavy_app_sync`, `OVRSHIM_SYNC_SUBMIT=1`): xr_probe with a synthetic CPU + GPU load per
  frame (`XR_PROBE_CPU_MS`, `XR_PROBE_GPU_COPIES`) that only fits 90 Hz when the app's CPU and GPU work overlap; laptop: 93.5 vs 65.4 fps.
- `e2e_modsim_fix_off`, `e2e_modsim_fix_on`, `e2e_modsim_nodup_fix_on` (2026-10-28): `xr_modsim` (tools/probe/xr_modsim.cpp) mimics the frame loop of a VR mod measured in the field
  (acbfvr, alternate-eye rendering): the render thread waits and begins on every second engine frame, a worker thread (one of a pool of 9, a single thread after `MODSIM_SWITCH_S`)
  draws and ends the frame and then runs a second wait / begin / end that resubmits the same layers, so every real frame is submitted twice. Frame generation at half rate.
  Knob off: nothing is dropped, the second wait is paced like any other (22.5 real fps on the laptop, the behavior of before); knob on (`drop_duplicate_frames`): every duplicate
  dropped, the second wait returns at once, real frames paced alone (45 real fps, spacing stdev 0.7 ms); knob on with a game that does not resubmit (`MODSIM_DUP=0`): the fix does not act.
  The app's own event CSV (`modsim.csv`) and the shim's pacing line (`duplicate frames seen N dropped M (pacing fix on|off, free waits K)`) are what the checks read.
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
`checks.pacing` (scenarios `e2e_baseline`, `e2e_openxr_probe`): client arrival spacing of the app's frames after `warmup_frames` (`client_p99_ms`, `max_late_frac` = share of gaps over 1.5 periods), and the host's own `pacing` report (`vsync_p99_ms`, `vsync_max_ms`, `intake_p99_ms`). The counter check pairs received frames with host rows in order (the host may skip frame numbers) and needs a constant counter offset. On a laptop GPU (the run records the GPU name in `rig.txt`) `harness/rigs.json` replaces the scenario's `client_p99_ms`, `max_late_frac` and `intake_p99_ms` with that rig's ceilings: the same laptop measured p99 about 14 ms and 0 % late on 2026-10-08/09 and about 22 ms and 2-3 % late on 2026-10-27, so they are a ceiling for the present state, not a target. Rewritten 2026-10-27: `e2e_openxr_probe` runs 1800 frames (about 20 s, warm-up 150: the GPU's wake-up ramp) so a rate means something (one late frame was 0.3 % of the old 370). Hard guards, the same on every rig: `max_gap_periods` (2.6: at most one skipped slot, never a stall), `p50_tolerance` (the typical gap is the display period within 3 %), `max_late_run` (2: three late frames in a row is a hitch), `max_drift_ms` (the pipeline latency of the last third within 4 ms of the first third's: a queue only grows). The one machine-dependent number is `max_late_frac`, with a per-rig ceiling in `rigs.json`. `pacing_late_attribution` prints the skipped slots at the client and at the host's send: equal = made before the encoder (the app or the boundary pick had nothing ready), more at the client = the network or the client added them.


## Follow-the-frames / follow-the-pose / SteamVR baseline (2026-10-11)
- `python3 harness/run.py pacing <scenario>` then `python3 harness/frames.py <results dir>`: per-frame join of the whole chain (needs a debug build, see docs/DEVELOPMENT.md); scenarios in `scenarios/pacing/exp/` (`ftf_*`, `replay_*`).
- Replay of recorded real head traces (`harness/fixtures/headtraces/*.csv`, cut with `tools/make_headtrace.py`) with `client.replay` + `client.real_views` in a scenario; the hop table in frames.py shows every pose hop.
- `python3 harness/steamvr_baseline.py <trace> --seconds 60`: stock ALVR + SteamVR + the probe + the mock client replaying the trace (needs stock ALVR and SteamVR installed; paths at the top of the script);
  `python3 harness/pose_compare.py <dir>` and `python3 harness/stamp_check.py <dir>` analyse it (and our own replay runs) the same way.
- `python3 harness/steamvr_hmd.py`: the same on a real headset. `python3 harness/live.py run harness/live_variants.json --only probe_light_pred100` runs the probe through our chain on the real headset.

## simrig
`harness/simrig/`: make_profile.py (timing profiles from a real session), judge.py (dots.csv -> verdict, `--selftest`), run_sim.py (scenarios on the PC). See docs/DEVELOPMENT.md.

## Lab overview: what each piece is

Moved here from the top-level README (2026-10-28). The story of why the lab exists is in the README; this is the lookup table.

| piece | what it is | where |
|---|---|---|
| **Fake game** | `xr_probe`, a real OpenXR app with knobs: frame time as a range (`XR_PROBE_LOAD=fps:40:50`, `jitter:30`, `ramp`, `burst`), CPU and GPU cost separately (`XR_PROBE_CPU_MS`, `XR_PROBE_GPU_COPIES`), a world-fixed ring of markers for the head-rotation judge, a marker moving at a known speed for the motion judge, on the game clock or the wall clock | `tools/probe/xr_probe.cpp` |
| **Fake headset** | the simrig (`cargo feature simrig`): a synthetic head, still or turning, that sends tracking with the timing imperfections of a *recorded* real session (tick jitter, bunching, prediction noise), and a viewer (`simviewer`) that displays what the headset would, reprojection included, at a chosen panel rate, waking up late the way a client does | `tools/host/src/simrig.rs`, `nvenc/hostlib/simviewer.cpp` |
| **Real-life conditions** | profiles made from recorded headset sessions: `real_games_pred0.json` (the timing of real game sessions), `real_probe_pred100.json` (the client's 100 ms prediction noise); the ideal profile is the null case that must be clean or the rig itself is wrong | `harness/simrig/profiles`, `make_profile.py` |
| **Judge** | from what the viewer displayed, not from host counters: the head-rotation residual against the analytic view of the ring (rms, p99, high-frequency part, stereo disparity) gives **CLEAN / TREMBLE** (0.04 deg hf, about 0.7 px on a Vision Pro); the moving marker's world angle per display tick (frozen periods, gaps the host did not fill, step regularity) gives **SMOOTH / JUDDER**. A self-test injects wrong poses and must say TREMBLE | `harness/simrig/judge.py` |
| **Scenarios** | Named conditions, each a sentence long: the null cases (`ideal`, `steady`, `steady_fg`), stuttering apps with and without generation (`stutter`, `stutter_fg`, `stutter_turn_fg`), each rate (`third`, `stutter_third`, `two_thirds`, `jitter_two_thirds`), uncapped generation (`*_unc`), GPU-bound apps (`gpu`, `gpu_unc`), the judge's own trap (`inject`). `run_sim.py list` prints them with what each expects | `harness/simrig/run_sim.py` |
| **Live harness** | the same loop on the real headset, unattended: build on the PC from the working tree, deploy, start the host and a game or the synthetic source, stream N seconds, fetch the debug session, score it; A/B variant files; a baseline run against stock SteamVR + ALVR on the same rig | `harness/live.py`, `live_variants.json`, `baseline.py`, `stock.py` |
| **Session logs** | with the launcher's debug tick: every host event (`host_events.jsonl`, 2-second encoder windows with the pipeline's counters), the headset's per-frame statistics (`client_frames.csv`), the shim's per-frame trace, the GPU's clocks and power state (`gpu.csv`); the analyses in `docs/PERFORMANCE_FINDINGS.md` are joins over these | `logs\debug\<session>` |
| **Benchmarks and probes** | the headless encoder quality sweep and the network bench behind the setup wizard, the wizard's own end-to-end test, the NVENC split-frame probe, the D3D11/D3D12 interop test that pinned the shared-texture flag bug | `harness/run.py`, `wizard_e2e.py`, `tools/nvenc_split_probe`, `tools/interop_test` |
| **Stability harness** | fault injection and soaks on the real chain (host, shim, fake game, mock headset): the headset's tracking silent for 5 or 30 s, the host or the headset frozen, 15 reconnects, the streamer window not reading the host's output, VRAM and RAM squeezed to what a smaller PC has, and a seeded soak of plausible events (settings, generation toggles, blackouts, audio device changes); a judge reads stalls and memory growth per process. Slow (about 90 minutes for the suite, hours for a soak), so it is **optional**: run it on major changes, not on every build | `harness/stability/` (`python3 harness/stability/run_stability.py list`), harness/README.md |
| **GUI matrix** | every window and wizard page at 1080p / 1440p / 4K x 100-200 % Windows scale, audited for fit, clipping, overlap and cut-off text, with a screenshot per case | `harness/ui_matrix.py`, `tools/gui/UiTest.cs` |
| **Launcher round trip** | the launcher hides while a tool is open and returns when all are closed; the root stub starts it | `harness/launcher_flow.py` |
| **Desktop mirror** | 7 desktop shapes x SDR / HDR (3 SDR-white levels) rendered on the GPU: colours, placement, cursor size; the input maths (including the left / right hand-over of the beam) as unit tests; the app through the whole chain with scripted controllers (right-handed: `e2e_mirror`; the left trigger takes the beam: `e2e_mirror_left`); real-desktop screenshots of both hands | `harness/mirror_check.py`, `tools/probe/desktop_probe.cpp`, `tools/mirrordesktop/`; `e2e_mirror`, `e2e_mirror_left`; `harness/mirror_screenshot.py --real` |
| **Stream sized to the visible window** | the chain with the stream frame shrunk to the calibrated rectangle at constant pixels per degree (the plan of docs/FOV_PIPELINE_OPTIM.md): negotiation, ALVR's foveated layout at the new size, the picture through the decoded stream, pacing, stage timings; the geometry oracle for sizes, encoded frames and the foveation's full-density band in degrees, with the re-anchored foveation parameters; the encode-cost curve by size (`bench-quality --eye`) and the generation cost by size (`fg_judge.py --eye`) | `e2e_fov_stream_r`, `fov_stream_geometry.py`, `run.py bench-quality full --eye WxH`, `fg_judge.py --eye WxH` |
| **Field of view the game is told** | what an OpenXR app receives at startup against what the headset declared and what was asked: the probe app logs what UEVR reads (the four tangents per eye from `xrLocateViews`, the recommended image size); the checks compare them with the headset's views, then sample the decoded stream (the trimmed margin black, the picture just inside it). Unrestricted control (nothing differs from 1.0), a restriction in percent (lab flag), an override in tangents (lab flag and as the streamer sends it on the host's stdin); the streamer forwards a change once and never repeats it (render quality included, as the fifth value); the render quality slider at 0.5 and 1 (the recommended size lands halfway and at the stream's eye size, the tangents, margin and picture unchanged), and its Advanced-window row in the GUI matrix at every resolution and scale; the headset profile's measured-window button (the shipped Vision Pro profile reproduces the documented row, the click fills the fields and the slider and saves) | `e2e_fov_restrict_none`, `e2e_fov_restrict`, `e2e_fov_override`, `e2e_fov_override_live`, `e2e_fov_restrict_quality_half` / `_full`, `ui_matrix.py` (`advanced-switches-fov-quality`, `advanced-switches-fov-profile`), `fov_profile_check.py`, `streamer_events.py` (`fov_forward`), host unit tests (`vision::fov_scale_from`) |
| **Frame generation judge** | the generated frame against the frame the scene really has there: the animated benchmark scene is rendered at two times (the pair a half-rate game delivers) and at the midpoint (the ground truth); each synthesis (classic, edge, edge-blend) generates the midpoint with the streamer's own generator; PSNR / SSIM over the frame, in the moving region and on its boundary band, edge energy in excess (double images) or in deficit (blur), holding and blending as references, scene / head / both motion at several speeds and rates, paired statistics with standard errors, picture montages. First run: edge +2.0 dB over classic (sharper in 63 of 63 cases), edge against edge-blend a tie | `harness/fg_judge.py`, `tools/host/src/fg_judge.rs`, [docs/FRAME_GENERATION_JUDGE.md](../docs/FRAME_GENERATION_JUDGE.md) |
| **Encoder A/B** | the quality benchmark as the judge of an encoder setting: the same sweep with the foveal QP map off and on, at several bitrates and a Quest-class eye size (centre / periphery / weighted PSNR, encode time, produced bitrate); the Encoding radio buttons of the settings window exclude each other | `harness/qpmap_ab.py`, `run.py bench-quality --qp on\|off --eye WxH --mbps N`, `harness/encoding_radios_check.py` |
| **Audio chain** | the streamer, the mock headset and the Littlest Tokyo scene (it plays music): in process-loopback mode the default output stays untouched and the headset's process plays what the host captured; in cable mode the cable is the default and carries the music; the PC speakers are muted (checked on the endpoint and, in a quiet room, with the builder's microphone against an unmuted control); the streamer exits when its window closes and everything is restored | `harness/audio_chain_check.py [--device]` |
| **Audio routing (cable path)** | defaults, mute, 100 % and the 2 ch / 48 kHz format, a default stolen while running, a named device, no cable, crash recovery | `harness/audio_route_check.py` |
| **Audio probes** | process loopback independent of mute, volume and the device's own format; an elevated player heard by a normal process | `harness/process_loopback_probe.py`, `harness/elevated_audio_check.py` (accept the UAC prompt) |
| **Firewall review** | the rule analysis against real throwaway rules, and the `.bat` | `harness/firewall_check.py` |
| **Compile checks from WSL** | host, shim and C++ checked without the Windows builder | `tools/wsl/check_host.sh`, `check_cpp.sh`, `check_gui.sh` |

How it is used: `python3 harness/simrig/run_sim.py build` once, then `run <scenario> --seconds 40`; results land in `harness/results/<UTC>_simrig_<name>/`
with the verdict JSON, the viewer's CSVs and the host's debug session. `docs/DEVELOPMENT.md` has the full recipe, the builder setup and the
profiles (`AIRFRAMEXR_PROFILE`) that point the harness at your PC.

**Debug and release builds (the telemetry flavour).** The per-frame traces the lab needs cost CPU on every frame, so they are a compile-time
flag. A **debug build** (the default of `python3 harness/run.py build ...`) keeps them: the shim's per-frame trace (`shim_frames.csv`: pose
read, head-sample arrival, release, submit, publish), the host's frame fates and sent-frame CSVs, the audio-capture trace and a scheduler
probe, written into a debug session folder when debug logging is on. The frame-by-frame analysis (`harness/frames.py`), the pacing and
judder scenarios and the simrig need them. A **release build** (`--release`: cargo `--no-default-features`, the shim compiled with
`AIRFRAMEXR_RELEASE`) compiles them out; the periodic statistics behind Game history, Diagnose and its suggestions stay in both. Build one
with `python3 harness/run.py build host shim gui package` (debug) or `... package --release` (release; what a public release uses);
`build.ps1` is the other way round (release by default, `debug` as a target). A debug package says so: its zip name ends in `-debug` and
the windows show "(debug build)" after the version. New per-frame telemetry must sit behind the flag (`docs/DEVELOPMENT.md`).
