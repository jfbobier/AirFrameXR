@echo off
rem FoV grid (docs\FOV_GRID.md): what your headset really shows, in true angles, through AirFrameXR like a game.
rem   fovgrid.bat                       the grid: a line every 5 degrees, the colour gives the angle, a white border at the declared edge
rem   fovgrid.bat 0.96,0.71,0.69,0.66   the outline of a rectangle (outer, inner, upper, lower tangents: the values of the Advanced window's
rem                                     field-of-view fields) on black with a centre cross, to check a calibration. Run it with the override
rem                                     OFF: the picture then covers the headset's whole box and the rectangle is drawn inside it.
rem Start the streamer first and connect the headset; untick "Override the headset's field of view" to see the headset's whole box.
rem The run ends when you close this window. Writes fovgrid.log next to this file.
setlocal
cd /d "%~dp0"
if exist "%~dp0..\bin\virtualdesktop-openxr.json" set XR_RUNTIME_JSON=%~dp0..\bin\virtualdesktop-openxr.json
set XR_PROBE_SCENE=fovgrid
if not "%~1"=="" (
  set XR_PROBE_FOVGRID=outline
  set XR_PROBE_RECT=%~1
  echo  Outline of the rectangle %~1 ^(outer, inner, upper, lower^) on black, with a centre cross.
  echo  UNTICK "Override the headset's field of view" first: with it on, the game's view IS that rectangle and the
  echo  outline falls on the edge of the picture ^(you see black and a few pixels of line^). Off, the picture covers
  echo  the headset's whole box and the rectangle is drawn inside it: it should sit where your view ends.
) else (
  echo  Grid only. To draw a rectangle on it: fovgrid.bat outer,inner,upper,lower
)
echo  Put the headset on, look straight ahead, head still. Close this window to stop.
"%~dp0fovgrid.exe" "%~dp0fovgrid.log" 100000000 86400
