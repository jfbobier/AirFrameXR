# OpenXR app behaviours that stress the chain (catalogue, 2026-11-13)

What the engine assumes of an OpenXR app, and the apps that do not follow it. Measurements are lab or field logs; "inferred" marks what no trace proved.
Where each one is handled: `docs/EXPERIMENTAL_SWITCHES.md` (`drop_duplicate_frames`, shown as "Maximum OpenXR compatibility mode" in Advanced), `docs/PERFORMANCE_FINDINGS.md`.

## What the engine assumes (the OpenXR contract as the shim sees it)

1. One `xrWaitFrame` / `xrBeginFrame` / `xrEndFrame` per display slot, in order, so the app's wait is what paces it (our throttle sits in the wait).
2. Each submit carries a new picture rendered with a new head pose, and a pose maps to one tracking sample (the frame's timestamp).
3. The app's CPU work between wait and end is the "frame"; one thread (or at least one in flight).

UEVR, Luke Ross's mods and native OpenXR games follow this: one submit per wait, poses from `xrLocateViews` at the predicted display time. Their oddities are rate and queue
effects (GPU-bound queues, bursts of pose reads), not a different frame loop.

## The behaviours

| # | Behaviour | Seen in | Measured | State |
|---|---|---|---|---|
| 1 | Every real frame submitted twice with the identical pose (a second wait / begin / end cycle in the worker thread); worker pool of about 9 threads collapsing to 1 at engine frame 12000; cutscene 30 fps lock | NoMoreFlat mod for AC Black Flag (acbfvr) | pairs 1 ms apart then 20 ms; ~25 pose-distinct fps submitted 50/s; second wait paced like a real one halves the real fps (lab mimic 22.5 vs 45) | fixed: game fps lock + `drop_duplicate_frames`, lab `e2e_modsim_*` |
| 2 | A layer generates frames: inside the app's `xrEndFrame` it runs its own wait / begin / end cycles, a generated picture in the app's slot then the real one, **same poses, other picture**; the app's begin takes a fresh wait; the app is paced to half rate | OFXR-Bridge (real layer built in the lab, V551 382065f) | n=3, 45 fps game: no OFXR 44.5 new frames/s shown; OFXR 87.9 (game 42.2 fps, 2.4 % repeated ticks); shim sees 50 % same-pose submits | **works through the chain**; stats only count 42.5 unique timestamps/s |
| 3 | The compatibility mode drops every same-pose frame | OFXR with the mode on | 71.6 new frames/s, 20.5 % repeated ticks (generated frames discarded) | open: parked fix = drop only when poses AND swapchain images (commit counts) match; patch `private/parked/dup_image_identity_2026-11-13.patch` (uncommitted idea, not in any release) |
| 4 | Two pose reads inside one 10 ms tracking sample, so two frames share a timestamp | Cyberpunk, Dead Space (Luke Ross) | 7-20 % same-timestamp frames | statistics only: the headset displays them (strobe test, 1.3.10 notes F3/F4); 1.3.10 UI counts display ticks |
| 5 | OFXR generation switching itself off when the GPU saturates | field log WEIRD HITCH, Borderlands 4 | dup rate 50 -> 0 %, submits 88 -> 52 fps, GPU 88 -> 97 %, VRAM -1.3 GB at the same time | inferred, not reproduced (needs GPU load in the lab: `XR_PROBE_GPU_COPIES`) |
| 6 | App's frame start invisible: `xrWaitFrame` right before `xrEndFrame` (VDXR calls `ovr_WaitToBeginFrame` right before the submit) | every engine measured | just-in-time hold never engages (0-2 holds in 8000 releases) | known limit: a frame-start hold would have to live in an OpenXR layer |
| 7 | GPU-bound games keep 2-3 frames queued; at 100 Hz half rate sits on the GPU edge and flips between two sticky states | Gears of War E-Day | see `gears-bistable` notes in PERFORMANCE_FINDINGS | open |
| 8 | Not OpenXR at all: D3D12 libOVR games | Lone Echo 2 | shim is D3D11 only; the bridge route works in the lab | experiment |

## Why most apps just work and some do not

- The engine and ALVR identify a frame by its tracking timestamp (the render pose's sample). Anything that submits more than once per slot, or a picture without a new pose, breaks that identity:
  statistics undercount, the pattern detector misreads, and the app's second wait is paced as if it were a frame. The picture itself still reaches the display (OFXR: lab).
- Whether VDXR / Virtual Desktop tolerates doubled or generated submits because its compositor warps each submit to the display-time pose is a HYPOTHESIS: nothing was measured on Virtual Desktop and
  VDXR's own reprojection path was not read for it. What is established: the OpenXR spec gives no frame ID (`predictedDisplayTime` is explicitly not one, and repeated `xrLocateViews` calls at one time
  may return different poses), so a pose or timestamp is a time coordinate, not an identity; ALVR's own design notes say the same about its timestamps.
- The two bad actors differ: acbfvr resubmits the SAME picture (a true duplicate: drop it, free the second wait); OFXR submits a NEW picture under the same pose (keep it). A pose-only test cannot tell
  them apart; the picture identity (commit count per swapchain) can.

## Lab tools

`xr_modsim` (mod mimic: `MODSIM_DUP`, `MODSIM_OFXR`, `MODSIM_STROBE`), the real OFXR layer scenarios `e2e_ofxr_tokyo_{off,on,on_compat}` and `harness/ofxr_report.py`, `e2e_modsim_control_ts` (clean-stream control).
