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
| the game | OpenXR app → VDXR (our fork of upstream, `docs/UPSTREAM.md`; Oculus-emulation mode) → `LibOVRRT64_1.dll` = **OVRShim** (`ovrshim/driver.cpp`), which composes the layers into a side-by-side texture and hands it to the host |
| `alvr_host.exe` | the streamer (`tools/host`): compositor boundary clock, encoder (ALVR's foveation pass + NVENC), ALVR's `server_core` for the connection, tracking, audio and the network |
| `AirFrameXR.exe` / `configure.exe` | GUIs (C#): launch, status, pairing; discovery is done by the host (`--discover`) |
| `mirrordesktop.exe` | the desktop mirror: an OpenXR app the streamer starts between games. **Not part of the engine**: it talks to it through OpenXR like any game (the handover when a game starts is the shim's signal), so it is changed without touching the frame path. Its beam and mouse follow a controller only once that controller is confirmed in use (an input, or an aim pose that moves away from the head) |
| `calibrate.exe` + `bench\xr_probe.exe` | the field of view calibration: a window that starts the probe's tool scene (`XR_PROBE_TOOL=fit`), an ordinary OpenXR app on the PC streamed like a game. Not part of the engine either (see "Field of view in 1.4") |
| `tray.exe` | the notification-area program: watches for the paired headset (`alvr_host --discover`, renewed when its one-hour process ends) and starts the streamer when it appears; idle while a streamer runs. Started at Windows sign-in by a per-user Run entry, switched in the launcher (below OpenComposite) or in the tray's own menu |

Shim and host share one memory block (`ovrshim/ipc.h`, IPC v40): host → shim config (eye size, FoV, the game's restricted FoV and render quality; since v38 no render ceiling; the
compatibility mode and, since v39, the lab's frame-identity arm), poses, display ticks, compositor pacing, the ring-slot ownership mask; shim → host frame announcements (seqlock),
heartbeat, haptics, diagnostics, the render size it told the app, and since v40 the VR guard's findings.

VDXR and the shim also talk past the libOVR API, inside the game process: the shim exports `ovrshim_Trace`, which VDXR's fork resolves and
calls from the app's `xrWaitFrame` / `xrBeginFrame` / `xrEndFrame` with what libOVR cannot carry (kinds and arguments:
`virtualdesktop-openxr/airframexr_trace.h` in the fork): when the app's frame really started, and per frame how many swapchain slices had
an image released since the previous frame (none = the app resubmitted its last picture) and how many went through VDXR's copy into a
second OVR swapchain (texture arrays, MSAA). Stock VDXR and libOVR games never call it. Since 1.5.0 the shim decides with it which submits are
repeats (next section); the debug log's `app frames (VDXR)` line every 900 submits reports it (resubmits, discarded frames, slices
released/copied/skipped, `xrWaitFrame` return → `xrEndFrame` and `xrBeginFrame` → `xrEndFrame` p50/p95). The fork also copies and commits a slice
that goes through that second swapchain only when the app released a new image since the last frame (upstream did it every `xrEndFrame`): a
resubmitted frame costs no GPU copy, and an OVR commit always means a new picture. The shim also exports
`ovrshim_Capabilities` (bit 0: it reads texture-array slices in place when a layer's flags carry `0x400000`, eye e's slice at bits 24 + 4 e);
only the experiment on fork branch `airframexr-slices` sets that flag (`docs/PLAN_VDXR_FORK_LAB.md`). Every 2D swapchain image is composed
through an array view, so a single-pass stereo swapchain (OpenXR `arraySize` 2) is read slice by slice.

## Frames, repeats and the pose each eye is shown at (1.5.0)

OpenXR gives each view of a frame its own pose and leaves it to the runtime to "map the view and FOV to the system display"; it has no notion
of "the same picture again". The shim (`ovrshim/driver.cpp`, `SubmitFrame`) follows that, using the fork's per-frame facts:

- **A repeat** is a submit whose projection slices released no new image since the previous frame. The head poses do not decide: OFXR's
  generated frames carry the real frame's render poses, and a mod can draw a new picture under old poses. Without the facts (stock VDXR, a
  libOVR game) no submit is a repeat.
- **The timestamp** a frame leaves with is the tracking sample its picture was rendered with (pose matching, `FrameClientTimestamp`). A repeat
  leaves with the previous frame's timestamp: the headset shows the same picture under the same pose instead of turning an old picture to a new pose.
- **Each eye at its own pose.** When the projection layer's two eyes carry head orientations more than 1e-4 rad apart (alternate-eye rendering
  mods submit one eye a frame older than the other), the frame is stamped with the newer eye's sample and the older eye is drawn rotated from
  its own render orientation to the newer one (qRel = qSrc⁻¹·qOut in `EyeHomography`, inside the projection pass every eye already goes through
  for the FoV change: no extra GPU work); quad layers follow. In a normal game both eyes share one orientation and the rotation is the identity.
  ALVR v20 carries one pose per frame; for a rotation, mapping in the shim is equivalent and keeps every client working.
