# AirFrameXR: notes for Claude

Wireless PCVR for NVIDIA: OpenXR runtime (VDXR + our shim `ovrshim/`) -> Rust host `tools/host/` (frame generation, foveation, NVENC via `nvenc/`) -> ALVR v20 streaming -> headset (Apple Vision Pro primary, Quest 3 verified). Windows + NVIDIA only; no OpenVR/SteamVR. Philosophy: performance first, no feature bloat, measurements beat intuition (`docs/DEVELOPMENT_PHILOSOPHY.md`).

## Rules

- **The engine is off limits without asking first**: `ovrshim/`, `nvenc/`, `tools/host/`, `tools/mirrordesktop/`, the IPC (`ovrshim/ipc.h`) and anything on the frame path. Allowed without asking: the GUIs (`tools/gui/`), docs, the harness, and behaviour changes that go through existing parameters (`session.json`, `airframexr.json`, host stdin commands). A comment-only edit in engine code still counts as touching it.
- Targeted changes. Reuse proven third-party code over writing our own. Prefer a manual toggle over an auto mode. Do not add settings or modes without a measurement that justifies them.
- Do not say "works" for something that only builds. Say what was tested, how, and what was not (legend in `docs/progress.md`).
- Commit only your own hunks; never reset or force-push. Another session may be editing the same tree. Ask before pushing, releasing, or deleting anything on GitHub.
- Windows is the build and test target; this checkout is Linux/WSL (edits, docs, compile checks). Build and run through `tools/builder.sh` (see `docs/DEVELOPMENT.md`).

## Where things are

- Current status and what is untested: `docs/progress.md`. Release notes: `docs/releases/`.
- How it works: `docs/ARCHITECTURE.md`, `docs/FRAME_GENERATION.md`, `docs/PERFORMANCE_KNOBS.md`, `docs/EXPERIMENTAL_SWITCHES.md`.
- Before optimizing, read what was already tried: `docs/PERFORMANCE_FINDINGS.md` (dead ends are listed there).
- Lab and tests: `harness/README.md`.
- Settings the user can change live in `airframexr.json` and the Advanced window; their meaning is in `docs/EXPERIMENTAL_SWITCHES.md`.

## Never read

`docs/PERMANENT_RECORD.md` and `docs/permanent_record/` are archives (old plans, the day-by-day log, removed features, deleted branches). Do not open them unless the user asks to revisit a past decision in detail.
