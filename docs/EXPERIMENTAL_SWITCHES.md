# Experimental switches (the Advanced settings window)

Built in 0.6 as `bin\configure.exe --switches` (launcher card **Advanced settings**, `tools/gui/Advanced.cs`): every key below except the removed ones, grouped, applied when changed, a switch at its default removed from `airframexr.json`. The streamer window forwards the live ones (`frame_gen_rate`, `frame_gen_cap_fps`, `frame_gen_lag_ms`) by watching the file. The uncapped tick moved from the streamer window into this window.

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
| `frame_gen_hold_age` | true / false, **false** | streamer restart (env `AIRFRAMEXR_FG_HOLD_AGE=1`) | holds the stamp regime: a boundary left with a single unused tracking sample repeats once instead of stamping it (which moves every later output one period younger until a skip and a bunch move them back). Vision Pro 2026-10-23: 5-7 regime switches a second, so the hold only converted them into 2.4-2.9 repeats a second: off. The `frame generation samples` log line (every 10 s) shows `held`, `regime switches`, `hold stepped aside` either way |
| `frame_gen_flow_scale` | 0.1-1.0, **0.5** | streamer restart (the generator is rebuilt; env `AIRFRAMEXR_FG_FLOW_SCALE`) | optical-flow input scale. 0.25 cut flow 2.2 -> 1.6 ms in the rig and changed nothing else; 1.0 = 4x the engine time |
| `frame_gen_perf` | "fast" / "medium" / "slow", **fast** | streamer restart (env `AIRFRAMEXR_FG_PERF`) | the flow engine's preset. The flow's budget is the running-start lead (a few ms), so slower presets risk late outputs; untested on a headset |
| `frame_gen_synthesis` | "edge" / "classic" / "edge-blend", **edge** | streamer restart (env `AIRFRAMEXR_FG_SYNTH`, `--frame-gen-synthesis`; "crossfade" is accepted for classic) | how generated frames are drawn from the optical flow. edge (default since 0.6.1, ideas from VectorXR): 3x3 vector median with our own luma cost, the vector solved where the content comes from, per-pixel choice among the neighbouring vectors at motion boundaries, doubtful motion shrinks toward the real frame (a little local judder instead of a double image). classic: the 0.4-0.6 recipe (crossfade fallback), the predictable fallback if edge misbehaves in a game. edge-blend: edge without the photometric gate (doubtful motion blends both frames). Rig (5090, 2026-10-24, steady_fg / stutter_fg, marker position error hf rms): classic 0.024-0.031 deg, edge 0.030-0.036, edge-blend 0.017-0.022 (all CLEAN + SMOOTH); the rig cannot show the double image the gate prevents. GPU on the 5090 at 7104x3200 (stutter_fg, all outputs): edge synthesis 0.24 ms average against 0.33 for classic, flow 2.17 ms either way; no added latency. Env-only tuning: `AIRFRAMEXR_FG_MOTION_LIMIT` (0.10 of the eye width), `AIRFRAMEXR_FG_COST_SCALE` (17), `AIRFRAMEXR_FG_PHOTO_SCALE` (13.5). Headset: edge "looked great" in Valor Mortis (2026-10-24) |
| `frame_gen_cap_fps` | 0-240, **0** = none | live `frame_gen_cap <fps>` (also the key, env `AIRFRAMEXR_FG_CAP_FPS`) | uncapped mode only: minimum spacing of the game's releases. A cap at ~90 % of the game's GPU-bound fps collapsed its render queue (Dead Space 62 -> 31 ms lag, 2026-10-15); with the gate on (section 1) the queue is smaller to begin with, so re-measure |
| `frame_gen_lag_ms` | 0-100, **0** = auto | live `frame_gen_lag <ms>` (also the key, env `AIRFRAMEXR_FG_LAG_MS`) | uncapped mode only: content lag behind the newest tracking sample; auto = envelope of the game's frame period + half a display period, up to 80 ms |
| env only `AIRFRAMEXR_FG_SAMPLE_WAIT_MS` | 0-20, **3** | streamer restart | how long a boundary waits for a tracking sample newer than the last sent frame. Lab knob; no json key |
| env only `AIRFRAMEXR_FG_ARRIVAL_WAIT_MS` | **4** | streamer restart | how long a boundary with nothing due waits for the frame the game is about to publish |
| env only `AIRFRAMEXR_FG_DARK_MS` | **100**, 0 = never | streamer restart | after this long without a fresh sample, outputs are stamped with the last known one (cures the tracking-blackout deadlock, 2026-10-22) |

