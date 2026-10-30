# Performance logging for 1.3: the Bench flavour (plan, 2026-10-30)

Status: plan with a measured prototype (`tools/perfprobe/PerfProbe.cs`, tested on the 3080 Ti laptop only). Steps 1-3 of the order of work exist in a first version (2026-10-30):
`tools/bench/PerfCore.cs` (`perfcore.dll`) and `PerfProbeCli.cs` (`perfprobe.exe`), built by `python3 harness/run.py build bench`, checked by `python3 harness/perfcore_check.py`
`PerfReport.cs` (the report, inside `perfcore.dll`; `perfreport.exe` over it, checked by `python3 harness/perfreport_check.py` against 9 generated fixtures with known faults and known non-faults),
and `tools/gui/Bench.cs` (`bench.exe`, matrix cases `bench-*` in `harness/ui_matrix.py`).
Tested on the 3080 Ti laptop: probe L0 / L1 captures, the report on fixtures and (structure only) on one real 2026-10-11 Cyberpunk session, the window in the UI matrix.
**Not tested:** a capture with a real headset and game through `bench.exe`, UAC and the Deep trace (`wpr` profile names), the 5090, the report's numbers against `harness/frames.py` on the same window
(the first real run flagged 1.72 % late frames while `frames.py` counts 17 of 9412 on its own subset: the windows differ and the comparison is owed), accuracy of the stage attribution on real data.
Nothing is in a release yet.

