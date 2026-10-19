# Desktop mirror (mirrordesktop.exe)

Between games the headset shows the Windows desktop as a monitor-sized screen in front of you; the controllers are laser pointers that move the mouse.
It is **an OpenXR app like any game**, not part of the streamer engine: `bin\mirrordesktop.exe` connects through the runtime and the shim, renders a projection
layer with the views the runtime gives it, and reads the controllers through OpenXR actions. The engine treats it as a game like any other (it is recorded in the game history); the streamer turns frame generation off while it runs
(`frame_gen off`, the user's setting is restored when it ends). The shim signals `Local\VisionALVR_GameStarting` when another app starts so the mirror leaves the headset at once.

- **Switch it on** in the streamer window ("Between games"). The streamer starts the app while a headset is connected and no game runs, and starts it again
  after a game ends. Or run `bin\mirrordesktop.exe` by hand (`--seconds N`, `--no-mouse`, `--clear RRGGBB`, `--log file`).
- **The picture:** DXGI desktop duplication of the primary monitor (SDR, or HDR converted to SDR with Windows' SDR white level), the screen 45 degrees wide at
  1.5 m, a perspective-correct quad, everything else `00FF00` (the headset's alpha / passthrough). The screen stays put for small head movements and glides in
  front of you when you turn more than 25 degrees or walk 0.5 m; End puts it in front of you again.
- **The controllers:** the right controller (or hand tracking) aims, the orange beam shows it; trigger / pinch (down at 0.3, up at 0.15) or the A / X button = left button, grip = right button, thumbstick
  up/down = scroll. The mouse moves over the whole virtual desktop (`MOUSEEVENTF_VIRTUALDESK`, per-monitor DPI aware), so a second monitor does not shift it.
- **Steadiness:** the pointer is smoothed (steadier when slow, immediate when fast) and held still for 12 pixels while a press starts, so a click lands where it was aimed.
- **Logs:** `logs\mirrordesktop.log` (the controller seen, the pointer reaching the screen, the aim and screen poses and the largest trigger / grip values every 5 s, every button press).
- **Update only the app:** replace `bin\mirrordesktop.exe`. Build: `build.ps1 mirrordesktop` or `python3 harness/live.py build mirrordesktop`.

Tests: `harness/mirror_check.py` (the maths in `mirror_math.h`, the picture on the GPU, a screen turned 40 degrees, the beam) and the scenario `e2e_mirror`
(the real chain with the mock client: 00FF00 outside the screen, the desktop in it, the beam in the stream, the pointer on the screen).

The old design (the mirror inside `alvr_host.exe`) is the git tag `pre-mirror-app`.

Credits: the idea of absolute mouse moves over the virtual desktop and wheel events follows Immersive-2's input injector (MIT,
https://github.com/PGSCOM/Immersive); the code is our own. Desktop+ (GPL-3.0), WayVR (GPL-3.0) and DesktopXR were studied and not used.
