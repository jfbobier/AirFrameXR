# Black Box: recorder and analyzer (design and test matrix, 2026-10-30)

Companion of [TELEMETRY_PLAN.md](TELEMETRY_PLAN.md). Brand: **AirFrameXR Black Box, performance flight recorder** (assets in `branding/blackbox/`: navy / cyan / blue, a small red REC lamp).
Two programs, because it is two tasks, started on purpose when a game does not run well:

| | **Black Box Recorder** (`bin\blackbox_recorder.exe`) | **Black Box Analyzer** (`bin\blackbox_analyzer.exe`) |
|---|---|---|
| Job | start the streamer as the main one and record | list, analyze, replay, export, delete recordings |
| When | while the game runs (must be light) | afterwards, offline; also on another PC |
| Code | the stock streamer window (`tools/gui/AirFrameXR.cs`, unchanged but for four empty partial hooks) + `tools/gui/BenchRecorder.cs` | `tools/gui/PerfAnalyze.cs` over `perfcore.dll` |
| Brand | `tools/gui/BlackBoxBrand.cs` header: icon, "AirFrameXR BLACK BOX RECORDER", red REC lamp while recording | the same header, no lamp |

The stock streamer carries none of it: partial methods without an implementation are removed by the compiler. The Black Box programs exist only in the telemetry package.

## Conventions (settled with the user, 2026-10-30)

- The recorder reads the normal streamer's settings and **never changes them**: at every start it copies Stock's `config\airframexr.json` and `session.json` (headset pairing included) into its own `config\`, one way,
  then switches debug logging on in its copy. Stock's folder is found two levels up when the install is nested as `<stock>\bench\telemetry`, else asked once (`config\bench.json`).
- To test other streamer settings: set them in the normal UI, close it, come back (one streamer at a time: one runtime, one port). Most late frames come from the game (DLSS quality, ray tracing, resolution), so the report
  says so when the stream waited for the game and the GPU was busy.
- Recording: the key **F8** by default (configurable, with Ctrl; F9 is "quick load" in some games). A registered hotkey is swallowed, so the game does not see it; F12 is reserved by Windows for debuggers, so it is read instead
  (the game then sees it too). Optional beep on start and stop. **Full session** tick: starts at the game's start, stops when it closes (`game_started` / `game_ended`), the key still stops it early.
- Recordings are written to `logs\performance\<time>_<scene>\` (probe files + the engine's per-frame files of the run cut to the recording). Recording never analyzes: **Analyze** is a click in the analyzer.
- The analyzer lists recordings with their size, replays an analyzed one without a click, exports a zip, deletes one (confirmation) or the ones older than N days, and opens a recording folder or zip from another PC.
  It refuses to analyze while a recording runs (the analysis would compete with the game for the CPU).
- The only elevated action is **Deep trace (admin)** in the recorder: Windows' own `wpr` for 20 s, one UAC prompt, declining changes nothing.

## The recorder window

The stock streamer layout (headset, game, streaming tiles, frame generation, picture, position, mirror) with, instead of the stock header, the Black Box header, and above the debug row a **Recording** group:
scene name, Full session tick, start/stop key, Beep, **Start/Stop recording**, a two-line state (ready / recording with GPU, clock, power and recorder health / saved), the "Saved ... nothing is analyzed yet" line,
**Analyze recordings...** (opens the analyzer on the last recording), **Recordings folder**, **Deep trace (admin)...**. The window keeps its fixed size: the state and notice lines have reserved space.

## The analyzer window

Left: the recordings (status first: `[PROBLEM]`, `[OK]`, `[not analyzed]`), **Delete old recordings...**, **Open a recording (folder or zip)...**. Right: the recording's name and facts, **Analyze this recording**, Open report.md,
Open folder, Export..., Delete...; then the verdict (colour + one sentence), the findings (PROBLEM, WATCH, INSUFFICIENT, OK, each with its measured reason and a suggested test), the worst 10-second windows
(when it went bad, for a long recording) and the worst late frames with the stage that made them late.

## Test matrix

- `harness/ui_matrix.py` (every window at 1080p / 1440p (100, 125, 150 %) and 4K (100, 150, 200 %)): cases `rec-idle`, `rec-full-waiting`, `rec-recording`, `rec-saved`, `rec-key-taken` (the streamer window with the panel) and
  `analyze-empty`, `analyze-unanalyzed`, `analyze-busy`, `analyze-smooth`, `analyze-problem`, `analyze-insufficient`, `analyze-long`: 0 FAIL required.
- `harness/perfcore_check.py` (the probe), `harness/perfreport_check.py` (the report against generated fixtures with known faults and non-faults, the recording window, the worst windows, byte-identical output),
  `harness/recorder_check.py` (the engine files cut to the recording, the hotkeys, the recorder's own settings).
- Package test (to add to `run.py build package`): Stock has no Black Box file and no `perfcore.dll`; the telemetry package has them and its host and shim contain the telemetry file names.

Not tested, and said plainly: a recording with a real headset and game, F8 against real games, the beep in the headset, UAC and `wpr`'s profile names, the 5090, a real 4K display (scale is emulated), global hotkey registration in the automated session
(it has no interactive desktop: error 1459, the check says SKIP).
