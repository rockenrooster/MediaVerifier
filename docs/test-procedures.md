# Test procedures

How each robustness scenario of [subplan 16](../plans/16-hardening-and-backlog.md#robustness-scenarios)
is checked (task H-03), and the manual checks that need a real machine. Every scenario must end with
a consistent database, no stray temporary files, no orphaned processes and a clear message.

Automated tests run with `dotnet test MediaVerifier.sln -c Release` (Integration tests need
`MEDIAVERIFIER_FFMPEG_DIR` or the bundle, fixtures need a full FFmpeg on `PATH`; see subplan 15).

## Robustness scenarios

| Scenario | How it is checked | Kind |
|----------|-------------------|------|
| USB or network root disappears mid-scan | `EngineRunTests.DeletedFilesGoMissingButAnOfflineRootIsLeftAlone`; procedure P-1 for a real drive | Test + manual |
| Drive returns CRC errors | `EngineUnitTests.UnreadableIsCriticalWithCodeAndOffset` (CRC error code and offset in `GEN-UNREADABLE`, `RUN-READ-ERRORS` alert) | Test |
| Data volume full | `RobustnessTests.A_full_data_volume_fails_the_run_and_the_library_stays_usable` | Test |
| Target volume full during a repair | `OperationRunnerTests.A_failure_at_any_step_leaves_the_original_and_no_stray_files` (every step, including the swap) and the free-space precondition | Test |
| Process killed during a scan | `KillTests.Twenty_kills_leave_a_consistent_library_that_a_new_scan_completes` (task T-08) | Test |
| Process killed during a repair | `OperationRunnerTests.Recovery_after_a_crash_removes_only_the_journaled_temporary_file` | Test |
| Process killed during a migration | `DatabaseTests.MigrationKeepsDataAndWritesABackup`, `DatabaseTests.UncommittedWorkIsGoneAfterACrashAndTheDatabaseStaysConsistent` | Test |
| Process killed during an update download | `UpdateDownloadTests` (a failed download leaves no file); a killed one leaves `MediaVerifier-Setup.exe.download`, which the next download overwrites and which is never started (only a verified file is moved into place) | Test + reasoning |
| Sleep or hibernate mid-scan | Procedure P-2 | Manual |
| System clock jumps | `RobustnessTests.A_clock_that_jumps_back_gives_no_negative_durations`; `SchedulingTests` (DST transitions, month ends) | Test |
| Two instances started at once | `IpcTests.MutexIsExclusiveAndReleasableFromAnyThread`, `IpcTests.SecondProcessSendsActivateAndTheFirstReceivesIt` | Test |
| Database locked by a backup tool or antivirus | `RobustnessTests.A_database_locked_by_another_program_waits_then_reports_and_is_not_damaged` | Test |
| FFmpeg removed or replaced while the app runs | Procedure P-3 | Manual |
| A file that makes FFmpeg crash | `RobustnessTests.A_crashing_ffmpeg_is_recorded_and_the_run_continues`; a stalled decoder: `CorruptionMatrixTests.A_stalled_decoder_is_a_hang_and_leaves_no_process_behind` | Test |
| 1,000,000 findings on one run | `QueryPerformanceTests.MillionFileLibraryMeetsTheQueryBudgets` (queries); procedure P-4 for the UI | Test + manual |
| Path over 260 characters, Unicode, `%`, reserved names | `PathMatrixTests`, `EngineUnitTests.PathChecks`, `EngineUnitTests.ALongPathIsFlagged` ([path matrix](path-matrix.md)) | Test |
| OneDrive online-only files | `EngineUnitTests` enumeration tests for recall-on-access attributes (skipped unless *Scan online-only cloud files* is on) | Test |

## Manual procedures

Run these on the release candidate (task H-07) with a library of a few thousand files, and note the
date and result in the release checklist.

**P-1. A removable drive disappears.** Add a USB drive as a second folder, start a Standard scan of
everything, and pull the drive while its files are being read. Expected: the scan carries on with
the other folder; the run ends with `RUN-ROOT-OFFLINE` for the drive; no file on the drive is
marked missing or damaged; the Library card shows *Not connected*. Plug it back in and scan:
everything is found again with its history.

**P-2. Sleep and hibernate.** Start a Deep scan of a video folder. Put the PC to sleep for a minute,
wake it, then hibernate and resume. Expected: the scan continues (or the file in flight times out
as stalled and the next one starts); no `COR-HANG` for a file that decodes fine on a rescan; the run
ends Completed.

**P-3. FFmpeg disappears or changes.** With the window open and no scan running, rename the app
folder's `ffmpeg\ffmpeg.exe`. Start a scan. Expected: the first job fails fast, the scan is refused
or ends with the media-engine message, and Tools → Media engine shows *Missing*; nothing is marked
damaged. Put the file back, restart the app: scanning works. Replace `ffmpeg.exe` with another build:
Tools shows *Changed* with the Medium info bar.

**P-4. A pathological library.** Generate 1,000,000 files with findings into a test database
(`QueryPerformanceTests` or `SyntheticLibrary`), point *Database location* at it and open Issues,
Library and Scans. Expected: each page shows within the budgets of subplan 09, scrolling stays
smooth, filters answer within 300 ms, and *Export* streams a report without the window freezing.

**P-5. Notifications from a headless run (subplan 13, N-03).** Create a schedule one minute ahead,
close MediaVerifier and wait. Expected: the scan runs without a window, a Windows notification
reports the result and stays in the notification centre after the process exits, and clicking it
(or *Review*) opens MediaVerifier at that run.

**P-7. Start-up, idle and size budgets (subplan 16, Performance pass).** After a reboot, start
MediaVerifier from the Start menu and read the log line `First paint … ms after process start`
(budget 500 ms); close it, start it again for the warm figure. With a library of 100,000 files and no
scan running, visit every page, then leave the window on each page for 60 seconds: Task Manager shows
0 % CPU and a private working set of at most 80 MB. Run `.\build.ps1 -NoIncrement` and check the size
of `artifacts\MediaVerifier\` (at most 200 MB) and of the installer (at most 90 MB). Record the numbers
in docs/perf.md.

**P-6. Keyboard and Narrator (subplan 16, Accessibility pass).** With the mouse unplugged, add a
folder, run a scan, open a finding, repair it, undo it, create a schedule and change a setting.
Repeat with Narrator on: page titles, list rows (name, severity, rule), buttons and state changes are
read out.
