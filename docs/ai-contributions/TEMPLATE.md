# AI-assisted work summary: <short title>

> Copy this file to `docs/ai-contributions/YYYY-MM-DD-short-topic.md` for a PR with material AI assistance. Replace the placeholders and delete guidance. This is a description of completed work, not a transcript or unverified claims.

- **PR / change:** <link or short description>
- **AI tools used:** <tool(s), if known>
- **Human reviewer/owner:** <GitHub handle>
- **Date:** YYYY-MM-DD

## What the AI contributed

Describe the specific code, design, tests, docs, or debugging performed with AI assistance. List the affected files/modules and why they changed. Do not claim AI work that did not happen.

## Human review and decisions

Explain what you inspected or modified yourself: critical logic, safety and fallback paths, performance implications, upstream license use, and whether the added complexity is justified.

## Validation actually performed

| Check | Result | Evidence / command |
|---|---|---|
| Build | PASS / FAIL / NOT RUN | `<command>` |
| Harness scenario(s) | PASS / FAIL / NOT RUN | `<scenarios and verdict>` |
| Real headset test | PASS / FAIL / NOT RUN | `<GPU, headset, game, result>` |
| Before/after performance | MEASURED / NOT MEASURED | `<baseline, FPS, latency, test conditions>` |

Clearly separate checks that *ran* from those merely suggested by AI.

## Limitations and risk

State remaining unverified assumptions, compatibility risks, known failures, or missing validation. Explain any new dependencies, settings, background work, or extra overhead; write **none** if there are none.

## Scope fit

One or two sentences explaining how this work respects **no bloat**, **one tool = one job**, and **native tooling where practical**.
