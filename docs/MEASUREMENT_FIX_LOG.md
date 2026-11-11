# Measurement fix log

Living document, started 2026-11-11 on branch `1.3.10`. One entry per step, dated; say what was measured, how, and what was not (legend in
`docs/progress.md`). First subject: the launcher's **"headset repeated"** figure, which disagrees with the headset's own counters. Second
subject: a review of the logging framework (ideas, not decisions).

## 1. The problem

- The launcher and the run record show "headset repeated" = the share of display periods without a new frame. Typical values in the 2026-11-10/11
  Dead Space runs at 97.8 to 98.5 fps on a 100 Hz display: **9.8, 10.4, 12.9, 13.0 %** (`game run recorded` lines in `AirFrameXR.log`).
- The headset's own pick counters (client telemetry builds, same sessions, clean 10 s windows) say **3 to 4 %** of ticks had no new frame:
  3.82 % with the catch-up pick, 2.70 % without (`docs/VISIONOS_TELEMETRY_LEARNINGS.md`, finding 17). A game at 98 fps on a 100 Hz display
  cannot have 10 % repeats, and the stream carried 97.5 fps.
- Where the figure is used: `tools/gui/Advice.cs` (frame generation is suggested when repeats exceed 5 % and the game runs under 90 % of the
  panel rate), the `game run recorded` line, `games.json`. If the figure is about three times too high, the advice fires for games that are fine.

## 2. What each number is (read in the ALVR 20.14.1 source, not guessed)

| Number | Where it comes from | Unit and window | Known bias |
|---|---|---|---|
| `GraphStatistics.client_fps` | `1 / frame_interval`; `frame_interval = vsync - prev_vsync` in `alvr/client_core/src/statistics.rs` `report_submit`, with `vsync = now + vsync_queue` (our client passes presentation time minus now, 0 when late) | one value per displayed frame, an instantaneous rate from one interval | a late frame (vsync_queue 0) gives a non-whole interval; absurd values (1704, 11248, 20366 fps) occur when two frames are presented back to back |
| `StatisticsSummary` (the `[STATS]` lines of `alvr.log`) | `alvr/server_core/src/statistics.rs`: every 0.5 s, **one frame's** values (`client_fps`, `server_fps`, latencies), packet counts per interval | one frame per 0.5 s | length-biased: the first frame after the timer fires is sampled, so long gaps are over-represented; 16 % of the lines read about 50 fps |
| Host "headset repeated" | `tools/host/src/logs.rs` `client_stats::graph`: per `GraphStatistics`, a gap over 1.5 display periods adds `round(gap / period) - 1` missed periods; per 10 s window `missed / (frames + missed)`; the run's "typical" is the median window | per window of 10 s | see hypotheses |
| Pick counters (client telemetry, builds of 2026-11-08 to 11-10, removed in 1.3.9) | the client's own count per tick: fresh / resubmit / reprojected / none | per tick, windows of 10 s | none known; this is the reference |

## 3. Corrections of earlier statements

- 2026-11-10 night, session notes: "the 49 to 50 `client_fps` single samples are a stats artifact" was **wrong as stated**. They are real 2-period
  gaps seen through length-biased sampling (the `[STATS]` lines carry one frame each). They do not by themselves explain the host metric,
  which uses every frame, not the 0.5 s samples.

## 4. Hypotheses (none confirmed)

- **H1, late frames counted as skipped refreshes.** A gap of 1.5 to 1.75 periods (15 to 17.5 ms at 100 Hz) rounds to 2 and counts as a full repeat.
  A frame that completes a few ms late and is followed by a short gap has no skipped refresh. Test: the new gap bins (`1.25-1.75` against `~2`).
- **H2, wrong period.** `phase::period_ns()` is the host's period. If it is the stream's 11.1 ms while the display is 10 ms, a normal 10 ms gap
  is 0.9 and a 20 ms gap is 1.8; thresholds and rounding then shift. Test: log the period next to the bins.
- **H3, reported but not displayed.** A frame whose submission visionOS skipped is still reported (`report_submit` runs in the completion
  handler) and then resubmitted without a second report; the interval to the next frame is measured from a presentation time that never
  happened. Test: compare the bins with the pick counters' resubmit share in the same minute.
- **H4, the 10 s window median.** "Typical" is a median of per-window shares and one window of a run is the stream start (33 % fresh). Test:
  recompute from the bins over the whole run.

## 5. Plan

