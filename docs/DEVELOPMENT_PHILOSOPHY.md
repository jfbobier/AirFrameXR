# AirFrameXR development philosophy

> **Performance first. Complexity last.**
>
> AirFrameXR exists to make the Windows/OpenXR-to-headset path as lean and predictable as possible on NVIDIA hardware. It is not intended to become a generic VR platform.

## Principles

### 1. No feature bloat

A feature belongs here only if it solves a real problem in rendering, frame pacing, frame generation, streaming, stability, diagnostics, setup, or a directly necessary user workflow. A wider feature matrix is not progress by itself. The maintainer may decline a well-implemented PR that increases maintenance cost or fragments the fast path.

Favor fewer modes, fewer settings, fewer dependencies, and fewer moving parts. Remove obsolete options when a better measured approach supersedes them. Avoid speculative compatibility features and scaffolding for hypothetical users.

### 2. One tool = one job

Each executable, module, and UI surface should have a clear, limited responsibility. Make boundaries explicit; do not create a general-purpose abstraction for a single specific need. Keep the streaming hot path free of unrelated work and background machinery.

### 3. Prefer native tooling when it fits

Use Windows capabilities and platform APIs already present where practical (for example, native audio capture, OpenXR, NVENC, NVIDIA Optical Flow, and standard Windows configuration mechanisms). Reuse pinned, reviewed upstream code instead of adding overlapping wrappers or building substitutes for mature components. This is not a blanket ban on dependencies: a dependency must offer a concrete benefit that justifies its cost.

### 4. Measurements beat intuition

Performance changes need a baseline, controlled conditions, and a repeatable test. Report frame pacing, displayed cadence, latency, frame quality, VRAM/CPU cost, and regressions as relevant. The existing harness and real-headset verification matter more than a benchmark headline. Frame generation can improve perceived smoothness while adding interpolation latency; never present these as the same metric.

### 5. Favor honest scope over universal compatibility

The maintained target is a Windows PC with an NVIDIA GPU, OpenXR applications and ALVR v20 headset clients. The Vision Pro is the primary tuning target, with Quest 3 verified. SteamVR/OpenVR-only applications, AMD/Intel encoding, and maintaining every possible headset profile are outside the project's core goals. A clean fallback to SteamVR + stock ALVR is better than an enormous compatibility layer.

### 6. AI contributions are welcome, not exempt

AI can propose and implement code, refactors, test cases, and documentation. Contributors must own the result, understand its behavior, and verify it. Every PR with material AI assistance must include `docs/ai-contributions/YYYY-MM-DD-short-topic.md`, based on [`TEMPLATE.md`](ai-contributions/TEMPLATE.md), explaining the actual AI work, human validation, tests run, remaining risks, and any added complexity. Do not commit model transcripts, private prompts, personal data, or secrets.

## A quick checklist before a PR

- Does this change serve the core OpenXR-to-headset performance path or an essential workflow?
- Is the simplest solution the one submitted? Could code, flags, files, dependencies or settings be removed instead?
- Does the change leave existing behavior and support boundaries clear?
- Are there before/after measurements or focused tests rather than only expected improvements?
- Is every new dependency or abstraction justified?
- If AI materially contributed, is its Markdown work summary included and human-reviewed?

The final criterion is not the number of features supported. **It is the quality, predictability, maintainability, and speed of the path we intentionally support.**
