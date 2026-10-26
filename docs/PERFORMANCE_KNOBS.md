# Performance knobs: what each one does, what we measured, what should drive it

Written 2026-10-17 after two headset nights on an RTX 5090 with a Vision Pro (90 / 100 Hz) and a Quest 3 (90 Hz), the lab on a 3080 Ti, and
the sessions of 2026-10-13 to 16 before them. The purpose: a future launcher page that proposes each knob from measurements of *this* PC,
router and headset instead of defaults tuned on one rig. Structural choices (bitrate, resolution, foveation, codec) are left out: they set
the budget; the knobs below decide how the budget turns into motion on the panel.

Every number here is from a log or a lab verdict named in docs/PERFORMANCE_FINDINGS.md. "Headset repeated" is the headset's own count of
display refreshes that showed no new frame; "host repeats" the outputs the streamer re-sent because the game had not delivered; "skipped"
the boundaries with no usable tracking sample.

## The frame-generation family

| knob | where | default | what it does | measured | what should drive it |
|---|---|---|---|---|---|
| Frame generation on / off | launcher tick, `video.frame_gen` | off | paces the game to a fraction of the display rate and interpolates the rest | games under the display rate: headset repeated 38-52 % -> 0.3-2 %; latency +1 game period (86 -> 57 ms on a 73 fps game at 1/2; 67 ms at every level since); some games look better raw to the user (Gears of War: 56 fps raw preferred to 2/3) | game fps against the display rate (the only measurable part); the rest is taste, so a per-game memory of the user's choice, never a proposal |
| Rate 1/3, 1/2, 2/3 | slider, `video.frame_gen_rate` | 1/2 | the cap the game is held to: 30 / 45 / 60 fps at 90 Hz, 33 / 50 / 67 at 100 | a game under its cap makes host repeats: Valor Mortis 1/2 = 15-31 % host repeats with the headset at 0.4 % (frozen content the headset counts as shown); 1/3 = 0.2 %; Dead Space and End of Abyss hold 2/3 (1.6-2.4 % headset repeated over 2-10 min) | **done**: `config/games.json` per game, headset and level (fps, slowest tenth, headset repeated typical and heavy parts, host repeats), the "Recommended" tick picks the fastest clean level. A clean level = both repeat counts under 5 % in the typical 10-second window |
| Display refresh rate | follows the panel | follow | 90 or 100 Hz on the Vision Pro as visionOS decides (100 under 50 Hz lighting whatever the client asks); the caps move with it | same game, 1/3: 30 fps cap at 90 Hz, 33 at 100: a game at the edge feels it; the panel switched 90 <-> 100 three times in one hour of Gears | nothing to choose; the UI should **show** the rate in force and key the records by it if a game flips between clean and dirty with the lighting |
| Flow scale, perf preset, grid | fixed: scale 0.5, preset 20 (fast), grid 4 (the first two were settings until 1.0) | as listed | optical-flow resolution and search effort | flow 2.3-3 ms per output on the 5090 at 0.5 scale; the FRUC probe gave 11.2 ms at full foveated size against 6.9 at half; the confidence (saturate(1 - 6 max\|dRGB\|)) is what ghosts small hard-edged objects, not the scale | GPU time per output (`gen.flow_ms`) against the display period: a UI can show the generator's share of the period and refuse a scale that does not fit; untested on anything but the 5090 and 3080 Ti |
| Uncapped mode, fps cap, content lag | streamer tick "Uncapped" (2026-10-23) = `frame_gen_rate: "uncapped"`; config only: `frame_gen_cap_fps` | off | the game runs free, every output interpolated behind a content lag | wins below and above 50 fps, not at 50 (night of 2026-10-15); a cap at ~90 % of the sustained fps shortens the lag 20-30 ms; about two game periods of latency | the `frame generation perf` line: `app fps` with most `releases late` = GPU-bound = cap it. In the streamer since 2026-10-23 (rig: smooth where 1/2 judders under the cap, 1/2 better at the cap) |

## The arrival family (where a frame lands against the headset's tick)

