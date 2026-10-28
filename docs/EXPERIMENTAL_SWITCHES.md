# Experimental switches (the Advanced picture and FOV settings window)

Built in 0.6 as `bin\configure.exe --switches` (launcher card **Advanced picture and FOV settings**, `tools/gui/Advanced.cs`): every key below except the removed ones (1.0.1 removed eleven keys that measured as no benefit or worse: they are listed in docs/PERFORMANCE_KNOBS.md, "Removed on purpose"; a file that still has them is ignored, with one log line), grouped, applied when changed, a switch at its default removed from `airframexr.json`. The streamer window forwards the live ones (`frame_gen_rate`, `frame_gen_cap_fps`) by watching the file. The uncapped tick moved from the streamer window into this window.

Written 2026-10-24 on branch `0.6`. Every knob below exists in the engine today and is **not** in the launcher's windows (the ones that are
have a note). For each: where it lives, its values and default, when a change takes effect, what it does, how to read it back, and what we
know. Three mechanisms, three "takes effect" rules:

| mechanism | where | takes effect | read back |
|---|---|---|---|
| `config\airframexr.json` key (`video.*` unless said otherwise) | the host reads it at start | **streamer restart** (the launcher restarts `alvr_host.exe`) | the file; the host logs the values it took in `logs\AirFrameXR.log` |
| live command on the host's stdin (what the launcher's `Send(...)` does) | `alvr_host.exe` | **immediately**, and the host writes the matching key back when there is one | the `frame generation config (...)` log line |
| registry DWORD (one knob: the frame-start gate) | `HKLM\SOFTWARE\VirtualDesktop-OpenXR` | **next game start** (VDXR reads it at `xrCreateSession`) | `Registry64` read, no elevation; writes need one UAC prompt (`Common.cs` `XrLayers` shows the pattern: `cmd.exe /c reg add ...` with `Verb = "runas"`) |

Precedence in the host: command line > environment variable `AIRFRAMEXR_*` > `airframexr.json` > built-in default. The environment variables are
for the lab and the command line; a panel should write the json key (or send the live command), never set variables.

## 1. The frame-start gate (0.6, VDXR patch 5)

