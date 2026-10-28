# AirFrameXR UI specification (0.5)

What each window is for, what it shows, where the numbers come from, and the rules every window follows. Written 2026-10-20 from what was built in
0.5 (its plan is archived) and the directions of the 2026-10-20 review (the "UI round 2" section of the plan). Marked **[target]** where the window is not
yet like that. The harnesses that hold each rule are named at the end of each section.

## Principles

1. **One window, one objective.** Launcher = choose; Streamer = stream and tune live; Game history = what happened; Diagnose = what is wrong; Setup = pair
   and configure; Advanced = everything ALVR has. A control lives in the window that is about it, once.
2. **Quiet by default.** Healthy state shows numbers and a green word; problems show one sentence and the one action that fixes them, in the component
   they belong to (no modal pop-ups for things the user did not ask for; a modal dialog only follows a click).
3. **No jargon without a reason next to it.** Names the user can act on; numbers get a unit; a measured value says where it came from.
4. **Reversible.** A switch the UI turns on (OpenXR runtime, firewall rule, OpenXR layer, DLL copy, mirror, debug) can be turned off in the same place.
5. **Lean.** Manual switches over automatic modes; a setting only exists if a person would change it. Nothing is added to the engine for the UI.
6. **Size from content, never from pixels.** Windows are as big as their content up to the work area (the body scrolls beyond it); every pixel constant is a
   96-dpi design value (`Ui.Px`, `AutoScaleDimensions 96`); widths come from content (`Ui.FitColumns`); controls built after load are scaled by hand
   (`Ui.LateScale`). Native per-monitor DPI (`bin\<exe>.config` + `TargetFramework` attribute + manifest without DPI entries + `DpiForm`).
7. **The logo greets on the launcher, the streamer and the first wizard page only.** The streamer is the window people are supposed to use.
8. **Accessible names** on every control that has no visible text; the tab order follows the reading order.

Harness: `harness/ui_matrix.py` (every window and wizard page at 1080p / 1440p / 4K x 100-200 % scale: fit, clipping, overlap, cut-off text, one
screenshot each; 0 FAIL required), `harness/launcher_flow.py`.

## Windows

### Launcher (`bin\launcher.exe`, started by the root stub `AirFrameXR.exe`)
Six cards, in this order (0.6): **Streamer** (primary), **Game statistics**, **Settings** (`configure.exe --settings`: pairing, benchmarks, video, audio, network), **Advanced settings** (`configure.exe --switches`: docs/EXPERIMENTAL_SWITCHES.md), **Diagnostics (experimental)**, **Restart headset setup** (the wizard). A status card says
"Ready" (green) or "Needs attention" (red) with one row per problem and its button: the OpenXR runtime (shows the active one, the same button goes back), OpenXR
layers that may take the controllers, the Windows Firewall (read in the background). Quiet links: *Advanced (full ALVR settings)...*, *Help*. The launcher
steps aside while a tool is open and returns when all are closed; starting the streamer from it first offers to close a running one. Single instance.

### Streamer (`bin\streamer.exe`) **[target layout]**
Top to bottom. Controls are grouped where they balance the window (Picture and Position side by side), not one box per feature.

```
[logo header: Integrated OpenXR renderer + streamer for ALVR client VR headsets · version]
Headset: connected - Apple Vision Pro (192.168.1.50)                  [Find headset...]   <- the button only while not connected
Game: Lies of P   (none while the mirror runs)    GPU ... · OpenXR runtime ...
+- Streaming ---------------------------------------------------------------------+
|  tiles: Game fps | Streamed fps | Headset shows | Bitrate | Latency | Loss        |   live during a game, the LAST SESSION's after it
|  one line: what the numbers are (live / last session of <game>) and the repeat/skip shares |
+- Frame generation --------------------------------------------------------------+
|  [switch]  [Auto][1/3][1/2][2/3]   [x] game vectors (only where the DLL is installed)    |   no explanatory text; the control is the explanation
|  after a session that used it: green "Game motion vectors worked (2558 frames)"            |
|                           or orange "Ran blind: no vectors reached the streamer" (+ cause)  |
|                           or, DLL missing: "No game vectors"  [Install]  [Not for this game] |
+- Picture ----------------------+- Position ------------------------------------+
|  Gamma      [----o---]  1.20   |  Eye height (o) Auto ( ) Manual [1.30] m        |   Auto only on the Vision Pro; "headset reports 1.62 m" under it
|  Sharpening [o-------]  0.00   |  [Recenter]   Desktop mirror [switch]           |
|  [Defaults]                    |                                                 |
+--------------------------------+-------------------------------------------------+
> Advanced   (opens full width: Brightness, Contrast, Saturation, Resolution scale + "Apply (restarts the streamer)")
Debug logging [ ]    [Export logs...]  [Open logs folder]            <- always visible, on or off
footer: one inline notice at a time (firewall, setup error, encode error), with its action button
```

