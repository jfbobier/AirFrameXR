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
| `mirrordesktop.exe` | the desktop mirror: an OpenXR app the streamer starts between games. **Not part of the engine**: it talks to it through OpenXR like any game (the handover when a game starts is the shim's signal), so it is changed without touching the frame path |

Shim and host share one memory block (`ovrshim/ipc.h`, IPC v39): host → shim config (eye size, FoV, the game's restricted FoV per eye and render quality with its ceiling, the live preview box, and the three lists the shim reads at an app's start: told the restricted box, told the whole view, trusted), poses, display ticks, compositor
pacing, the ring-slot ownership mask; shim → host frame announcements (seqlock), heartbeat, haptics, diagnostics, the render size it told the app, and what the game submitted per eye (size and FoV, seqlock; the intake below).

## Threads (v0.2)

| Stage | Thread | Clock / rule | Blocks on |
|---|---|---|---|
| Game render → VDXR async submission → shim `ovr_EndFrame` compose into a ring slot | VDXR submission thread | the game's own loop | `PickSlot()`: never a slot the host holds, one still on the GPU, or the last published |
| Shim `ovr_WaitToBeginFrame` (**running start**) | VDXR submission thread | release = next host boundary − app frame-time envelope − `running_start_ms` (2 ms); one release per boundary; a game slower than a period free-runs. The game's `xrWaitFrame` waits for this release (VDXR patch 5, 2026-10-23: VDXR's own running start woke the app a period earlier) | the boundary grid (host ticks); GPU throttle ≤ 1 frame in flight |
| Shim GPU-complete wait → IPC publish | shim publisher thread (time-critical) | DXGI completion event per slot | the GPU |
| Shim display clock (predicted display time to the game) | shim server thread (time-critical) | host vsync event | the host tick |
| **Host clock**: display ticks to the shim, **compositor boundary** pick (newest complete frame → encoder mailbox; green idle frame at 30 fps with no game; empty boundary left empty) | host vsync thread (HIGHEST, sleep-then-spin) | ALVR's `duration_until_next_vsync` grid at the headset's refresh | nothing (one-deep mailboxes, newest wins) |
| **Field of view**: the headset's FoV (ViewsConfig) is the stream frame's and the composition target's; the game is told a per-edge restricted FoV and a recommended size reduced with its area (and raised again by the render quality slider, up to the stream's eye size) (shim `GetEyeFov` / `GetRecommendedResolution`, host `fovScale`, taken at the app's start); the three-box model of the section "Field of view: one negotiation for every headset" below | shim, host | the user's Advanced fields | nothing |
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
- **Phase lock** (`tools/host/src/phase.rs`): from the headset's own reports of how long a decoded frame waited for its render tick, it slides the host's display grid in steps of at most 2 ms, no more than one per two evaluations, until the earliest arrivals sit about 7 ms before the tick; the game's release, the pick, the encode and the send all follow that grid. It is a closed loop only if the reports follow the slides: since 1.4 it switches itself off for the connection when two corrections in a row did not move the estimate by a third of their step (a client that reports a constant queue, as the lab's mock does, would otherwise slide for ever: 2 ms every 2 s, measured). It needs about 4.5 s to settle at a game's start, which is why a pacing check must not judge a run shorter than that.
- **Newest-wins mailboxes** between host threads (intake → clock, clock → encoder) and a FIFO with release times to the sender.
  A frame superseded before a boundary (`superseded`) means the game ran faster than the display; an encoder overrun means an
  encode took longer than a period.

## Field of view: one negotiation for every headset (1.4)

The engine knows no headset types. Per eye there are three boxes, all decided on the PC and all in tangents (design, measurements and the
reasons: `docs/PICTURE_EDGES_DESIGN.md`; this section is the mechanism):

| Box | Meaning | Who decides | Who applies it |
|---|---|---|---|
| **P** panel | what the headset's app can show | the client, declared at connection (Vision Pro: Apple's frustum; Quest: the runtime's lens window) | the client |
| **V** headset view | what the stream covers and the wearer sees | the PC (`video.headset_view`: P by default, or a fitted window plus a margin) | the client declares min(P, V); the host sizes the stream and the foveation from it |
| **G** game box | what the game renders | the PC (`video.fov_override`, per eye; the restriction is a bonus: V unless the game honoured a box or is trusted) | the shim tells the game G and a recommended size reduced with its area, or V at the game's first start; black appears only where the game drew nothing |

G sits inside V inside P, per eye. A Quest is the same model with V = P (its runtime already reports the lens window), so nothing in the engine
branches on the headset: the Vision Pro card writes V, the Advanced window writes G, and both headsets use G and the intake.

