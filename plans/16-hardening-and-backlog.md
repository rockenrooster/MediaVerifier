# 16 — Hardening, release criteria and backlog

Phase 16. Depends on: all earlier phases. Ends with v1.0.

## Goal

Prove the budgets, survive bad days (yanked drives, full disks, killed processes), pass an
accessibility and security review, and write the user-facing documentation. Then freeze v1.0. The
backlog at the end records ideas deliberately left out of v1.

## Performance pass

Measure first; change only what the numbers justify. Record results in `docs/perf.md` with the
machine description.

| Budget (from the master plan) | How to measure |
|-------------------------------|----------------|
| Installed size ≤ 200 MB, installer ≤ 90 MB | `build.ps1` output |
| Cold start ≤ 500 ms | Timestamp at process start and at first paint, after a reboot and after a warm launch |
| Idle CPU 0 % | 60 seconds on each page with no scan; no timers firing on hidden pages |
| Idle memory ≤ 80 MB at 100,000 files | Private working set after visiting every page |
| Scrolling 1,000,000 rows at 60 Hz, zero allocations per scroll step | Frame timing and allocation counters in the list test host |
| Enumeration ≥ 50,000 files/s on SSD | Synthetic tree |
| Database ≥ 5,000 file rows/s | Storage performance test |
| Fingerprint limited by storage | Compare with a raw sequential read of the same file |
| Engine overhead < 5 % of one core | Run with no-op providers |
| Scan memory ≤ 400 MB | Peak private bytes over a 1,000,000-file synthetic run |
| UI thread never blocked > 50 ms | A watchdog in debug builds logs any UI-thread stall with a stack trace |

Likely hot spots to check before guessing elsewhere: row cache churn in the virtual list, directory
interning during enumeration, JSON evidence serialisation, the log collector on very noisy files,
and transaction batch size in the writer.

## Robustness scenarios

Each must end with a consistent database, no stray temp files, no orphaned processes and a clear
message.

| Scenario | Expected behaviour |
|----------|--------------------|
| USB or network root disappears mid-scan | That root's work stops with `RUN-ROOT-OFFLINE`; no file is marked missing or damaged; other roots continue |
| Drive returns CRC errors | `GEN-UNREADABLE` per file, `RUN-READ-ERRORS` alert, no retry storm |
| Data volume full | Writer fails cleanly, run ends as `Failed` with the reason, the app stays usable |
| Target volume full during a repair | Operation fails before the swap; original untouched |
| Process killed during scan, repair, migration, update download | Recovers at next start per subplans 05, 12, 03, 14 |
| Sleep or hibernate mid-scan | Resumes or times out the in-flight file and continues |
| System clock jumps (DST, manual change) | No negative durations, schedules recompute, scrub ages clamp at zero |
| Two instances started at once | One wins; the other forwards or exits |
| Database locked by a backup tool or antivirus | Busy timeout, then a visible error; no corruption |
| FFmpeg removed or replaced while the app runs | Next job fails fast with the setup card; capability cache invalidated |
| A file that makes FFmpeg crash | Exit code recorded, `COR-DECODE` or `COR-HANG` raised, run continues |
| 1,000,000 findings on one run (pathological library) | UI stays responsive; lists page; reports stream to disk |
| Path over 260 characters, Unicode, `%`, reserved names | Scanned and repaired correctly, or skipped with a reason |
| OneDrive online-only files | Skipped by default; never downloaded without opt-in |

## Security review

- Child processes: arguments only through `ArgumentList`; `-protocol_whitelist file,pipe`; no shell.
- Untrusted inputs: media files, FFmpeg output, activation URIs, IPC messages, update manifests,
  downloaded archives. Each has a parser that validates lengths and ranges and has fuzz coverage.
- Downloads: HTTPS only, size and SHA-256 verified, archive entries confined to the target folder.
- Update trust: signature verified before any manifest value is used; private key absent from the
  repository and build logs (scan the repository history before going public with a release).
- IPC pipe ACL restricted to the current user; message size capped.
- No elevation anywhere. The app never asks for administrator rights.
- Logs contain paths but no file contents. A *Copy system report* action redacts the user name in
  paths.

## Accessibility pass

- Complete every flow in subplan 09 with the keyboard only.
- Narrator reads page titles, list rows (name, severity, rule), buttons and state changes.
- All three themes at 100 %, 150 % and 200 % scale, and with the Windows text-size setting raised.
- Contrast test green; focus visible everywhere; nothing conveyed by colour alone.

## Documentation

