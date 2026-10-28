# Progress

Current status only. The day-by-day log of 2026-10-07 to 2026-10-28 is archived (see `docs/PERMANENT_RECORD.md`; do not read it unless you must revisit a past decision).
Add a line here for anything a user would notice; write what was tested and how, and say what was not.

Legend: **source-verified** (read only) | **built** (compiles) | **tested-pass** | **tested-fail** | **blocked**.

## State at 1.1.1-pre (2026-10-28, main)

- Released: 1.0.0, 1.1.0-pre, 1.1.1-pre (notes in `docs/releases/`). IPC version 35: host, shim and package come from the same build.
- Chain: game (OpenXR/UEVR via VDXR + our shim) -> host (frame generation, foveation, NVENC) -> ALVR streaming -> visionOS client (Apple Vision Pro), Quest 3 also works. See `docs/ARCHITECTURE.md`, `docs/FRAME_GENERATION.md`, `docs/PERFORMANCE_FINDINGS.md`.
- Lab: `harness/` (see `harness/README.md`); whole suite on the build PC with the pacing fix forced on: 43 of 46 passed, the other three explained.

## Not yet tested on a headset

- The hardened game frame pacing fix (`video.drop_duplicate_frames`) and the game fps lock on Assassin's Creed Black Flag (field results come from the build before the hardening).
- The field-of-view override with a real UEVR game and on the Vision Pro; the left-handed desktop mirror; the audio fix on the 5090.
- Chroma-key threshold 4 (raise it if room edges look eaten).
- visionOS client (fork `kteque/alvr-visionos`, branch `airframexr-client`, TestFlight build of 2026-10-28): the PS button override on visionOS 27. Two earlier builds failed in the field with the app listed under Home Button Overrides. Apple documents the app's half as `preferredSystemGestureState = .disabled` on the profile's Home element; the new build claims before the first press, reads the press from both input collections, sends it as the game's menu click, and shows a "PS button" row in the Entry window. Decides it: the `[home]` lines in the host log (`Button Home=absent` means visionOS does not expose the PS button to apps at all).

## Known open

- The Black Flag mod's change of rhythm about 4 minutes in (its submit thread pool shrinks): the lock evens out its effect, nothing removes it.
- Desktop mirror: a second game started later was once not evicted in the field; the lab chain scenario passes, root cause open.
- Hand posture reversed in some games (fix not working yet); autoconfig wizard proposals can be inconsistent.