First package (tonight's): the Bench tools sit in `bin\` of the **debug (telemetry) package** (`bench.exe`, `bench.exe.config`, `perfcore.dll`, `perfreport.exe`;
`stage13_package.ps1` ships them only when the flavour is debug and moves them out of a release package). The debug package *is* the Bench install described in Architecture (unzip it anywhere, or into `bench\telemetry\` of a Stock install); the dual-flavour packaging that produces both from one command comes next.
Legend as in [progress.md](progress.md): "measured" means run on the builder with the numbers given, "untested" means not run.

## Decision

Two flavours of the same code, not one app with logging levels:

| | **Stock** (what users run) | **Bench** (separate folder, for diagnosing one game) |
|---|---|---|
| Build | release: telemetry compiled out | the existing `telemetry` flavour (cargo feature `telemetry`, `AIRFRAMEXR_TELEMETRY`) |
| Rights | user, never a UAC prompt | user; **one** action asks for UAC (deep trace) |
| Logging | what it logs today (`encoder_stats`, the pacing line) | per-frame CSVs, system probe, report |
| UI | the simple one | a diagnostics UI built for the task |

Why: a performance analysis is done once or twice per game, so it is a different task and gets a different tool. Stock stays free of cost
and of prompts, and there is no `perf_level` setting to add (DEVELOPMENT_PHILOSOPHY: no setting without a measurement).

Architecture (2026-10-30, decided with the user: clean by design): the Bench flavour is a **complete, separate portable install** of its own, built entirely from the
telemetry (debug) build, with every dependency in its own folder. It shares nothing with Stock at run time. Nothing is added to the engine for this feature.

```
<stock install>\                       Stock: release build, unchanged, no bench code anywhere in it
    AirFrameXR.exe, bin\, config\, profiles\, docs\, logs\, bench\ (the wizard's scene assets: littlest_tokyo.vab, xr_probe.exe, fovgrid.*)
    bench\telemetry\                   the Bench flavour: a whole install, the same layout as Stock (Paths.Dir = the parent of bin\, so every exe finds its own files)
        AirFrameXR.exe                  the stub, starts bin\launcher.exe (its launcher shows a Bench card because bin\bench.exe exists)
        bin\  alvr_host.exe, LibOVRRT64_1.dll (shim), virtualdesktop-openxr.dll/.json (VDXR), streamer.exe, configure.exe, performance.exe,
              launcher.exe, mirrordesktop.exe, register_openxr_runtime.ps1, flavour.txt = debug   <- all from the debug build, none copied from Stock
              bench.exe, bench.exe.config, perfcore.dll, perfreport.exe                             <- the Bench tools (new)
        bench\ (assets), profiles\, docs\, config\ (its OWN session.json and airframexr.json), logs\ (its own, incl. logs\bench\<session>\)
        (optional, later) bin\afxr_trace.dll   OpenXR API layer DLL for the frame-cycle spans (see below)
```

Consequences of "separate install": its own `config\` means the headset has to be paired once in the Bench install (or `config\` copied from Stock by the user); its own
`alvr_host.exe` path means the Windows Firewall review (which already handles "another copy") must allow it too; the OpenXR runtime registration points at the Bench
folder's `virtualdesktop-openxr.json` while a Bench session runs (the launcher's runtime row switches it and switches back); only one streamer runs at a time (ALVR port 9943).
The same folder is also what the `-debug` zip is today, so it can be unzipped anywhere, nested in a Stock install or on its own.

The engine's own per-frame CSVs are the existing telemetry-flavour output and keep their formats: `bench.exe` joins them by frame timestamp
exactly as `harness/frames.py` does today, and writes the session metadata itself. So the engine work list of the first version of this plan
(stable IDs, `session.json`, `state_events.csv`, `logger_health.csv`) is **dropped from the engine**: `bench.exe` writes `session.json` (versions,
flavour, settings hash from `airframexr.json` and `session.json`, refresh rate, GPU and driver), `state_events.csv` (it watches those files while
capturing) and `logger_health.csv` (its own writer). If a later measurement shows that a join by timestamp is not enough, an engine change would be
proposed then, telemetry flavour only.

Rule for any engine edit that does turn out to be needed (approved 2026-10-30): any engine change for this feature sits behind `telemetry` / `AIRFRAMEXR_TELEMETRY` and is
compiled out of the release flavour. Proof for every such change: `tools/wsl/check_host.sh --no-default-features` and
`EXTRA_DEFS="/D AIRFRAMEXR_RELEASE" bash tools/wsl/check_cpp.sh` still compile, and the release package is unchanged in behaviour.

## What the Bench flavour records

Everything below needs **no admin rights** except the last row.

| Source | Content | Rate | Owner |
|---|---|---|---|
| Engine, existing | `host_frames.csv`, `host_pipeline*.csv`, `client_frames.csv`, `shim_frames.csv`, `tracking.csv`, `host_events.jsonl` | per frame | engine (telemetry flavour, as today) |
| NVML (`nvml.dll`, installed with the driver) | util, clocks, power, temperature, VRAM, clock-event reasons, encoder util, `nvmlDeviceGetEncoderStats` (sessions, fps, latency) | 1-5 Hz | probe |
| NVML PCIe throughput | tx / rx KB/s | 1 Hz, own thread | probe |
| Windows counters (PDH through `System.Diagnostics`) | CPU, RAM available, page faults, NIC bytes, disk | 1-5 Hz | probe |
| Windows `GPU Engine` counters | 3D / Copy / VideoEncode / VideoDecode per process | 1-2 Hz, filtered to named processes | probe |
| **Deep trace (UAC)** | `wpr.exe` GPU + CPU profile (10-30 s) and PresentMon (MIT licence, unmodified binary) on the game process | on demand | elevated helper |

Rules kept from the first draft of this idea: a counter that is unavailable is **blank, never 0** (`capabilities.csv` says why); event times
are stamped when they happen and sensors are polled (a 1 Hz sensor is context for a sustained effect, not the explanation of one 11 ms frame);
the probe never blocks anything on the frame path and fails open.

`host_frames.csv` and `client_frames.csv` already join on the frame timestamp; the host already writes `gpu.csv` (NVML, 1 Hz) in debug
sessions. The probe replaces `gpu.csv` as the one owner of hardware data only after it has proven it covers the same fields; nothing is removed in 1.3.

## Measured: what the sources cost (3080 Ti laptop, driver 617.42, idle desktop)

| Source | Result |
|---|---|
| NVML utilization, clocks, power, temperature, memory, P-state, encoder / decoder util | 0.1-0.6 us per call |
| NVML clock-event reasons | 2.4 us |
| NVML encoder stats | 6.9 us (reads 0 sessions, 0 fps with no encoder running: not yet seen under load) |
| **NVML PCIe throughput** | **about 30 ms per call** (it measures over a window): own thread, never in the tick |
| **NVML stalls** | any call can block 0.4-1.3 s now and then (seen about once per 15-60 s, whichever call was running): the probe lives on its own thread |
| NVML fan speed, process utilization | not supported / error on this card (process utilization err 6-7) |
| Windows CPU, RAM, faults, NIC | about 2 ms per tick |
| `GPU Engine` counters | 362 instances in total, 90 ms for one full read; **about 0.25 ms per instance** (75 instances = 19 ms): filter by process id |
| Probe process CPU | 1 Hz with 75 engine instances 3.0 % of one core; 5 Hz 11.7 %; 5 Hz without PCIe the same (PCIe is not the cost); engine counters are the cost |

Not usable: a `Thread.Sleep(10)` jitter probe in the probe process (15.6 ms timer granularity without `timeBeginPeriod`, p50 5.6 ms late).

## Measured: what the probe does to the stream

Lab scenario `e2e_openxr_probe` (1800 frames at 90 Hz, mock client on the same laptop), probe started on the builder as a separate process during
the run (`Win32_Process.Create`: a process started over ssh dies with the session, the first attempt at this test measured nothing).
3 runs per condition, counterbalanced order OFF L0 L1 L1 L0 OFF OFF L1 L0 (medians of the 3 runs):

| Condition | client p95 | client p99 | client max | late frames | host vsync p99 | intake p99 |
|---|---:|---:|---:|---:|---:|---:|
| OFF | 12.58 ms | 21.94 | 23.16 | 2.85 % | 11.38 | 25.03 |
| L0: 1 Hz, no engine counters | 12.63 | 21.63 | 24.19 | 2.70 % | 11.42 | 24.45 |
| L1: 5 Hz, 75 engine instances, CSV flushed every tick | 12.83 | 22.20 | 27.30 | 3.30 % | 11.44 | 24.95 |

Reading (practical effect threshold from the plan: p99 +5 % or late frames +0.5 point):
- **L0 is invisible** next to the run-to-run noise (OFF alone spans p99 21.8-22.0, late 2.3-3.0 %).
- **L1 is below the threshold on p99 (+1.2 %) and late frames (+0.45 point), but p95 (+0.25 ms) and max (+4 ms: 27.3, 24.0 and 27.8 against 23.1-23.7) are consistently higher in all three runs.** On this laptop, with the mock client on the same CPU, 19 ms engine-counter bursts every 200 ms are the likely cause (untested: not isolated from the per-tick flush).
- Consequence for the design: the engine counters read at 1-2 Hz and only for the named processes, the other counters may run at 5 Hz; a batched raw-PDH query (one `PdhCollectQueryData` for all instances) is the only optimisation worth trying, and only if the 1 Hz engine cost shows up.
- Not measured yet: the telemetry build against the release build on the same scenario (the engine's own per-frame CSVs cost), the 5090, a real headset.
  Run `python3 harness/run.py build host shim --release` then the scenario, to be added to this table.

## Deep trace (the only elevated action)

- The Bench UI runs unelevated. The **Deep trace** button starts `wpr.exe` (GPU + CPU profiles) and/or PresentMon filtered to the game process
  with `runas`; declining UAC just leaves the L1 session as it is.
- The whole streamer is **not** elevated: its child processes (the game) would inherit it and the numbers would no longer describe how users run
  games, and an elevated host with an unelevated game risks the shim's IPC.
- No custom privileged helper, no named pipe: Windows tools do the privileged part. The `.etl` is opened in WPA or GPUView; decoding events
  ourselves (Microsoft TraceEvent, MIT) is a later step, not 1.3.
- The window and its test matrix are in [BENCH_UI.md](BENCH_UI.md) (GUI code only).
- Optional, documentation only: adding the user to "Performance Log Users" allows ETW without a prompt.

## Build and package (what has to be rebuilt)

Everything in `bench\telemetry\` comes from one **debug build tree** (`run.py build ...` with the default flavour); nothing is copied from the release build. Only two binaries differ by
a compile flag (`alvr_host.exe`, `tools/remote/stage8_host.ps1` `--no-default-features` for release; the shim, `tools/remote/stage11_ovrshim.ps1` `/DAIRFRAMEXR_RELEASE`); `nvenc/`, `tools/mirrordesktop/`, VDXR and
the GUIs have no flavour switch, but they are rebuilt and packaged from the same tree anyway so that the folder is one build, not a mix.

Today both flavours write to the same output paths (`target\release\alvr_host.exe`, `out\vdxr-shim`, `out\dist\AirFrameXR`), so a distribution holding both needs the order
**build release, package Stock; build debug, package Bench; assemble**, with each package kept in its own `out\dist\<flavour>` folder. Steps, all in build scripts and `harness/run.py`, none in engine source:
1. `run.py package` takes the flavour of the last build and writes `out\dist\AirFrameXR` (release) or `out\dist\AirFrameXR-bench` (debug), never overwriting the other.
2. The Stock zip optionally embeds the Bench package under `bench\telemetry\` (a release decision: one download that holds both, or the Bench zip separately; the build is the same).
3. `stage13_package.ps1` already ships `bench.exe`, `bench.exe.config`, `perfcore.dll`, `perfreport.exe` in `bin\` of the debug package only (done 2026-10-30) and moves them out of a release package.
4. The package test checks the flavours by content: the Stock host and shim contain none of the telemetry names (`host_frames.csv`; wide string `shim_frames_`), the Bench ones do, Stock has no `perfcore.dll`, the Bench
   registration JSON points at the Bench folder, and `flavour.txt` agrees with the binaries (the first check on 2026-10-30 found a package labelled debug whose host had no telemetry).

## Bench and Stock side by side

They cannot both be active: the OpenXR runtime registration, ALVR's port 9943 and the config files are shared. The Bench launcher shows the
active flavour, uses its own logs folder (a bench session never overwrites stock logs), and tells the user which runtime is registered.
Package: the `bench\` folder above, shipped as a second zip (`-bench`, built from the telemetry flavour as the `-debug` zip is today) that unpacks into
its own folder; whether it may also unpack into `bench\` of a stock install is an open point.

## The report

A .NET `perfreport` (offline, reads one session folder, writes `report.md`; same Roslyn `csc` build as the GUIs):
- p50 / p95 / p99 per stage, late-frame count and worst streak, which stage finished last on each late frame, sensor context next to the worst
  windows. Definitions: service time = time executing, queue age = start minus ready, blocking wait = time blocked, slack = deadline minus ready,
  region fan-in = max of the regions' ready times (never a sum), a missing region is an incomplete frame (never dropped from the max).
- About 8 verdicts, each OK / PROBLEM / INSUFFICIENT DATA with the evidence frames: trace intact, deadlines met, encode not the last stage,
  encoder pool not blocking, sender not starving, queues not growing, logger lost nothing, counters not misattributed (DWM fps is never source fps,
  NIC bytes are never stream loss, "bus load" is never PCIe saturation).
- Test fixtures with known faults and non-faults (slow encode, stalled send, missing sensor, writer stall); the same files give byte-identical output.

## Engine work: none planned

The three items of the first draft (stable frame IDs, `session.json` / `state_events.csv`, `logger_health.csv`) move into `perfcore.dll` / `bench.exe`
(see Architecture). The existing telemetry-flavour CSVs are read as they are. No lineage graph, no GPU pass timestamps, no game-side producer IDs in 1.3.

Optional, only if the report cannot name the late stage from today's CSVs: `afxr_trace.dll`, an **OpenXR API layer** (a separate DLL in `bench\`, enabled
by the Bench launcher for the session only) that stamps `xrWaitFrame` / `xrBeginFrame` / `xrEndFrame` and swapchain waits with QPC in the game process, above our
runtime, so the shim stays untouched. It is native code in the game process: it would be measured for its own cost first (same lab A/B as the probe) and the
existing "OpenXR layers that may take the controllers" review must treat it as ours.

## Order of work

1. `perfcore.dll`: NVML + counters probe (1-2 Hz engine-counter rule), session folder, health. Prototype exists (`tools/perfprobe/`). No engine change.
2. `perfreport.exe` over `perfcore.dll`, on today's CSVs, with fixtures. A useful 1.3 on its own.
3. `bench.exe` static form with every demo state and the `ui_matrix` cases ([BENCH_UI.md](BENCH_UI.md)), then wired to the probe and the report.
4. `bench\` packaging (`-bench` zip, own launcher entry, own logs folder), Deep trace button, elevation-boundary package test.
5. Overhead table completed: release against telemetry build, 5090, a real headset run; decide on `afxr_trace.dll` from what the report cannot yet explain.

## Not in 1.3 (and why)

ETW helper with a named pipe, per-pass GPU timestamps, the dependency graph, game-side producer IDs and AER freshness detectors (they need the
game to cooperate), PresentMon CSV parsing, `nvmlDeviceGetProcessUtilization` (fails here), most of the 33 assertions of the first draft
(no measurement yet that justifies them; add one when a hand-diagnosed problem needs it).
Candidates for 1.4 from that draft, in order: source-view provenance for frame generation, the Quest / Vision Pro receiver clock alignment.

## Open points

- Does the 5090 return more NVML data (encoder sessions under load, process utilization)? The notes say Blackwell lacks OFA but nothing about NVML.
- Do the `GPU Engine` counters list the 5090's three NVENC engines separately?
- Distribution: one Stock zip that embeds `bench\telemetry\`, or two zips (Stock, Bench). Layout and build are the same either way; embedding keeps one download, a separate zip keeps Stock's zip free of any bench file.
- Is the join by timestamp between `host_frames.csv`, `client_frames.csv` and `shim_frames.csv` enough for the report, or does it need IDs? (`harness/frames.py` joins them today; check on a real session first.)
- The first draft of this idea lives outside the repo (`AirFrameXR_Performance_Telemetry_Implementation_Plan.md`); its assertion registry and fault-injection
  table are the source for the report's verdicts and fixtures.
