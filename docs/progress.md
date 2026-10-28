# Progress

Current status only. The day-by-day log of 2026-10-07 to 2026-10-28 is archived (see `docs/PERMANENT_RECORD.md`; do not read it unless you must revisit a past decision).
Add a line here for anything a user would notice; write what was tested and how, and say what was not.

Legend: **source-verified** (read only) | **built** (compiles) | **tested-pass** | **tested-fail** | **blocked**.

## State at 1.1.1 (2026-10-29, main)

- Released: 1.0.0, 1.1.0-pre, 1.1.1-pre, 1.1.0, 1.1.1 (profile-only on the 1.1.0 binaries; notes in `docs/releases/`). IPC version 35: host, shim and package come from the same build.
- Chain: game (OpenXR/UEVR via VDXR + our shim) -> host (frame generation, foveation, NVENC) -> ALVR streaming -> visionOS client (Apple Vision Pro), Quest 3 also works. See `docs/ARCHITECTURE.md`, `docs/FRAME_GENERATION.md`, `docs/PERFORMANCE_FINDINGS.md`.
- Lab: `harness/` (see `harness/README.md`); whole suite on the build PC with the pacing fix forced on: 43 of 46 passed, the other three explained.

## Not yet tested on a headset

- The calibrated Vision Pro field of view (0.96 / 0.71 / 0.69 / 0.61, render quality 0, `docs/AVP_FOV.md`, release 1.1.1): **tested-pass** in a game by the author (one session, one fit: the black strip of the 1.1.0 values at the right eye's outer edge is gone). Not tested: other wearers and fits, HUD corners in other games, frame time (the pixel shares are computed from areas). The client branch declaring the rectangle itself is not built.
- The hardened game frame pacing fix (`video.drop_duplicate_frames`) and the game fps lock on Assassin's Creed Black Flag (field results come from the build before the hardening).
- The field-of-view override with a real UEVR game and on the Vision Pro; the left-handed desktop mirror; the audio fix on the 5090.
- Chroma-key threshold 4 (raise it if room edges look eaten).
- visionOS client (fork `kteque/alvr-visionos`, branch `airframexr-client`, TestFlight builds of 2026-10-28): the PS button on visionOS 27. **tested-fail** in the field (Silas, PSVR2 Sense): the host log shows `Button Home=absent` on both controllers with `inAppAction=deliveredToApp`, i.e. the Home Button Overrides setting is read, but visionOS exposes no Home element on the Sense profile, so no app can take the PS button (Apple's side). The Menu (Create/Options) press also opened the Home View; the second build of the day claims only Home and logs each Menu press to tell whether our earlier claim on Menu caused that. Untested.

## Known open

- The Black Flag mod's change of rhythm about 4 minutes in (its submit thread pool shrinks): the lock evens out its effect, nothing removes it.
- Desktop mirror: a second game started later was once not evicted in the field; the lab chain scenario passes, root cause open.
- Hand posture reversed in some games (fix not working yet); autoconfig wizard proposals can be inconsistent.
