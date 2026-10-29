@echo off
rem FoV calibration sweep (docs/FOV_CALIBRATION.md). Read README.txt first.
setlocal
cd /d "%~dp0"
echo.
echo  AirFrameXR field-of-view calibration
echo  ------------------------------------
echo  Before you continue: AirFrameXR is running, the headset is connected,
echo  and the field-of-view override is OFF (README.txt, step 1).
echo.
set /p NAME=Your name, no spaces (e.g. alex): 
set /p HEADSET=Your headset (e.g. visionpro, quest3), no spaces: 
set /p FIT=Run number (1 the first time; 2 or 3 after taking the headset off and on again): 
if "%NAME%"=="" goto :bad
if "%HEADSET%"=="" goto :bad
if "%FIT%"=="" goto :bad
set CSV=%~dp0fov_sweep_%NAME%_%HEADSET%_run%FIT%.csv
if exist "%CSV%" (
  echo.
  echo  The file %CSV% already exists: choose another run number.
  pause
  exit /b 1
)
if exist "%~dp0..\bin\virtualdesktop-openxr.json" set XR_RUNTIME_JSON=%~dp0..\bin\virtualdesktop-openxr.json
set XR_PROBE_SCENE=fovgrid
set XR_PROBE_FOVGRID=sweep
set XR_PROBE_SWEEP_FIT=%HEADSET%_%FIT%
set XR_PROBE_SWEEP_GAZE=following
set XR_PROBE_SWEEP_DEG_S=2
set XR_PROBE_SWEEP_START_DEG=15
set XR_PROBE_SWEEP_EYES=01
set XR_PROBE_SWEEP_CSV=%CSV%
echo.
echo  Starting. Put the headset on, keep your head still, look at the white +.
echo  Click once to start (any controller button, or the trigger).
echo  When the screen stays black after the last ray, the run is finished:
echo  take the headset off and close this window (or press Ctrl+C).
echo.
"%~dp0fov_sweep.exe" "%~dp0fov_sweep.log" 100000000 86400
echo.
echo  First pass done (eyes following the dot). Second pass: the same rays with your
echo  eyes FIXED on the white + (do not follow the dot; click when it is gone).
echo  The first set that shipped was too tight because only the following pass was run.
echo  Put the headset back on if you took it off; press a key to start the second pass.
pause
set XR_PROBE_SWEEP_GAZE=fixed
"%~dp0fov_sweep.exe" "%~dp0fov_sweep.log" 100000000 86400
echo.
echo  The probe has stopped. Result file (both passes, one row per ray): %CSV%
pause
exit /b 0
:bad
echo  A name, a headset and a run number are all needed.
pause
exit /b 1
