# Frame generation, uncapped: every output synthesised (2026-10-15)

Status: implemented on branch `fg/uncapped` (host pipeline, shim cap, GUI, logs), lab-checked on the 3080 Ti simrig; first headset night to come.
Builds on `docs/FRAME_GENERATION.md` (half rate, optical flow) and `docs/MOTION_VECTORS.md` (the game's DLSS vectors). Replaces the 2/3-ratio
plan of `docs/FRAME_GENERATION_RATIOS.md` (kept as the record of why ratios are not the answer).

## Idea

Half rate holds the game to half the display rate and alternates a rendered and a synthetic frame. **Uncapped** lets the game render at
whatever rate it reaches (paced at the display rate at most, like generation off) and synthesises **every** output: at each boundary the host
shows the content at a fixed lag behind the newest tracking sample, interpolated between the two newest rendered frames at the fraction that
time falls at. The game sees a regular cadence of its own; the headset sees a uniform 90 or 100 Hz with in-scene motion advancing every
period; every output is stamped with the newest tracking sample and rotated to it (uniform staleness, which the Vision Pro accepted at half
rate). A 5090 rendering Callisto at 76 fps keeps its 76 instead of being held to 50.

## How the host picks (`tools/host/src/pipeline.rs`, `FgState` uncapped fields, `fg_plan_uncapped`)

- Content time of a rendered frame = the tracking timestamp the game rendered it with (the shim's `frameClientTsNs`); regular when the game is.
- The generator keeps a ring of the **four** newest rendered frames with the flow (or vectors) of each consecutive pair (`FrameGen::kHist`);
  `Render(mode, t, age)` interpolates inside the pair whose newer frame is `age` frames behind the newest.
- Each boundary: `content = newest sample time - lag`. The pair of history frames that brackets the content time gives `age` and
  `t = (content - older) / (newer - older)` (clamped to [0, 0.98]). Past the newest frame: that frame once (real), then repeats
  (*behind*: the game is later than the lag covers). Before the oldest frame kept: the oldest pair at t = 0 (*ahead*: the lag is longer
  than three game frames).
- `lag` = the setting when given, else an **envelope**: at every arrival, `need = headset time now - the previous frame's content time`
  (the frame's own interval plus its render latency: the time during which the previous frame was the newest usable one); the lag follows
  `need + half a display period` with a fast attack and a decay of 0.2 ms per arrival, at most 80 ms. This is a jitter buffer: a late frame
  raises it at once, a quiet stretch lowers it slowly. A longer lag means fewer repeats and more latency; the logs give both.

Why the ring (lab, 2026-10-15): with two frames only, the lag that avoids repeats (about two game frames: interval plus render latency)
is longer than one game interval, so the content time fell before the older frame on a tenth of the outputs, which were then clamped to it:
t = 0 repeats, frozen 12 %, verdict JUDDER on the stutter scenario against 2 % for half rate. Four frames cover three intervals.
- Rendered frames are taken **in order and never superseded** (each is a link of the interpolation chain and the frame the game's vectors
  describe the motion from); the app is paced at the display rate so at most one arrives per boundary on average. No arrival wait at the
  boundary (the content lags by design). The generator is unchanged: one flow (or the vectors) per rendered pair, the synthesis pass at `t`.

## The knobs (all live from VisionALVR.exe, all saved, all logged)

| Setting (`config/visionalvr.json`) | GUI | Live command | Meaning |
|---|---|---|---|
| `video.frame_gen` | toggle "Frame generation" | `frame_gen on\|off` | the generator on the encoder, as before |
| `video.frame_gen_rate` = `half` (default) \| `uncapped` | "Game rate: Half / Uncapped" | `frame_gen_rate half\|uncapped` | this note's mode vs today's alternation; the shim's pacing follows (`appPeriodMul` 2 or 1) |
| `video.game_motion_vectors` (default on) | tick "DLSS motion vectors" | `game_mv on\|off` | the shim taps the game's vectors when the VisionALVR-OptiScaler DLL is in the game folder and the generator synthesises from them; off = optical flow only (the tap is idle) |
| `video.frame_gen_cap_fps` (0 = none) | "fps cap" | `frame_gen_cap <fps>` | uncapped mode: the shim never releases the game sooner than 1/cap after the previous release (IPC v27 `appMinPeriodUs`); the headroom knob below |
| `video.frame_gen_lag_ms` (0 = auto) | "lag ms" | `frame_gen_lag <ms>` | uncapped mode: the content lag; 0 = automatic as above |

CLI `--frame-gen-rate`, `--frame-gen-cap`, `--frame-gen-lag-ms`, `--game-motion-vectors`; env `VISIONALVR_FG_RATE`, `VISIONALVR_FG_CAP_FPS`,
`VISIONALVR_FG_LAG_MS`. The cap and the lag only act in uncapped mode (greyed out otherwise). Nothing engages by itself.

## What is logged (how to read the night)

- `frame generation config (<why>)` INFO line and `frame_gen_config` event at start and at every change: generation on/off, rate, vectors
  on/off, cap, lag. The night's choices are all there, in order, with the game's name from `game started`.
- Every 10 s while generation is on, `frame generation perf` INFO line and `fg_perf` event: app fps and the shim's frame-time envelope, late
  releases (the game free-running against the display), stream fps, measured game period with its p50 / p95 / max interval, the lag in
  force with the slack it left at arrivals (p5 / p50; negative = a repeat happened), the mean `t` and mean `age` of the synthetic
  outputs, `behind` (repeats: the game later than the lag) and `ahead` counts, the generator's stats (flow and synthesis GPU ms,
  `mv.used_eyes` = eye passes synthesised from the game's vectors, `mv.frames` = frames that carried vectors), and NVML's sample
  (`gpu %, mem %, enc %, dec %, graphics MHz, memory MHz, video MHz, power W, temp C, pstate, vram MB, clock-event reasons`).
- `motion_tap` event when the shim's tap state changes (attached / no provider / ABI mismatch), see `docs/MOTION_VECTORS.md`.
- The shim's own log (debug on): `pacing: ... period mul N, fps cap F (holds H)` every 900 frames, and the tap's streams and evaluates.
- With debug on, `logs/debug/<session>/fg_uncapped.csv` has one row per output (kind G/R/Q, t, age, lag, how far the content trailed the
  newest frame, the history's oldest and newest timestamps, history depth), `gpu.csv` the NVML sample every second, `host_frames.csv` the per-frame fates
  (`G` synthetic, `R` real, `Q` repeat), `pipeline` CSVs the arrival-to-send times.

A good uncapped session reads: `behind` near zero (the lag covers the game's jitter), mean `t` around 0.5 spread evenly, late releases
close to all releases (the game runs free), `gpu %` below 95 and the graphics clock not dropping, app fps steady, `mv.used_eyes` rising when
the DLL is in the game folder. A bad one: `behind` climbing (raise the lag, or cap the game), `gpu %` pinned at 100 with the app fps sagging
(cap the game: see below), or app fps above the headset rate with nothing gained (half rate or generation off is cheaper).

## The bottleneck concern, and the simple answer first

"Let it rip" lets the game take the whole GPU and CPU; the host's own work (flow or vectors, synthesis, foveation, NVENC) then queues behind
the game's frames, and queueing is what turns into late outputs and repeats. What already protects the host:

- The host's D3D11 device runs at the realtime D3DKMT scheduling class (`gpu_sched_class` 5) and NVENC is a separate engine; the flow engine
  is separate too. What competes is the synthesis and foveation passes on the graphics engine (about 1 ms per output on a 5090), and the
  copy traffic.
- The GPU's own scheduling preempts at draw boundaries, so a game drawing long passes delays the host by a pass, not a frame.

What the branch adds, deliberately simple: the **fps cap** (the shim holds the game's releases to a minimum spacing). Setting it a little
under what the game sustains (the `app fps` of the perf line) is the "throttle just below the average" idea, by hand, and it is logged with
its effect (`behind`, `gpu %`, app fps). Nothing automatic: the night's logs say whether a cap is needed at all, how far under the average it
has to sit, and whether the GPU or the CPU is the one that saturates (gpu % vs app fps against the game's known CPU-bound rate). If the logs
show a stable rule, an automatic cap (for instance 90 % of the measured game rate) is a few lines later; if they show the headroom problem
is small, the knob stays manual. Not tried on purpose: reserving GPU time (no such API on Windows beyond the scheduling class), or holding
frames back in the shim at submit time (that is the cap, in a worse place).

## Lab (`harness/simrig`)

Scenarios `stutter_unc` (flow), `stutter_unc_mv` and `stutter_turn_unc_mv` (the probe's exact vectors), `steady_unc`; compare with
`stutter_fg` / `stutter_mv` (half rate). The judge's motion verdict and the `fg_perf` lines in `host_stdout.txt`.

## Next

The headset night decides the automatic lag and whether the cap is needed. Then extrapolation from the vectors (no wait for the later frame:
the latency of the unthrottled game) and, with it, the lag goes away.