| | |
|---|---|
| where | registry `HKLM\SOFTWARE\VirtualDesktop-OpenXR`, DWORD `ovrshim_vdxr_running_start` |
| values | absent or 0 = **gate on** (default); 1 = gate off (VDXR's own running start restored) |
| takes effect | next game start |
| what | on: the game's `xrWaitFrame` waits for the shim's release, so the shim's running start and GPU throttle act at frame start. Off: the game wakes one display period minus 2 ms after the previous release whatever the shim does (the pre-0.6 behaviour: CPU a frame ahead, render queue 2-3 frames in GPU-bound games) |
| measured | rig: frame start -> GPU done 55 -> 42 ms at the 1/2 cap, 47 -> 36 ms free-running, same fps. Headset night 2026-10-23/24: no fps loss in 6 h (Gears 49.0 fps median at a 50 cap); latency gain not isolated yet |
| risk | a game whose CPU frame is longer than its GPU frame loses some overlap (watch fps) |
| needs | a `bin\virtualdesktop-openxr.dll` from the 0.6-alpha zips or newer (1,480,192 bytes from the 5090 build, 1,478,144 from the laptop build); older DLLs ignore the value |
| commands | `reg add HKLM\SOFTWARE\VirtualDesktop-OpenXR /v ovrshim_vdxr_running_start /t REG_DWORD /d 1 /f` (off), `reg delete HKLM\SOFTWARE\VirtualDesktop-OpenXR /v ovrshim_vdxr_running_start /f` (on) |

## 2. Frame generation (docs/FRAME_GENERATION.md, docs/FRAME_GENERATION_UNCAPPED.md)

Already in the launcher: `frame_gen` (tick), `frame_gen_rate` third | half | two_thirds | uncapped (slider + Uncapped tick),
`frame_gen_recommended` (tick). Live commands behind them: `frame_gen on|off`, `frame_gen_rate <name>`. The DLSS motion vectors
(`game_motion_vectors`, `frame_gen_vector_candidate`) were removed in 0.6.1 (docs/MOTION_VECTORS.md).

| key | values, default | effect | what it does, what we know |
|---|---|---|---|
| `frame_gen_synthesis` | "edge" / "classic" / "edge-blend", **edge** | streamer restart (env `AIRFRAMEXR_FG_SYNTH`, `--frame-gen-synthesis`; "crossfade" is accepted for classic) | how generated frames are drawn from the optical flow. edge (default since 0.6.1, ideas from VectorXR): 3x3 vector median with our own luma cost, the vector solved where the content comes from, per-pixel choice among the neighbouring vectors at motion boundaries, doubtful motion shrinks toward the real frame (a little local judder instead of a double image). classic: the 0.4-0.6 recipe (crossfade fallback), the predictable fallback if edge misbehaves in a game. edge-blend: edge without the photometric gate (doubtful motion blends both frames). Rig (5090, 2026-10-24, steady_fg / stutter_fg, marker position error hf rms): classic 0.024-0.031 deg, edge 0.030-0.036, edge-blend 0.017-0.022 (all CLEAN + SMOOTH); the rig cannot show the double image the gate prevents. GPU on the 5090 at 7104x3200 (stutter_fg, all outputs): edge synthesis 0.24 ms average against 0.33 for classic, flow 2.17 ms either way; no added latency. Env-only tuning: `AIRFRAMEXR_FG_MOTION_LIMIT` (0.10 of the eye width), `AIRFRAMEXR_FG_COST_SCALE` (17), `AIRFRAMEXR_FG_PHOTO_SCALE` (13.5). Headset: edge "looked great" in Valor Mortis (2026-10-24); the maintainer's side-by-side in games (2026-10): edge visibly better than classic, edge and edge-blend indistinguishable. Ground-truth judge (harness/fg_judge.py, docs/FRAME_GENERATION_JUDGE.md, 2026-10-26): edge against classic +2.0 dB whole-frame PSNR, SSIM +0.013 in the moving region, less blur in 63 of 63 cases; edge against edge-blend a statistical tie |
| `frame_gen_cap_fps` | 0-240, **0** = none | live `frame_gen_cap <fps>` (also the key, env `AIRFRAMEXR_FG_CAP_FPS`) | game fps lock, in every mode (frame generation off, fixed rates, uncapped): minimum spacing of the game's releases. A cap at ~90 % of the game's GPU-bound fps collapsed its render queue (Dead Space 62 -> 31 ms lag, 2026-10-15); with the gate on (section 1) the queue is smaller to begin with, so re-measure |
| `drop_duplicate_frames` | true / false, **false** | streamer restart (lab flag `--drop-duplicate-frames`) | game frame pacing fix, for an OpenXR client that submits every real frame more than once (a VR mod with alternate-eye rendering: a second wait / begin / end cycle resubmits the frame with the same render poses; measured on acbfvr, Black Flag Resynced: 25 real frames a second submitted 50 times). With it ON: once the shim has seen a duplicate PATTERN (windows of 40 submits, two in a row to start, one to stop; 1 or 2 duplicates per real frame, 40-70 % of the submits) a frame with exactly the previous frame's render poses is neither composed nor published, and the wait that follows a real submit returns at once (no boundary, no release), so the real frame's wait is the only one paced and the fps lock counts real frames. A client with a stationary pose is not that pattern and is left alone; an app that stops duplicating loses the free waits within a second; a duplicate is published anyway when no frame went out for 250 ms. OFF: none of it runs (the comparison runs only while debug logging is on, and then only counts). The debug log's pacing line ends `duplicate frames seen N dropped M (pacing fix on|off, K duplicates per real frame, free waits W)`. Lab: `e2e_modsim_*` (tools/probe/xr_modsim.cpp mimics the mod): off changes nothing, on doubles the real rate (22 -> 45 fps on the laptop), 3 submits per frame, duplicates that stop, a stationary pose and a normal game are all handled |
| env only `AIRFRAMEXR_FG_SAMPLE_WAIT_MS` | 0-20, **3** | streamer restart | how long a boundary waits for a tracking sample newer than the last sent frame. Lab knob; no json key |
| env only `AIRFRAMEXR_FG_ARRIVAL_WAIT_MS` | **4** | streamer restart | how long a boundary with nothing due waits for the frame the game is about to publish |
| env only `AIRFRAMEXR_FG_DARK_MS` | **100**, 0 = never | streamer restart | after this long without a fresh sample, outputs are stamped with the last known one (cures the tracking-blackout deadlock, 2026-10-22) |

## 0. Field of view the game is told (1.1)

| key | values, default | effect | what it does, what we know |
|---|---|---|---|
| `fov_override` | true / false, **false** | next game start (the streamer forwards a change to the host as `fov_override off` / `fov_override on <outer> <inner> <upper> <lower>`; lab flag `--fov-override o,i,u,l`) | the streamer's override of the field of view the game is told. Off: the headset's own, untouched (the window's greyed fields show it, as the headset reported it at its last connection, `config\airframexr_views.json`). On: the four fields below. |
| `fov_outer`, `fov_inner`, `fov_upper`, `fov_lower` | tangents, X.XX in the window; **unset** (the headset's own values) | with the override | per edge, the tangents the game is told: outer = the side away from the nose (the left tangent of the left eye, the right one of the right eye), inner = towards it, upper, lower. Never more than the headset's (a larger value is the headset's own), at least a fifth of it. The host turns them into per-edge fractions of the headset's tangents averaged over the two eyes (`vision::fov_scale_from`), so a value that equals what the field showed (to the two-decimal mask) restricts nothing. The game renders a smaller view and, because the recommended image size shrinks with the area, fewer pixels; the stream frame keeps covering the headset's whole view (the client maps it over the FoV it declared) and the trimmed margin is black, so encode, decode, network, frame generation and the client are unchanged: only the game's GPU work drops. On the Vision Pro's field of view as visionOS reports it (left eye 60.7 / 45.5 / 50.6 / 45.5 degrees outer / inner / up / down, tangents 1.78 / 1.02 / 1.22 / 1.02): 10 % off outer saves about 6 % of the pixels, inner 4 %, upper 5 %, lower 5 %; 90 % on all four 81 % of the pixels, 85 % 72 %. |
| `fov_render_quality` | 0 to 1, **0** | next game start (the streamer sends it as a fifth value of `fov_override on o i u l q`; lab flag `--fov-quality`) | where the game's recommended render size sits per eye between two limits worked out from the user's own resolution settings: the floor, the stream's per-eye size (`openvr_config.eye_resolution_*`, the same one the shim composes at) scaled by the restricted span per axis, and the ceiling, the stream's per-eye size itself. 0 is the floor (what 1.1 did), 1 the ceiling; both axes move linearly between them, sizes even and at least 64. The game's picture is filtered down to the stream's pixels by composition, so the extra pixels are supersampling (cleaner edges, less shimmer), not detail beyond the stream; the cost is game GPU time and video memory. Without a restriction floor and ceiling are the same size and the slider is hidden. The window shows both limits and the size at the slider, from the same maths as the shim (`ovrshim/render_size.h`). Not per headset: a fraction, so it stays meaningful when the stream size changes (a Quest's smaller one). Per axis the ceiling over the floor is 1/(span ratio) (1.25 wide, 1.43 high for 70 / 90 / 80 / 60), so at the top the render's pixels are not square relative to the stream's. |

A headset's profile can offer measured values for these fields with a button (profile key `fov`, docs/HMD_PROFILES.md); the shipped Vision Pro profile does (docs/AVP_FOV.md).

The two headsets compared (host `views_config` events): the Quest 3's stock client forwards the runtime's real optical FoV (outer 54, inner 40, up 44, down 55 degrees, 94 x 99, constant between sessions, the same in both eyes), so trimming it loses visible picture; the Vision Pro's app sends the tangents of its render views (about 60.8 / 45.6 / 50.6 / 45.6, 106 x 96, a little different between eyes and with the IPD, scaled by its own FoV Scale slider in the Metal path), a render frustum whose edges are optically weaker: that is where the override is worth trying. The override is stored once, not per headset: values tuned for one headset are tangents for that headset (on a Quest, a lower of 1.00 would cut its 55-degree down edge to 45).

How a game gets it (the OpenXR contract): `xrLocateViews` returns the four angles per eye, `xrEnumerateViewConfigurationViews` the recommended image size; VDXR reads both from the shim when the app starts (`GetEyeFov`, `GetRecommendedResolution`; it caches them for the session), so a game takes new values at its next start and a running one keeps what it began with. UEVR reads exactly these (the four angles per eye for its projection, the first view's recommended size times its own resolution slider, once). Other OpenXR games are expected to behave the same way, untested. The shim keeps the headset's field of view for composition (`EyeHomography` maps the game's layer into the stream frame; pixels outside its source are discarded and the frame is cleared to black), IPC version 34 carries `fovScale` and `renderQuality`; with no restriction nothing differs from 1.0 (the recommended size stays exactly the stream's eye size).
Lab: `e2e_fov_restrict` (the lab percent flag: 70 / 90 / 80 / 60 % of the headset's tangents), `e2e_fov_override` (outer 1.20 / inner 1.40 / upper 1.00 / lower 1.20: the app's four tangents per eye exact, recommended size 1472 x 1132 for a 1760 x 1600 stream, margins black and the picture just inside them in the decoded stream), `e2e_fov_override_live` (the same through the `fov_override on ...` stdin command, taken once) `e2e_fov_restrict_quality_half` / `_full` (the same restriction at render quality 0.5 and 1: the app's recommended size is halfway and at the stream's eye size, the tangents, margins and picture as before) and `e2e_fov_restrict_none` (100 %: nothing changes, all the probe checks hold); `streamer_events` fov_forward (a change is forwarded once, nothing is resent while the values stay the same).

## 3. The arrival family (docs/PERFORMANCE_KNOBS.md)

Already in the launcher: `phase_margin_ms` (Diagnose's Apply button; default 7, env `AIRFRAMEXR_PHASE_MARGIN_MS` wins).

| key | values, default | effect | what it does, what we know |
|---|---|---|---|
| `phase_lock` | true / false, **true** | streamer restart (env `AIRFRAMEXR_PHASE_LOCK=0`) | slides the host's display grid so frames arrive `phase_margin_ms` before the headset's render tick. Off on 2026-10-23 for a test: no visible change, latency still alternated. Known debt: it slides ~12-25 times a minute whatever it sees, including under a free-running game below the display rate where its input means nothing |
| `running_start_ms` | ms, **2**; 0 = release the game at the tick | streamer restart | the shim's running-start margin: the game is released `envelope + margin` before its boundary. With the gate on this acts at frame start for the first time; 1-4 are plausible values to try, 0 disables the running start |
| `intake_gate` | true / false, **true** | streamer restart (env `AIRFRAMEXR_INTAKE_GATE=0`) | the shim skips one release when the host's queue has stood 3 boundaries (`AIRFRAMEXR_GATE_AFTER`): drains a standing queue without dropping a frame. Headset verdict 2026-10-13: works when the game keeps up, fires uselessly 3/s in GPU-bound games |
| `head_wait_ms`, `predict_missing_head` | ms / bool, **off** | streamer restart | the removed in-order head-sample consumption and the predicted-sample stand-in: both measured worse on the headset (2026-10-13/16). Keep off; listed so nobody re-adds them as "new" |

## 4. GPU and encoder

Already in the launcher: `encode_profile` (foveated / full-split), `split_encode`, `gpu_keepalive` (Diagnose's Apply button), `idle_rgb`.

| key | values, default | effect | what it does, what we know |
|---|---|---|---|
| `gpu_priority` | | streamer restart (env `AIRFRAMEXR_GPU_PRIORITY`) | older GPU thread priority knob, superseded by the class above |
| `qp_map` | true / false, **true** | streamer restart (env `AIRFRAMEXR_QP_MAP`) | HEVC per-CTB QP delta map: QP -3 in the foveal region, +6 at the edges of the foveated frame. **On by default** (since 0.1; this table said off until 1.0.1, and the Advanced window could not turn it off). Not in stock ALVR: it is this project's own code. Built and proven against an oracle, never judged on a headset; costs encode time on a one-engine GPU. The full-frame + split profile switches it off |
| env only `AIRFRAMEXR_ZEROCOPY=0` | | streamer restart | disables the zero-copy encoder input (about 0.2 ms; on by default because it costs nothing) |
| env only `AIRFRAMEXR_SPIN_US` | **600** | streamer restart | the spin margin of the host's high-resolution waits; CPU against precision |

## 5. Removed on purpose: do not offer these

`video.game_pacing` jit | queue (the just-in-time release, 2026-10-17: it was inert because of the gate problem; may return as a fixed rule, item A2 of
docs/PLAN_0.6.md, not as a switch), the automatic rate controller ("Auto"), the frame-cost readout, `frame_gen_predict_sample`, the
duplicate-timestamp re-stamp paths. Each is documented in docs/PERFORMANCE_KNOBS.md "Removed on purpose" with the measurement that killed it.

## Panel notes

- Show the value read back, not the last click: a json write can be stale until the restart, a registry write can be declined at the UAC prompt.
- Say the restart rule next to each switch ("next game start" / "streamer restart" / "live").
- Everything here changes timing; the signals to judge by are in `logs\AirFrameXR.log`: the `frame generation perf` and `frame generation samples`
  lines every 10 s (app fps, late releases, skipped boundaries, held, regime switches), the `game run recorded` line at game end (fps, headset
  repeats), and with debug logging on, the client's per-frame latency in `logs\debug\<session>\client_graph.csv`.
