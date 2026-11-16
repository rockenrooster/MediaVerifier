# MediaVerifier implementation plans

These documents are the build specification for MediaVerifier. They are written for an implementing
agent (or developer) who starts with only this repository. Read `00-master-plan.md` first, then work
through the numbered subplans in order.

## Reading order

| # | Document | What it covers | Milestone |
|---|----------|----------------|-----------|
| 00 | [00-master-plan.md](00-master-plan.md) | Product scope, decisions, architecture, phases, budgets, global rules | all |
| 01 | [01-foundation.md](01-foundation.md) | App shell, settings, logging, single instance, IPC, self-check | M1 |
| 02 | [02-findings-model.md](02-findings-model.md) | Severity tiers, finding model, the full rule catalog | M1 |
| 03 | [03-storage.md](03-storage.md) | SQLite schema, migrations, repositories, UI query patterns | M1 |
| 04 | [04-ffmpeg-toolchain.md](04-ffmpeg-toolchain.md) | Bundle location and integrity, process runner, probe model, log classifier | M1 |
| 05 | [05-scan-engine.md](05-scan-engine.md) | Enumeration, change detection, pipeline, concurrency, progress | M1 |
| 06 | [06-integrity-and-rot.md](06-integrity-and-rot.md) | Baseline hashing, bit-rot detection, scrub policy, healthy copies | M1 |
| 07 | [07-corruption-detectors.md](07-corruption-detectors.md) | Structural and decode-level corruption checks for all media | M1 |
| 08 | [08-ui-design-system.md](08-ui-design-system.md) | Exact tokens, icons, components, states, layout rules, visual QA | M1 |
| 09 | [09-ui-screens.md](09-ui-screens.md) | Every page, dialog and state with exact wording, bindings and keyboard map | M1 |
| 10 | [10-audio-quality-detectors.md](10-audio-quality-detectors.md) | Fake lossless, upsampling, bit depth, clipping, distortion | M2 |
| 11 | [11-video-photo-quality-detectors.md](11-video-photo-quality-detectors.md) | Fake upscaling, bitrate adequacy, compression artifacts | M2 |
| 12 | [12-remediation-and-recommendations.md](12-remediation-and-recommendations.md) | Recommendation engine, repair tools, safe-operation protocol | M3 |
| 13 | [13-scheduling-and-notifications.md](13-scheduling-and-notifications.md) | Scan plans, Task Scheduler, headless runs, toasts | M4 |
| 14 | [14-updater-and-release.md](14-updater-and-release.md) | Signed updater port, installer, release pipeline | M1 |
| 15 | [15-testing-fixtures-calibration.md](15-testing-fixtures-calibration.md) | Test strategy, fixture generator, corruption matrix, calibration | continuous |
| 16 | [16-hardening-and-backlog.md](16-hardening-and-backlog.md) | Performance and robustness pass, release criteria, backlog | M5 |
| 17 | [17-bundled-ffmpeg.md](17-bundled-ffmpeg.md) | Building, verifying, publishing and licensing the bundled FFmpeg | M1 |
| A | [appendix-a-ffmpeg-verified-behavior.md](appendix-a-ffmpeg-verified-behavior.md) | Commands and outputs verified against FFmpeg 8.0.1 | reference |
| B | [appendix-b-audio-prototype.md](appendix-b-audio-prototype.md) | Listing and output of the prototype that validated the audio algorithms | reference |
| D | [../docs/design/README.md](../docs/design/README.md) | Reference mockups, design tokens and icon geometry: the binding visual specification | M1 |

**This repository contains planning documents only. No source code exists yet.** Creating the
solution, the build and release scripts, the CI workflows, the FFmpeg build and the installer script
are tasks in subplans 01, 14 and 17. The numbers and commands in the plans were verified with
throwaway experiments (appendices A and B, and the measurements quoted in the master plan) so the
implementing agent starts from measured facts, not guesses. The design is finished: implementers
reproduce it and do not invent visuals or wording (master plan D-15).

## How to work from these plans

1. Work one subplan at a time, in order, unless the master plan marks phases as parallel.
2. Each subplan lists tasks with stable IDs (`F-03`, `E-07`, ...). Reference the ID in commit subjects,
   for example `E-07: per-volume I/O gates`.
3. A task is done only when its acceptance criteria pass. Do not mark a phase complete while any of
   its exit criteria fail.
4. Keep the solution building and all tests passing after every commit.
5. When reality contradicts a plan (an FFmpeg flag behaves differently, an API is missing), trust
   what you measured: fix the plan document in the same commit and add a line to the deviations log
   below. Do not silently diverge.
6. Thresholds marked *calibrate* are starting values. Subplan 15 defines how to calibrate them; record
   the final values and the evidence in `docs/calibration/`.
7. Anything visual or worded follows subplans 08 and 09 and `docs/design/` exactly. When a case is not
   covered, reuse the nearest specified pattern and log it below as an *unspecified design case*.
8. Add user-visible changes to the `Unreleased` section of `CHANGELOG.md` as you go.

## Status board

Update this table as phases progress (`Not started`, `In progress`, `Done`, `Blocked: reason`).

| Phase | Subplan | Status | Notes |
|-------|---------|--------|-------|
| 0 | Repository and plans | Done | Public repository, plans, agent instructions. No code |
| 1 | 01 Foundation | Not started | |
| 2 | 02 Findings model | Not started | |
| 3 | 03 Storage | Not started | |
| 4 | 04 FFmpeg toolchain | Not started | |
| 5 | 05 Scan engine | Not started | |
| 6 | 06 Integrity and rot | Not started | |
| 7 | 07 Corruption detectors | Not started | |
| 8 | 08 UI design system | Not started | Can start after phase 1 |
| 9 | 09 UI screens | Not started | Can start after phases 2 and 8 using fake data |
| 10 | 10 Audio quality detectors | Not started | |
| 11 | 11 Video and photo quality detectors | Not started | |
| 12 | 12 Remediation and recommendations | Not started | |
| 13 | 13 Scheduling and notifications | Not started | |
| 14 | 14 Updater and release | Not started | Can start after phase 1; required before the first public release |
| 15 | 15 Testing, fixtures, calibration | Not started | Runs alongside every phase |
| 16 | 16 Hardening and backlog | Not started | |
| 17 | 17 Bundled FFmpeg | Not started | Can start any time; required before the first public release |

## Deviations log

Record every place where the implementation intentionally differs from a plan.

| Date | Subplan / task | What changed | Why |
|------|----------------|--------------|-----|
| | | | |
