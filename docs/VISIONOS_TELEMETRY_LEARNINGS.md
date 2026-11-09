# visionOS client telemetry: what we learned

Living document. Add a dated entry under "Sessions" after each log read, move settled items into "Findings", and strike or delete open questions that get answered.
Evidence level words follow `docs/progress.md`: say what was measured, how, and what was not.

## How it is collected

- Client branch `additional_telemetry` (fork `kteque/alvr-visionos`, from `kteque/1.3.6` `099e02c`). Read-only: it writes log lines and changes no state. Compiled in CI and uploaded to TestFlight through the fork's TestFlight workflow; not otherwise verified to leave behaviour unchanged.
- Lines start with `[tele]` and go through `alvr_log` at error level (the only level the streamer logs while streaming), so they appear in the streamer's session log (`alvr.log`). Find them with `grep "\[tele\]"`.
- Line types:
  - `drawable color WxH layers N views N`, `view i viewport WxH at x,y`, `ratemap layer i screen WxH physical WxH` (or `ratemap none`): once per stream start (from build `b7b4e1a`; the first build logged them before the log connection existed, so they never appeared).
  - `decoder hardware Optional(1) codec <fourcc> WxH`: once per decoder session. `Optional(1)` = hardware decoder in use. `1752589105` = `hvc1` (HEVC).
  - `pick over N ticks: fresh / resubmit / reprojected / none, queue depth at pick, wait for a late frame, age of a fresh frame`: every 10 s (commit `61a8f6f`, added to the branch after `b7b4e1a`; not written in the session that wrote this file). Reprojected = the compositor tick had no new frame and the client reprojected the previous one.
  - `display interval mean/min/max ... over N frames`: every 10 s and on a thermal change. From the compositor's presentation times. From `b7b4e1a` it also has `N over 15 ms`, `deadline misses`, `min slack` (end of `endSubmission` against the predicted `renderingDeadline`), `thermal` (0 = nominal) and `stream target` (the rate the stream was set to).
- Earlier probes of the same kind: `[ratemap]` (1.3.2 to 1.3.4, result went into `RateMapFoveation.Milder`), `[proj]` and `[view]` (only on the archived 1.4 line, tag `archive/1.4-experiment`).

## Findings

Settled by measurement (logs below). Device: Apple Vision Pro M2, streamer 20.14.1 session, HEVC, stream target 90 Hz, constant 275 Mbit/s.

