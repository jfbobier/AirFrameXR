# Contributing

Thanks for helping. A few things keep this project workable:

## Scope
- **Headset: Apple Vision Pro with the stock ALVR visionOS app is the main focus.** The ALVR 20.14.1 protocol stays unchanged so the app
  cannot tell the difference. Other headsets with an ALVR client (the protocol is device agnostic) are expected to work but are not actively
  supported: reports and small fixes are welcome, with no promise of an answer; nothing that makes the Vision Pro path worse will be merged.
- **GPU: NVIDIA only** (NVENC), RTX 40 series or newer for users. The point of the project is an encode path written for one
  vendor. **OS: Windows** for the streamer.
- OpenXR games only (no SteamVR/OpenVR games).

<!-- AirFrameXR contribution policy -->
## Development philosophy: keep the project lean

**No bloat. One tool, one job. Native tooling when appropriate.** AirFrameXR prioritizes a small, understandable, measurable PC-side OpenXR-to-headset pipeline over features for their own sake. The maintainer may decline requests that add broad compatibility layers, persistent services, configuration sprawl, dependencies, or maintenance burden without a clear benefit to the core performance path.

Before writing a change, ask: does it measurably improve performance, reliability, correctness, or an existing core workflow? Can it be solved with the OS/platform tools already used by the project? Can an existing component own it without adding another layer? See [docs/DEVELOPMENT_PHILOSOPHY.md](docs/DEVELOPMENT_PHILOSOPHY.md).

## AI-assisted contributions

**Welcome, but documented and reviewed.** AI tools may help with code, tests, debugging and documentation. Contributors remain responsible for understanding and validating every change. An AI-generated test plan is not a passed test.

For each pull request that includes material AI-assisted code or design work, add a **Markdown work summary** under `docs/ai-contributions/YYYY-MM-DD-short-topic.md`, using [the template](docs/ai-contributions/TEMPLATE.md). State:

1. What the AI did, including the touched components and the reasoning for the change.
2. Which decisions and code paths a human actually reviewed.
3. Which builds, harness scenarios, and headset tests ran, with results; distinguish these from suggested or unrun checks.
4. Known limitations, regressions, unverified assumptions, new dependencies and any effect on performance or complexity.

Do **not** paste private prompts, credentials, personal data, or raw chat logs. A concise, factual summary is enough; opaque, unreviewed AI changes are not.

## Before a pull request
1. Build on Windows with `build.ps1` (`docs/DEVELOPMENT.md`) and try your change with a game and the headset if you can.
2. If you can run the automated harness (optional, `docs/DEVELOPMENT.md`): the scenarios your change touches, and
   `python3 harness/run.py all` for anything in the host, the encoder library or the shim. Installer changes: `test-install`;
   package changes: `test-zip`; benchmark changes: `bench-quality full`.
3. In the pull request, say what you **tested** (scenario names, measurements, headset if any) and what you did **not**
   test. "Builds" is not "works". Add a line to `docs/progress.md` for anything a user would notice.

## Code
- Keep upstream code unmodified where it is vendored (`nvenc/upstream/`): change it through the asserted build-time patches in
  `tools/host/build.rs`, so a changed upstream fails loudly.
- Match the surrounding style and comment density. No new dependencies without a reason in the PR (the harness is stdlib
  Python on purpose; the GUIs need nothing beyond .NET Framework 4.8).
- Never commit logs, `harness/results/`, session files with your headset in them, or `tools/builder.local`.

## Headset profiles
A profile is one small JSON file for a headset (see `docs/HMD_PROFILES.md` and `profiles/template.profile.json`). They are shared between users
(the Discord), not collected in the repository: only the template and the Apple Vision Pro profile live here. Put **only values you have run** in a
profile and say on what (`verified`).

## Bug reports
Attach `logs\AirFrameXR.log` and the matching `logs\debug\<time>\` folder (turn debug on in AirFrameXR.exe, reproduce, then zip
the folder), the game and how it is started (UEVR, mod, native OpenXR), GPU and driver.