- **Nothing is dropped**: every submit is composed and published.
- **Maximum OpenXR compatibility mode** (`video.drop_duplicate_frames`, Advanced, off by default): when the repeats form a pattern (windows of 40
  submits: 1-2 repeats per real picture, 40-70 % of the submits; two windows to start, one to stop), the waits that follow a real picture return
  at once (a free wait: no boundary, one release token), so a client that submits each picture two or three times is paced once per picture.
  Off is OpenXR's letter: `xrWaitFrame` "should block until the beginning of the next frame interval".
- **Counters:** the debug log's `identity:` line (arm, re-stamped, without runtime facts, with several facts; eyes mapped by their own poses, with
  the angle), the pacing line's `duplicate frames seen N dropped 0 (pacing fix on|off, K duplicates per real frame, free waits W)`, and at game
  exit, in every flavour, `eyes mapped by their own poses in N submits`.
- **Lab switch** `lab_identity` 0-3 (host `--lab-identity N` or stdin `lab_identity N`, harness `AIRFRAMEXR_LAB_IDENTITY`; IPC `labIdentity`; no
  GUI, no config file): 0 = 1.5's rule (a repeat = the previous submit's exact poses), 1 = same poses and nothing released, 2 = nothing
  released and the re-stamp, **3 = 2 and each eye at its own pose (the default)**. The design and the measurements: `docs/PLAN_UNIVERSAL_OPENXR.md`
  (sections 6 and 6b); the frame-identity matrix: `harness/scenarios/identity`, `run.py identity`.

**VR guard** (`VrGuardCheck`): once when the game creates its session (`SetSubmissionDevice`) and once at its first submit, the shim looks for
NVIDIA Smooth Motion in the game's process (`NvPresent64.dll`, the driver's frame generation for flat games: it doubles a VR mod's submits and
later presents from its own thread). A finding goes to the run log (WARN), to the IPC (`shim.vrGuard`, bit 0), to the host's `game_ended` event
(`"vr_guard": ["smooth_motion"]`) and to the streamer's footer: a red line after the game, cleared by the next clean game. Nothing runs per frame.

## Threads (v0.2)

