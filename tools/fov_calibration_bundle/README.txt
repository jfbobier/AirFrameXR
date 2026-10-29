AirFrameXR field-of-view calibration
====================================

What this is for
----------------
Your headset does not show the whole field of view the game renders: the lens cuts
the edges, and how much depends on the headset and on how it sits on your face.
This test measures what YOU actually see, in two passes of 48 short steps (24 per eye), so we
can choose the picture size that wastes nothing. Time: about 25 minutes per run (two passes).

What you send back: the file  fov_sweep_<name>_<headset>_run<N>.csv  (in this folder),
plus one line saying which headset and controllers you used, and anything that went wrong.
Two or three runs (take the headset off and put it back on between runs) are better
than one: the difference between runs tells us how precise your numbers are.


Before you start (once)
-----------------------
1. Put this whole folder ("calibration") inside your AirFrameXR folder, next to
   AirFrameXR.exe and the "bin" folder.
2. Start AirFrameXR and connect your headset as usual. Open "Advanced picture and FOV
   settings" and make sure "Field of view override" is NOT ticked. In the headset app,
   FoV Scale must be 1.0. If you change either, restart the streamer and reconnect.
   (A restriction would cut the measurement off.)
3. Use a controller if you can: any button or trigger works. In the Vision Pro app,
   hands as controllers (pinch) also works.
4. Double-click run_sweep.bat. Answer the three questions (name, headset, run number).
   If Windows complains about a missing VCRUNTIME / MSVCP DLL, install the Microsoft
   Visual C++ Redistributable (x64) once and try again.


The test, explained once
------------------------
Put the headset on and keep your head still for the whole run. Do not move your head to
look at things; only your eyes move.

  1. You see a white +  in the middle. Click once. This only starts the run.
  2. For each step the same thing happens, 24 times per eye, in random directions:
       - the white + shows for a moment: look at it;
       - the + disappears and a small white square appears (inside your field of view,
         toward one of the edges). It drifts slowly OUTWARD;
       - you follow it with your eyes. It keeps going until it leaves what you can see.
       - the moment it is gone, and you only see black where it was: CLICK ONCE.
  3. The + comes back and the next step starts. Do not click then.

The rule to remember: click only when you see black and the square is gone. Not when it
fades, not when you are unsure, not when it appears. Be consistent: use the same
standard every time (the first moment you can no longer see it).

  - A step takes about 6 to 15 seconds. That is normal, it moves slowly.
  - Do not double-click. One click per step. If you clicked too early by mistake, just
    carry on; do not try to fix it.
  - If you cannot see the square at all on a step (it starts already outside your view),
    do not click. After about 30 seconds the step ends by itself and is marked as a time-out.
  - After the 24 steps of the first eye, there is a 10 second pause, then the other eye.
    You may close the eye that is not being tested, or leave both open: do the same every run.
  - When the screen stays black after the last step, the FIRST pass is finished. The command
    window then asks you to press a key for the SECOND pass: the same 48 steps, but this time
    keep your eyes FIXED on the white + and do not follow the square; click when, looking at the +,
    you can no longer see the square at the edge of your view. Both passes matter: with the eyes
    following the square the window is smaller than with the eyes forward, and a game is played
    both ways.
  - When the screen stays black after the last step of the second pass, the run is finished:
    take the headset off, then close the black window (or press Ctrl+C).
  - Controllers sleeping: some controllers go to sleep after a while. If a click stops
    working, wake the controller, and tell us which steps were affected.


If something is wrong
---------------------
- Nothing appears in the headset: the OpenXR runtime must be AirFrameXR (launcher: runtime
  switch) and the headset connected before you double-click run_sweep.bat.
- Clicks do nothing: in the Vision Pro app turn on hands as controllers, or use a controller.
- The run was interrupted: start again with a new run number; send the file anyway if it has
  rows (we ignore incomplete runs).
- fov_sweep.log (same folder) is the technical log. Send it with the CSV only if something
  looked wrong.

Thank you!
