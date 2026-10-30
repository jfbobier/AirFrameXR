# Bench UI and its test matrix (design, 2026-10-30)

Companion of [TELEMETRY_PLAN.md](TELEMETRY_PLAN.md). Status: first version built 2026-10-30 (`tools/gui/Bench.cs`: one window with Prepare / Capture / Read, F9, Deep trace button, session list, export; matrix cases `bench-idle`, `bench-ready`, `bench-capture`, `bench-read-smooth`, `bench-read-problem`, `bench-read-insufficient`, `bench-long`). **Not built yet:** Compare, the timeline control, the waterfall of one frame, the Settings/repeatability line, the sources-with-states list (`capabilities.csv` is not shown in Prepare), `bench_flow`, the elevation-boundary package test. Written written to the rules of [UI_SPEC.md](UI_SPEC.md).
This is **GUI code** plus a new managed library: the engine is not touched, in either flavour (see Architecture in the plan).

## Who uses it and when

A user (or us) starts the Bench flavour **on purpose**, for one game, to answer "where does the time go?". They are at the PC for a minute, then in the
headset, then back at the PC to read the result. So the window is a task flow, not a dashboard to leave open:

1. **Prepare** (at the PC): is everything measurable, what will be measured, name the scene.
2. **Capture** (the headset is on): one big control, no clutter; starts and stops without the mouse.
3. **Read** (back at the PC): one verdict, three findings with the evidence, the worst frames, export.
4. **Compare** (optional): this session against an earlier one, with the configuration differences shown first.

The Stock streamer window is unchanged and keeps the principle "quiet by default". Bench may show more numbers, but it keeps the others:
one window one objective, no jargon without its reason next to it, every number has a unit and says where it comes from, a counter that is
unavailable says so (blank, never 0), no modal dialog except after a click, size from content.

## Executable and flavour

- `bench\telemetry\bin\bench.exe` (WinForms net48, built with Roslyn `csc` like the other tools) and `bench\telemetry\bin\perfcore.dll` (managed: probe, session writer, report; the
  form only draws what the library returns, so the lab and `perfreport.exe` test the same code), shipped **only** in the `-bench` package. Manifest `asInvoker`:
  the window never runs elevated. The stock package contains neither, and no manifest with `requireAdministrator` (checked by the package test). No engine change.
- The Bench package has its own launcher entry and starts `streamer.exe` (telemetry build) itself; the window title and a fixed badge say
  **BENCH** (measurement build: per-frame logging on, not for normal play) with the version and the flavour from `bin\flavour.txt`.