| Stage | Thread | Clock / rule | Blocks on |
|---|---|---|---|
| Game render → VDXR async submission → shim `ovr_EndFrame` compose into a ring slot | VDXR submission thread | the game's own loop | `PickSlot()`: never a slot the host holds, one still on the GPU, or the last published |
| Shim `ovr_WaitToBeginFrame` (**running start**) | VDXR submission thread | release = next host boundary − app frame-time envelope − `running_start_ms` (2 ms); one release per boundary; a game slower than a period free-runs. The game's `xrWaitFrame` waits for this release (VDXR patch 5, 2026-10-23: VDXR's own running start woke the app a period earlier). With the compatibility mode acting, the waits after a real picture return at once (free waits, see "Frames, repeats and the pose each eye is shown at") | the boundary grid (host ticks); GPU throttle ≤ 1 frame in flight |
| Shim GPU-complete wait → IPC publish | shim publisher thread (time-critical) | DXGI completion event per slot | the GPU |
| Shim display clock (predicted display time to the game) | shim server thread (time-critical) | host vsync event | the host tick |
| **Host clock**: display ticks to the shim, **compositor boundary** pick (newest complete frame → encoder mailbox; green idle frame at 30 fps with no game; empty boundary left empty) | host vsync thread (HIGHEST, sleep-then-spin) | ALVR's `duration_until_next_vsync` grid at the headset's refresh | nothing (one-deep mailboxes, newest wins) |
| **Field of view**: the headset's FoV (ViewsConfig) is the stream frame's and the composition target's; the game is told a per-edge restricted FoV and a recommended size reduced with its area (shim `GetEyeFov` / `GetRecommendedResolution`, host `fovScale`, taken at the app's start). 1.4: our Vision Pro app declares the box the host sent it (`video.client_box`, Reserved message), so the restriction scales nothing and the game's frame is the stream's | shim, host | the FOV card's profile | nothing |
| **Host intake**: IPC frame event → frame queue (three deep, oldest first), slot marked busy for the shim | host intake thread (HIGHEST) | the shim's publish event | nothing |
| **Host encoder**: foveation pass + NVENC on the picked slot (slot released when its copy is done), packets → sender queue; with frame generation on, the generator's passes first (see "Frame generation") | host encoder thread (HIGHEST) | encoder mailbox | the encode itself only; the main loop rebuilds the encoder under the same lock, only when no game runs |
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

- **Phase lock** (`tools/host/src/phase.rs`): from the headset's own reports of how long a decoded frame waited for its render tick, it slides the host's display grid in steps of at most 2 ms, no more than one per two evaluations, until the earliest arrivals sit about 7 ms before the tick; the game's release, the pick, the encode and the send all follow that grid. Measured 2026-11-24: it does not settle. In the lab (the mock client without its display model reports no wait) and in the field's debug logs (Vision Pro, estimates 0-5 ms against the 7 ms target) it corrects every 2-3 s for whole sessions; an open question (`docs/progress.md`, Known open).

## Frame generation (1.5.0: x2 / x3, neural or edge)

For a game that cannot hold the headset's refresh rate (docs/FRAME_GENERATION.md). Two fixed regimes and two ways to draw the frames in
between, nothing adaptive: a regime that follows the game's jitter amplifies its hitches, and the network places the picture right only at
fixed moments (docs/NEURAL_RIFE_CUDA.md "Output time"). The 2/3 rate and the uncapped mode (a content clock asking for any moment) and the
classic / edge-blend syntheses were removed in 1.5.0.

**Pacing (shim + host).** The rate (`video.frame_gen_rate`: `half` = x2, `third` = x3) sets the IPC's `appPeriodMul` = m: the shim releases
the game every m-th compositor boundary, so its frames come one game period (m display periods) apart and each is rendered for the boundary
it will be shown at. The user's game fps lock (`appMinPeriodUs`, `video.frame_gen_cap_fps`) applies on top, in every mode. The host's encoder
thread keeps a fixed pattern per game period (`pipeline.rs` `FgState`): rendered frame B arrives, A being the previous one, and the
boundaries get S(A, B, 1/m), ..., S(A, B, (m-1)/m), then B itself. A frame C that arrives while any of these is still due waits in the intake
(the clock thread does not pick it), so the schedule never shifts and every rendered frame is shown in order; if the encoder fell a boundary
behind, the late B is dropped and C's pattern starts at once (the motion stays continuous). A boundary with nothing due waits up to 4 ms
for the expected frame (`fg_wait_arrival`), else re-sends B rotated to the new head sample. Every output is stamped with a tracking sample
newer than the last sent frame, one sample kept in hand, and rotated to it (head turns stay at the display rate); no such sample: the
boundary is skipped and what it owed stays owed. A frame without a projection layer (a loading screen, a menu of quads) goes out as is and
the generator's history restarts. The streamer's x2 / x3 buttons change the rate live (stdin `frame_gen_rate`: the pattern and
`appPeriodMul` change together, the game record starts a new segment).