| Step | What | Status |
|---|---|---|
| S1 | The host logs the gaps between displayed frames in display periods, binned (`<0.75`, `~1`, `1.25-1.75`, `~2`, `2.25-3.25`, `>3.25`), once a minute, in `AirFrameXR.log`; read-only, the metric is unchanged. `tools/host/src/logs.rs` `GAP_BINS`, `gap_bin`, `log_gap_bins`; commit `12104ae`. | done, compiled in isolation against a stub (2 unit tests pass); **not compiled in the host crate** (the ALVR tree's `host_harness` symlink points at the main checkout; a normal build on the builder is the check) |
| S2 | Log the host period (`phase::period_ns`) in the same line, to test H2. | to do |
| S3 | Build on the builder, play one session, read the bin lines. | to do (the builder did not answer ssh on 2026-11-11) |
| S4 | **Validate against ground truth in the rig**: run a rig scenario with a known repeat rate (`ticks.csv` of the rig client lists every display tick and whether it showed a new frame) and compare the host's figure with it. This decides H1 to H4 without waiting for a play session. | to do |
| S5 | Choose the counting rule from S3 and S4 (candidates: count only gaps of 1.75 periods and more, count by `round` of the gap but weight by frames; keep a second number "late frames"). Unit tests with synthetic gap sequences. | to do |
| S6 | Re-check `Advice.cs` thresholds: if the figure is inflated they may have been set against inflated values (not verified). Re-label the UI ("ticks without a new frame"). | to do |
| S7 | Add the metric to the harness as an oracle (the rig's ground truth against the host's figure, tolerance 1 point). | to do |

Acceptance: the host's figure is within about 1 point of the rig's ground truth and of the pick counters in a field session at the same display
rate.

## 6. Logging framework review: ideas (not decisions)

What exists (`tools/host/src/logs.rs`, 606 lines before S1, 57 `logs::general` call sites): the general log `logs/AirFrameXR.log` (all components, full
dates), a debug session folder only when debug is on (`host_events.jsonl`, `alvr.log`, `gpu.csv`, `shim_<exe>_<pid>.log`, CSVs: `client_graph`,
`client_frames`, `host_frames`, `head_samples`, `sched`, `audio_capture`, `fg_uncapped`, `client_stats_outliers.jsonl`), three aggregation
windows (2 s encoder, 10 s status, benchmark step), the run record `games.json`, and `alvr.log` (ALVR's lines with `[STATS]`, `[EVENT]`,
`[CAPS]`, the client's lines at error level).

Observed on 2026-11-10/11 logs, and ideas that follow:

1. **Say what every metric is, next to its name.** The table in section 2 took an evening to reconstruct. Put a short definition (source event, unit,
   window, known bias) beside each metric in code and in one `docs` table, and keep the table next to the code that computes it.
2. **Always-on, bounded summaries instead of debug-only CSVs.** The 5090's host ran with `debug off`, so no per-frame data existed when we needed
   it. Keep a per-minute histogram line (like the gap bins) for the quantities that matter: frame gap, decoder queue, vsync lateness, encode
   time. A few hundred bytes a minute, no debug session.
3. **Self-describing logs.** `alvr.log` lines carry a time without a date and no version; users download them as `alvr (3).log`, and one file was
   byte-identical to the previous one. Write a header line at start and on rollover (date, host version and flavour, commit, stream fps and size,
   negotiated rate, display rate seen).
4. **Dedupe the Session event.** `alvr.log` holds 14 near-identical Session events of about 12 KB at every start: 173 KB, half of a 3.5 minute
   log. Log it once and log only changes afterwards.
5. **Do not log one frame per 0.5 s as if it were a statistic.** `[STATS]` is 45 to 55 % of `alvr.log` and each line carries a single frame's
   rate. Either drop it from the file or replace it by the host's own window aggregates (median and p95 over 10 s), which are what we analyse.
6. **One parser, kept.** The per-window table (arm, new frames, latency, misses) was a scratch script that dies with the session. A small
   `tools/log_summary.py` that reads `AirFrameXR.log` and `alvr.log` into a table would have saved most of tonight's work.
7. **Every metric that reaches the UI gets a ground-truth check in the harness** (as in S7); today only some do.
8. **Units and names.** `client_fps` and `server_fps` are rates computed from one interval, and absurd values appear (1704, 11248, 20366; guarded
   in code by `< 500`). Store the interval, derive the rate when displaying, and count the dropped values visibly.
9. **Client side.** All client telemetry was removed in 1.3.9, so the client has no timing log. One line per minute (display interval minimum
   and mean, deadline misses, the thermal state) is what the 2026 logs showed to be worth keeping; the owner decides whether to bring any back.
10. **Rotation and size.** `AirFrameXR.log` is appended across days; a size cap or a per-day file with the last N kept.
11. **Time base.** The CSVs use the host's QPC (`t_recv`), `alvr.log` the wall clock without date, the client its own clock. Document how to
    line them up and write one shared stamp in the header of each file.
12. **Safe to share.** Host logs contain IP addresses, the headset hostname and install paths (`D:\_VR\...`), and users post them publicly (issue
    reports, TestFlight feedback). An "export for sharing" action that redacts IPs, hostnames and user paths would be cheap and avoids leaks
    like the contact-details one of 2026-11-11.

## 7. Log of steps

- 2026-11-11: this document; S1 committed (`12104ae`, branch `1.3.10`, local, not pushed).