1. **The display ran at 100 Hz for the whole session, with the stream target at 90.** 344 ten-second windows over two logs (about 57 minutes), mean interval 10.0 ms in every window; no switch to 90 or 96. The client offers `[100, 96, 90]`. Why the system chose 100 is not in the logs. Hypothesis (from the research notes, not shown by our data): visionOS picks 90/96/100 from the room's lighting (flicker sensor, 50 Hz mains in France), so the app cannot choose it.
2. **Hardware HEVC decoding in use** in both sessions (`decoder hardware Optional(1)`), stream 3840x1664 as encoded.
3. **Thermal state 0 (nominal)** in all 344 windows. Sessions were 36 and 21 minutes, so this says nothing about longer sessions.
4. **Occasional doubled display intervals are client deadline misses.** In the 2026-11-09 midday log, every interval over 15 ms coincides with a submission that ended after the compositor's rendering deadline (48 of 48; min slack -5.8 ms, at 90 Hz = 11.1 ms frames). Earlier logs (night) had the same doubled intervals (~0.2 % of frames) but no deadline counter. Cause of the misses not established.
5. **The display ran at 90 Hz in the midday log (13:00), 100 Hz in the night logs (23:55 to 00:39).** Consistent with the lighting hypothesis in finding 1, but the two sessions also differed in stream settings (see Sessions), so it is not isolated.
6. **Geometry numbers visionOS gave the client** (midday log, one session, fit not recorded): drawable colour texture 2432x2368, 2 layers; each view's viewport 6261x5023 (larger than the texture, so not texture pixels: probably the screen-space size the rate map is defined over; interpretation unconfirmed); rate map `screenSize` read as 0x0 and `physicalSize` 2432x2368 per layer. A 0x0 screen size means the rate map was not usable as read; whether foveation was active is not established.
7. **Repeats track the frames the stream actually delivers, not the pick policy** (midday log, 17 windows; the log's `[STATS]` lines give the streamer's `server_fps` and the client's `client_fps` per second). Per 10 s window, the share of compositor ticks without a fresh frame correlates 0.89 with the share the delivered rate cannot cover, (90 - client_fps) / 90. Windows at client_fps 90 had 0 to 3 % repeats; windows at client_fps 48 to 72 had 22 to 45 %, and `server_fps` in those windows was 50 to 72: the game or the streamer was not producing frames (a loading scene in the first minute). So in this log the repeats are born upstream of the client. Open: two windows repeat clearly more than the delivered rate explains (13:00:55 +3 points; 13:01:05 +13; 13:03:26 +30 with client_fps 65 and 58 % repeats). The client's queue holds 2 frames and discards the oldest on overflow, which would turn bursty arrival into extra repeats; the overflow drops were not counted until build `visionos_client_performance` (`queue overflow drops` line).
8. **The streamer in that log was slow to encode**: `encode_latency_ms` 11 to 17 ms in most windows (above the 10 ms budget of a 100 fps stream), `server_fps` 42 to 109 and 84 to 96 in the steady part, constant 300 Mbit/s at 4096x3609. Which streamer and machine produced it is not recorded in the log; note it before reading it as a statement about the AirFrameXR engine.
9. **Reprojection share varied a lot at a 100 fps stream on a 90 Hz display** (midday log): 15 % of compositor ticks overall, 0 to 58 % per 10 s window, 10 ticks with no frame at all. The windows with the most reprojection do not line up with the windows with the most deadline misses.

## Open questions

- **Is the display rate fixed for the whole session?** In every log so far, yes: each session's 10 s windows have the same mean interval (night 100 Hz, midday 90 Hz; sessions up to 36 minutes; no window between the two rates). That does not show the system never changes it (a lighting change mid-session was never tried). Build `visionos_client_performance` logs `DISPLAY RATE CHANGED x Hz -> y Hz` when a window's mean differs by more than 3 % from the previous one, so a switch cannot go unnoticed.

- Why 100 Hz at night and 90 Hz at midday? Lighting is the hypothesis; stream settings differed too. Test: same session settings in daylight and under artificial light, compare the `display interval` lines.
- Why did the client's `stream target` read 90 Hz when the session asked for 100 fps (midday log)? Not looked into.
- What makes the client miss the rendering deadline (up to 20 in one 10 s window, then 0 to 5)? Candidates, none tested: a late frame wait in the pick, work in the render path, the 100 fps stream against the 90 Hz display. Compare against the pick lines per window.
- What the 6261x5023 viewport and the 0x0 rate-map screen size mean, and whether foveation was active. Panel resolution is still the estimate (3660x3200 from iFixit, 3648x3144 from Omdia); our default declared view size is a number, not a measurement.
- Does the rate map change with gaze? Not tested; we do not depend on it either way.
- Thermal behaviour over 30 to 60 minutes at the final profile: not tested.
- Decode callback time and dropped-frame counts: not logged (would hook the decode path).

## Background (not measured by us)

From the research notes of 2026-11-08, secondary sources, treat as leads: Apple publishes no panel resolution or FOV; visionOS supplies projection, viewport and rasterization rate map per drawable; the M2 media engine lists H.264/HEVC/ProRes, not AV1; Wi-Fi is 802.11ax, 5 GHz, 80 MHz, 2 streams (1.2 Gbit/s PHY, application throughput unpublished); the compositor expects extended linear Display P3. Foveated Streaming/CloudXR is out of scope (no raw gaze for apps; we do not use it).

## Sessions

### 2026-11-09: first telemetry logs (build from `c7fca92`)
- Files: `alvr1.log` (23:55 to 00:01+, 216 windows), `alvr2.log` (00:32 to 00:39+, 128 windows), from the owner's Downloads folder (not kept in the repo).
- Result: findings 1 to 4 above. Missing: geometry lines (fixed in `b7b4e1a`).
- Next: read a log from `b7b4e1a`; one session with the stream at 100 Hz; one in different lighting.

### 2026-11-09 (midday): `alvr (2).log`, build with `61a8f6f`
- One session, 13:00:37 to 13:03:36, 17 windows. Session asked for 100 fps, 4096x3609 view, constant 300 Mbit/s, HEVC; encoded 8192x3584 (foveated encoding). Display 11.1 ms (90 Hz) in every window, thermal 0, hardware decoder, 48 deadline misses (all the over-15 ms intervals), 15 % reprojected ticks. Findings 4 to 7.
- Geometry lines now appear (stream start logged twice: the client reconnected at 13:00:45).
- Next: same settings at night (does it go back to 100 Hz?); a longer session; compare misses against pick waits per window.

### 2026-11-09 (lab): protocol harness, repeats at the display
Client branch `visionos_client_performance` (from `additional_telemetry`). Not a device result: the Linux protocol harness runs the real `EventHandler.swift` against a scripted streamer and a render-loop model.
- The harness counted a resubmitted frame as new; fixed. A tick is now new only when the pick took a frame from the queue.
- Scenarios `pace_*`, `rate_follows_hint` (all pass; the existing 15 scenarios still pass). Share of display ticks showing a new frame: 90 fps stream on a 100 Hz display 90 %; free-running 95 / 97 / 98 fps into 100 Hz 95 / 97 / 97-98 %; 98 fps into 90 Hz 100 %; paced 100 fps on 100 Hz 100 %. So the lab reproduces drift ((display - fps) / display) and the rate mismatch, and nothing beyond it. It does not reproduce the 12 to 35 % repeats of the field and lab tables.
- Change tested: the client's refresh rate (display link and `AVDisplayCriteria` request) follows the stream's negotiated rate (`refresh_rate_hint`) instead of the 90 default. `rate_follows_hint` fails with the old behaviour and passes with it. Not run on a device.
- Reading of the pick-policy question: with the log's evidence (finding 7) and the lab's, a longer pick wait or a held frame has nothing to fix in the cases we can reproduce; no toggle was added.
