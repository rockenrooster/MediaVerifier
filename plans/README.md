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
| 1 | 01 Foundation | Done | CI workflow written; first GitHub run pending a push |
| 2 | 02 Findings model | Done | Catalog texts written for every rule; docs/rules.md generated |
| 3 | 03 Storage | Done | Budgets met on 1,000,000 files; numbers in docs/perf.md |
| 4 | 04 FFmpeg toolchain | Done | Integration tests run against MEDIAVERIFIER_FFMPEG_DIR until the bundle exists; FF-10 lands with UI-09 |
| 5 | 05 Scan engine | Done | |
| 6 | 06 Integrity and rot | Done | |
| 7 | 07 Corruption detectors | Done | Corruption matrix (T-04) passes at every depth; the detection-rate benchmark (T-05) follows with phase 15 |
| 8 | 08 UI design system | Not started | Can start after phase 1 |
| 9 | 09 UI screens | Not started | Can start after phases 2 and 8 using fake data |
| 10 | 10 Audio quality detectors | Not started | |
| 11 | 11 Video and photo quality detectors | Not started | |
| 12 | 12 Remediation and recommendations | Not started | |
| 13 | 13 Scheduling and notifications | Not started | |
| 14 | 14 Updater and release | Not started | Can start after phase 1; required before the first public release |
| 15 | 15 Testing, fixtures, calibration | In progress | T-01, T-02, T-03 and T-06 seeded; runs alongside every phase |
| 16 | 16 Hardening and backlog | Not started | |
| 17 | 17 Bundled FFmpeg | Not started | Can start any time; required before the first public release |

## Deviations log

Record every place where the implementation intentionally differs from a plan.

| Date | Subplan / task | What changed | Why |
|------|----------------|--------------|-----|
| 2026-11-16 | 01 / F-10 | DPI awareness is set only by `ApplicationHighDpiMode=PerMonitorV2`, not in `app.manifest` | The .NET 10 Windows Forms build fails with WFO0003 when the manifest declares DPI awareness; the project property gives the same PerMonitorV2 mode |
| 2026-11-16 | 01 / F-01 | The app project sets `NoWarn` for `WFO1000`; the test project sets `ValidateExecutableReferencesMatchSelfContained=false` | WFO1000 concerns designer serialisation, and no designer is used. The test host must reference the self-contained exe project to test its internals |
| 2026-11-16 | 01 / F-08 | `Ctrl+7` also opens Settings, besides `Ctrl+,` | Subplan 01 asks for `Ctrl+1`..`Ctrl+7`, subplan 09 for `Ctrl+,`; both work |
| 2026-11-16 | 00, 03 / S-01 | `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14 (SQLite 3.54.0) instead of 2.1.11 (3.49.1); the minimum-version check is 3.54 | 2.1.11 bundles a SQLite with CVE-2025-6965 (GHSA-2m69-gcr7-jv3q) and fails the NuGet vulnerability audit, which is an error with warnings as errors. 2.1.14 is the latest 2.1 patch and works with `Microsoft.Data.Sqlite.Core` 10.0.9 |
| 2026-11-16 | 03 / S-03 | The writer reads new row ids with `last_insert_rowid()`, never `RETURNING` | Measured: reading a `RETURNING` row inside the per-operation savepoint is about 20 times slower and degrades as a batch grows (details in `docs/perf.md`) |
| 2026-11-16 | 04 / FF-03 | `-nostdin` is prepended to `ffmpeg` commands only | `ffprobe` 8.0.1 rejects `-nostdin` ("Option not found") and never reads the console |
| 2026-11-16 | 04 / FF-04 | The self-check skips the media-engine check in a development build that has no bundle beside it; the shipped single-file build and any override are checked | The CI build and local builds have no bundle until `build.ps1` adds one (master plan, definition of done 4) |
| 2026-11-16 | 04 / FF-10 | The Tools → Media engine view is built with the Tools page (task UI-09) | It needs the UI kit from subplan 08 |
| 2026-11-16 | 02, 07 / C-11 | `COR-DECODE` ranges Low – Critical and `COR-INDEX` Low – Medium in the catalog | Subplan 07 gives `COR-DECODE` Low when only minor decoder complaints appear, and `COR-INDEX` Low for unfinalised RIFF sizes and wrong length headers; the catalog ranges did not allow it |
| 2026-11-16 | 07 / C-11 | Audio lost mid-file for reported errors (up to `COR-TRUNCATED.secondsPerError`, 1.5 s each) is not `COR-TRUNCATED`; the loss is added to the decode or checksum finding | Measured: two flipped bytes in an Opus file drop two Ogg pages (2 s) and read as "cut short" although the end is there |
| 2026-11-16 | 07 / C-14 | Finding summaries show positions and lengths inside media as `m:ss` (`h:mm:ss` from an hour) | Subplan 09's duration format (`37 min`, `1 h 48 min`) is for elapsed time and too coarse for "Stops at 2:31 of 4:05" |
| 2026-11-16 | 07 / C-01 | The sniffer also recognises BigTIFF and the RAW formats with their own magic (ORF, RW2, RAF) | Without it those RAW files are reported as unrecognised |
| 2026-11-16 | 07 / C-02, C-04, C-05 | `COR-TRAILING` is also checked after PNG, RIFF and Matroska ends. Zero or `FF` padding, Samsung motion-photo and SEFT trailers, ID3/APE tags after RIFF, OpenDML `AVIX` chunks and a second EBML document are not reported | The plan named only ISO-BMFF and JPEG; the exceptions are legitimate data written by common tools and cameras |
| 2026-11-16 | 07 / C-02 | HEIF and AVIF images need a `meta` box instead of `moov`; a RIFF size one byte past the end is not truncation | Still images in ISO-BMFF have no `moov`; many writers count the final pad byte without writing it |
| 2026-11-16 | 07 / C-13 | MP3 length tolerance is max(1 %, one frame); a CBR `Info` header whose frame count disagrees with its byte count is `COR-INDEX`. Missing Matroska Cues are not reported for a truncated file | Short clips would otherwise be flagged; a truncated file already has a more serious finding |
| 2026-11-16 | 07 / C-06 | A file without a signature whose extension is `mp3`, `mp2`, `mpa` or `mp1` is searched for three consecutive MPEG audio frames in its first 64 KiB | MP3 files often start with junk or a damaged tag |
| 2026-11-16 | 15 / T-04 | Matrix cells re-measured at mid-file: a truncated WebP already gives `COR-CONTAINER` at Quick; 2,000 random bytes in `av.mp4` at mid-file hit a key frame and audio, giving `COR-DECODE` at Standard and Deep without concealment | Measured with FFmpeg 8.0.1; the table in subplan 15 is updated |
