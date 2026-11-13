# 1.3.10: plan to finish and publish (for review, written 2026-11-12 evening)

Branch `1.3.10` (worktree `/home/jeff/openxr-1310`), work on the 3080 Ti builder on 2026-11-13, a last field check on the 5090.
Everything below comes from the first real headset session of 1.3.10 (5090, Apple Vision Pro, Dead Space / Mortal Shell 2 / Boltgun 2 / probe,
logs pulled from `D:\_VR\AirFrameXR\logs`) and the two tests made after it (control + strobe with `xr_modsim` on the headset).
Nothing here is built yet unless it says so. Decisions of the owner (2026-11-12 evening), already applied below: the engine (`tools/host/`) is touched only
for the two logging items (1 and 2); the statistics fix (item 3) is **UI only**: the raw numbers in the logs and in the host's events are not changed, the UI
interprets them; H5 is a scenario; the 25 % threshold of H1 is fine-tuned in the lab; a publish with the headset checks of section 5 points 1 and 2 not done is
accepted, said "not tested" in the release notes.

## 0. What the field session showed

| # | Finding | Evidence | State |
|---|---|---|---|
| F1 | The visionOS client's `[floor]` line (every 2 s) is 854 of 988 lines of `AirFrameXR.log` | field log | client fixed in the fork (`kteque/main` 07034d1). TestFlight: run 43303600643 failed at signing (certificate cap), run 43316206224 (build 75) uploaded but never appeared in Apple's build list, run 43325628243 (build 78, upload step took 16 min) is the build that arrived. **Seen on the headset 2026-11-13 00:44**: new `AirFrameXR.log` 57 lines, 0 `[floor]` (the rolled file of the old client: 81 of 132). The Entry window's floor readout is not visible in the logs, not checked. Host rule still owed for older clients |
| F2 | A run's own log lacks that run's WARN/ERROR lines (Boltgun `stall detected`), the `[shim:...]` lines and the once-a-minute `client frame gaps` line | game logs vs main log | owed |
| F3 | Game statistics say "headset shows 86 fps" while the game rendered 98 and the headset's own ticks say 97 | Dead Space debug session: host `same_ts_frames` 3955 of 20144 frames (19.6 %, 10.8 % settled); unique 16189 vs 16110 client rows; ticks 98 % fresh | cause found, fix owed |
| F4 | The headset displays frames that repeat a timestamp | strobe test on the headset: green + magenta fuse to white, pure green would mean lost | measured (not for Dead Space itself) |
| F5 | The link-loss policy (60 s), the skipped-frame path and the moved `report_frame_decoded` never ran in the session | no link loss happened | owed on the headset |

## 0b. Confirmed in the field 2026-11-13 (5090, build 78, Lone Echo 2, logs folder cleared first, so only this build)

- `[floor]` flood gone with the new client (above). Daily roll worked: the old session's file became `AirFrameXR.2026-11-12.log` at the next start (H6's roll point is seen in the field; the 7-day and 20-run pruning are not).
- Run logs `games/<exe>/<ts>.log` exist for both games of the session, with the start and end lines, the headset ticks and the memory lines; UI logs under `ui/`.
- Not checkable: item 2 (the session had no WARN; the frame-gap lines are still only in the main log, as before the fix).
- Lone Echo 2, 11 min 49 s: no disconnect, no stall; 99.3 fps streamed, headset repeated 0.9 % typical / 1.5 % worst; ticks 98.4 new frames/s, 351 frames dropped by the headset queue (about 0.5/s; the first 10 s window 34 % without a new frame). The frame-gap lines still say 4 to 9 % of periods skipped: the F3 artifact again. The field log of 2026-11-12 (24 disconnects in 25 minutes) was Lone Echo 2 through the libOVR bridge; this session ran it through our native libOVR shim (owner: that is why it behaves better), so the two sessions differ in the game's path and say nothing about the 1.3.10 client hardening. One good run is not a result for either.
- The `tlg-probe` run on the older client in the same logs: 83 new frames/s by ticks, 1335 frames dropped by the headset queue (windows of 336 and 199 per 10 s). Not looked into; a test program on the earlier build.

## 1. Host: `[floor]` rule (F1) - `tools/host/src/logs.rs`, ALVR log hook