## 3. The arrival family (docs/PERFORMANCE_KNOBS.md)

Already in the launcher: `phase_margin_ms` (Diagnose's Apply button; default 7, env `AIRFRAMEXR_PHASE_MARGIN_MS` wins).

| key | values, default | effect | what it does, what we know |
|---|---|---|---|
| `phase_lock` | true / false, **true** | streamer restart (env `AIRFRAMEXR_PHASE_LOCK=0`) | slides the host's display grid so frames arrive `phase_margin_ms` before the headset's render tick. Off on 2026-10-23 for a test: no visible change, latency still alternated. Known debt: it slides ~12-25 times a minute whatever it sees, including under a free-running game below the display rate where its input means nothing |
| `boundary_offset_ms` | ms, **0** | streamer restart | compositor boundary offset after the display tick. Never found useful; lab knob |
| `running_start_ms` | ms, **2**; 0 = release the game at the tick | streamer restart | the shim's running-start margin: the game is released `envelope + margin` before its boundary. With the gate on this acts at frame start for the first time; 1-4 are plausible values to try, 0 disables the running start |
| `intake_gate` | true / false, **true** | streamer restart (env `AIRFRAMEXR_INTAKE_GATE=0`) | the shim skips one release when the host's queue has stood 3 boundaries (`AIRFRAMEXR_GATE_AFTER`): drains a standing queue without dropping a frame. Headset verdict 2026-10-13: works when the game keeps up, fires uselessly 3/s in GPU-bound games |
| `intake_depth` | 1-3, **3** | streamer restart (env `AIRFRAMEXR_INTAKE_DEPTH`) | how many finished frames the host's intake holds in order |
| `pick_policy` | "drain" (default) / "oldest" / "newest" | streamer restart (env `AIRFRAMEXR_PICK`) | which waiting frame a boundary encodes. "newest" superseded 15-30 % of frames in heavy games (removed from the UI 0.3); "drain" = in order plus one resync drop when a queue stood 8 boundaries |
| `client_queue_drain` | true / false, **false** | streamer restart (env `AIRFRAMEXR_CLIENT_DRAIN`) | drop one frame when the headset's decoder queue stands a whole period, with a backoff. Never fired in the lab, untested on a headset |
| `follow_client_rate` | true / false, **true** | streamer restart | follow the headset's display rate inferred from its sample timestamps (the Vision Pro's 90 <-> 100 Hz switches); false pins the 90 Hz grid. Guarded: only cadences within 3 % of a declared refresh rate are followed (the Quest's 270 Hz tracking is not) |
| `send_pacing` | "asap" (default) / "vsync" / "phase" | streamer restart | when an encoded frame leaves the host. No visible gain over asap in 2026-10 tests |
| `head_wait_ms`, `predict_missing_head` | ms / bool, **off** | streamer restart | the removed in-order head-sample consumption and the predicted-sample stand-in: both measured worse on the headset (2026-10-13/16). Keep off; listed so nobody re-adds them as "new" |

## 4. GPU and encoder

Already in the launcher: `encode_profile` (foveated / full-split), `split_encode`, `gpu_keepalive` (Diagnose's Apply button), `idle_rgb`.

| key | values, default | effect | what it does, what we know |
|---|---|---|---|
| `gpu_sched_class` | 5 .. 2, 0 = leave, **5** (realtime) | streamer restart | the host's D3DKMT scheduling class (needs the elevated host). 0 tested 2026-10-23: clearly worse (stream 70-86 fps, headset holes 25 %) |
| `gpu_priority` | | streamer restart (env `AIRFRAMEXR_GPU_PRIORITY`) | older GPU thread priority knob, superseded by the class above |
| `qp_map` | true / false, **false** | streamer restart (env `AIRFRAMEXR_QP_MAP`) | HEVC per-CTB QP delta map (lower QP in the foveal region). Built and proven against an oracle, never judged on a headset; costs encode time on a one-engine GPU |
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
