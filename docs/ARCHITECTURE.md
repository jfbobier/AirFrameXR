# Architecture: the frame pipeline and its threads (v0.2)

AirFrameXR replaces the SteamVR + ALVR chain (three processes: the game, `vrcompositor`, `vrserver` with ALVR's driver)
with one runtime and one streamer process. What the multi-process chain gets for free, a thread and a one-deep buffer at
every boundary, each stage keeping its own clock, v0.2 reproduces with threads. The principles come from what every working
VR stack does (SteamVR's running start, Meta's Phase Sync, the OpenXR `xrWaitFrame` contract, ALVR's own send rule), see
`docs/progress.md` 2026-10-10 and the research notes:

1. **The display is the master clock and the game is throttled at frame start**, never more than one frame ahead.
2. **Exactly one frame per display period leaves the host**; an empty period is left empty and the headset re-presents its
   last frame (what the SteamVR compositor does with a late app frame).
3. **Poses are predicted for the real display time, late, and never over-predicted** (prediction amplifies tracking noise).

## Processes

| Process | Role |
|---|---|
| the game | OpenXR app → VDXR (upstream, Oculus-emulation mode) → `LibOVRRT64_1.dll` = **OVRShim** (`ovrshim/driver.cpp`), which composes the layers into a side-by-side texture and hands it to the host |
| `alvr_host.exe` | the streamer (`tools/host`): compositor boundary clock, encoder (ALVR's foveation pass + NVENC), ALVR's `server_core` for the connection, tracking, audio and the network |
| `AirFrameXR.exe` / `configure.exe` | GUIs (C#): launch, status, pairing; discovery is done by the host (`--discover`) |
| `mirrordesktop.exe` | the desktop mirror: an OpenXR app the streamer starts between games. **Not part of the engine**: it talks to it through OpenXR like any game (the handover when a game starts is the shim's signal), so it is changed without touching the frame path. Its beam and mouse follow a controller only once that controller is confirmed in use (an input, or an aim pose that moves away from the head) |
| `calibrate.exe` + `bench\xr_probe.exe` | the field of view calibration: a window that starts the probe's tool scene (`XR_PROBE_TOOL=fit`), an ordinary OpenXR app on the PC streamed like a game. Not part of the engine either (see "Field of view in 1.3.5") |

Shim and host share one memory block (`ovrshim/ipc.h`, IPC v36): host → shim config (eye size, FoV, the game's restricted FoV and render quality with its ceiling), poses, display ticks, compositor
pacing, the ring-slot ownership mask; shim → host frame announcements (seqlock), heartbeat, haptics, diagnostics, the render size it told the app.

## Threads (v0.2)

| Stage | Thread | Clock / rule | Blocks on |
|---|---|---|---|
| Game render → VDXR async submission → shim `ovr_EndFrame` compose into a ring slot | VDXR submission thread | the game's own loop | `PickSlot()`: never a slot the host holds, one still on the GPU, or the last published |
| Shim `ovr_WaitToBeginFrame` (**running start**) | VDXR submission thread | release = next host boundary − app frame-time envelope − `running_start_ms` (2 ms); one release per boundary; a game slower than a period free-runs. The game's `xrWaitFrame` waits for this release (VDXR patch 5, 2026-10-23: VDXR's own running start woke the app a period earlier) | the boundary grid (host ticks); GPU throttle ≤ 1 frame in flight |
| Shim GPU-complete wait → IPC publish | shim publisher thread (time-critical) | DXGI completion event per slot | the GPU |
| Shim display clock (predicted display time to the game) | shim server thread (time-critical) | host vsync event | the host tick |
| **Host clock**: display ticks to the shim, **compositor boundary** pick (newest complete frame → encoder mailbox; green idle frame at 30 fps with no game; empty boundary left empty) | host vsync thread (HIGHEST, sleep-then-spin) | ALVR's `duration_until_next_vsync` grid at the headset's refresh | nothing (one-deep mailboxes, newest wins) |
| **Field of view**: the headset's FoV (ViewsConfig) is the stream frame's and the composition target's; the game is told a per-edge restricted FoV and a recommended size reduced with its area (and raised again by the render quality slider, up to the stream's eye size) (shim `GetEyeFov` / `GetRecommendedResolution`, host `fovScale`, taken at the app's start) | shim, host | the user's Advanced fields | nothing |
| **Host intake**: IPC frame event → frame queue (three deep, oldest first), slot marked busy for the shim | host intake thread (HIGHEST) | the shim's publish event | nothing |
| **Host encoder**: foveation pass + NVENC on the picked slot (slot released when its copy is done), packets → sender queue | host encoder thread (HIGHEST) | encoder mailbox | the encode itself only; the main loop rebuilds the encoder under the same lock, only when no game runs |
| **Host sender**: release each packet at its send time `asap` \| `vsync` (next tick) \| `phase` (boundary + encode-time envelope, default) → `send_video_nal` | host sender thread (HIGHEST) | sender queue | nothing |
| Network send (shards over UDP/TCP) | ALVR sender thread (`server_core`) | | socket |
| Tracking receive → head/hand poses into the IPC block (seqlock), PLL residual statistics | ALVR receive thread → host event thread | the headset's one sample per display frame | nothing |
| Game audio capture → audio packets | ALVR game-audio thread (`server_core`, WASAPI loopback callback) | the audio device clock (10 ms batches) | nothing |
| Headset discovery (mDNS `_alvr._tcp` + UDP 9943) → `headset_seen` events | host discovery thread (`discovery.rs`) | | nothing |
| Settings, haptics, connect / reconfigure, status and statistics windows, scripted input | host main thread | 2 ms loop | nothing in the frame path |

Compared with v0.1, where one host thread at normal priority did IPC wait → synchronous encode (p95 12 ms, more than a display
period under GPU contention) → send → statistics, so a long encode blinded the intake and shifted the whole chain's phase: now
the boundary cadence belongs to the clock thread alone, encode time only moves the send time within the period, and the game is
paced against the same boundaries. The host also asks for the realtime GPU scheduling class (class 5, elevated host),
the one SteamVR's compositor runs with, so the encoder's copy never queues behind the game's GPU work.

## Buffers

- **Ring of 6 side-by-side textures** in the game process (`kSlots`), shared with the host by handle. Ownership: the shim
  writes a slot only if it is not in `slotBusyMask` (set by the host's intake for the newest published frame and by its encoder
  while it reads one), not still completing on the GPU, and not the last one published. Up to two frames can be in flight on the
  GPU, two held by the host; six slots never wait in practice (`slotBusyWaits` counts if they do).
- **Intake queue and gate.** The host's intake holds up to two finished frames in order; a boundary takes the oldest. When the game
  delivers one frame per period but runs a frame ahead of the stream (after a bunch: two frames completed in one period), the queue
  never empties and every frame waits a period more than it needs to (a standing queue, measured at 10-12 ms on the headset). Once
  the queue has stood three boundaries in a row (one left-behind frame is jitter: a late frame paired with an on-time one, both
  shown in order) the host publishes the count (`intakeWaiting`) and the shim's `WaitForVsync` skips one release of the game: the
  game renders one frame fewer, the gap reaches the host's intake a lead later (release to completion, several periods for a
  pipelined engine) and the boundary there empties the queue. Every rendered frame is shown and no hole reaches the headset; the
  gate then stays quiet until that gap has arrived (`gate_holds`, `gate_suppressed` in the statistics; `video.intake_gate`, default
  on). The `drain` pick policy is the fallback when the gate could not act (a queue standing 8 boundaries drops its older frame, at
  most once a second).
- **Newest-wins mailboxes** between host threads (intake → clock, clock → encoder) and a FIFO with release times to the sender.
  A frame superseded before a boundary (`superseded`) means the game ran faster than the display; an encoder overrun means an
  encode took longer than a period.

- **Phase lock** (`tools/host/src/phase.rs`): from the headset's own reports of how long a decoded frame waited for its render tick, it slides the host's display grid in steps of at most 2 ms, no more than one per two evaluations, until the earliest arrivals sit about 7 ms before the tick; the game's release, the pick, the encode and the send all follow that grid. It needs about 4.5 s to settle at a game's start, which is why a pacing check must not judge a run shorter than that (`e2e_baseline` measures the drift from 0.5 s after the last correction and runs 900 frames).

## Field of view in 1.3.5

The engine tells a game **one box for both eyes** (IPC v36, `fovScale` mirrored): the headset's own field of view, with each edge scaled by the host's fraction (`video.fov_override`, four tangents capped at what the headset declares; the shim answers `GetEyeFov` and the recommended size at the game's start, composition keeps the headset's frame and leaves the rest black). The choice in the interface is one radio pair for every headset, *Stock vendor view* or *FOV restriction* with a profile; nothing in the engine knows the headset type. (1.4 explored three boxes per eye, a headset view message to the client and a per-game honour check; none of it is in 1.3.5, see the `1.4` branch.)

- **The calibration** is a scene of the probe (`tools/probe/xr_tool.h`, `XR_PROBE_TOOL=fit`): four bright line segments on the edges of a box, one eye at a time, keys read from the PC keyboard (`GetAsyncKeyState`, or scripted for the lab). `calibrate.exe` starts it with `XR_PROBE_PARENT_PID` (the scene ends when the window does: an orphaned scene once made every new host fail with `OpenSharedResource` in a restart loop), stops the desktop mirror, takes the keyboard focus, sets the restriction aside while the scene runs, and keeps the result as a profile (`UserFovStore`, per eye in the file, the larger value per edge in `Tan` and in what the engine gets).
- **The launcher's side of the foveation** (`tools/gui/PipelineModel.cs` `PictureEdgesFoveation`, settings only, no engine; a Vision Pro on the foveated encode profile): from the frame the headset declares, Apple's rasterization rate map (`ratemap` in `apple-vision-pro.profile.json`) and the restriction in force, it writes the foveation (`RateMapFoveation.Milder`) and the stream size to `session.json` through the parameters the host already reads. The compression allowed at an angle is `max(1, 0.6 / rate)`, so a share of what Apple's map already loses; the centre is at least 10 % larger and the edge ratio at least 20 % below ALVR's defaults (0.45 x 0.40, 4 / 5); with a restriction on, the centre band is the window plus 2 degrees and the outside, which the game does not draw, compresses up to that cap. The size is the bitrate's (the user's constant bitrate and the display rate give a pixel budget at the profile's bits per pixel). It runs at each streamer start, after Settings saves and at every restriction choice; hand-typed foveation numbers are left alone (`video.foveation_manual`) until the next choice; a typed stream size is kept (`video.stream_size_manual`) but its foveation is still computed. The render choice is a percentage of pixel for pixel with the declared resolution (`PipelineModel.RenderPlan`, `video.render_spend_pct`, `video.render_allow_above`, `video.render_reference_eye`), turned into the engine's `fov_render_quality` at every start and choice (`harness/render_plan_check.py`). `harness/ratemap_check.py` compares the zero-loss geometry with the Python oracle (30 cases); `harness/milder_check.py` checks the caps.

## Settings: golden master and compiled session

Three files, three roles. Nothing but the first two is edited by a person or a window.

| File (`config\`) | Role | Written by |
|---|---|---|
| `airframexr.json` | AirFrameXR's own settings: the stream fit switch, the density reference (`stream_reference_eye` / `stream_reference_fov`), the restriction (`video.fov_*`), "Spend the savings" percentage, hand-edit flags, display, audio | the windows (Settings, wizard, FOV panel, Advanced) |
| `session.base.json` | **the golden master** of the ALVR settings: the resolution the user chose, fps, bitrate, codec, the foveation numbers the user typed, audio, chroma, the pairing | the windows only (`Session.Load()` / `Session.Save()`) |
| `session.json` | **transient**: what ALVR and the host read for this streamer run | `SessionCompiler.Compile` before every streamer start (`tools/gui/PipelineModel.cs`), then ALVR itself while it runs |

The compile step (`RuntimeForm.StartHost`, before `host.Start`) is a pure function of the golden master, `airframexr.json` and the headset's declared box (`airframexr_views.json`):

1. Clone the golden master.
2. **Stream size.** The golden size is the user's resolution. With the stream fit on, the size becomes `FitEye(reference eye, reference box, declared box)` (the same pixels per tangent unit over the smaller box the client declares), capped at what NVENC takes. Otherwise it stays as typed.
3. **Foveation** (a Vision Pro on the foveated profile): `PictureEdgesFoveation.Apply` computes the geometry from the picture edges, Apple's rate map and the restriction window, over the size from step 2, and, when the size is not typed and the fit is off, sizes the stream for the bitrate. Foveation numbers the user edited by hand (`video.foveation_manual`) are left as they are.
4. Write `session.json` atomically. The launcher then adds what depends on the audio route (ALVR's `mute_when_streaming`) to that file, and the host applies the NVIDIA profile and the encode profile to it as before.

What follows from it:

- **The restriction, the fit and the bitrate never change the resolution the user chose.** They change the compiled size, at every start, and the FOV panel shows the compiled size (`SessionCompiler.Preview`); Settings shows the golden one. Before 2026-11-08 the fit, the foveation and the FOV panel wrote the stream size into the only session file, over the user's resolution.
- **A hand edit of `session.json` is lost at the next start**, on purpose. Edit the settings in the windows (or `session.base.json`).
- **ALVR's own state is the one thing that flows back.** A headset ALVR trusted while the streamer ran is in the live file only; the next compile takes `client_connections` into the golden master (as Disconnected) when the live file is newer than the master, so a pairing made in a window in between wins.
- **The one derived value outside `session.json`** is the engine's render quality (`video.fov_render_quality` in `airframexr.json`): the engine reads it from there, so the compile writes it there, from the "Spend the savings" percentage and the compiled size.
- **Upgrade**: an install without `session.base.json` gets one from its `session.json`, with the stream size put back to the resolution the user chose (`stream_reference_eye`) instead of the fit size the old code had written.
- **Lab and harness** start the host on a `session.json` they write themselves; the compile step belongs to the launcher, so those runs are unaffected. `harness/session_compile_check.py` tests the compile (fit, no-fit, hand edit, pairing both ways, migration, idempotence).

## Telemetry and the Black Box

**Two builds of the same code, and the split is the performance guarantee.** The *stock* build (what people stream with) is compiled without the per-frame telemetry: the host without the
cargo feature `telemetry`, the shim with `AIRFRAMEXR_RELEASE`, and the GUIs without any of the Black Box code (the streamer window carries four empty partial methods that the compiler removes).
Nothing in the frame path writes a per-frame record, so the telemetry costs the stock streamer nothing. The *telemetry* build keeps all of it.

Until 1.3 the telemetry build was a developer tool: the lab, `harness/frames.py` and debugging a judder used it. **Since 1.3 it ships, as the Black Box suite**
([TELEMETRY_PLAN.md](TELEMETRY_PLAN.md), [BENCH_UI.md](BENCH_UI.md)): a complete second install, nested in the bundle as `blackbox\` (started by `blackbox_perf.exe` in the folder's root; `bench\` keeps only the small benches), made only of the telemetry build plus two programs of its own,
the **Black Box Recorder** (`blackbox_recorder.exe`: the streamer window with a recording panel; it copies the normal streamer's settings at start and never writes to them) and the
**Black Box Analyzer** (`blackbox_analyzer.exe`: reads a recording, says where the time went; separate, run afterwards). `perfcore.dll` holds the shared probe (NVML and Windows counters, one thread per
source, an unavailable reading is blank and never 0) and the report. The engine is not edited for any of this: the recorder joins the engine's existing per-frame files by timestamp.
A user who never opens the Black Box never has the telemetry on; one who does opens a separate install, so the two cannot affect each other's numbers.

What the telemetry build writes (the engine's own files): `host_events.jsonl` (`encoder_stats` every 2 s with a `pipeline` block: slots filled/empty, superseded, overruns, send_late,
encode envelope, shim running-start envelope and late releases, GPU class), `host_frames.csv` per frame (boundary, arrival,
encode start, encode ms, send time, hold), `tracking.csv`, the shim log (`pacing:` line every 900 frames), and with the
client-statistics patch `client_frames.csv` / `client_graph.csv` (the headset's per-frame report). The pacing lab (`harness/run.py pacing`) measures
the same things against an emulated Vision Pro display without the headset; `harness/live.py` drives real headset runs.