Rules:
- **Fixed window**: the streamer is sized once, at load, for its fullest ordinary content (long numbers, the vectors line, the vectors tick, the height report); notices that come and go
  (vectors line, export result, audio line, the footer) have reserved lines, so nothing resizes or moves the window while you use it. Only *Advanced* changes its size (and back), and a
  blocking message (the firewall warning at the top, a long error in the footer) makes it grow once to show it. `harness/streamer_events.py` window_size holds this.
- **Frame generation** is its own component: the switch, the rate (Auto + three segments; Auto = the rate proposed from the game's recorded runs), the
  *Uncapped* tick (2026-10-23: `frame_gen_rate uncapped`, slider and Auto greyed while it is on; tooltip: latency traded for smoothness) and the
  DLSS-vectors tick (always shown since 2026-10-23: which game comes next is not known; tooltip: needs the DLL in each game's folder and a DLSS game). The generated-frame state of the *last session* is shown here
  (green worked / orange blind / the install offer), never as a pop-up. Frame generation is forced off while `mirrordesktop.exe` runs (command `frame_gen off`
  to the engine; the saved choice returns when it ends).
- **Position**: *Auto* sends height 0 (the engine uses the headset's own floor estimate: correct when the Vision Pro saw enough of the room, i.e. standing at the
  start of the session); *Manual* sends the number (default 1.30 m, recommended seated). A Quest or other headset reports its floor correctly: the choice is
  shown as Auto and disabled. The number replaces the slider (saves space; typed or stepped with the arrows, 0.5-2.2 m).
- **Picture**: the two settings people touch; the rest behind *Advanced*. Everything applies live except the **resolution scale** (0.50-2.00, default 1.00 = nothing; above 1 is
  supersampling): it multiplies the per-eye resolution and reaches the engine through `session.json` (`EyeScale`; the unscaled size is kept in `airframexr.json`), which is negotiated at
  connection, so it is applied by a streamer restart (button) and the game restarted. The setup panel shows and saves the unscaled size.
- **Desktop mirror**: a switch next to Recenter (no box of its own). The streamer starts `bin\mirrordesktop.exe` while a headset is connected and no game runs (docs/DESKTOP_MIRROR.md). The mirror is
  an app like a game to the engine but is **hidden from every game list and label** (Game line, Game history, Diagnose facts).
- **Debug and feedback** are one row at the bottom, always visible: the checkbox (default off), *Export logs...* (the same zip as Game history: the general log, the
  newest debug run, the game records) and *Open logs folder*. Exporting never leaves a dialog open and ends with an inline "Saved to ..." line with a link.
- **Streaming tiles after a session**: the numbers of the last real game stay until the next game starts, whatever else happens (the mirror starting does not
  replace them); a run shorter than 20 s still updates the tiles (it is just not saved to the game's record). A latency tile shows "n/a" instead of an absurd
  value (see the plan: outliers are dropped and counted by the engine's aggregator).
- Inline notices (firewall, setup problem, DLL offer, encode error) are one component with a message, an optional action button and a dismiss; never a modal.

Data: host events `client_connected/disconnected`, `status` (every 10 s), `game_started`, `game_ended`, `game_run` (the finished run: numbers, `net`, `mv`),
`height_calibrated`, `views_config`; commands to the host: `gamma`, `height`, `color`, `frame_gen on|off`, `frame_gen_rate`, `game_mv`, `recenter`, `debug`.

Harness: `ui_matrix` cases `streamer`, `streamer-clean`, `streamer-empty` (+ the new states below), the streamer event-replay (plan: `harness/streamer_events.py`), the
mirror scenarios (`e2e_mirror`, `e2e_mirror_desktop`).

### Game history (`bin\performance.exe`)
Left: the games with a record (newest first; **the desktop mirror is not listed**). Right, for the selected game: name and path; the last session in one line
(date, minutes, headset, generation level); four tiles (Game fps, Headset shows, Latency, DLSS vectors) and *More numbers*; under them ALVR's own breakdown on one line, `Streamer latency N ms = encoder + transport + decoder` (n/a when not believable); the sessions table (the last ones, newest
first: date, headset, generation, minutes, game fps, shown fps, repeated %, latency, vectors); the DLL line (one status and one action: Install / Update / Remove /
Restore); *Export log...*, *Forget this game*; a post-run line under the tiles (orange: vectors did not reach the streamer; the wording is the one of the
streamer's Frame generation component). *Troubleshooting and settings* (collapsed): debug logging, auto-delete of uninstalled games' logs, *Open logs folder*.
Export: a Save dialog, then an inline "Saved to ... [Open folder]" line; no dialog stays open. Numbers outside any plausible range are shown as "n/a".
Harness: `ui_matrix` case `performance` (fixtures `harness/fixtures/ui_matrix`, including a record with an absurd latency and a mirror record), `harness/advice_check.py`.

### Diagnose (`configure.exe --diagnose`) and Setup (`configure.exe`)
Four visible steps numbered from 1: **Headset, Setup, Test, Result** (Diagnose hides *Setup*). *Setup* shows one **Recommended** choice (the matching community profile,
else the manual configuration pre-filled from the headset) and *Other ways to set up* (an imported profile, or an existing ALVR configuration with *Import my working
ALVR session.json* which asks for the ALVR folder, finds the file, checks it and keeps a backup). *Result* leads with the verdict (colour + one sentence) and the
top three suggestions with their measured reason and an **Apply** button where the engine has a setting for it (arrival margin, GPU keepalive, frame generation
Auto / lower rate, debug off); the rest under *More suggestions*; every measurement under *Details*. Settings page: the values with the reason next to each,
*Experimental* badges instead of suffixes.
Harness: `ui_matrix` wizard pages 0-6, `harness/advice_check.py`, `harness/wizard_e2e.py`.

### Settings (`configure.exe --settings`, launcher card "Settings"; 0.5: Advanced)
Tabs: headset (pair), benchmark (network sweep, recommended bitrate), settings (all of ALVR's we expose), and *Import ALVR settings...*.

### Dialogs
Firewall review (one **Clean up** button: adds the missing rule and removes the ticked leftovers; the rules under *Details*); OpenXR layers review (list with reversible
switches); DLL offer **[target: removed as a pop-up, becomes the inline offer in Frame generation]**; chroma-passthrough step (Vision Pro: the reference picture). A dialog
exists only after a click.

## Cross-window facts
- Colours: accent blue for the primary action and the active step; green = works, orange = works with a limitation, red = does not work; dim grey for provenance.
- Fonts: the system message-box family; body 9.5 pt, big 11 pt.
- Every number has a unit and, where it is measured, a window ("typical 10 s window", "slowest tenth").
- Logs: `logs\AirFrameXR.log` (one general log for the streamer and the tools), `logs\debug\<time>\` per debug run, `logs\mirrordesktop.log`, `config\games\<exe>.json`.

## Test matrix the UI must keep passing
`ui_matrix` (0 FAIL), `launcher_flow`, `advice_check`, `firewall_check`, `mirror_check`, `e2e_mirror`, and (planned) `streamer_events` (event replay with text assertions),
`export_logs` (the export without a dialog left open), `dll_install_selftest` (install / update / remove in a temporary game folder, including the elevated copy).
