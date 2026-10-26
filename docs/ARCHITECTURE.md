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

Shim and host share one memory block (`ovrshim/ipc.h`, IPC v12): host → shim config, poses, display ticks, compositor
pacing, the ring-slot ownership mask; shim → host frame announcements (seqlock), heartbeat, haptics, diagnostics.

## Threads (v0.2)

| Stage | Thread | Clock / rule | Blocks on |
|---|---|---|---|
| Game render → VDXR async submission → shim `ovr_EndFrame` compose into a ring slot | VDXR submission thread | the game's own loop | `PickSlot()`: never a slot the host holds, one still on the GPU, or the last published |
| Shim `ovr_WaitToBeginFrame` (**running start**) | VDXR submission thread | release = next host boundary − app frame-time envelope − `running_start_ms` (2 ms); one release per boundary; a game slower than a period free-runs. The game's `xrWaitFrame` waits for this release (VDXR patch 5, 2026-10-23: VDXR's own running start woke the app a period earlier) | the boundary grid (host ticks); GPU throttle ≤ 1 frame in flight |
| Shim GPU-complete wait → IPC publish | shim publisher thread (time-critical) | DXGI completion event per slot | the GPU |
| Shim display clock (predicted display time to the game) | shim server thread (time-critical) | host vsync event | the host tick |
| **Host clock**: display ticks to the shim, **compositor boundary** pick (newest complete frame → encoder mailbox; green idle frame at 30 fps with no game; empty boundary left empty) | host vsync thread (HIGHEST, sleep-then-spin) | ALVR's `duration_until_next_vsync` grid at the headset's refresh | nothing (one-deep mailboxes, newest wins) |
| **Field of view**: the headset's FoV (ViewsConfig) is the stream frame's and the composition target's; the game is told a per-edge restricted FoV and a recommended size reduced with its area (shim `GetEyeFov` / `GetRecommendedResolution`, host `fovScale`, taken at the app's start) | shim, host | the user's Advanced fields | nothing |
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

## Telemetry

`host_events.jsonl` (`encoder_stats` every 2 s with a `pipeline` block: slots filled/empty, superseded, overruns, send_late,
encode envelope, shim running-start envelope and late releases, GPU class), `host_frames.csv` per frame (boundary, arrival,
encode start, encode ms, send time, hold), `tracking.csv`, the shim log (`pacing:` line every 900 frames), and with the
client-statistics patch `client_frames.csv` (the headset's per-frame report). The pacing lab (`harness/run.py pacing`) measures
the same things against an emulated Vision Pro display without the headset; `harness/live.py` drives real headset runs.
