# AVP telemetry run (branch `telemetry/avp-2026-10-14`)

A test build for finding out why the Vision Pro dislikes re-stamped frames, together with a telemetry build of the visionOS client.
Normal clients are unaffected: nothing below runs unless the client announces itself.

## What it does

1. **Live-switchable re-stamp mode** `restamp_mode` = `off` (default) | `shim` | `host`
   - `off`: frames leave with the timestamp of the tracking sample the game rendered with (duplicates stay, the headset skips them).
   - `shim`: the shim waits up to `dup_wait_ms` (12) for the next tracking sample and re-stamps (IPC `dupRestamp`, read per frame).
   - `host`: the host stamps a duplicate with the oldest newer sample at encode time and rotates the picture to it (warp pass, `VISIONALVR_HOST_STAMP`).
   - Configured with `video.restamp_mode` in `visionalvr.json` or `VISIONALVR_RESTAMP_MODE`; the older `VISIONALVR_HOST_STAMP=1` /
     `VISIONALVR_DUP_RESTAMP=1` still work. Both paths were parked on 2026-10-14 (`exp/restamp-2026-10-13`); this branch restores them (IPC v25).
2. **Scheduler** (`tools/host/src/telemetry.rs`). The telemetry client logs `[pt] ...` lines through ALVR's client log channel (the
   session setting `client_log_report_level` defaults to Error, so the client logs at that level). The host captures every such line with
   its receive time into `telemetry_client.log` in the debug session folder (turned on by the first line), keeps them out of the other
   logs, and on `[pt] start` runs the schedule on its own clock:

   | time | segments | host mode per segment |
   |---|---|---|
   | 0-120 s | warm-up (seg 0) | off (the host-side warp resources are created at 30 s with an identity warp, so the first host segment does not hitch) |
   | tier A, cycle 1 | seg 1-6, 45 s each | off, shim, host, off, shim, host |
   | tier A, cycle 2 | seg 7-12 | host, shim, off, host, shim, off |
   | tier B, cycle 1 | seg 13-16 | off, off, shim, shim |
   | tier B, cycle 2 | seg 17-20 | shim, shim, off, off |

   Total 120 + 20 x 45 s = 17 minutes, then `[tel] end` and the configured mode again. The client switches its own conditions (focal depth,
   frame-pick policy) on the same segment numbers and announces each as `[pt] cond seg=.. focal=.. pick=.. t=..`; the host never controls
   them. A second `[pt] start` (the client reconnected) restarts the schedule.
3. **Host per-frame CSV** `telemetry_host.csv` (one row per frame sent while a run is armed, also in the release flavour): sender time, segment, mode
   in force, frame counter, the sample the game rendered with (`orig_ts_ns`), the stamp that went out (`final_ts_ns`, the client keys on it),
   whether the host re-stamped it, the warp applied (millidegrees), queue wait, encode ms, send hold ms, bytes, IDR, deferred/dropped totals.
   `head_samples.csv` (the tracking samples as received, with host arrival times) is written during a run too; the report takes the arrival
   gaps (Wi-Fi uplink, AWDL) and the pose difference between orig and final stamp from it.
4. **`tools/telemetry_report.py <session dir>`**: parses the client lines and the host CSV, joins client rows to host rows by the stamp,
   drops the first 3 s after each condition change, and prints per condition (mode | focal | pick): frames, repeat %, shown fps, age,
   the residual the client's reprojection must hide (`comp`), its frame-to-frame change (`dcomp`, the tremble metric) also by head speed
   (still < 5, slow 5-30, fast > 30 deg/s), walk-back %, rows without a send record, host queue wait, restamp %, controller rates, thermal,
   and an AWDL signature (arrival-gap p99 and the autocorrelation of > 20 ms gaps at 0.2-1.2 s). Writes `telemetry_client_rows.csv` and
   `telemetry_report.csv`. `tools/telemetry_synth.py` makes a synthetic session in the same formats (a fixture for the report).

## Client line formats

All lines start with `[pt] <verb> n=<seq>` then `key=value` tokens: `start ver=1 build=<sha> renderer=<rk|metal> telemetry=1`,
`cond seg focal pick t`, `sum seg frames repeat nosend walkback fallback age_p50 ... ctrlL_hz ctrlR_hz thermal gpu_ms_p95 decode_ms_p95`
(1 Hz), `rows seg=<n> cols=<header> | <row>;<row>;...` (the header names the columns; unknown columns are kept, missing numbers are empty), `end`.

## Disable

Run a normal client: nothing is armed and nothing is written. To keep the telemetry client but not the scheduler, `VISIONALVR_RESTAMP_MODE`
only sets the configured mode (the scheduler would override it during a run); there is no switch to ignore `[pt] start` besides not running the telemetry client.

## Risks for normal sessions

None expected: the default mode is `off` (before this branch the shim path was on by default in the parked code; the branch sets it off, as on main),
the new code paths run only after a `[pt] start`, the IPC version is 25 (a shim and a host of different builds refuse each other).
A mode switch from `shim` or `host` back to `off` can let one frame leave with a timestamp one sample older than the previous one.