- `README.md`: what it is, screenshots, download, first scan in three steps, what the tiers mean,
  what it can and cannot detect (the honest table from subplan 07), privacy statement, build from
  source, release notes for maintainers.
- `docs/rules.md`: generated from the catalog (task M-07); the in-app `F1` help opens the matching
  anchor.
- `docs/detection-rates.md`, `docs/calibration/*.md`, `docs/perf.md`: generated or recorded by
  subplan 15 and this one.
- `docs/troubleshooting.md`: FFmpeg not found, scheduled scans not running, no toasts, slow scans on
  HDD, false positives and how to tune or ignore a rule.
- `THIRD-PARTY-NOTICES.md`, shipped in the app folder and shown under Settings → About → Licences:
  the .NET runtime and libraries (MIT), Microsoft.Data.Sqlite (MIT), SQLitePCLRaw (Apache-2.0),
  SQLite (public domain), System.IO.Hashing and System.Numerics.Tensors (MIT), FFmpeg (LGPL 2.1 or
  later, with where to get its source), dav1d (BSD-2-Clause), libjxl (BSD-3-Clause) and each library
  linked into it, and zlib (zlib licence). Generated from the package metadata and the bundle's
  `THIRD-PARTY.txt`, not typed by hand.

## v1.0 release criteria

1. All phase exit criteria met; status board all `Done`.
2. Budgets met and recorded.
3. Robustness table passes.
4. Corruption benchmark targets met; zero findings on healthy fixtures; calibration reports current.
5. No known data-loss bug. No open Critical or High defects.
6. Update from the previous public build to the release candidate works from inside the app.
7. Uninstall leaves nothing but the user's data folder.
8. README, rules reference and troubleshooting are current.

## Backlog (not in v1)

Ordered roughly by value. Each needs its own short plan before implementation.

| Idea | Notes |
|------|-------|
| Sidecar and embedded checksum verification (`INT-SIDECAR`) | `.sfv`, `.md5`, `.sha1`, `.sha256`, `.ffp`; also export checksum manifests per folder |
| Folder watching | `FileSystemWatcher` while the app or tray is running; Quick scan of new files as they arrive |
| Spectral-hole detector for high-quality VBR lossy sources | Time-varying emptiness in the 16–20 kHz band; closes the LAME V0 gap noted in subplan 10 |
| Visual corruption heuristic for JPEG (`COR-VISUAL`) | Block-aligned discontinuity from a mid-scan bit error; train thresholds on mutated fixtures |
| RAW support beyond structure | Embedded preview checks for more makers; optional full decode through an external tool |
| Audio checks on video soundtracks | Clipping and silence on the default track, reusing the PCM analyzers |
| Fake 10-bit and fake HDR detection | Low two bits always zero; HDR metadata versus measured luminance |
| Loudness and dynamics (`AUD-DYNAMICS`) | EBU R128 through `ebur128`; album-level view |
| Learned-upscale detection | Research item; no reliable no-reference method is assumed |
| Duplicate and near-duplicate report | Exact duplicates fall out of the fingerprint index; perceptual hashes for near-duplicates |
| Parity protection | Create and verify PAR2 sets for chosen folders through an external tool |
| Media-server integration | Read Plex or Jellyfin libraries as roots; write a collection or tag of damaged items |
| Command-line interface | A console front end over `MediaVerifier.Core` for scripting and servers |
| Email notifications | SMTP summary after scheduled runs |
| Hardware-accelerated Deep scans | Needs `d3d11va` added to the bundle (subplan 17) and evidence, per decoder, that errors are still reported |
| ARM64 build | Requires an ARM64 bundle (BtbN publishes `winarm64` images) and testing |
| Localisation | Strings are already centralised; needs translation workflow |
| Lossless trailing-data trim, MP3 frame repair | Small repair tools for Low findings |
| Portable mode | Keep settings and database beside the exe when a marker file is present |

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| H-01 | Performance suite run and `docs/perf.md` | Every budget met or a recorded, owner-approved exception |
| H-02 | UI-thread stall watchdog (debug builds) and fixes for anything it finds | No stall over 50 ms in the flows of subplan 09 |
| H-03 | Robustness table, one test or scripted manual procedure per row | All rows pass; procedures saved in `docs/test-procedures.md` |
| H-04 | Security review checklist with findings fixed | Checklist committed with dates |
| H-05 | Accessibility pass with fixes | Flows completed by keyboard and with Narrator |
| H-06 | Documentation set | Files listed above exist and are accurate |
| H-07 | Release candidate, in-app update test, uninstall test | Criteria 6 and 7 demonstrated |
| H-08 | v1.0 tag and release notes | Published through the normal release flow |