**The generator** (`nvenc/hostlib/framegen.cpp`) runs on the **host encoder thread**, on the encoder's D3D11 device and immediate context:
no thread of its own, no extra copy of the stream frame on the way to NVENC. Per rendered frame it copies the slot into a short history
(4 frames), turns the older frame of the pair into the newer frame's camera (the head's rotation and the measured camera turn removed) and
runs NVIDIA Optical Flow on the pair (the driver's `nvofapi64.dll`; the camera turn measures from it and the edge synthesis draws from it).
Per output boundary it draws the picture (mode 0 = a real frame re-rotated, mode 1 = a generated one at time t = k/m) and encodes it.
Two syntheses (`video.frame_gen_synthesis`): **neural** (default, below) and **edge**, the fallback: the optical flow's vectors with a
3 x 3 median, the vector solved where the content comes from, a per-pixel choice at motion boundaries, doubtful motion shrinking toward the
real frame (ideas studied in VectorXR, re-implemented).

**The neural fields** (synthesis `neural`, the default since 1.4.2; `nvenc/hostlib/neural.cpp`, `nvenc/rife/`, docs/NEURAL_RIFE_CUDA.md), per network run:

| Step | Where | What |
|---|---|---|
| prep | D3D11 compute pass | both frames of the pair at half the eye's resolution (padded to multiples of 32), the older one in the newer camera, as planar floats into structured buffers registered with CUDA |
| map | CUDA driver API (`nvcuda.dll`, no toolkit), the D3D11 adapter's primary context, one stream | `cuGraphicsMapResources` orders the network after the D3D11 work |
| network | `bin\neural\airframexr_rife.dll` (our CUDA + CUTLASS, loaded by name, interface number checked), weights `rife44.afxw` | RIFE 4.4: per block a pack kernel (the warp computed only where the downscale reads), 10 implicit-GEMM convolutions, the last one, an upscale-and-add into the fields (flow to each frame, blend mask) |
| unmap | CUDA | orders the D3D11 output pass after the network |
| output | the edge synthesis's pixel shader (`neuralSynth`) | one sample of each original frame through the fields and the rotation chain, the older frame outside the newer view, still content kept exact |

The fields belong to one output time: x2, one network run per game frame at t = 1/2 (run as soon as the pair exists); x3, one per output,
at 1/3 and 2/3. The edge synthesis draws every output when the user picks `edge`, when the network's files are missing, on a GPU before
compute capability 8.0 (RTX 30) or after a failure (logged in debug mode). The network enqueues in a fraction of a millisecond of the encoder thread's CPU and
costs GPU time on the shader cores the game also uses (docs/NEURAL_RIFE_CUDA.md "Checks"); its buffers are allocated once.

## Field of view in 1.4

One rule holds the design together: **the engine never knows the headset type.** Everything headset-specific is computed by the launcher's session compile (from the headset profile in `profiles\`) and reaches the engine through the parameters it already reads (`session.json`, `airframexr.json`, host stdin commands), or lives in our own headset apps. The plan and its measurements: `docs/FOV_CONTRACT_PLAN.md`.

### Every headset

- **What the game is told** (shim and host): one box for both eyes (IPC `fovScale`, mirrored), the headset's declared field of view with each edge scaled by the host's fraction (`video.fov_override` and the four tangents `fov_outer` / `fov_inner` / `fov_upper` / `fov_lower`, capped at what the headset declares). The host takes the restriction from the settings only, when it starts (1.4-foundation, IPC v38: a `fov_override` line on stdin is ignored and logged as `command_ignored`, the render ceiling is gone; lab flag `--settings <file>`). The shim answers `GetEyeFov` and the recommended size (`ovrshim/render_size.h`, the area rule over the eye size) when the game starts; VDXR latches both at that moment, so a change is for the next game. Composition keeps the headset's frame and leaves what the game does not draw black.
- **The message to the headset app**: the host sends one JSON line over ALVR's `ServerControlPacket::Reserved`, with the first real-time update of every connection and again when it changes (`tools/patches/headset-view-message.patch` in `server_core`; `vision::client_message`):
  `{"airframexr":1,"view":{"left":[outer,inner,upper,lower],"right":[...]},"sharpen":true}`, from `video.client_box` (no `view` when absent) and `video.client_sharpen` (default on; `client_sharpen on|off` on stdin, forwarded live by the streamer window; lab flag `--client-box`). The host logs it at connection (`sent to the headset app`). A stock ALVR client ignores Reserved packets. When the app declares the box it was sent, the host's scale is 1 on every edge: the game is told exactly the stream's box and renders the stream's own size, so the compose pass copies pixel for pixel. The lab's mock headset takes the message too (`client-reserved-message.patch`, `client-reserved-message-capi.patch`, scenario `e2e_fov_client_box`).
- **Profiles** (`config\user_fov_profiles.json`, `UserFovStore` in `tools/gui/UserFovProfiles.cs`): per profile the four tangents (`tan`, the larger value of the two eyes per edge), optionally per eye (`tan_left` / `tan_right`), the measured window of a sweep, and since 1.4 the **mask** (`mask`: per eye four corner fractions, upper-outer, upper-inner, lower-inner, lower-outer; `FovMask`). A corner fraction f is a straight line across that corner, perpendicular to the direction from the view axis to the rectangle's corner, at f times the corner's distance (1 = nothing cut); the rectangle and its four lines make an octagon per eye. The FOV card and the map show it. **The mask is stored only**: nothing sends it to games yet (planned: the shim's `ovr_GetFovStencil`, which VDXR already maps to `xrGetVisibilityMaskKHR`).
- **The mask in the encoder and the frame generator** (1.4-foundation, `docs/PLAN_1_4_FOUNDATION.md`): when the profile in force has a mask and the encoding is foveated, the session compile (`MaskEncoding` in `tools/gui/PipelineModel.cs`) writes `video.encode_mask` (`polygon_uv_v1`: per eye the octagon in the stream eye's uv over the box the stream covers, u = 0 at the outer edge, v = 0 at the top); none with "No foveation", without a mask, or when the restriction was changed by hand after the profile was chosen. The host hands the polygons to the encoder library (`nvh_set_mask`) before the encoder starts: the QP map gives every 32 x 32 block whose whole footprint, through the inverted foveation, lies outside its eye's polygon a QP that climbs from its normal value to +51 across a band of 10 % of the eye past the outline, and +51 beyond (blocks that touch the outline keep their normal QP; `nvidia_profile::MASK_QP_BAND`, since 2026-11-21: +51 at once was too abrupt), an optional flat fill clears the blocks at +51 to black (lab switch `mask_fill`, off), and the frame generator's output pass draws only each eye's polygon plus that band (`docs/FRAME_GENERATION.md`). ALVR's six foveation numbers stay the separable two-level foveation (it cannot follow cut corners); on the Vision Pro the launcher computes them and shows them read-only, and its Encoding radios read "Foveated from the mask of <profile>" (the default) / "No foveation". Lab: `e2e_qpmap_mask`, `e2e_qpmap_mask_fill`, `harness/mask_compile_check.py`.
- **The calibration** is a scene of the probe (`tools/probe/xr_tool.h`, `XR_PROBE_TOOL=fit`), the same for every headset: four bright edge lines and four dotted corner lines, one eye at a time, keys read from the PC keyboard (`GetAsyncKeyState`, or scripted for the lab with `XR_PROBE_KEYS`). Everything is on the numpad (Num Lock on or off; the arrow keys are the same edge keys): 8 / 2 / 4 / 6 push an edge out until the lens cuts it, 7 / 9 / 1 / 3 (the screen corners) push a corner line out; Shift + the same key brings a line back, until the wearer just sees it; Enter ends an eye. An Xbox controller does the same (XInput, also read whatever window has the focus; for laptops without a numpad): the right stick pushes the line in its direction out, the left stick brings it back, the diagonals move the corner lines, A starts and ends an eye. A corner line pushed until it just disappears hides nothing visible (the window is convex); one brought back until just visible cuts a sliver at the far edge, the wearer's choice. `calibrate.exe` starts it on the profile in use (`XR_PROBE_START_BOX`, `XR_PROBE_START_CORNERS`) with `XR_PROBE_PARENT_PID` (the scene ends with the window), stops the desktop mirror, takes the keyboard focus, sets the restriction aside while the scene runs, and keeps the result as a new profile with both eyes' edges and corners. Lab: `e2e_fov_tool_fit`, `e2e_fov_tool_mask`, `e2e_fov_tool_pad` (scripted controller events).

### Apple Vision Pro only

Detected by the headset's name (`AppSettings.Kind == HeadsetKind.VisionPro`); the numbers come from `profiles/apple-vision-pro.profile.json` (Apple's rasterization rate map: full rate = 2292 px per tangent unit, 40 px/deg at the centre of the view).

- **Picture presets** (`video.stream_density`, the FOV card's *Picture (Vision Pro)*; `PipelineModel.StreamDensity`): the stream size follows the golden resolution (Settings, 3648 x 3200 by default) and the box the app declares, not the bitrate. *Performance* = 3/4 of the default density; *Balanced* (default) = the default resolution's density over the Vision Pro's whole box (about 23 x 25 px/deg); *Sharp* = the default resolution's pixels over the declared box. All capped at Apple's full-rate density and NVENC's width. The game renders the stream's own density over its box (1.3's "Spend the savings" and the stream fit do not apply). The bitrate is a result: the bits per pixel it leaves are shown and logged. `bitrate` (the 1.3 sizing) is only taken when set by hand.
- **Foveation** (`PictureEdgesFoveation.Apply`, `RateMapFoveation.Milder`): ALVR's two-level foveation from Apple's map, never stronger than ALVR's defaults; with a preset the full-density band follows the map inside the restriction too (`insideFollowsMap`), and outside the window, which the game does not draw, compresses up to the cap.
- **The box sent to the app**: with a preset and a restriction, the compile writes `video.client_box` = the restriction capped at the Vision Pro's own box at FoV 1 (`StreamDensity.AvpBoxAtFov1`, 1.79 / 1.02 / 1.22 / 1.02: the client circumscribes the canted views), a derived value like the render quality. With the stock vendor view (no restriction) no box is sent and the app declares its whole frustum.
- **Our visionOS app 1.4** (`kteque/alvr-visionos`, branch `1.4-fov`; TestFlight, internal testers): a `client_core` patch (`patches/client_core-reserved-message.patch`) turns the Reserved text into an event; the app declares the box it was sent (per eye, never wider than its circumscribed frustum; logs `[view] declared ...` to the streamer), has no FoV sliders any more, and sharpens the magnified picture when told to (AMD FidelityFX CAS in its single video pass, luma only, a shader function constant). With an older streamer it declares its whole frustum. A 1.3 app still declares its sliders' box: the streamer window then warns that the app did not take the box (`CheckCompiledBox`, which also takes the warning back when a later declaration matches, since a reconnect starts with a transient first declaration).
- **The readout**: the FOV card shows what the next start gives at the centre (stream and game px/deg, the headset's 40, encoded size, bits per pixel at the session's bitrate and refresh) and the slider values a 1.3 app would need; the `session compiled` log line carries the same numbers and where the box came from. `harness/density_check.py` runs the real compile through `uitest.exe` against the Python model (`harness/fov_contract_model.py`).

### Meta Quest and other headsets

- No preset, no box for the app, no rate map: `PictureEdgesFoveation` and `StreamDensity` run for the Vision Pro only, and the compile writes no `client_box` (the host's message carries only `sharpen`, which a stock app ignores).
- The restriction works as in 1.3.5: the game is told the profile's box, the stream covers the box the headset declares (the Quest 3's own lens window, 54 / 40 / 44 / 55 degrees), and the margin is black; the game's GPU saving is the gain. The stream size is the resolution in Settings; the foveation numbers are the session's (the headset profile's).
- The FOV card keeps 1.3's rows for these headsets (the render percentage, the stream fit, the pipeline preview); the calibration and the mask storage work the same as for the Vision Pro.
- Our Quest app (`external/alvr-quest`) does not take the message yet (plan items Q1-Q3: the same `client_core` patch, the box declared from `xrLocateViews`).

## Settings: golden master and compiled session

Three files, three roles. Nothing but the first two is edited by a person or a window.

| File (`config\`) | Role | Written by |
|---|---|---|
| `airframexr.json` | AirFrameXR's own settings: the restriction (`video.fov_*`, the profile in use `video.fov_profile`), the Vision Pro's picture preset (`video.stream_density`) and the app's sharpening (`video.client_sharpen`), the stream fit switch and its density reference (1.3, other headsets), "Spend the savings" percentage, hand-edit flags, display, audio | the windows (Settings, wizard, FOV panel, Advanced) |
| `session.base.json` | **the golden master** of the ALVR settings: the resolution the user chose, fps, bitrate, codec, the foveation numbers the user typed, audio, chroma, the pairing | the windows only (`Session.Load()` / `Session.Save()`) |
| `session.json` | **transient**: what ALVR and the host read for this streamer run | `SessionCompiler.Compile` before every streamer start (`tools/gui/PipelineModel.cs`), then ALVR itself while it runs |

The compile step (`RuntimeForm.StartHost`, before `host.Start`) is a pure function of the golden master, `airframexr.json` and the headset's declared box (`airframexr_views.json`):

1. Clone the golden master.
2. **Stream size.** The golden size is the user's resolution. A Vision Pro with a picture preset: the size follows the preset and the box the app will declare (see "Field of view in 1.4"; the fit tick and the typed-size flag do not apply). Other headsets: with the stream fit on, the size becomes `FitEye(reference eye, reference box, declared box)` (the same pixels per tangent unit over the smaller box the client declares), capped at what NVENC takes; otherwise it stays as typed.
3. **Foveation** (a Vision Pro on the foveated profile): `PictureEdgesFoveation.Apply` computes the geometry from the picture edges, Apple's rate map and the restriction window, over the size from step 2 (with the 1.3 `bitrate` sizing it also sizes the stream for the bitrate). Foveation numbers the user edited by hand (`video.foveation_manual`) are left as they are. With a preset and a restriction it also writes `video.client_box`.
4. Write `session.json` atomically. The launcher then adds what depends on the audio route (ALVR's `mute_when_streaming`) to that file, and the host applies the NVIDIA profile and the encode profile to it as before.

What follows from it:

- **The restriction, the fit and the bitrate never change the resolution the user chose.** They change the compiled size, at every start, and the FOV panel shows the compiled size (`SessionCompiler.Preview`); Settings shows the golden one. Before 2026-11-08 the fit, the foveation and the FOV panel wrote the stream size into the only session file, over the user's resolution.
- **A hand edit of `session.json` is lost at the next start**, on purpose. Edit the settings in the windows (or `session.base.json`).
- **ALVR's own state is the one thing that flows back.** A headset ALVR trusted while the streamer ran is in the live file only; the next compile takes `client_connections` into the golden master (as Disconnected) when the live file is newer than the master, so a pairing made in a window in between wins.
- **Two derived values live outside `session.json`**, in `airframexr.json`, because the engine reads them from there: the render quality (`video.fov_render_quality`, from the "Spend the savings" percentage and the compiled size; 0 with a Vision Pro preset) and, for a Vision Pro with a preset and a restriction, the box the host sends to the app (`video.client_box`).
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

## Logging (1.3.10)

**One writer thread per process owns the log files; everything else only queues lines.** The code is `tools/host/src/logsink.rs` (std only, no ALVR types; its unit tests run on their own with `rustc --test`) behind the `logs::` API of the host (`general`, `run_line`, `game_start`, `game_end`, `set_level`, `flush`).

```
host code / ALVR's log crate ──► Sink::log ──► bounded queue ──► log-writer thread ──► logs\AirFrameXR.log
 (any thread, never blocks)       stamp + level check (one          (format, batch, flush        logs\games\<exe>\<yyyymmdd-hhmmss>.log
                                  atomic read), try_send            every 200 ms and at once     logs\debug\<time>\...  (debug level only)
                                                                    for warnings, roll, prune)
```

- **Callers never wait for the disk.** `log` stamps the line, `try_send`s it and returns. A full queue drops the line and counts it; the writer then writes `N log lines dropped (queue full)` once. A log that stalls a frame is worse than a gap. The frame path logs nothing at the default level: it increments counters, and the writer's side prints the summary lines.
- **Verbosity is one scale** (`logsink::Level`): `error`, `warn`, `info` (default: events, warnings, errors), `verbose` (the periodic detail lines: phase lock, tracking samples), `debug` (the debug session folder with its CSVs and raw streams). It is set by `log_level` in `airframexr.json`, `--log-level`, the stdin command `log_level <level>` (live), and the old `debug` switch is kept as an alias for the `debug` level (turning it off returns to the chosen level). The filter is one atomic read on the caller's side, so a line that is filtered out costs no allocation.
- **Line format** (unchanged, so the old readers keep working): `yyyy-mm-dd hh:mm:ss.mmm [component] LEVEL message`.
- **Two destinations.** `AirFrameXR.log` has what matters later, small: start and stop, headset connect, negotiated stream, game start and end, the run record, warnings and errors. The **run's own log**, `logs\games\<exe>\<yyyymmdd-hhmmss>.log`, opens at `game started` and closes at `game ended`; it has the run's timeline lines (frame generation perf every 10 s, memory every 30 s; `verbose`: phase lock, tracking samples), which are not written to the main log. Lines meant for the run (`Dest::RunOnly`) are dropped when no game runs. The desktop mirror does not get a run log.
- **History.** `AirFrameXR.log` rolls over at midnight to `AirFrameXR.<yyyy-mm-dd>.log` (and past 10 MB to `AirFrameXR.<yyyy-mm-dd>-<n>.log`); the last 7 days are kept. A run's log is named by its start time, so a game's folder sorts as its history; the last 20 runs per game are kept. A failed rename (another process holds the file) keeps appending to the same file.
- **A panic or exit flushes**: the panic hook and `host stop` wait (up to 2 s) until the queue is on disk.
- **The telemetry build uses the same framework.** It differs only in what it sends to it (the per-frame traces and CSVs at `verbose` and `debug`); there is no second logging path. The stock build keeps the frame path free of per-frame records, as before ([Telemetry and the Black Box](#telemetry-and-the-black-box)).

**Two logs that deliberately stay outside this framework** (decided 2026-11-12):
- **`mirrordesktop`** is an OpenXR client like any game, so it has its own writer, its own files and no channel to the host: `logs\mirrordesktop\<yyyymmdd-hhmmss>.log` per run (last 20 kept), the same line format, its own in-process queue and thread so its render loop never waits on the disk. The host does not open a run log for it either. (Today it still writes the single `logs\mirrordesktop.log`.)
- **The UIs** get a log of their own for pure UI problems (windows, DPI, layout, unhandled exceptions, host launch and exit, the commands a user action sent): `logs\ui\<yyyymmdd-hhmmss>-<component>.log`, one file per process and run (no concurrent appends), last 20 per component. The lines that tell the host's story (host launched, host exit code, errors the user must see) stay in `AirFrameXR.log`. Planned, not built.

**Status on 2026-11-12** (what is built, what is not):
- Built, unit-tested alone and on the builder (25 host tests): the writer thread, levels, drop on overflow, midnight roll, size roll, day pruning, run logs and their pruning.
- Tested on the 3080 Ti builder (debug/telemetry flavour): the host's lines through the writer, `log_level`, run logs, `[alvr]` lines in the unified log with the raw stream only in the debug folder, the shim's lines through the ring buffer in the IPC block (`ipc.h` v37), `mirrordesktop`'s per-run log file; scenarios `e2e_installed_layout`, `e2e_log_level_info`, `e2e_log_level_verbose`, `e2e_mirror`; `log_timeline_check` on a run log; `ui_matrix` (one case, 64 renders) with its UI log. One run of `e2e_log_level_info` failed because the host never started (builder shared with another session); it passed when repeated.
- Built, **not tested**: the shim's fallback when no host is alive (direct append), a game crash with lines still in the ring, the UI log's window/DPI/exception capture in the real windows (the matrix only exercises the per-case lines), the export zip with run logs, the release flavour, the full suite.
- **Not through the writer:** the GUIs' `AirFrameXR.log` lines go through their own in-process writer thread (batched appends, no host needed); they are not routed through the host.
- **Not done:** one-line-per-minute summaries at `info` (gap/tick histogram, decoder queue, encode time), the header line at start and on roll-over, the redacted "export for sharing", `tools/log_summary.py`; the Black Box analyzer still reads nothing from the run logs.
