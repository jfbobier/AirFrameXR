# 0.5-alpha plan: UI and just enough data for performance stats

Focus of 0.5: specialised, uncluttered windows, and enough recorded data per game to tell the user how it performed.
Rule for every window (0.4 high-DPI lesson): size from content (AutoSize, flow/table layout, no absolute `Location`, no fixed container heights);
any pixel constant is a 96-dpi design value (`AutoScaleDimensions = 96,96`).

## Decisions taken
- Statistics are shown as **tiles** (not a chart), plus a small history strip.
- **Separate EXEs**, one per tool, so experts run only what they need. `launcher.exe` is the front door for everyone else: a card per tool
  (what it does, a Launch button). Tools: Streamer (`VisionALVR.exe`), Logs and performance, Diagnose, Setup wizard (`configure.exe`).
- Desktop mirror: **hand tracking + pinch** drives the mouse, the pointer is the hand's ray (not gaze). Optional, **off by default**.

## Phase 0: groundwork
- [x] DPI scaling fix in all four forms (AutoScaleDimensions) — verified at 125 % on the 3080 Ti laptop; 150-200 % still to check.
- [x] Remove the remaining absolute sizes: `Ui.CompactHeader` Height 84, stats box and live group `Location`.
- [ ] Shared code in `Common.cs`: game record reader/writer, install helper (DLL), diagnose engine (extracted from `Wizard.cs`).
- [ ] `tools/wsl/check_gui.sh` and `harness/gui_shot.py` build every new EXE; shots at 100/125/150/200 % become part of the GUI check.

## Phase 1: launcher menu + streamer window
- [x] `launcher.exe` (Launcher.cs; Streamer + Setup wizard cards, more as tools appear; a temporary Open logs folder button until the Logs window): cards for each tool, each launching its EXE. Reads the same folder layout as today.
- [ ] Streamer window:
  - [x] remove Logs and Configure buttons (the wizard stays reachable from the launcher);
  - [x] bottom bar: **Debug logging, off by default** (default changes; saved value kept for existing users);
  - [x] **Recenter** moves next to Defaults in the Position group (it re-anchors forward + position, it is not a frame-generation setting; already sent at every game start);
  - still to do in this window: Apple-style mirror toggle (Phase 5), stats tiles, DLL popup;
  - new toggle under Frame generation: **Mirror Windows desktop between sessions** (off by default);
  - live stats and post-session verdict as tiles: game / streamed / shown fps, bitrate, latency split (encode + network + decode + other) as a
    stacked bar, frames not streamed, packets lost; the verdict is one status line plus the same tiles for the last run.