- A running Stock streamer, or a Stock OpenXR runtime registration, is shown in the status strip with the one button that switches (same rule as the
  launcher's runtime row: the switch can be undone in the same place).

## Window layout (one window, three states: Prepare, Capture, Read)

```
[header: AirFrameXR Bench - measurement build (per-frame logging on)                 version, flavour]
Headset: connected - Apple Vision Pro   Game: Lies of P (running)   GPU: RTX 5090, driver 5xx.xx   Runtime: Bench (active)   <- status strip, one line
+- Sessions ------------+- Prepare / Capture / Read -----------------------------------------------------+
| newest first          |  (the right side changes with the state, the left list stays)                     |
| name, game, date,     |                                                                                   |
| length, verdict dot   |                                                                                   |
| [Compare with...]     |                                                                                   |
+-----------------------+-----------------------------------------------------------------------------------+
footer: one inline notice at a time with its action (as in the streamer)
```

### Prepare
- **What will be recorded**: a two-line list of sources with a state each, from `capabilities.csv` of the last probe: "GPU sensors (NVML): ready",
  "Windows counters: ready", "Per-process GPU engines: ready (2 processes)", "Encoder sessions: not available on this driver" (orange, with the reason), never a silent gap.
- **Scene name** (text, required: it becomes the session folder name), **Duration** (60 / 90 / 120 s or *Until I stop it*), **Warm-up excluded** (default 10 s: the GPU wake-up ramp and
  loading are not in the numbers; the lab already uses this rule).
- **Repeatability line**: the settings hash and the three settings that matter most (refresh rate, bitrate, frame generation), with *Same as session X* when the last session of this
  scene used the same ones. A setting change during a capture is written to `state_events.csv` and the report splits its windows there.
- **Start capture** (primary). Disabled with its reason on the button's line when the headset is not connected or no game runs ("Start a game first").
- **Deep trace (admin)...** (shield icon, secondary): "Records GPU and CPU scheduling for 20 s. Windows asks for permission. The stream keeps running." Disabled unless a game runs.
  Declining UAC is a normal outcome: a green line "Deep trace skipped, the capture is unchanged"; never an error.

### Capture
- A large state line ("Recording 00:42 of 01:30", or "Warm-up 6 s") and **Stop** (primary). The same Start/Stop on a global hotkey (F9) so it works with the headset on.
- Live tiles (the streamer's tile style): Game fps, Streamed fps, Encode ms (typical / slowest tenth), Late frames %, GPU util / clock / power, CPU, Logger health (events lost: must stay 0).
  A tile with no reading shows "n/a", not 0.
- One timeline of the last 60 s of frame spacing with late frames marked and setting changes as vertical ticks (a single custom-drawn control; no chart library).
- Nothing else moves: the window is fixed-size for its fullest content, like the streamer; no notice resizes it.

### Read
- **Verdict** (colour + one sentence): "Smooth: 0.4 % late frames" / "Late frames: the encoder was the last stage on 71 % of them" / "Not enough data: no frame IDs in this session".
- **Top three findings**, each: what, the measured reason (numbers and unit and window), and the suggested controlled test. The rest under *More*; every number under *Details* (tabs: Stages, Sensors, Settings, Capabilities).
- **Worst frames**: a short list (frame, how late, last stage, the stage times); selecting one shows its stage bar (a waterfall drawn from the frame's own stamps; no overlapping stages are summed).
- **Export report...** (zip of the session folder without media, one inline "Saved to ... [Open folder]" line, no dialog left open), **Open folder**.
- **Compare with...** opens the comparison.

### Compare
- Picks an earlier session; the **configuration differences come first** (a table of the settings that differ; if any besides the one under test: orange "Not a like-for-like comparison"
  and the numbers stay visible but greyed). Then a table: p50 / p95 / p99 per stage, late frames, per-run medians when several sessions of the same condition are ticked
  (the plan's protocol: at least 3 runs per condition, counterbalanced); an effect is only called when it passes the threshold (p99 +5 % or late frames +0.5 point) and says so otherwise.

## States the matrix must cover

Each is a fixture session folder (`harness/fixtures/bench/<name>/`) or a demo-state call on the form (`ApplyDemoState`, as the streamer does):

| Case | What it shows |
|---|---|
| `bench-idle` | nothing connected, no sessions: empty list text, Start disabled with its reason |
| `bench-ready` | headset and game up, all sources ready |
| `bench-partial` | encoder stats and process utilization not available: orange rows with reasons, no zeros |
| `bench-flavour-conflict` | Stock streamer or runtime active: the switch row |
| `bench-capture-warmup`, `bench-capture` | tiles with and without readings, logger health 0 and non-zero, timeline with a setting-change tick |
| `bench-read-smooth` | green verdict, no findings |
| `bench-read-problem` | red verdict, three findings, worst-frame list, waterfall |
| `bench-read-insufficient` | grey verdict, which data is missing |
| `bench-deeptrace-declined` | the green "skipped" line |
| `bench-compare`, `bench-compare-mismatch` | clean comparison; differing settings (orange, greyed numbers) |
| `bench-long` | long scene name, long game name, 3-digit numbers, French strings (the builder's locale) |

## Test matrix

1. **`ui_matrix`** (existing harness): every case above at 1080p and 1440p (100 / 125 / 150 %) and 4K (100 / 150 / 200 %): fits the work area, nothing clipped or overlapped,
   no cut-off text, one screenshot each; 0 FAIL. New cases register in `tools/gui/UiTest.cs` like `streamer`.
2. **`bench_flow`** (new, builder): synthetic session folders (the plan's fixtures: slow encode, stalled send, missing sensor, writer stall, a clean run) go through `perfreport`;
   `bench.exe --test-read <folder>` asserts the verdict text, the three findings and that a blank counter is shown as "n/a" and never "0".
3. **Window-size stability**: Prepare, Capture and Read at their fullest content keep the same size while notices come and go (same check as `streamer_events` window_size).
4. **Elevation boundary** (package test): `bench.exe` has no `requireAdministrator`; the stock zip has no `bench.exe`; the Deep trace command line is built from fixed paths and a bounded
   duration; Win32 error 1223 (UAC declined) leads to the "skipped" line, not an exception; the capture is unaffected.
5. **Capture control**: Start / Stop by button and by the F9 hotkey write the same session folder; stopping early writes a valid session; starting without a game is refused with its reason.
6. **Flavour switch**: with a Stock runtime registered, Bench shows the conflict row; the button switches and a second click switches back (the launcher's runtime-row test already covers
   the mechanism; Bench adds the row).

Not tested by any of this and said plainly: UAC itself (a person must click once), a real 4K display (scale is emulated, as in `ui_matrix`), the 5090, a real headset capture.

## Build steps

1. Static form with `ApplyDemoState` for every case and the `ui_matrix` entries (no probe, no engine): the layout is fixed before anything is wired.
2. Probe and session folder behind Prepare / Capture; hotkey; logger-health tile.
3. `perfreport` behind Read; fixtures and `bench_flow`.
4. Compare; Deep trace button and the elevation boundary test; `-bench` package.
