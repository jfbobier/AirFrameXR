# Frame generation: a 2/3 ratio next to half rate (design note, not implemented)

Status 2026-10-15: design and lab plan only, no code. Builds on `docs/FRAME_GENERATION.md` (half rate, optical flow) and runs in
parallel with `docs/MOTION_VECTORS.md` (game motion vectors; steps 1 and 2 are on main).

## Goal

Today frame generation is always **half rate**: the game is throttled to half the display rate and every other output is synthetic.
A GPU that renders 60+ fps is held to 45-50 fps. Add one more manual choice, **2/3**, for games that hold about 60 fps: two real frames and
one synthetic frame per three display periods. This is the target of the work; 1/2 stays exactly as it is.

| Choice | Real frames per output | Game rate at 90 Hz | Game rate at 100 Hz | Synthetic share | Output pattern per cycle | t |
|---|---|---|---|---|---|---|
| Off | 1 | 90 | 100 | 0 | R | - |
| **2/3** (new) | 2 of 3 | **60** | 66.7 | 1/3 | R, R, S | 0.5 |
| **1/2** (today, default) | 1 of 2 | 45 | 50 | 1/2 | S, R | 0.5 |

Not planned: **1/3** (30 fps at 90 Hz). Decision of the user: lower the game's settings long before reaching that. (Luke Ross's AER v2 has
"1/2 rate" and "1/3 rate" modes that lock the game to that fraction of the headset refresh rate; 2/3 is not one of his modes.)

### Display rate: 90 Hz officially, 100 Hz sometimes, the notch follows

