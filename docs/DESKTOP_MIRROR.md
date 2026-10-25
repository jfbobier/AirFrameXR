# Desktop mirror (mirrordesktop.exe)

Between games the headset shows the Windows desktop as a monitor-sized screen in front of you; the controllers are laser pointers that move the mouse.
It is **an OpenXR app like any game**, not part of the streamer engine: `bin\mirrordesktop.exe` connects through the runtime and the shim, renders a projection
layer with the views the runtime gives it, and reads the controllers through OpenXR actions. The engine treats it as a game like any other (it is recorded in the game history); the streamer turns frame generation off while it runs
(`frame_gen off`, the user's setting is restored when it ends). The shim signals `Local\VisionALVR_GameStarting` when another app starts so the mirror leaves the headset at once. The event has an explicit security descriptor (any integrity level and account of the session may signal it; a game running elevated or not no longer matters), the mirror leaves anyway 1.5 s after the signal if the runtime keeps its session open, and the engine ends the mirror's run and starts the game's when another app takes over (the app that was told to leave does not clear the shared "alive" flag).

- **Switch it on** in the streamer window ("Between games"). The streamer starts the app while a headset is connected and no game runs, and starts it again
  after a game ends. Or run `bin\mirrordesktop.exe` by hand (`--seconds N`, `--no-mouse`, `--clear RRGGBB`, `--log file`).
- **The picture:** DXGI desktop duplication of the primary monitor (SDR, or HDR converted to SDR with Windows' SDR white level), the screen 56.5 degrees wide
  (1.6 m) at 1.5 m, a perspective-correct quad, everything else the streamer's idle colour (`video.idle_rgb` in `config\visionalvr.json`, `107C10` by default: the
  headset's alpha / passthrough). The screen has four positions a quarter turn apart (where you looked when it started, to its left, behind, to its right):
  it stays in one until your head is 55 degrees from it, then glides to the nearest. It follows you when you walk (more than 15 cm away from where it would
  be in front of you); End starts the four positions again from where you look now. The capture is copied on the GPU when the screen's adapter is the
  renderer's (no round trip through the CPU).
- **The controllers:** the right controller (or hand tracking) aims, the orange beam shows it; trigger / pinch (down at 0.3, up at 0.15) = left button, A / X = double click (Windows runs a program or opens a file on one), grip or B / Y = right button, thumbstick
  up/down = scroll. The mouse moves over the whole virtual desktop (`MOUSEEVENTF_VIRTUALDESK`, per-monitor DPI aware), so a second monitor does not shift it.
- **Steadiness:** the pointer is smoothed (steadier when slow, immediate when fast) and held still for 12 pixels while a press starts, so a click lands where it was aimed.
- **Logs:** `logs\mirrordesktop.log` (the controller seen, the pointer reaching the screen, the aim and screen poses and the largest trigger / grip values every 5 s, every button press).
- **Update only the app:** replace `bin\mirrordesktop.exe`. Build: `build.ps1 mirrordesktop` or `python3 harness/live.py build mirrordesktop`.

Tests: `harness/mirror_check.py` (the maths in `mirror_math.h`, the picture on the GPU, a screen turned 40 degrees, the beam) and the scenarios `e2e_mirror` (the real chain with the mock client on a synthetic desktop: the idle colour
outside the screen, the screen in it, the beam in the stream, the pointer on the screen, and the timing of every scripted trigger / grip / A / B edge against what the
app decided, the app's frame rate and picture cost) `e2e_mirror_handover` (a game, `xr_probe`, starts while the mirror runs: the signal arrives, the mirror leaves within 2 s, the engine records the mirror's run then the game's) and `e2e_mirror_desktop` (the same on the real desktop, for a PC whose screen can be duplicated). Flags for
the harness: `--synthetic` (a known picture instead of the desktop, no real mouse), `--input-log <csv>` (every button event with its wall-clock time).

The old design (the mirror inside `alvr_host.exe`) is the git tag `pre-mirror-app`.

Credits: the idea of absolute mouse moves over the virtual desktop and wheel events follows Immersive-2's input injector (MIT,
https://github.com/PGSCOM/Immersive); the code is our own. Desktop+ (GPL-3.0), WayVR (GPL-3.0) and DesktopXR were studied and not used.
