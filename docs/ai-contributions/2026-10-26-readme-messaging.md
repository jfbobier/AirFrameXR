# AI-assisted work summary: README positioning and contribution guidelines

- **Change:** AirFrameXR GitHub README messaging, architecture comparison image, and contribution policy.
- **AI tools used:** ChatGPT (writing and packaging assistance); a prior ChatGPT-generated illustration selected by the project maintainer.
- **Human owner:** AirFrameXR maintainer — final content review pending before commit.
- **Date:** 2026-10-26

## What the AI contributed

- Drafted a replacement README opening explaining that AirFrameXR replaces the **PC-side SteamVR + ALVR server path**, while the ALVR v20 headset client remains unchanged.
- Incorporated the developer-reported improvements of **up to 10+ FPS** and **about 20 ms less latency** as observational, non-guaranteed measurements; described frame-generation latency trade-offs separately.
- Prepared the selected Before/After graphic for `docs/images/airframexr-pipeline-comparison.png` (reused from the project's earlier design discussion rather than re-generated for this update).
- Drafted the design philosophy and AI-assisted contribution requirements for the README, `CONTRIBUTING.md`, and `docs/DEVELOPMENT_PHILOSOPHY.md`.
- Created a standard-library-only local apply script and a reusable summary template.

## Human review and decisions

The maintainer selected the image and requested the positioning and principles. Final review of wording, technical claims, image, and repository modifications **has not yet been performed**; that review is required before committing.

## Validation actually performed

| Check | Result | Evidence |
|---|---|---|
| Update script syntax and dry run | PASS | Ran script with `--check` on synthetic README / CONTRIBUTING fixtures |
| Update script application | PASS | Ran with `--apply` on synthetic fixtures |
| Preservation and idempotence | PASS | Checked original sections remained; second run produced identical file hashes |
| Bundle integrity | PASS | ZIP CRC / structure check |
| Apply against actual Git checkout | NOT RUN | Source README / CONTRIBUTING content reviewed online; no local clone available in this environment |
| Windows build / automated harness / headset | NOT RUN | Documentation-only change; no runtime code changed |
| FPS / latency benchmark | NOT RUN | Existing performance claims are maintainer-supplied, not reproduced here |

## Limitations and risk

The apply script depends on the current README's `# AirFrameXR`, warning block and developer section anchors, plus `CONTRIBUTING.md`'s PR heading. It stops without editing if anchors are missing. Review image legibility on GitHub mobile and confirm all measured claims against the original logs before merging. No project runtime dependencies, code, settings or background processes are added.

## Scope fit

The update aims to reduce confusion rather than broaden the feature surface. It documents the project boundaries, one-tool-one-job approach and preference for native tooling, adding only documentation and one image.
