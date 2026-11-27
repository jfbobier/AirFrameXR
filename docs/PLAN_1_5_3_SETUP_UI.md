# Plan: one window for setup and settings (1.5.3)

Status: proposal for the owner's review (2026-11-27). Nothing built. GUI only (`tools/gui/`), through the existing settings
(`airframexr.json`, `session.base.json`); no engine change.

## 1. Why

The owner, 2026-11-27, after the first Quest 3 session with 1.5.1: "review once more how settings / diagnostic / setup are organized in
the UI as a whole and rebalance for AVP and Meta Quest ... fold the settings + wizard relaunch in the same UI by making the wizard
skippable". Today a user meets the same subjects in five places:

| Where | What it holds |
|---|---|
| Setup wizard (`configure.exe`, 5 steps) | headset pairing, the handshake, the mode (profile / manual / existing), the tests, the result, the field of view |
| Settings (`configure.exe --settings`, tabs 1. Headset, 2. Benchmark, 3. Settings) | the same pairing, the same tests as buttons, the stream settings |
| "Diagnostics and headset setup" (the launcher's chooser) | the wizard again, or its Diagnose mode (tests on the current settings) |
| Advanced (`Advanced.cs`) | the field of view (restriction, profiles, calibration), the game's fps lock and pacing, the stream size, the experimental switches |
| Field-of-view manager (`configure.exe --fov`) and `calibrate.exe` | profiles and the calibration, reached from Advanced |

The same test or setting can be reached two or three ways, and what applies to which headset is decided in each window separately
(1.5.2 fixed the worst of it: the Vision Pro's field of view reached a Quest).

## 2. The proposal

**One window, "Setup and settings"**, with one page per subject, in the order a first setup needs them:

| Page | Holds | Vision Pro | Quest / other |
|---|---|---|---|
| Headset | pairing (find, by address, the Quest's USB bridge), the handshake's facts | as today | as today |
| Network | the network test (to 400 Mbps, past it on a cable), the bitrate | yes | yes |
| Encoding | encoder speed, the quality benchmark, foveated / full frame, the stream size | Apple's rate map foveation, the picture-edges presets | ALVR's foveation |
| Frame generation | x2 / x3, neural / edge, the wizard's scene test with it | yes | yes |
| Field of view | restriction, profiles, calibration, mask | yes | not shown (the headset's own) |
| Audio | output, the chroma key | yes | yes |
| Diagnose | the suggestions (`Advice.cs`) and the logs they read | yes | yes |
| Advanced | the experts' switches (today's Advanced window without its field of view) | yes | yes |

- **The wizard is the guided path through the same pages.** On a first start (no headset paired) the window opens on Headset with Next /
  Back and a step list; every page has **Skip**, and the tests are buttons on their page ("Run the network test"), so a guided setup and
  a later visit are the same controls. "Re-run the setup" = open the window and press Next from Headset; what was set by hand stays unless
  that page's test is run again.
- **The headset decides what is shown**, in one place (`AppSettings.Kind`, already the gate since 1.5.2): the Field of view page, the
  Vision Pro's encoding presets and rates for a Vision Pro; the Quest's rates (72 / 80 / 90 / 120) and no Field of view page for a Quest.
- **The launcher** keeps the runtime switches (OpenXR runtime, OpenComposite, tray) and gets one entry, "Setup and settings"; the
  "Diagnostics and headset setup" chooser goes.
- **What disappears:** the duplicated tests (Settings' Benchmark tab and the wizard's test page become the pages' buttons), the separate
  Advanced window and the field-of-view manager window (pages), the Diagnose mode of the wizard (a page).

## 3. What stays as it is

- The settings files and their keys; the streamer; the calibration tool (`calibrate.exe`, started from the Field of view page).
- The tests' code (`TestRunner.cs`): each test becomes callable from its page instead of from a fixed sequence.
- The Black Box (its own zip since 1.5.1) and the Performance and Logs windows.

## 4. Tests

- `harness/ui_matrix.py`: every page at the 9 screen / scale cases, for a Vision Pro and a Quest (the Quest without the Field of view
  page), the guided path's step list and Skip.
- `wizard_e2e.py` rewritten for the guided path (the mock headset: pair, network, encoding, frame generation, finish); `advice_check`,
  `fov_store_check`, `fov_profile_check` unchanged in what they check.
- A headset pass on the Vision Pro and the Quest (the owner).

## 5. Questions for the owner

1. The page list and its order (section 2): anything to add, merge or drop?
2. A first start: open the guided path automatically, or a "Set up now / later" choice?
3. Advanced as the last page, or behind a "Show advanced settings" switch on each page?