- [ ] "DLSS motion vectors" ticked but the DLL absent: after the game ends, a popup offers to install it (uninstallable from the Logs window),
  with "don't show for this game again" (stored in the game's JSON).

## Phase 2: Logs and performance window + per-game data
Data model: `config\games\<exe name>.json`, one file per game (UEVR style), replacing `config\games.json` (no migration: old records are not carried over;
the launcher's "Recommended" reads the new files). The streamer's own log, `logs\VisionALVR.log`, stays one file, independent of the games.
- Display name = the game's main window title (fallback: exe name). Paths seen. Preferences (don't-ask flags).
- Per session: start/end, headset, generation level, game / streamed / shown fps (mean, p1), repeated and missed period shares, bitrate,
  latency split, packet loss and network stats (as ALVR's summary), DLSS-vectors DLL present and **actually used** (flow skipped counters),
  GPU name / driver / VRAM from NVML (`nvidia-smi` fallback).
- Retention: last 30 sessions in full; older ones fold into the per-level 10 s-window summaries the recommendation needs. Bounded disk.
- Window: left list of games that ran, right panel with the selected game's stored data and history tiles.
  - **Install / Uninstall DLSS motion-vector DLL**: copy the packaged DLL as `dxgi.dll` (if `dxgi.dll` exists: `version.dll`); log what was done so
    uninstall is one click; the packaged master copy is never touched; MD5 check against the master detects stray old installs;
    admin elevation only when the game folder is not writable.
  - **Export log**: one zip (the game's JSON, VisionALVR.log, the relevant debug folder).
  - **Auto-delete logs of uninstalled games**: on open, delete a game's file when its exe no longer exists (setting, default on).

## Phase 3: Diagnose window + wizard refactor
- [ ] The wizard's verdict (measurement / value / target / verdict table, Wizard.cs) becomes a shared class; wizard steps and a standalone
  **Diagnose** window both use it. No copied code.
- [ ] AVP-only wizard step for chroma passthrough: try to set it from the host, query the AVP to verify; if refused, show
  `ALVR_VisionOS_Chroma_Passthrough.png` (move from the repo root to `tools/gui/res`), re-check, continue.
  **Open question:** whether ALVR exposes setting or reading it from the server; check the visionOS client protocol first. Fallback: manual
  confirmation with the screenshot.

## Phase 4: in-VR keys
- [ ] The INSERT dialog in VR (user: VDXR captures the key, two presses turn it off so UEVR gets it). **Checked on the built binaries (2026-10-19): not VDXR, not our shim, not our host.** `virtualdesktop-openxr.dll` imports no keyboard API (no GetAsyncKeyState/GetKeyState/RegisterHotKey/hooks/raw input; its only 'Insert' string is `cJSON_InsertItemInArray`; GameInput is used for gamepads only, `GameInputKindGamepad`). `LibOVRRT64_1.dll` (our shim) polls only R W A S D Q E U I O P, and only without a host connection (`!m_ipc.state`). The host reads no INSERT either (its only key is the recenter key). So the dialog is something else in the game process: UEVR's own VR overlay (INSERT is its menu key; placement relative to the controller/world would explain 'unreadable on the Vision Pro'), an OpenXR API layer (the new layers check lists them), or a game-specific overlay. To settle it on a PC: same game and UEVR under SteamVR or the Oculus runtime (dialog still there = not ours), and the layers dialog (any enabled implicit layer?).
- [x] Global hotkey for a full recenter while playing: host polls the key (`controls.recenter_key` in visionalvr.json, default **End** (used by no mod known: UEVR is Insert, Luke Pause, Mutar F11), `off` disables); chosen in the wizard's configuration page.
- [x] (code read; lab test still to do) Head pose at session start: forward + horizontal position (X, Z) are re-anchored by `recenter` at game start, height by Eye height;
  verify with a test that X/Z really are zero at start (lab: simrig with an off-centre start pose) instead of assuming.

## Phase 5: desktop mirror between sessions (last, optional, experimental) — BUILT, not yet tried on a headset
Status: host + C++ built on the builder; `harness/mirror_check.py` passes the maths and the picture (SDR, HDR->SDR, cursor); the live desktop capture is blocked on the 3080 Ti laptop by the OS (access denied: corporate capture protection) and untested elsewhere; hands, pinch and the screen's stability are only testable with a headset (checklist in the harness).
Goal: for people who start games from a distance (garage): see the Windows desktop in the headset between sessions and use the hand as a mouse.
Minimal code:
- **Capture**: Windows Graphics Capture (or DXGI desktop duplication) of the primary monitor, only while no game session runs and the toggle is on.
  **HDR**: if the desktop is HDR (scRGB / PQ), one small pass converts to SDR (tone-map + sRGB) before the existing encode path; SDR passes through.
- **Idle frame**: today the between-sessions view is the chroma-key frame; with the toggle on, the desktop replaces it on a virtual screen
  (position in front of the user, set at session start). That overrides the chroma key by design, and it is fine: UEVR/desktop-launched games keep the
  passthrough technique (toggle off, the default); the mirror is for true VR-controller games started from the headset, where you need room.
- **Input**: the pointer is the hand's aim ray intersected with the virtual screen; pinch (or trigger on controllers) = left click, a second
  gesture/button = right click; applied with `SendInput`. Hands arrive as emulated Quest 2 controllers in the shim today: **first step is to find out
  exactly what the AVP client and the Quest client send** (pose, aim, pinch as button value) and map from that.
- Default off; documented as experimental. Stops mirroring the moment a game session starts.

### Harness for the mirror (built with Phase 5, before trusting it)
1. **Input injection test (no headset)**: extend the simrig so the host receives scripted hand/controller samples (pose + pinch) from a
   file; assert the host computes the expected screen point and click events (log the `SendInput` target without executing it in the test mode).
2. **Capture/convert test**: a test window with known colour patches on the desktop, SDR and HDR (virtual HDR display or a recorded HDR frame fed
   to the converter); the harness compares the converted frame's patches with expected SDR values (tolerance), the same way `metrics.py` judges frames.
3. **Round trip on the PC**: scripted pointer path over a test window with buttons (a small WinForms target that logs click positions); the
   harness checks the logged clicks land within N px of the intended targets.
4. **Real device checklist** (cannot be automated): AVP hands and Quest hand tracking, pinch reliability, ray offset, jitter, HDR desktop on and off.
   The harness prints the checklist and records the observed numbers from the run log.

## Added during the build
- [x] Launcher: **OpenXR runtime** row: shows the active runtime and switches to the other one (VisionALVR <-> the one active before, saved by the register script), reversed after each click; going back also offers to re-enable the OpenXR layers VisionALVR switched off.
- [x] **OpenXR API layers** (registry lists of the Khronos loader, HKLM/HKCU, 64/32-bit, implicit/explicit): the verdict (Diagnose and the wizard) flags enabled implicit layers, rated Poor when one may capture input (hand/controller/input tools); a dialog lists them and disables/enables the ticked ones (reversible: a registry DWORD, recorded in config\layers_disabled.json). Reference: FredEmmott/openxr-api-layers-gui (MIT), same lists; no code taken.
- [x] Per-game records verified end to end on the builder (a 26 s run wrote config/games/<exe>.json with the session, network, GPU and vectors); idle/green scenarios still pass with the mirror off.

## Package layout (2026-10-19, built and tested)
The root holds one file, `VisionALVR.exe` (the launcher). `bin\` has every exe and DLL the launcher starts or loads (including the OpenXR runtime), plus `register_openxr_runtime.ps1`. Other folders: `DLSSMotionVectors\`, `bench\`, `profiles\`, `config\`, `docs\`, `logs\` (on use), `extras\` (personal builds only). No `.bat` files. `python3 harness/run.py build package` moves an older-layout `out\dist\VisionALVR` aside (renamed `.old-<time>`, never deleted); `test-install` and `test-zip` check the root and the absence of `.bat`.

## Desktop and DPI coverage (2026-10-19)
`harness/ui_matrix.py` (every window and wizard page at 1080p/1440p/4K x 100-200 % scale), `harness/launcher_flow.py` (the launcher hides while a tool is open, returns when all are closed), `harness/mirror_check.py` (7 desktop shapes x SDR/HDR). They found and fixed: sizes set in code were raw pixels (windows and docked panels did not scale), late-built wizard pages were not scaled, fixed list-column widths broke with fonts (columns are now sized from their content), windows could be taller than a 1080p work area (now capped, the body scrolls), tall desktops made a screen taller than the view (capped at 40 degrees), and the cursor dot was tiny on a 4K desktop (now scales with the desktop's height).

## Native DPI, stub, firewall (2026-10-19)
- GUIs run in WinForms' own per-monitor mode: `bin\<exe>.config` (`DpiAwareness=PerMonitorV2`, copied from `tools/gui/app.exe.config`), the `TargetFramework` assembly attribute (Common.cs; csc does not add it), a manifest without DPI entries, and `DpiForm` (designer pattern: built suspended, scaled at load) under every window. Controls created after load: `Ui.LateScale`; pixel constants: `Ui.Px` / `LogicalToDeviceUnits`. Measured: manifest-only = legacy mode (`DeviceDpi` 96).
- The root `VisionALVR.exe` is a stub (tools/gui/Stub.cs) that starts `bin\launcher.exe` and exits (also tells a user who runs it from inside the zip to extract first); the launcher is single-instance. The streamer is run directly by the owner (`bin\streamer.exe`).
- Firewall: `Firewall.Analyze` (PowerShell NetSecurity cmdlets: locale independent, protocol and network aware) finds the rule for this folder's exact exe, blocking rules, redundant / duplicate / stale rules and other copies; the streamer, wizard and Find headset only offer to add a rule when none is in place; `FirewallForm` (launcher button, verdict row) lists everything with the leftovers ticked; `tools/check_firewall_rules.bat` does the same from a prompt (for the 5090).

## Post-run flags and the debug box (2026-10-19 night, after the Valor Mortis black screens)

What the night established (headless A/B on the 5090, no headset, UEVR attached by script; details in PERFORMANCE_FINDINGS.md): the black
screen was **UEVR's stereo set-up never completing when it attaches during the game's loading movie** (its weekend builds), identical with no
extractor DLL in the game folder, with vectors off, with the 0.4 shim and host. It was **not** the motion vectors: in every run the tap
attached and copied cleanly, and the run that worked had the same chain with vectors on. So the engine must not turn vectors off on this
signal; it would hide the real cause and change nothing on the headset. What the UI should do instead:

- [ ] **Debug logging stays a checkbox in the streamer**, on and off, as in 0.4. The 0.5 notice-only variant leaves the user unable to turn
  debug back on without editing `config\visionalvr.json` by hand (the user hit this on 2026-10-19). Default off is fine; the control must
  be reversible in the window.
- [ ] **Orange flag after a run: "motion vectors did not reach the streamer".** Conditions: the DLL is installed in the game's folder
  (MvDll.State.Installed / OlderCopy), the tick was on, generation was on, and the record's `mv.frames` is 0 (or the per-window
  `status.mv.share` stayed under, say, 0.5 for the whole run). Orange, not red: the game played, generation ran on optical flow, only the
  vectors are missing; that is the engine's failsafe (`vecSource` falls back to flow per eye, nothing to switch). Text: what was expected,
  what the shim saw (`motion_tap` streams and evaluates, "sources not copied"), and the one action: the DLL version and the game's DLSS
  setting. Data: `game_run.mv` / the game record's `mv`, the `status.mv` window counters, `motion_tap` events.
- [ ] No flag for a black screen: the user sees it. What the night cost was the *why*, so the diagnosis goes where one looks for it: one
  line in the general log at game end when the game rendered for more than ~10 s and no frame carried a projection layer ("the game
  rendered N frames but never sent a stereo picture: the VR injector did not set up, VisionALVR received no image to stream"), and the
  same sentence in the Logs window's last-run summary and the exported zip. Not a tile, not a colour.
- [ ] The orange flag belongs to the per-game page (Performance / inventory) as a line under the tiles, with the same wording in the Logs window.

## UI round 2 (2026-10-20, from a night of play on the Vision Pro and the Quest 3)

Spec of the target windows: **docs/UI_SPEC.md** (written with this section). The rule for everything below: the engine (`alvr_host.exe`, the shim) is not touched
for the UI; where a number is wrong at its source (latency, U7) the fix is in the engine's aggregator and says so. Every item names the harness that proves it; an
item is not done until that test exists and passes. Items marked (host) change the engine's reporting only.

### A harness for the streamer window first (shared by most items)
- [x] **Event replay.** `uitest.exe --replay <events.jsonl>` builds the streamer, feeds the lines to `RuntimeForm.HostEvent` (the same function the live host drives) with a
  fake clock, and writes a text dump of the window (every visible label, tile, switch state, notice) and a screenshot. `harness/streamer_events.py` runs scripted event
  sequences on the builder and asserts the dump: a 90 s game with vectors; a game without the DLL; the DLL installed but no vectors; a 12 s run (shorter than the 20 s
  the engine records); the mirror starting and stopping before, during and after a game; an absurd latency window; a Vision Pro and a Quest connecting. Scripts are
  committed (`harness/fixtures/streamer_events/*.jsonl`), several taken from real `host_stdout.txt` files of past runs.
- [ ] `ui_matrix` gets the new states of the streamer as cases (`streamer-avp-auto`, `streamer-quest`, `streamer-after-ok`, `streamer-after-blind`, `streamer-no-dll`,
  `streamer-debug-on`) so the layout is audited at every desktop size and scale; `UiTest` can already put the window in a demo state (`ApplyDemoState`).

### Bugs
- [x] **U1. Export logs leaves a dialog open** (Game history, *Export log...*). Likely cause: after the zip is written the code starts `explorer.exe /select,<zip>`, a new
  Explorer window that looks like a folder-selection dialog, and the work runs inside the `using` of the Save dialog. Fix: dispose the dialog before the work; do the
  work; show an inline line under the buttons "Saved to <file>  [Open folder]" (no Explorer unless clicked). One `LogExport` class (Common.cs) used by Game history and the
  streamer's new Export button (U4). Test: `uitest --export-selftest <zip>` runs the export to a path without any dialog, checks the zip's entries (game record, general
  log, newest debug run, DLL record, info.txt) and that `Application.OpenForms` holds only the window; `harness/export_logs.py` runs it on the builder.
- [ ] **U2. The "no motion vectors" pop-up** (streamer): eliminated. Root cause of "Install does nothing" is not known (the offer is non-modal; the click should run
  `MvDll.Install` in the form's `FormClosed`): first log each step (offer shown, button, `MvDll.Install` result and elevated-copy outcome) and reproduce in
  `uitest --dll-install-selftest` (install / update / remove in a temporary game folder, including the elevated fallback and a read-only folder). The offer becomes
  the inline line of U6 (Frame generation component). Test: the selftest + `streamer_events` (offer appears only after a run that used generation, the vectors tick on, with no
  DLL and no vectors, not dismissed for that game; *Not for this game* hides it for good: `MvDll.Dismiss`; installing it flips the line to "installed: takes effect next session").
- [ ] **U7. Absurd latency numbers** (Lies of P: 4 958 802 102 ms, p95 29 752 812 252 ms, encode 137 ms average; the other tiles are fine). The per-window means in
  `logs.rs` (`take`) sum every client sample; a handful of garbage samples (a duration that went negative and wrapped, most likely from a generated or re-stamped frame whose
  timestamps do not pair with the client's) dominate the mean, and `net_summary` takes the mean of means. (host) Fix at the source of the aggregate: drop samples
  with any component above 2 s (or total above 5 s), count them (`latency_outliers` in the window and the run's `net`), keep the first ten raw samples in the debug log
  (`client_stats_outlier` events) to find the cause, and report the median and p95 of per-window **medians**, not means. UI: a value above 1 s, or a run where outliers
  are more than 10 % of the samples, shows "n/a" with a tooltip saying why (Game history tile, sessions table, streamer tile). Tests: Rust unit tests on the aggregator
  (synthetic samples with a wrapped duration; clean samples unchanged; all-outlier window gives none), `streamer_events` / `ui_matrix` with the absurd record, and an e2e
  with generation on (`FRAME_GEN` scenarios in the simrig and the mock client) asserting `net.latency_ms` < 200 and `latency_outliers` == 0 on clean runs; then find
  which frames produce them (the debug samples) and fix the cause if it is in our re-stamping.
- [x] **U8. The desktop mirror is not a game in the UI.** It is a game to the engine (it keeps its record and log) and stays so; the UI hides it: one
  `Games.IsHidden(exe)` (exe name `mirrordesktop.exe`, case-insensitive) used by the game list, the Game line, the Diagnose facts (`LogFacts`), the advice and the
  auto-delete of logs. Test: `ui_matrix` performance fixture with a mirror record (the list must not show it), `streamer_events` (the mirror's `game_started/ended/run` never
  change the Game line, the tiles or the DLL offer).
- [x] **U9. The last session's statistics must be on the streamer when a session ends.** Today the tiles follow `status` while `lastSession` is false and the `game_run` event
  fills them once; the engine emits `game_run` only for runs of 20 s or more, so after a short run the tiles keep (or fall back to) live values, and anything that
  restarts `status` after the game (the mirror, an idle window) can overwrite them. Rules: the tiles have two states, *live* (a game runs) and *last session* (the last
  real game's `game_run`), the latter kept until the next real game starts; a run shorter than 20 s sends a short `game_run` (`recorded:false`, same numbers) so the
  tiles update (the record is still not saved) (host: one extra flag on an existing event); `status` windows from the mirror or with no game never touch the tiles; the
  caption says "Last session: <game>, <minutes>, <headset>" or "Live". Test: `streamer_events` with the six sequences above, asserting tile text and caption after each event.

### New behaviour
- [ ] **U3. Debug logging is always visible** at the bottom of the streamer: the checkbox (on or off), *Export logs...* (U1's `LogExport`) and *Open logs folder*, so sharing
  feedback is one click from anywhere. (This replaces the "notice only" variant and completes the item "Debug logging stays a checkbox" of the 2026-10-19 night section.)
  Test: `ui_matrix` cases with debug off and on; `streamer_events` (toggle sends `debug on|off`, persists `debug`).
- [ ] **U5. The streamer's component layout** (docs/UI_SPEC.md): *Streaming*, *Frame generation*, *Position*, *Picture*, *Desktop mirror*, then the debug row. Frame generation:
  switch + Auto/1-3/1-2/2-3 + the vectors tick, no explanatory text; Desktop mirror: the switch only, no "Between games" caption; Picture: gamma and sharpening, with
  *Advanced* unfolding brightness, contrast and saturation (and *Defaults*); Position: height and *Recenter* together. Size from content as always. Test: `ui_matrix`
  at every size and scale (0 FAIL), `streamer_events` (every control still sends its command: `gamma`, `color`, `frame_gen`, `frame_gen_rate`, `game_mv`, `recenter`, `height`),
  README screenshot regenerated.
- [ ] **U5b. Eye height: Auto and Manual (Vision Pro only).** Auto sends `height 0` (already the engine's meaning: the headset's own floor estimate, `eye_height = headset y`
  at the 45th sample); Manual sends the typed value, a number box with 1.30 m as the default (0.5-2.2 m) instead of the slider. Saved as `display.height_mode` (`auto` |
  `manual`; existing users stay on Manual) next to `display.height_m`. In Auto the window shows what the headset reported (`height_calibrated.eye_height_m`: "headset
  reports 1.62 m"). A Quest or any non-Apple headset is shown as Auto and disabled (its floor is right). Hint: Auto is right when the headset saw the room (standing at the
  start of the session), Manual when seated. Test: e2e with the mock client at two head heights (`--height`), asserting `height_calibrated` for Auto (the head's y) and
  Manual (the typed value); `streamer_events` for the AVP and the Quest states; the number box's range.
- [ ] **U6. Frame generation component: what the last session did with vectors.** After a session that used generation: **green** "Game motion vectors worked (N frames)" when
  `game_run.mv.frames` > 0; **orange** "Generation ran blind (optical flow only): the DLL is installed but no vectors arrived" with the cause list (DLL version, the game's DLSS setting,
  upscaler not DLSS) when the DLL is installed (`MvDll.State.Installed` / `OlderCopy`), the tick was on and `mv.frames` is 0 (the same wording as the Game history page's
  orange line, from one function); **plain** "No game vectors: install the extractor  [Install]  [Not for this game]" when the DLL is not installed. Nothing for a session without
  generation, and nothing for the mirror. The line stays until the next real game. Test: `streamer_events` (worked / blind / no DLL / dismissed / generation off), the simrig
  motion-vector scenarios (`harness/simrig/check_mv.py`) asserting `game_run.mv.frames` on both sides of the line, `dll_install_selftest`.
- [ ] **U10. The virtual audio device's volume is held at 100 % while the streamer is up.** The engine already does it (a thread in `main.rs` sets the endpoint volume of the
  default output to 1.0 every 3 s when its name contains "cable" or "vb-audio", event `audio_volume_forced`). Check on the 5090's logs whether it fired; extend it so it
  also covers the device named in the audio settings (not only the default one) and the moment the streamer switches the default (the user's `startvisionalvr.bat` does it
  after the host started), log the result on every change, and show it in the streamer as one line under the debug row only when it matters ("Audio: VB-Audio Cable held at
  100 %"; orange when the device is missing or the call fails). Test: an e2e on the builder that sets the default device's volume to 40 % through the Core Audio API before
  and after the host starts and asserts `audio_volume_forced` and the volume back at 100 % within 6 s (the device selected by a test variable, restored at the end).

### Status (2026-10-20)
Done and passing on the builder: U1 (`harness/export_logs.py`), U8, U9 (`game_run` now also sent, `recorded:false`, for runs of 2-20 s), and the replay harness (`harness/streamer_events.py`, three fixtures; the other sequences join the items they test). `ui_matrix` 0 FAIL. Added by the user mid-session: shorter text everywhere in the streamer (done with U5).

### Order
U1, U8, U9 + the replay harness (small, bugs) → U7 (engine aggregator + tests) → U5 / U5b / U3 (layout, height, debug row) → U6 + U2 (Frame generation component) → U10.
Each step: tests first for the state it changes, `ui_matrix` at 0 FAIL, README screenshots regenerated at the end.

## Order
0 → 1 → 2 → 3 → 4 → 5. Phase 2 before the DLL popup (shared install code). Phase 5 last.
