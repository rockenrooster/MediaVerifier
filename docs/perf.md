# Performance measurements

Budgets are in the [master plan](../plans/00-master-plan.md#8-budgets). Each section records the
command, the machine and the numbers; re-run after changing the code it covers.

The whole suite runs with one command, `.\scripts\Measure-Performance.ps1` (tasks T-11 and H-01),
which writes the current table to [perf-results.md](perf-results.md). The window budgets (cold start,
idle CPU and memory, installed size) are measured by hand on the release candidate
([test procedures](test-procedures.md), P-7).

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

## Frame sampling (subplan 11, task VP-02)

`dotnet test -c Release --filter "Category=Perf&FullyQualifiedName~FrameSamplingPerfTests"`, 2026-11-17,
FFmpeg 8.0.1. The same FFmpeg command the frame-sample provider builds; best of three.

| Measurement | Before | After | Target |
|-------------|--------|-------|--------|
| 2160p film, 24 samples (measured on 10 samples of `uhd10m.mp4`, scaled) | 5.8 s | 3.3 s | under 5 s |
| 3840 × 2160 photo (`img2x.jpg`) | 371 ms | 231 ms | under 0.3 s |

Profile of the 2160p run before the change (10 samples): seeking and decoding the key frames about
1,050 ms, the three down-up SSIM branches about 100 ms, and `siti`, `blockdetect` and `blurdetect` on
the whole 2304 × 1296 crop about 1,070 ms. The measures now run on the centre quarter of the crop
(its corner on the frame's 16-pixel grid, so block edges keep their phase), and photos measure only
spatial information (blockiness and blur are video rules).

## Engine budgets (subplan 16, task H-01)

`EnginePerfTests` (in the suite above), 2026-11-17, on 100,000 empty photo files in a synthetic tree.

| Measurement | Result | Budget |
|-------------|--------|--------|
| Enumeration | about 600,000 files/s | at least 50,000 files/s on SSD |
| Fingerprint, 1 GiB from the file cache | 6.9 GB/s against a raw read of 7.8 GB/s | limited by storage |
| Engine overhead with no-op providers | about 450 µs of CPU a file: under 1 % of a core at 20 analysed files a second | under 5 % of one core |
| Scan memory, peak private bytes above the start | 62 MB at 100,000 files and 62 MB at 300,000 | at most 400 MB at 1,000,000 files |
| UI thread on the demo library, every page twice | longest block 28 ms | never over 50 ms |

Scan memory does not grow with the number of files (the engine streams directories and writes in
batches), so the 1,000,000-file budget is met by a wide margin without creating a million files.

