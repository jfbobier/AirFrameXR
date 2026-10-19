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

## Order
0 → 1 → 2 → 3 → 4 → 5. Phase 2 before the DLL popup (shared install code). Phase 5 last.
