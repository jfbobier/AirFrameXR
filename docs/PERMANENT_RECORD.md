# PERMANENT_RECORD

> **Do not read this file, or anything under `docs/permanent_record/`, unless you need to revisit a past decision in detail.**
> It is the project's permanent record: history, not current truth. Current docs are in `docs/`; current status is `docs/progress.md`.
> (Pun intended.) Older text can describe code that no longer exists.

Moved here on 2026-10-28 (branch `housekeeping`). Each entry: file, what it was, why it was retired. Everything is also in git history.

## Documents

| File | What it was | Retired because |
|---|---|---|
| `permanent_record/progress-log-2026-10-07_to_10-28.md` | The 109 KB day-by-day log: recon, builder bring-up, pilots 1 and 2, benchmark S1, rename to AirFrameXR, simrig, 0.2 to 1.1.1 work, all numbers | Replaced by a short current `docs/progress.md`; the log is history |
| `permanent_record/PLAN_0.6.md` | The 0.6 performance list (items A-D, what was ruled out) | 0.6 shipped; the experiments and dead ends live on in `docs/PERFORMANCE_FINDINGS.md` |
| `permanent_record/PERFORMANCE_NOTES.md` | Performance ideas noted 2026-10-10, none started | Superseded by `PERFORMANCE_FINDINGS.md` (what was actually tried) |
| `permanent_record/VISIONOS_CLIENT.md` | visionOS client fork/branch layout and stamping analysis, 2026-10-14, compile-verified only | Client work moved on (Metal-only AirFrameXR client branch); no inbound references |
| `permanent_record/FRAME_GENERATION_RATIOS.md` | Design note for a 2/3 ratio next to half rate, never implemented | Replaced by the uncapped mode; kept as the record of why ratios are not the answer |
| `permanent_record/MOTION_VECTORS.md` | Game (DLSS) motion vectors as a flow source, 0.4-0.6, and why they were removed in 0.6.1 | Feature removed; code on branch `exp/dlss-vectors` (tag `archive/exp-dlss-vectors`) |
| `permanent_record/PLAN_0.5.md` | The 0.5 plan: UI windows, launcher, performance stats, desktop mirror phases (U1-U10) | 0.5 shipped; `docs/UI_SPEC.md` and the UI harnesses describe and hold the result |
| `permanent_record/STABILITY_PLAN.md` | The 2026-10-21 stability plan after the Gears stalls: fault injection, soak, memory caps | Built as `harness/stability/` (see `harness/README.md`); the harness, not the plan, guarantees stability |
| `permanent_record/releases/*` | Release notes v0.1.0-alpha, v0.2.0-alpha, v0.4.0-alpha, v0.5.0, v0.6.0-alpha, v0.6.1-alpha, fg-test-20261014 | Pre-1.0 (project was VisionALVR); the GitHub releases carry the same text |

Engine comments still cite the old paths `docs/PLAN_0.5.md` (`tools/mirrordesktop/desktop.h`) and `docs/STABILITY_PLAN.md` (`tools/host/src/watchdog.rs`, `tools/stability/vram_ballast.cpp`): they now live in `permanent_record/` (left as is, engine code is not edited for comments).

Kept in `docs/` although old, because code or the harness still points at them: `CONFIGURE_WIZARD.md`, `BENCHMARK_DESIGN.md`, `FRAME_GENERATION_UNCAPPED.md`, `UI_SPEC.md`. They describe built features; treat their "plan" wording as history.

## Branches deleted on 2026-10-28

Every deleted branch was fully merged into `main`; the tip SHA is listed so it can be restored with `git branch <name> <sha>`. The two that held archived code also keep a tag: `archive/exp-dlss-vectors` (DLSS motion vectors) and `archive/exp-restamp-2026-10-13` (duplicate-timestamp re-stamp paths). Remote copies of these branches were deleted too.

| Branch | Tip SHA | Last commit |
|---|---|---|
| `1.0.1` | `443b238` | 2026-10-26 |
| `airframexr-1.0` | `31920fc` | 2026-10-25 |
| `0.3` | `9e30d56` | 2026-10-14 |
| `0.4` | `875db94` | 2026-10-19 |
| `0.6` | `6193de6` | 2026-10-24 |
| `0.6.1` | `7cb8b80` | 2026-10-25 |
| `1.0.1` | `443b238` | 2026-10-26 |
| `1.1.1` | `b14758f` | 2026-10-28 |
| `airframexr-1.0` | `31920fc` | 2026-10-25 |
| `avp-client-scale-hint` | `8f49799` | 2026-10-27 |
| `avp-fov-profile` | `2951125` | 2026-10-27 |
| `exp/dlss-vectors` | `8d90596` | 2026-10-25 |
| `exp/restamp-2026-10-13` | `64341cc` | 2026-10-13 |
| `fg-testing` | `8d90596` | 2026-10-25 |
| `fg/optical-flow` | `bc5051a` | 2026-10-15 |
| `fg/uncapped` | `2ff3f81` | 2026-10-16 |
| `fov-render-quality` | `37e68e0` | 2026-10-27 |
| `fov-restrict` | `1bdac95` | 2026-10-26 |
| `harness/fg-lab` | `9e30d56` | 2026-10-14 |
| `native-windows-audio` | `ec9c2e1` | 2026-10-21 |
| `trim-advanced` | `69ee208` | 2026-10-26 |

## Branches kept (not merged into main)

| Branch | Ahead of main | Tip SHA | Why kept |
|---|---|---|---|
| `0.5` | 7 | `4096677` | not merged; check before deleting |
| `performance-learnings` | 3 | `40d6b58` | performance ideas/findings, read when optimizing |
| `exp/pacing-20261010`, `main-exp-20261010-local` | 9 | `e9b92af` | early pacing experiment, never merged |
| `telemetry/avp-2026-10-14` | 10 | `065aa8e` | telemetry experiment; has a worktree at out/wt-telemetry |

## Assistant memory

The assistant's own notes (outside the repo, `~/.claude/projects/-home-jeff-openxr/memory/`) were consolidated on 2026-10-28. The pre-consolidation copy is in `~/.claude/projects/-home-jeff-openxr/memory_archive_2026-10-28/` (read only to revisit a decision).