| knob | where | default | what it does | measured | what should drive it |
|---|---|---|---|---|---|
| **Arrival margin** | `AIRFRAMEXR_PHASE_MARGIN_MS` or `video.phase_margin_ms` in airframexr.json | **7** (was 3 until 2026-10-17) | the phase lock steers the earliest 10 % of arrivals this far before the headset's render tick by sliding the host's clock | the arrival cluster spans 8-9 ms of a 10 ms period, generation on or off; at 3 ms a share of frames landed after the tick and were shown a tick late: headset repeated 1.2-2.3 %; at 7 ms (first by accident, debug mode delaying every frame 4.6 ms, then by hand) 0.5 %, latency unchanged at 67 ms because the panel displays at its next tick either way; the user: "much better" | **the knob the UI should own.** Measure the arrival spread (`phase_lock` events: `spread_ms`, `est_ms`) and the client's decoder queue p5; propose the smallest margin that puts the p90 arrival before the tick. The right value depends on the PC's encode variance and the Wi-Fi, not on the GPU model: 7 on this rig, maybe 5 on a wired-quality link, maybe 9 on a noisy one. Cost: up to one period of latency, invisible under the panel's quantum until it crosses a tick |
| Phase lock on / off | `AIRFRAMEXR_PHASE_LOCK` | on | the controller above | slides every 2-4 s by up to 2 ms in every session (12-25 per minute); 999 slides in an hour of Gears with generation off, where its input (arrival phase of a free-running 56 fps game) means nothing | keep on; add a guard: no action when the stream runs well under the display rate (generation off and app fps < 0.9 x panel) |
| Tracking-sample wait | `AIRFRAMEXR_FG_SAMPLE_WAIT_MS` | 3 | how long a boundary waits for a tracking sample newer than the last sent frame | with one sample kept in hand (2026-10-16 fix) the wait is rare: 0-7 waits per 1000 outputs, skipped 0.2-1.9 % | the tracking spacing p95 and the share of gaps over 15 ms (`frame generation samples` line): bunched Wi-Fi needs the in-hand rule, a regular 100 Hz stream pays one period of pose age for it; the 67/77 ms cycles are this regime switching (a sample late by more than a period); `regime_switches` in the samples line counts them; `video.frame_gen_hold_age` (off) trades each switch for one repeat; the Vision Pro night of 2026-10-23 measured 5.5 switches a second, so the hold cost 2.4 repeats a second: off |
| Running-start margin, head wait, intake gate | `running_start_ms` 2, `head_wait_ms`, `intake_gate` on (the intake depth is fixed at 3) | as listed | the shim releases the game a lead before the boundary; the intake keeps up to three frames in order and skips one release when a standing queue builds | 2026-10-13: the gate works when the game keeps up and fires uselessly in GPU-bound games (3/s), hence "only without recent empty boundaries"; stock ALVR's standing queue was 11.7 ms on the same rig | `encoder_stats` pipeline counters (`intake_drains`, `gate_holds`, `standing_total`); no UI value proposed yet, the defaults held through both nights |

## The GPU and encoder family

| knob | where | default | what it does | measured | what should drive it |
|---|---|---|---|---|---|
| GPU keepalive | `--gpu-keepalive <duty %>` or `video.gpu_keepalive` | off (90 in lab scenarios) | a small D3D11 load that keeps the GPU out of its idle power state | a lightly loaded laptop GPU sits in P8 with the video clock at 555 MHz: encode 30 ms against 9 ms at P0; the 5090 stayed P0 in every game (menus and idle: P1) | `gpu.csv`: p-state and video clock while streaming; propose the keepalive on laptops and whenever encode time rises with the GPU under 30 % busy |
| GPU scheduling class | fixed at 5 (realtime; was `gpu_sched_class`) | 5 | the host process's D3DKMT priority | 0 was clearly worse on 2026-10-23 (stream 70-86 fps, headset holes 25 %); the levels between were never tested | leave; logged |
| NVENC preset and multi-engine split | `encode_profile`, session `encoder` block | P3 low-latency on the 5090 | encode speed against quality | the lab must use P2/P3 (P5+ breaks the period); encode p95 4.3 ms per output here, 9 ms in debug mode (open bug) | encode p95 against the display period: a UI can warn when encode leaves no room for the generator |
| Debug logging | launcher tick | on | observation only, in principle | **costs 4.6 ms of send delay per frame** on this rig (encode p95 4.3 -> 9.0 ms, send after tick 3.9 -> 8.6 ms); it is how the margin was found. Not a knob: a bug to fix (suspects: the NVML sampler thread, the event writer) | fix it; until then the UI should not offer it as a performance setting |

## Removed on purpose (so nobody re-invents them)

- **Just-in-time game release** (2026-10-16 -> 17): the hold never engaged (holds 0 in 8 000+ releases, UE and Unity alike) and was removed
  with its IPC field and setting. **Corrected 2026-10-23:** the cause was VDXR's own running start waking the game's `xrWaitFrame` a period
  minus 2 ms after the previous release, whatever the shim did (docs/PERFORMANCE_FINDINGS.md, 2026-10-23). VDXR patch 5 turns it off, so a
  release is a frame start again; the just-in-time hold is a candidate once more (docs/PLAN_0.6.md).
- **The automatic rate controller** (2026-10-16 -> 17): worked where the stream's verdict was clean, but every trial was a hiccup, the
  frame cost it first relied on measures nothing useful (5-10 ms for every game at 1/3: the GPU tail of a frame submitted right after its
  release), its memory keyed by executable let the Vision Pro's verdicts pollute the Quest's, and the "off" level it climbed to lost to 2/3
  on both headsets. Replaced by the run record and the launcher's proposal.
- **The frame-cost readout** with it; **the predicted tracking sample** (jitter); **the duplicate-timestamp re-stamp** (needs the client).