```
 GUI / settings ── video.headset_view (V) ─┐                  ┌─ video.fov_override [+ _right] (G) ─┐
                                           ▼                  ▼                                     │
 host (alvr_host) ── ServerControlPacket::Reserved(JSON) ──► client: declares min(P, V) per eye      │
   │  first real-time update of a connection,                 (view params), P when no message        │
   │  again on change; never before StartStream               │                                      │
   │◄──────────────── ViewsConfig (the declared box D = min(P, V)) ◄─────────────────────────────────┘
   │  stream size + foveation from D;   IPC fovScale[2][4] (G per eye) ──► shim ──► game: GetEyeFov, recommended size
   │◄── IPC gameSubmit* (size and FoV the game really submitted, per eye, once at 2 s and on change) ◄─┘
   └─► game_fov_verdict event + run record; restrictExes / unrestrictedExes / trustedNames ──► at the app's start the shim chooses V or G
```

- **The message.** One JSON line over `ServerControlPacket::Reserved`, `{"airframexr":1,"view":{"left":[outer,inner,upper,lower],"right":[...]}}`
  (tangents; the right eye's outer edge is its right one). Stock clients ignore it; ours parses it and declares again when it changes. The
  host reads the declared views once per connection, so a V that arrives after the first declaration takes effect for the stream size at the next
  connection. Host side: `tools/patches/headset-view-message.patch` (`server_core`), stdin command `headset_view on|off`. Client side: the
  visionOS fork (branch `1.4`, `patches/alvr-reserved-message.patch` for its `client_core`).
- **G per eye.** `fovScale` is `[2][4]` since IPC v38; four typed values mirror to the other eye as before. The tool app and the Advanced window
  preview a box live (`fov_preview`, `previewOn` / `previewBox`): the shim paints black outside it in the next composed frame on any app, nothing is
  re-told to the game.
- **The intake** (`tools/host/src/fov_intake.rs`, shim record): some games ignore G (a mod with its own frustum, a fixed render size). The shim
  records, once per game start, the size and FoV the game submits per eye against what it was told; the host classifies it (`honours`, `own_frustum`,
  `fixed_size`, `undecided`) and keeps `config\fov_verdicts.json`. The restriction is a bonus: **a game is told V at its first start** and G only from a start
  after its record showed it follows the box (or when it is trusted); the choice is made by the shim at the app's start (VDXR latches FoV and size then)
  from three lists the host publishes: `restrictExes` (honoured a box, or the user's tick), `unrestrictedExes` (own frustum, fixed size, the user's list: always V,
  even when trusted) and `trustedNames` (`config\fov_restriction_whitelist.default.txt`, shipped, plus the user's `config\fov_restriction_whitelist.txt`: a module loaded in the game's process, such as UEVR's `UEVRBackend.dll`,
  or a game executable: told G at its first start, still verified by its first record). An `undecided` game (a resolution scale) stays on V until the user
  ticks it or trusts it. The rules and the table of fake games the lab uses are in section 7 of the design.
- **The headset tools** are scenes of the probe (`xr_probe` with `XR_PROBE_TOOL=fit|restrict`): an ordinary OpenXR app on the PC, streamed like a
  game, so neither headset needs a client update for them. While one runs the host sends no V (the fit measures the whole panel) and the probe
  is never put on the ignore list. `calibrate.exe` starts them and keeps the result as a profile (`tools/gui/Calibrate.cs`).
- **The launcher's side** (`tools/gui/PipelineModel.cs` `PictureEdgesFoveation`, settings only, no engine; a Vision Pro on the foveated encode profile): from V and Apple's
  rasterization rate map it writes the foveation (band and edge ratios, the zero-loss geometry) and the stream size to `session.json` through the parameters the host already reads. The
  size is the bitrate's: the user's constant bitrate and the display rate give a pixel budget at the profile's bits per pixel (`stream_budget` in `apple-vision-pro.profile.json`), spent
  as one density over V, capped by Apple's own and NVENC's width, so a lower bitrate is a smaller stream. It runs when a picture-edges choice is made or a fit finishes, in the wizard,
  after Settings saves and at each streamer start; the start (passive) and Settings leave numbers and a size the user typed alone (`video.foveation_manual`) until the next choice. The
  `full-split` encode profile forces foveation off, so none of it takes effect there.
- **Open:** the client may declare the vertical axis upside down (step 0: the client logs `[proj]` matrices to decide; until then one switch in
  `PipelineModel` holds the documented orientation). Status of each part, what ran in the lab and what has not run on a headset: `docs/progress.md`.

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