The Vision Pro runs at 90 Hz and occasionally switches to 100 Hz by itself. The host's boundary clock follows ALVR's vsync grid at the
headset's actual refresh (docs/ARCHITECTURE.md, "Host clock"), and the frame generation schedule is defined on that grid, so both notches work
at whichever rate the headset picks (the user's observation: frame generation stayed smooth either way). What changes with the rate is the game's
budget per frame:

| Notch | Game rate at 90 Hz | Frame budget at 90 Hz | Game rate at 100 Hz | Frame budget at 100 Hz |
|---|---|---|---|---|
| 2/3 | 60 | 16.7 ms | 66.7 | 15.0 ms |
| 1/2 | 45 | 22.2 ms | 50 | 20.0 ms |

So "2/3 = 60 fps" is the 90 Hz case, the one the user targets. If the headset moves to 100 Hz while 2/3 is on, the game is asked for 66.7 fps: a game
that was just holding 60 will start to slip and the user moves the selector to 1/2 by feel. No automatic switching (user's rule); the active
ratio and the measured period go into the session telemetry so a slip can be tied to a rate change afterwards.

## Why 2/3 works on the boundary grid

The shim already holds the game back to whole display boundaries (`appPeriodMul`). Replace the integer by a release **mask**: for 2/3 the game is
released at two of every three boundaries (1, 1, 0). The two released frames are one period apart, the next pair two periods later, so the
frame the app is shown with moves in steps of 1P and 2P. The synthetic frame S sits exactly halfway (t = 0.5) between the second frame of a
pair and the first frame of the next pair, the same interpolation as today's half rate, on one output in three. The pair that is one
period apart needs no synthetic frame between its two real frames.

Content time follows the predicted display time the shim returns to the app (`GetPredictedDisplayTime`), not the moment the GPU finishes,
so the 1P/2P spacing holds even when rendering is slow.

Latency (model, arrival of the frames at the host vs the schedule): with a frame cost r the outputs are shown about **one period later** than
without frame generation, the same added period as half rate, as long as the average r stays under 1.5 P (16.7 ms at 90 Hz, 15 ms at 100 Hz).
Worked case with r = 1.2 P and with r = 1.5 P (GPU fully busy): the schedule holds with the output delayed by three boundaries from the release.

### The main risk: the game's own time step (review 2026-10-15)

1/2 gives the game a regular cadence (one release every two periods). 2/3 gives it an irregular one: 1P, 2P, 1P, 2P. Head motion is safe
either way, the pose comes from the predicted display time the shim hands out. In-scene motion (animation, physics, moving objects) is only
right if the engine advances its world by the true wall-clock delta between releases: then the world time has the same 1P/2P spacing as the
display and the midpoint frame lands where it should. Engines that clamp or smooth the delta break it. Unreal's frame-rate smoothing clamps
deltas to a 22-62 fps band by default and is only disabled when the engine knows it is rendering in stereo; UEVR-injected flat titles (the main
target) are flat as far as the engine is concerned, so an 11 ms frame is advanced by 16 ms, a 22 ms frame by 22, and the world moves
16, 22, 16, 22 against a display that expects 11, 22: a one-third-period judder of moving objects that no generator can repair, because the
two frames it interpolates between are themselves wrong in time. Fixed-timestep physics without interpolation (Unity 20 ms steps) is a
milder version of the same thing (it hurts 1/2 too).

Consequences: (a) the lab cannot see it today, the probe animates its marker from `predictedDisplayTime`; a probe option that animates by
wall-clock delta, with an optional clamp that mimics engine smoothing, is **lab step 0**, run before the cycle table is written; (b) on the
headset the UEVR titles are tried first, with `t.MaxFPS` and the smoothing settings noted; (c) if engines in practice need the regular
cadence, the answer is the free-running mode below, not another ratio.

**The practical limit is the game's GPU time.** At 2/3 a game has 1.5 periods per frame on average (two frames per three periods); at half rate
it has two. A game that needs 16 ms at 90 Hz is at 96 % utilization and any hiccup slips the schedule. 2/3 is for games with some headroom
above 60 fps; a game that cannot should use 1/2. This is why the choice stays manual, and the note keeps the old rule of thumb ("a steady
70-75 fps is often better left at full rate") for the other end.

## Other ratios, and the alternative that uses the whole GPU

**No "magic" ratio exists on the boundary grid.** Any ratio k/n between 1/2 and 1 releases the game on an integer grid, so its frame spacing
is always a mix of 1P and 2P (3/4 = 1,1,1,0 gives 1P, 1P, 2P; 4/5 adds one more 1P). The share of 2P gaps shrinks as the ratio rises, the
irregularity itself does not: every one of them has the time-step risk above, exactly as 2/3 does, and each adds a notch the user must pick by
feel while the game's rate wanders. Only 1/m ratios are regular, and 1/3 is out. So 2/3 is the only extra notch worth having on this grid: the
largest step above half rate that still leaves 1.5 periods per frame.

**The way to use the 5090 fully is not a ratio.** Let the game run at its natural rate (or at a regular cap the user chooses: 72, 80 fps),
and let the host build **every** output for its boundary from the two bracketing real frames at the fraction t that boundary falls at
(the synthesis pass already takes any t; the pipeline already carries `fg_t`). The game sees a perfectly regular cadence, the display shows
a uniform 90 or 100 Hz, and a game fluctuating between 60 and 85 fps is smoothed without choosing a notch: the shim stops throttling, the host
adapts t per output. What it costs: every output is synthetic (the generator's artefacts apply everywhere, which the game's motion vectors
make acceptable: `docs/MOTION_VECTORS.md` step 2), one flow per real frame at the game's rate (2.3 ms each on the 5090 at the foveated
size: 75 flows per second is about 17 % of the GPU, so with vectors the flow should run only as the fallback), and the same added period as
today while the host waits for the later frame. With extrapolation from the vectors (newest frame warped forward by the fraction of a game
period to the boundary, ASW-style) the wait disappears and the latency is that of the unthrottled game. Measured reference: Callisto on
the 5090 at 100 Hz ran 76 fps with frame generation off and 50 with half rate; free-running it would keep its 76.

Position of the two: 2/3 is the small, manual step that fits today's pipeline (a mask and a cycle table); free-running interpolation is a
different pacing mode, manual too (the generator is switched on by the user, the game is simply not held back), and the one that makes the
most of a fast GPU. The free-running mode is the natural second phase once the vectors work on a real game; it reuses the generator and the
stamping unchanged and replaces the mask by a per-boundary pick of the bracketing pair.

## What has to change

1. **Shim pacing** (`ovrshim/driver.cpp` `WaitForVsync`, `GetPredictedDisplayTime`): a release mask next to `appPeriodMul` (new IPC field,
   version 27; mul 1 and 2 stay bit-identical to today) plus the display-time offset it implies. 1/2 = (1, 0), 2/3 = (1, 1, 0), off = (1).
   Two interactions to design in from the start: the intake gate skipping a release shifts the mask phase, so the shim and the host resync on
   content time (the boundary the frame is for), not on a release count; the running-start lead uses the frame-time envelope, which now sees
   alternating 1P and 2P targets, so the lead aims at the next *permitted* boundary.
2. **Schedule** (`tools/host/src/pipeline.rs` `FgState`, `fg_plan_app`, `fg_plan_hold`): today a strict S/R alternation with two arrival rules
   (a frame due waits one boundary; an empty boundary waits up to 4 ms for the expected frame). Generalise to a cycle table (per slot: show the
   real frame, or synthesize between these two at t, or hold). 1/2 must reproduce today's counters and fates exactly.
3. **Generator** (`framegen.cpp`, `nvh_fg_*`): unchanged; the synthesis pass already takes t. The pipeline skips the flow computation for the
   pair that is one period apart (no synthetic output comes from it): 30 flows per second instead of 60 at 90 Hz.
4. **Settings**: `video.frame_gen_ratio` = `"1/2"` | `"2/3"`; when the key is absent the value is **1/2** (the safe default: today's behaviour,
   so existing users and first-time users see no change until they pick 2/3 themselves). CLI `--frame-gen-ratio`, env `VISIONALVR_FG_RATIO`,
   live GUI command `frame_gen_ratio 1/2|2/3`. Switching while running resets the cycle (the first cycle after a switch may hold one frame)
   and the shim mask follows at the next boundary.
5. **Telemetry**: the active ratio in the session JSON; per-slot-kind counters (R, S, repeat, drop) already exist.

Not changed: stamping (every output stamped with the newest tracking sample and rotated to it, uniform staleness, which the Vision Pro
accepts), foveation, the encode tail, the Vision Pro client.

## UI (the user's spec)

Keep the existing control as is and only change its label and add one selector.

Streamer window (`tools/gui/VisionALVR.cs`, the live row under the sliders: today the iOS-style `ToggleSwitch` plus a bold title and a
one-sentence explanation):

```
 off:  [ o    ]  Frame generation            [ 1/2 | 2/3 ]   (selector greyed out)
 on:   [    o ]  Frame generation            [ 1/2 | 2/3 ]   (selector enabled, 1/2 selected on first run)
```

- The title reads **"Frame generation"** and nothing else: **remove the explanatory line** ("The game renders at half the display rate ...").
- The selector is a two-position control (a segmented pair of radio buttons styled flat, or a two-stop slider; same behaviour): greyed
  out while the toggle is off, enabled while it is on, the toggle's own behaviour unchanged. A change applies at once like the toggle does
  (`host.Send("frame_gen_ratio 2/3")`, setting saved).
- Default selection on first run (no `video.frame_gen_ratio` in the settings): **1/2**, so nobody is moved off today's behaviour without choosing it (decision of the user, 2026-10-15: a safe default, fewer support questions).

Settings panel (`tools/gui/SettingsPanel.cs`, the checkbox "Frame generation (experimental): the game renders every other frame ... "):
shorten the text to **"Frame generation"** and put the same selector next to it, with the same greyed-out rule. `tools/gui/Common.cs`
settings gets `FrameGenRatio` next to `FrameGen` (`video.frame_gen_ratio`, default "1/2").

No tooltip or help text is added: the user chooses the notch by feel.

## Motion vectors: effect on this work

None expected, and the design agrees:

- Game vectors describe the motion between two consecutive game frames (the DLSS "previous to current" convention in `docs/MOTION_VECTORS.md`).
  They do not depend on how many display boundaries separate the two frames, so the vector-driven synthesis (`PSVec`, `PSDilate`, `vecSource`,
  midpoint solve at t = 0.5) works unchanged for the pairs that are two periods apart. The pair one period apart gets no synthetic frame.
- t stays 0.5 for every synthetic frame at both choices, so no t-dependent tuning of the vector path is needed.
- **Things to check in the lab, not expected to fail:** the shim's vector ring (three copies per stream) with two game frames only one
  period apart; `IngestVectors` keeping the history of two real frames per cycle; the stream idle timeout (two seconds) at the lower game rate.
- **A bonus the cadence gives later:** in the 2/3 cycle the pair that is one period apart has a vector per frame that equals exactly one period of
  motion. The planned extrapolation mode (warp the newest real frame forward one period, no added period) can use it for the S slot, which
  would remove the added period at 2/3. Out of scope here; noted so the cycle table keeps a slot kind "extrapolated" open.
- Overlap in code: both pieces touch the IPC header and `nvh.cpp` stats. The ratio work stays out of `framegen.cpp`'s synthesis, so the merge
  surface is the IPC version number and `nvh_fg_stats`; agree the next IPC number with the motion-vector branch before starting.

## Expected cost

- GPU: unchanged per synthetic frame (flow 2.3 ms per real pair, synthesis 0.13-0.27 ms on the 5090 at the foveated size; vectors 0.4-0.7 ms
  synthesis on the 3080 Ti in the lab). At 2/3 with the flow skipped for the one-period pairs: 30 flows and 30 synthetic frames per second
  against 45 and 45 today.
- NVENC: the display-rate of frames either way, so unchanged.
- Quality: the artefacts of the generator (ghosting of small fast hard-edged objects) apply to one output in three instead of one in two.

## Lab plan (headset-less, `harness/simrig`)

0. **Time-step check first**: the probe animates the marker by wall-clock delta (`XR_PROBE_MOVING_CLOCK=wall`), optionally clamped like an engine's
   smoothing (`XR_PROBE_DELTA_CLAMP=16.1`), under the 1/2 mask and the 2/3 mask without the generator: the judge's motion metric shows whether the
   irregular cadence alone moves the marker unevenly. This decides whether 2/3 proceeds or the free-running mode comes first.
1. `steady_fg`, `stutter_fg`, `stutter_turn_fg` with the app load at the new rate (`fps:60:70` at a 90 Hz display) and with the vector scenarios
   (`steady_mv`, `stutter_mv`); judge SMOOTH, frozen ticks, fate counters (G/R/Q), `fg_no_sample`, `fg_dropped`, `fg_arrival_waits`.
2. Regression: 1/2 identical to build 9 of 2026-10-14 (steady_fg, stutter_fg all CLEAN + SMOOTH) and to the current main with vectors.
3. Pacing lab: arrival-to-send latency (target at most today's 24 ms rendered / 16 ms synthetic) with the GPU load at 1.0 P, 1.2 P and 1.5 P
   per frame, to find where the schedule slips.
4. Stress: a late frame (one period), a skipped release, a tracking gap longer than 12 ms, the encoder overrun path, a ratio switch while running.

## Headset plan

One session: a game at about 60 fps (Callisto, Dead Space) at 90 Hz, the selector flipped between 1/2 and 2/3 by feel, with the
telemetry host log and the client intervals. Check the measured period in the log when judging a slip (a 90 to 100 Hz switch moves the 2/3 budget from 16.7 to 15 ms). Pass bar: no new tremble (uniform stamping), missed periods at or under today's 1 %, latency no
higher than 1/2, and the user's verdict on smoothness.

## Order of work

1. This note reviewed (done 2026-10-15; this revision holds the review's additions).
2. The real-game motion-vector check on the 5090 first (it decides how much the free-running mode can lean on vectors). IPC 27 is reserved
   for the mask.
3. Lab step 0 (time-step check).
4. Shim mask + cycle table, behind `frame_gen_ratio` (1/2 unchanged), lab scenarios 1-4; flow skipped for the one-period pairs.
5. GUI and settings (the UI section above), live switching.
6. Headset session.
7. Second phase: extrapolation of the S slot from the vectors (removes the added period at 2/3), then the free-running mode.

## Open questions

- Whether rendered frames should be sent unwarped when their stamp orientation is within the copy threshold (today 0.02 degrees): same rule as now.