Next to the existing `Received button not mapped` rule: for a client message containing `[floor]`, log the first one and any whose
`floor y` text or `offset` changes at Info, every other one at Verbose. Old clients (1.3.9 and earlier TestFlight builds) keep sending it, so this stays.
Expected: the field session's main log goes from 988 to about 140 lines.
Test: a host unit test with a synthetic stream (same floor, then a changed floor y, then a changed offset); the offline noise check of H1.

## 2. Host: run log completeness (F2) - `tools/host/src/logsink.rs`, `logs.rs`

New `Dest::Both`: written to the main log and, while a game runs, to its run log. Applied by the sink to every WARN and ERROR line, and set by hand on
`shim_line`, the `client frame gaps` line and `game started`. Everything else keeps its destination.
Test: `logsink.rs` unit tests (a WARN during a game is in both files; a `Both` line with no game is in the main log only; levels still filter).
Not changed: what the Black Box / LogTimeline read (still the main log; moving them to the run log stays a later step).

## 3. UI: "headset shows" is read from the headset's own count (F3) - `tools/gui/Performance.cs`, `AirFrameXR.cs`; no engine change

The host keeps writing the raw figures as it does now (`displayed_fps` = reported frames per second, i.e. unique timestamps; `same_ts_frames`; the
`headset repeated` figure from the ticks; the `[client] headset ticks` lines in the run log). The UI no longer shows `displayed_fps` as "headset shows"
when better raw data is there:
- ticks present (the run record's `headset_repeated_*` comes from `va_ticks`, 1.3.10 visionOS and Quest clients): headset shows = display rate x (1 - repeated share),
  the repeated share being the one the UI already shows next to it; the Headset stage notes "x fps reported by timestamp, y % of frames repeat a timestamp".
- no ticks (stock ALVR client, older builds): the reported-frames figure as today, labelled "reported" and not "shown".
**To check first on the builder, before writing it:** that the run record and the live `net` block carry the repeated share and the display rate in the fields
the two files read today (`displayed_fps`, `rep` at `AirFrameXR.cs:857`, `Performance.cs:196` / `:280`); if a needed field is not in the raw data the
item is reduced to the relabel and the note, and the missing field is listed as a 1.3.11 engine item, not added now.
Not changed: the frame path, the stamping, `client frame gaps` text, `window_json`. The cause of the repeated timestamps (two frames matched to one tracking sample,
`shim.ts_matched` / `ts_fallback`) is only measured (H5).
Test: a UI-matrix fixture row pair (with and without ticks: the 860-reported / 970-fresh window must show 97 shown / 86 reported, the tick-less one 86 "reported"),
`performance_check.py`, `uitest.exe` Game statistics rows.

## 4. Harness (new and changed; run on the 3080 Ti builder)

| ID | Check | Where | Pass means |
|---|---|---|---|
| H1 | Log noise: at the default level no one message shape (digits folded) is more than 25 % of the main log's lines once it has 100 lines; offline on a fixture trimmed from the 2026-11-12 field log, and on every `e2e_log_level_info` run | new `harness/log_hygiene_check.py` + a `log_noise` check in `lib.py` | the fixture FAILS before item 1 and PASSES after it (shown in that order); the percentage is checked against that log: `[floor]` 86 %, the next shape under 10 % |
| H2 | Routing: every WARN/ERROR and `[shim:` line of the main log inside a run's time window is in that run's log | same script, a `log_routing` check on `e2e_log_level_info` (needs one WARN: the scenario's stall injection or a synthetic line) | passes after item 2; the Boltgun field logs as fixture FAIL before |
| H3 | A `[floor]`-shaped line every 2 s through the host's ALVR log hook | host unit test (item 1), not a scenario: the lab's mock headset is ALVR's own client and cannot be made to send it | - |
| H4 | The UI's "headset shows" from ticks (item 3) | `performance_check.py` and the UI matrix with a fixture window carrying ticks and repeated timestamps (taken from the 2026-11-12 Dead Space debug session) | 97 shown / 86 reported from the fixture; 86 "reported" without ticks |
| H5 | The repeated-timestamp share on a clean stream (the control: `xr_modsim`, `MODSIM_DUP=0`, 100 fps) with debug on | new **scenario** `e2e_modsim_control_ts` (stays in the suite), reads `same_ts_frames`, `ts_matched`, `ts_fallback`; the headset control of 2026-11-12 22:04 (11.8 % of periods skipped, debug off) is the number to compare | informational, no threshold: the number goes in the release notes and tells whether the repeats come from the stamping |
| H6 | The rest of the logging framework never run in the field: `log_level` from `airframexr.json`, `--log-level` and the stdin command; daily roll and 7-day / 20-run pruning (a clock-shifted fixture); the drop counter under a full queue (exists in `logsink` tests, add a line to the run's end summary); a killed game with lines in the ring | `e2e_log_level_*` extended, `logsink` tests | pass |

Existing checks that must stay green: host unit tests (25 of 25 at the last run, plus the new ones), `e2e_installed_layout`, `e2e_log_level_info`,
`e2e_log_level_verbose`, `e2e_baseline` (repeat once if it flaps: bimodal latency, see `visionos-harden-stall` notes), `e2e_mirror`,
`advice_check.py`, `performance_check.py`, `log_timeline_check.py`, the UI matrix rows for Game statistics and the Advanced window (`uitest.exe --only advanced-switches`).

## 5. Headset (the owner, 5090) - what the lab cannot show

0. Done: the TestFlight build with the `[floor]` change exists and ran (build 78, section 0b). Point 3 of section 5 stays for the final build.
1. With that build: the Entry window still shows the floor height; the main log has no `[floor]` flood.
2. Link loss, once under and once over 60 s (Wi-Fi off about 20 s and about 90 s mid-game): expected stays in the immersive space and resumes, then leaves for the Entry window after 60 s and re-enters when the streamer is back.
3. Dead Space with debug on, 3 minutes: Game statistics' "headset shows" within about 2 fps of the headset's ticks and of the streamed rate; the run log has the run's warnings and the shim lines.

## 6. Order of the day and release gates

1. Items 1 and 2 (the only engine edits) with their unit tests, compile check from WSL (`tools/wsl`), then H1 and H2 written against the field-log fixture and shown failing before the fixes.
2. Item 3 (UI) and H4, after the data check named in item 3. H5, H6.
3. Build on the 3080 Ti, run the checks above, `harness/bundle.py` for the personal bundle (release flavour; the builder holds whatever was built last).
4. The owner's headset checks (section 5).
5. Release notes: `docs/releases/v1.3.10.md` gets the three host changes, the stats change and what ran / did not run in this document's own words; `docs/progress.md` open items 1 to 3 of the logging entry move to done or stay open as tested. Version is already 1.3.10.
6. Publish only after the owner's yes (push, GitHub release, store build: each asked separately). The Quest client's store build needs the owner's own release-signed APK (draft quest-v1.3.10 has the debug-signed one).

Publish gate in one line: items 1 to 3 built and checked in the lab, H1 to H4 green, section 5 point 3 passed on the 5090. Points 1 and 2 of section 5 may be
published as "not tested on a headset" if they are not done, said so in the notes.

## 7. Not in 1.3.10

- Fixing why frames get a repeated timestamp (the stamping, `pipeline.rs`): measured by H5, decided after.
- The same statistics under-count in the Black Box report (`tools/bench/PerfResources.cs` reads `displayed_fps` too: it follows item 3 automatically; its numbers in older recordings stay as recorded).
- The ticks-based drop of the older frame (visionos-sustained-catchup-idea), Lone Echo 2 deaths, the visionOS `.ips` reports.

## 8. Decisions taken (owner, 2026-11-12 evening)

1. The statistics fix is UI only; the engine is touched for items 1 and 2 only.
2. H1's threshold is a starting value, fine-tuned in the lab.
3. H5 is a scenario.
4. A publish with section 5 points 1 and 2 not done is accepted, labelled "not tested on a headset".

Done on 2026-11-12/13: the TestFlight signing limit was cleared by the workflow's revoke option (owner's yes) and build 78 arrived. Lesson for the next upload: check Apple's build list (workflow `expire_versions=0.0.0`, a version with no builds, lists installable builds with their state) before telling the owner it is available.
