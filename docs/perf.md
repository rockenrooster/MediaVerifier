# Performance measurements

Budgets are in the [master plan](../plans/00-master-plan.md#8-budgets). Each section records the
command, the machine and the numbers; re-run after changing the code it covers.

Machine: AMD Ryzen 9 5950X 16-core, 64 GB RAM, NVMe SSD (Sabrent Rocket 4.0), Windows 11 Pro 26200.

## Storage (subplan 03, task S-07)

`dotnet test -c Release --filter Category=Perf` (`QueryPerformanceTests`), 2026-11-16. A synthetic
library of 1,000,000 files in 4 roots (100 files per folder, 2 % with an open finding), SQLite 3.54.0.

| Measurement | Result | Budget |
|-------------|--------|--------|
| Generate 1,000,000 files with findings (writer, 20,000 rows per operation) | 17.8 s | under 60 s (T-10) |
| Writer upserts, `UpsertAsync` in 1,000-file operations (unit test, 200,000 files) | over 100,000 rows/s | 5,000 rows/s |
| Issues snapshot, all open findings by severity (20,110 ids) | 105 ms | under 300 ms |
| Library folder snapshot, a top-level folder's subtree by name (27,775 ids) | 172 ms | under 300 ms |
| Search snapshot, text over names and folders of all 1,000,000 files (1,300 ids) | 155 ms | under 300 ms |
| Fetch 200 rows by id | 6 ms | under 20 ms |
| Open findings per tier | 2 ms | under 100 ms |
| Whole library, 1,000,000 ids sorted by size (no view does this; recorded for reference) | 809 ms | — |

Finding: reading an `INSERT … RETURNING` row inside the writer's per-operation savepoint was about
20 times slower than `ExecuteNonQuery` plus `last_insert_rowid()`, and got slower as the batch grew
(160,000 rows: 27.4 s against 1.2 s). The writer never uses `RETURNING`.

## Audio analysis (subplan 10, tasks A-09 and A-11)

`dotnet test -c Release --filter "Category=Perf&FullyQualifiedName~AudioPerfTests"`, 2026-11-16, FFmpeg
8.0.1, fixture `long5m.flac` (5 minutes of 44.1 kHz 16-bit stereo pink noise, generated). Best of
three runs; one core.

| Measurement | Result | Target |
|-------------|--------|--------|
| FFmpeg decode of the FLAC to the WAV pipe, counted only | 140–165 ms (about 2,000 × real time) | — |
| All analyzers over the same samples (levels, bit depth, clipping, stereo, spectrum, dropouts, clicks) | 253 ms (about 1,200 × real time) | under 500 ms |
| Evidence for the drawer (decode, spectrogram, waveform, three 20 ms windows) | 350–610 ms | under 1 s |
| Three hours of stereo through the analyzers: bytes allocated after the first 10 minutes | 0 | flat |

Per analyzer (5 minutes, one run each): spectrum 107 ms, clipping 38 ms, stereo 35 ms, clicks 34 ms,
dropouts 18 ms, bit depth 17 ms, levels 12 ms.

What made the difference (712 ms before): SIMD butterflies in the FFT for stages at least one vector
wide; whole-chunk fast paths in the clipping, dropout and click analyzers (a vectorised test rules a
chunk out, and the exact per-sample code runs only where a run, a gap or a click is possible); per-lane
SIMD sums in the level analyzer; a click median from every sixteenth step. The level analyzer also
stopped keeping one value per second (it now counts positive and negative windows), which was the
only allocation that grew with the length.

Subplan 10 asked for analysis under 25 % of the FLAC decode time. FFmpeg 8.0.1 decodes FLAC at about
2,000 times real time, which leaves about 35 ms for 5 minutes, less than the FFT of the long-term
spectrum alone; the target is restated as above (deviations log).
