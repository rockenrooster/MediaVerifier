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
| 8 | 08 UI design system | Done | Kit, theme service, render harness with reference comparison |
| 9 | 09 UI screens | In progress | All pages built; Schedule (UI-08) without its editor drawer; repair dialog A shown as a confirmation dialog (deviations log) |
| 10 | 10 Audio quality detectors | Done | Calibrated on the generated corpus (docs/calibration/audio.md); real recordings still to run when provided |
| 11 | 11 Video and photo quality detectors | In progress | Frame sampling, VID-UPSCALED, VID-STARVED, VID-BLOCKY, VID-INFLATED, VID-HDR, IMG-UPSCALED, IMG-HEAVY-COMPRESSION and IMG-LOW-RES done on the generated fixtures. Open: evidence views (V-05), VID-INTERLACE, VID-FROZEN, VID-BLACK, VID-AUDIO-SILENT (M3), calibration on real footage (VP-01), performance check (VP-02) |
| 12 | 12 Remediation and recommendations | In progress | Operation runner (journal, temporary output, verification, swap with rollback, crash recovery), quarantine, undo, purge; actions restore copy, remux, fix extension, 16-bit, FLAC rewrite, salvage, quarantine, accept baseline; Issues drawer, Fix… and Quarantine wired with confirmation and Undo toasts. Open: extract preview, de-clip copy, Recycle Bin, recommendation engine (R-11), Tools page restore/delete/undo buttons and Ctrl+Z (R-14), pausing a running scan |
| 13 | 13 Scheduling and notifications | In progress | Plan model and validation, schedule calculator (DST, month ends), task XML, Task Scheduler backend with reconciliation and `--cleanup`, headless plan runs (conditions, stop event, notification), notification policy, Schedule page with templates, switches, Run now, depth and delete. Open: full plan editor (schedule-edit), GUI view of a background run (SCH-06), toast registration and activation (N-02, N-03), Settings → Notifications test (N-05), webhook (N-06) |
| 14 | 14 Updater and release | In progress | Updater ported (trust, manifest validation, download and install); build.ps1 (app folder, portable zip, installer and signed manifest when Inno Setup and the key exist), release.ps1, release.yml, installer script with the runtime check, key script, Settings → Check now. Open: the owner generates the signing key and pastes the public key (U-01), download tests against a local listener (U-04), full Updates card states and nav indicator (U-08), startup check (U-09), self-check round trip (U-10), first release dry run (U-11). Inno Setup is not installed on the dev machine, so the installer has not been compiled yet |
| 15 | 15 Testing, fixtures, calibration | In progress | T-01 to T-04, T-06, T-10 done; audio calibration on the generated corpus (docs/calibration/audio.md). Open: corruption benchmark (T-05), parser fuzz in CI (T-07), kill-test harness (T-08), video and photo calibration on real footage (T-09), full performance suite (T-11), path matrix (T-12) |
| 16 | 16 Hardening and backlog | In progress | THIRD-PARTY-NOTICES.md, docs/troubleshooting.md and docs/security-review.md written (H-04, H-06 in part). Open: stall watchdog (H-02), robustness procedures (H-03), accessibility pass (H-05), release candidate and v1.0 (H-07, H-08) |
| 17 | 17 Bundled FFmpeg | In progress | build-ffmpeg.sh (from this plan), ffmpeg-bundle.yml (build in the pinned image, package with bundle.json, Windows verification, pre-release, lock-file pull request) and Get-FfmpegBundle.ps1 written. Open: first workflow run (needs a push), THIRD-PARTY.txt from the image's dependency scripts (FFB-07), appendix A re-run with the bundle (FFB-05) |

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
| 2026-11-16 | 08 / UK-09 | Tooltips appear 8 px below the pointer | Subplan 08 fixes the tooltip's look and delay but not where it appears |
| 2026-11-16 | 08 / UK-09 | Dialog windows get their 8 px rounded corners from DWM (`DWMWA_WINDOW_CORNER_PREFERENCE`, round) and are square on Windows 10 | A top-level window cannot paint outside its rectangle without becoming a layered window, which breaks child controls |
| 2026-11-16 | 08 / UK-04 | Antialiased strokes of 1.25 to 1.5 device pixels are drawn with a 1.501 px pen | Measured: GDI+ draws antialiased pens of 1.5 px or less with 1 px of coverage, so icons at 100 % looked half as heavy as in the references |
| 2026-11-16 | 08 / UK-11 | The content area ends 16 px left of the open detail drawer instead of 24 | The `issues-detail` reference; subplan 08 is updated |
| 2026-11-16 | 08 / UI-03 | The Health card's caption lines may use 4 px of the card's 16 px padding | GDI text runs about 2 % wider than the references' browser text, which cut `have no problems` short |
| 2026-11-16 | 09 / UI-01 | Recent dates show the year when the date is not in the current year or more than six months away | Subplan 09's own example `14 Mar 2026` (with "now" in November 2026) and the references need it; subplan 09 is updated |
| 2026-11-16 | 09 / UI-01 | File sizes keep their decimal (`212.0 GB`) | Subplan 09 says one decimal; the dashboard references show `212 GB` and `96 GB` |
| 2026-11-16 | 09 / UI-02 | The header Scan button is disabled with `A scan is already running` while a scan runs | Subplan 09 says so; the references, drawn during a scan, show it enabled |
| 2026-11-16 | 09 / UI-05 | The byte map draws as many 4 px columns as fit (about 97 in a 440 px drawer), each an equal share of the file | One column per 1/200 of the file needs 800 px |
| 2026-11-16 | 09 / UI-04 | `Fix…` and `Quarantine` in the selection bar stay disabled until the action catalog exists (task R-01) | Subplan 12 decides which actions apply |
| 2026-11-16 | 03 / S-07, 09 / UI-04 | Schema v2 copies each file's name and media kind into its findings and adds the covering index `ix_findings_list`; `ix_findings_open` also covers kind and category. The Issues page reads the list and its counts in parallel | Measured with 1,000,000 open findings: joining `files` for the name sort took 6–8 s; with the index the list, groups and counts load in 130–260 ms. Grouping by rule or folder still sorts the whole list (about 1.4 s at that size) |
| 2026-11-16 | 09 / UI-06 | Recent dates follow subplan 09 everywhere: weekday and time within six days, day and time after (`Sat 03:40`, `12 Oct 03:00`) | The library mockups mix in day-only dates (`Scanned 14 Nov`, `12 Oct`) that the format table does not have |
| 2026-11-16 | 09 / UI-06 | In the Browse table, left-aligned cells may run to the next column and right-aligned cells keep 4 px from their edges | The column offsets of subplan 09 leave 72 px for `Scanned`, too little for `Today 03:12` with 16 px padding on both sides |
| 2026-11-16 | 09 / UI-07 | The status strip hides while the Scans page is shown | The scans mockup shows a running scan without the strip; the page carries the same controls |
| 2026-11-16 | 09 / UI-07 | `Data read` shows the bytes read without a total; whole-hour durations keep their minutes (`2 h 00 min`) | The engine status has no byte total for the scope; the mockup writes `2 h 00 min` |
| 2026-11-16 | 09 / UI-10 | Settings → Rules lists the rules in catalog order; Advanced → `Change…` opens Tools (the database sub-view moves it) | Subplan 09 gives no rule order; moving the database belongs to Tools → Database |
| 2026-11-16 | 10 / A-05 | `AUD-CLIPPING` thresholds `lowPerMinute` 5, `mediumPerMinute` 60, `highPerMinute` 600 added to the rule defaults before the detector | The Settings rule editor (UI-10) edits them; the detector (A-05) reads the same keys |
| 2026-11-16 | 09 / UI-09 | A Tools sub-view shows its back button and title at the top of the page content, not before the page title in the header; Create report offers `All open problems` and `Last scan` (the Issues filters are not known on the Tools page); Database `Move…` waits for a restart-safe move in phase 16 | Keeps the shell header unchanged; the report is created from the Tools page |
| 2026-11-16 | 00 / D-01, D-16; 01 / F-01; 14 / U-05, U-06 | The app is published framework-dependent (`SelfContained=false`, single file with ReadyToRun): the .NET 10 Desktop Runtime (x64) installed on the PC is a prerequisite. The installer checks for it and points to the download page; the test project no longer needs `ValidateExecutableReferencesMatchSelfContained` | Owner decision. Measured: `MediaVerifier.exe` 6.7 MB plus `e_sqlite3.dll` 2 MB, running on the installed 10.0.15 runtime; the master plan, subplans 01, 14 and 16, AGENTS.md and README are updated |
| 2026-11-16 | 10 / A-02, A-05 | The spectrum uses every non-silent frame (hop = N; the FFT costs a few percent of the decode, and the length is not known before decoding); the left channel's spectrum is kept from every fourth frame for the out-of-phase fallback. Clipping counts are per worst channel (`clip.flac`: 203 runs per channel, 406 in total as the prototype counted) | Streaming without the track length; tiers are defined per channel |
| 2026-11-16 | 10 / A-11 | Target restated: all analyzers under 500 ms for a 5-minute stereo track on one core (measured 253 ms), instead of 25 % of the FLAC decode time | FFmpeg 8.0.1 decodes FLAC at about 2,000 × real time (140–165 ms for 5 minutes); 25 % leaves about 35 ms, less than the long-term spectrum's FFT alone (`docs/perf.md`) |
| 2026-11-16 | 10 / A-12 | `COR-CLICKS`: the local level is the highest of the three 1,024-sample block medians around a sample (each from every sixteenth step); a click must step back the other way with ordinary steps on both sides. The catalog now has `COR-CLICKS` disabled by default | The median of the three took the edges of noise bursts next to silence for clicks; subplan 10 says the rule stays off until calibrated |
| 2026-11-16 | 10 / A-09 | Evidence views: the spectrogram has 256 linear bands, each the highest level of its bins; the waveform zoom draws the 20 ms window as an envelope in `Text2` with a dashed `Accent` line at the mark and the caption `{time} · 20 ms around the mark · click to go back`; for an inflated bitrate the expected knee is a second marker in `Text3` with a `Bg` label `{n} kHz expected` on the left; while a view renders its frame shows the skeleton fill; no separate Nyquist line (the top of the scale is the Nyquist frequency and is captioned) | Unspecified design cases: subplan 08 fixes the spectrogram's cutoff marker and the waveform, not these |
| 2026-11-16 | 09 / UI-10 | Rule settings with decimals (for example `0.5` seconds) are edited with their decimals; the number box takes the decimal separator when a setting has decimals | The rule editor rounded them to whole numbers, and a setting below 1 (`AUD-DC` levels) made Settings fail to open |
| 2026-11-16 | 11 / V-02 | When every candidate passes with no fall-off, the confidence is lowered by 0.15; the confidence never goes below 0.30 | Subplan 11 says "lower the confidence" without a number |
| 2026-11-16 | 11 / V-01 | Frames are sampled only when the Standard decode of the file completed without damage (otherwise the artifact records `damaged` and no quality rule runs) | Sampling a damaged file produced decoder messages the classifier does not know (corruption matrix), and quality is judged on whole files, as for audio |
| 2026-11-16 | 11 / V-04 | `VID-BLOCKY` starts with a median blur threshold of 8.0 (`blur`); `VID-BLOCKY` findings carry confidence 0.7, `VID-INFLATED` without an upscale 0.6 | Subplan 11 names a blur threshold without a value (verified 5.8 normal, 9.5 starved) and gives no confidences for these |
| 2026-11-16 | 11 / P-01 | Photos run the decode check and the down-up test as two FFmpeg processes, not one | The single-process path is an optimisation; it waits for the performance check (VP-02) |
| 2026-11-16 | 11 / P-02 | JPEG quality uses the mean ratio over the table entries not clamped at 1 or 255 (all 64 when fewer than 8 remain); a table counts as a scaled standard table when every entry is within 1.5 + 3 % of the scaled standard entry | At quality 20 most entries clamp at 255 and the plain mean misjudged the scale; measured within ±3 for qualities 20–95 |
| 2026-11-16 | 15 / V-06 | The HDR fixture is tagged with the `h264_metadata` bitstream filter | With FFmpeg 8.0.1, `-color_trc smpte2084 -color_primaries bt2020` on libx264 output left both `unknown` in the stream |
| 2026-11-16 | 12 / R-02 | A finished operation resolves the file's open findings at once (resolution `op:{id}`) instead of after a confirming rescan; undo reopens exactly those findings. A file moved to quarantine keeps its index row, marked missing | The verification step already proves the result; keeping the row lets undo restore the file's history, fingerprint and findings exactly |
| 2026-11-16 | 12 / R-02 | Every file step that already happened is rolled back when a later step fails (the original is moved back, a new file removed), including a failure while updating the index | Subplan 12 names the rollback only for the rename; fault injection at every step must leave the original in place |
| 2026-11-16 | 09 / UI-11, 12 / R-12 | Dialog A is the standard confirmation dialog with its facts as text lines (what and size, where originals go, "You can undo this"); no time estimate, no skipped-files info bar and no scan-pause check box yet. The accept dialog asks with the button `I changed this file myself` instead of a check box | Unspecified design case until the full dialog A is built; the facts and the explicit confirmation are kept |
| 2026-11-16 | 12 / R-07 | PCM equality for `ACT-SHRINK-BITDEPTH` and `ACT-REENCODE-FLAC` compares MD5 of the audio decoded to `pcm_s32le` from both files | 16-bit and padded 24-bit samples scale to the same 32-bit integers; `pcm_s16le` would hide a lost low byte in a real 24-bit file |
| 2026-11-16 | 13 / SCH-01 | Plan JSON stores `roots` as an array of root ids and leaves it out for all folders; weekdays are full names (`Sunday`); Monthly gains `weekOfMonth` (1–4, 0 for last) with one weekday, and `dayOfMonth` 0 means the last day | The template "Monthly deep scan" runs on the first Saturday, which the trigger kinds of subplan 13 could not express; a single typed array is simpler than the `"all"` string union |
| 2026-11-16 | 13 / N-03 | System notifications of headless runs use the tray-balloon channel (shown by Windows as a toast for about 8 seconds); every notification is also stored for the in-app bell. No AUMID or protocol registration yet | The WinRT toast API needs a Windows SDK target framework (about 25 MB of projections in the app folder); recorded until the interop of N-03 is written |
| 2026-11-16 | 13 / SCH-07 | Schedule page: Edit offers the depth (Quick, Standard, Deep) and the more menu Delete; New schedule offers the three templates. The full editor drawer of `schedule-edit` is not built yet | Unspecified until the editor lands; the templates cover the common plans |
| 2026-11-16 | 13 / SCH-07 | The more button of a plan row keeps the page background behind its icon | Icon buttons clear with their parent's colour, and the rows are painted by the page rather than hosted in a card control |
| 2026-11-16 | 14 / U-02 | UpdateTrust.PublicKeyPem is empty until the owner adds the real key, and verification of an empty or malformed key returns false instead of throwing | The production key must be generated by the owner (AGENTS.md); an empty key keeps the updater fail-closed in the meantime |
| 2026-11-16 | 14 / U-05 | build.ps1 builds the folder from the publish output (MediaVerifier.exe, e_sqlite3.dll, licence files) plus `ffmpeg\` from deps\ffmpeg\bin, zips it as MediaVerifier-portable.zip, and calls Get-FfmpegBundle.ps1 itself; a local build without a bundle only warns | The app is a folder (framework-dependent exe, native SQLite, FFmpeg bundle); CI fails without the bundle |
| 2026-11-16 | 14 / U-08 | Settings → Updates has a "Check now" row: an up-to-date or unverified result is a toast; a newer verified release asks, downloads with size and hash checks, and starts the silent installer | A first working path until the full card with its states lands |
| 2026-11-16 | 11 / V-07 | The Standard video pass runs at log level `info` (as the Deep pass already did) so that `volumedetect` reports the soundtrack peak; freeze and black positions are kept in the video decode artifact (version 2) | Subplan 11 asks for exactly this; the corruption matrix still finds no unclassified line |
| 2026-11-16 | 05 / E-04 | Provider artifacts serialise infinite and NaN measurements as named literals | blurdetect gives `inf` on black frames; the JSON writer refused it and the whole file ended in Error |