- **Eleven settings, removed in 1.0.1** because the measurements showed no benefit or a loss (a `airframexr.json` that still has them is ignored, one log line):
  `send_pacing` (asap | vsync | phase: no visible gain over asap, so a frame leaves when it is encoded), `boundary_offset_ms` (never found useful),
  `frame_gen_flow_scale` (0.25 cut the flow from 2.2 to 1.6 ms and changed nothing else; fixed at 0.5), `frame_gen_perf` (slower presets risk late outputs,
  never tried on a headset; fixed at fast), `frame_gen_hold_age` (the stamp-regime hold: the Vision Pro night measured 5.5 regime switches a second and the
  hold turned them into 2.4 repeats a second, off for good), `pick_policy` (`oldest` left a standing queue a period late, `newest` lost 15-30 % of the frames
  of heavy games, `adaptive` / `aged` never beat `drain`, now the only policy), `intake_depth` (3), `client_queue_drain` (never fired in the lab, never tried
  on a headset), `follow_client_rate` (off pinned the 90 Hz grid and broke the Vision Pro's 90 / 100 Hz switching), `gpu_sched_class` (fixed at 5) and
  `frame_gen_lag_ms` (the automatic lag is the only sensible value). Their environment variables and command-line flags went with them (`AIRFRAMEXR_PICK`,
  `AIRFRAMEXR_INTAKE_DEPTH`, `AIRFRAMEXR_FG_HOLD_AGE`, `AIRFRAMEXR_CLIENT_DRAIN`, `AIRFRAMEXR_FG_LAG_MS`, `AIRFRAMEXR_FG_FLOW_SCALE`, `AIRFRAMEXR_FG_PERF`,
  `--send-pacing`, `--boundary-offset-ms`, `--gpu-sched-class`, `--no-follow-rate`, `--frame-gen-lag-ms`, `--frame-gen-flow-scale`, `--frame-gen-perf`) and the
  pacing experiments that compared them (`harness/scenarios/pacing/exp/x_d*`, `ftf_tokyo_100hz_nofollow`, `pace_vsync`). The git history before 1.0.1 has the code.

## What a measurement-driven page would show

Per connected headset and PC, from the counters that already exist: the panel rate in force; the arrival spread and where the cluster sits
against the tick, with the margin proposal; encode p95 and the generator's GPU time against the period; the tracking spacing (Wi-Fi quality
in one number: p95 and gaps over 15 ms); the GPU p-state while streaming; and, per game, the record table the "Recommended" tick already
reads. Each proposal with the measured reason next to it, applied by the user, remembered per rig. The engine stays as it is: it measures and
records; the launcher proposes.

## Implemented in 0.5: the suggestions in Diagnose

`tools/gui/Advice.cs` is that page, in the Diagnose window and the wizard's results page. It reads the wizard's measurements of the run (encode p95, tracking gaps and rate,
repeated head samples) and, when there are any, the logs: the newest debug session with a game streaming (`encoder_stats` windows at the display rate: encode p95, tracking gap
p95 and the share over 15 ms; `phase_lock` events: the arrival spread; `gpu.csv`: power state, video clock, load) and the latest session of each recorded game
(`config/games/<exe>.json`: fps against the panel, the headset's and the host's repeats). Rules, each with its measured reason shown:

| suggestion | measurement | rule |
|---|---|---|
| encoder leaves little room for generation | encode p95 (wizard, else logs) against the period | hint at 50 % of the period, warning at 70 % |
| tracking arrives in bursts (Wi-Fi) | tracking gap p95 over 15 ms, over 15 ms in more than 5 % of gaps, or under 0.8 x the display rate of samples | warning; the Wi-Fi advice from the nights (channel without neighbours, Ethernet) |
| repeated head samples | the scene test's share of same-timestamp frames >= 5 % | hint; the known Vision Pro client issue |
| **arrival margin** | the phase lock's spread (median, p90) | proposes `round(0.85 x spread)` clamped 4-10 ms; says "the default 7 fits" within 2 ms of it. Apply button sets `video.phase_margin_ms` (next start); environment variable `AIRFRAMEXR_PHASE_MARGIN_MS`, not a launcher setting yet |
| GPU low power state | `gpu.csv`: median video clock under 900 MHz with the GPU under 30 % busy | warning; Apply button sets `video.gpu_keepalive` 90 |
| debug logging on | the setting | hint with a button that turns it off (4.6 ms of send delay per frame on the reference PC) |
| per game | last session: fps under 0.9 x the panel with the headset repeating over 5 % and generation off; host repeats >= 5 % under generation; headset repeats >= 5 % with the game keeping up; vectors on but none arrived; vectors flowing | frame generation as a taste; the next lower rate; the link and the margin; the DLL; good news |
| display rate | the headset's supported rates | information: 90 <-> 100 Hz moves the caps |

Not suggested, on purpose (the table above says why): the generation on/off choice beyond the measurable part, uncapped mode, the flow settings, the intake and sample-wait knobs.
`python3 harness/advice_check.py` tests every rule against synthetic metrics and fixture logs, including a clean setup that must stay quiet.
