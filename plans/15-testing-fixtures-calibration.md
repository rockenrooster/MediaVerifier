# 15 — Testing, fixtures and calibration

Continuous. Starts in phase 1 and grows with every phase.

## Goal

Every claim the product makes is backed by a test that would fail if the claim stopped being true:
that damage is detected, that healthy files are not flagged, that thresholds sit where the evidence
says they should, and that repairs do not lose data.

## Test layers

| Layer | Needs | Runs | Purpose |
|-------|-------|------|---------|
| Unit | Nothing external | Always, CI on every push | Detectors over hand-built artifacts, parsers, classifiers, reconciliation and decision tables, scheduling arithmetic, XML and JSON builders, policy tables, storage against temp databases |
| Integration | FFmpeg | Locally and in CI when FFmpeg is available; **skipped, never failed,** when it is not | Providers against generated fixtures; end-to-end runs over a temp library |
| System | Windows services (Task Scheduler, toasts, shell) | Locally; optional in CI | Task registration, per-user registry entries, IPC between processes |
| Performance | Time and a large synthetic library | On demand; results recorded | Budgets from the master plan |
| Visual | The screen harness | After every UI task, by eye | Layout, states, contrast |

Conventions:

- xUnit. Traits: `Category=Integration`, `Category=System`, `Category=Perf`. The default `dotnet test`
  run excludes `Perf`.
- `FfmpegFactAttribute` (derives from `FactAttribute`) sets `Skip` with a clear message when the
  locator finds no usable FFmpeg, so integration tests need no extra package to skip dynamically.
- Tests never touch the user's real profile: `AppPaths` is pointed at a per-test temp directory.
- Time is injected (`IClock`); no test sleeps to wait for a schedule.
- No media files are committed to the repository. Fixtures are generated; parsers are also tested
  with byte arrays built in code.

## Fixture generator

`FixtureFactory` (test project) creates media with FFmpeg's built-in sources into a cache directory
keyed by generator version and FFmpeg version, so the cost (about 20 seconds) is paid once per
machine. All recipes below were run on FFmpeg 8.0.1. `FF` = `ffmpeg -hide_banner -nostdin -v error -y`.

### Audio

```
FF -f lavfi -i "anoisesrc=d=20:c=pink:r=44100:a=0.5:seed=7" -ac 2 -sample_fmt s16 src16.flac
FF -i src16.flac -c:a libmp3lame -b:a 128k m128.mp3
FF -i src16.flac -c:a libmp3lame -b:a 320k m320.mp3
FF -i src16.flac -c:a libmp3lame -q:a 0   mV0.mp3
FF -i src16.flac -c:a aac -b:a 128k       a128.m4a
FF -i src16.flac -c:a libopus -b:a 128k   o128.opus
FF -i m128.mp3  -sample_fmt s16 fake128.flac          # likewise fake320, fakeV0, fakeAac, fakeOpus
FF -i m128.mp3  -c:a libmp3lame -b:a 320k infl320.mp3 # inflated bitrate
FF -i src16.flac -ar 96000 -sample_fmt s32 up96.flac  # upsampled
FF -i src16.flac -sample_fmt s32 pad24.flac           # padded 24-bit
FF -f lavfi -i "anoisesrc=d=20:c=pink:r=96000:a=0.5:seed=9" -ac 2 -sample_fmt s32 true96.flac
FF -i src16.flac -af "volume=14dB" -sample_fmt s16 clip.flac
FF -i clip.flac  -af "volume=-3dB" -sample_fmt s16 clipatt.flac
FF -i clip.flac  -c:a libmp3lame -b:a 320k clip320.mp3
FF -i src16.flac -af "lowpass=f=9000:poles=1" -sample_fmt s16 natural.flac
FF -i src16.flac -af "highshelf=f=6000:g=-30,lowpass=f=15000:poles=2" -sample_fmt s16 dull.flac
```

Add as the audio rules arrive: sine tones (`sine=f=30`, `f=40` with clipping) for the clipping edge
test, a stereo file with one silent channel, an inverted-phase file (`aeval=val(0)|-val(0)`), a file
with a 588-sample zero gap spliced in, long leading silence (`adelay`), a DC-offset file (`dcshift`).

### Video

```
ENC="-c:v libx264 -crf 20 -preset veryfast -g 48"
FF -f lavfi -i "mandelbrot=size=1920x1080:rate=24" -t 6 -vf "noise=c0s=6:c0f=t+u,format=yuv420p" $ENC native1080.mp4
FF -f lavfi -i "mandelbrot=size=1280x720:rate=24"  -t 6 -vf "noise=c0s=6:c0f=t+u,scale=1920:1080:flags=lanczos,format=yuv420p" $ENC up720.mp4
FF -f lavfi -i "mandelbrot=size=960x540:rate=24"   -t 6 -vf "noise=c0s=6:c0f=t+u,scale=1920:1080:flags=bicubic,format=yuv420p" $ENC up540.mp4
FF -i native1080.mp4 -c copy -movflags +faststart fs.mp4          # index at the front
FF -i fs.mp4 -c copy t.mkv                                         # Matroska
FF -i fs.mp4 -i a128.m4a -map 0:v:0 -map 1:a:0 -c copy -shortest -movflags +faststart av.mp4
FF -i native1080.mp4 -c:v libx264 -b:v 300k -preset veryfast starved.mp4
```

The fractal source has detail at every scale, so a version rendered small and enlarged genuinely
lacks detail. That makes it a fair stand-in for the upscale tests, but thresholds still need real
footage (see Calibration).

### Photos

```
FF -ss 2 -i native1080.mp4 -frames:v 1 -q:v 3 img.jpg
FF -ss 2 -i native1080.mp4 -frames:v 1 img.png
FF -ss 2 -i native1080.mp4 -frames:v 1 -c:v libwebp -q:v 80 img.webp
```

Plus enlarged copies for `IMG-UPSCALED`, JPEGs at qualities 20–95 for the quality estimator, and files
named `a%03d.jpg`, `100% real.jpg`, a 300-character path and a non-ASCII name for path handling.

If an encoder is missing from the local FFmpeg build, the fixtures that need it are skipped along
with their tests.

## Corruption mutators

`Mutate` copies a fixture and damages the copy deterministically (seeded):

| Mutator | Effect |
|---------|--------|
| `Truncate(fraction)` | Keep the first fraction of the file |
| `FlipByte(position)` | XOR one byte |
| `ZeroBlock(position, 4096)` | Overwrite with zeros, 4 KiB aligned |
| `RandomBlock(position, n)` | Overwrite with random bytes |
| `PreserveStat()` | Restore size-neutral mutations' modified time afterwards, to simulate rot |

Positions are fractions of the file length, drawn from 0.30–0.70 by default so that headers are not
hit unless a test asks for it.

## Corruption matrix

The expected result for one mutation at mid-file, taken from the verified experiments in appendix A.
"—" means that depth cannot see it. The **Fingerprint** column assumes a baseline existed and the
modified time was preserved.

| Fixture | Mutation | Quick | Standard | Deep | Fingerprint |
|---------|----------|-------|----------|------|-------------|
| FLAC | Flip one byte | — | `COR-CHECKSUM` | same | `INT-ROT` |
| FLAC | Zero 4 KiB | — | `COR-DECODE` (+ checksum) | same | `INT-ROT` |
| FLAC | Truncate 60 % | — | `COR-TRUNCATED` | same | (size changed: Modified) |
| MP3 | Random 3,000 bytes | — | `COR-DECODE` | same | `INT-ROT` |
| MP3 | Truncate 55 % | `COR-TRUNCATED` (M2 header check) | `COR-TRUNCATED` (11.0 of 20.0 s) | same | (Modified) |
| M4A (AAC) | Random 1,500 bytes | — | `COR-DECODE` | same | `INT-ROT` |
| Opus | Flip two bytes | — | `COR-CHECKSUM` | same | `INT-ROT` |
| MP4, index at end | Truncate 60 % | `COR-CONTAINER` | same | same | (Modified) |
| MP4, index at front | Truncate 60 % | `COR-TRUNCATED` | `COR-TRUNCATED` (4.0 of 6.0 s) | same | (Modified) |
| MP4 with audio | Random 2,000 bytes | — | `COR-DECODE` | + `COR-CONCEALED` | `INT-ROT` |
| MP4, video only | Random 2,000 bytes | — | may be missed (non-key frames) | `COR-CONCEALED` | `INT-ROT` |
| MP4, video only | Flip one byte | — | may be missed | `COR-CONCEALED` | `INT-ROT` |
| MKV | Truncate 60 % | `COR-TRUNCATED` when the segment size is known | `COR-TRUNCATED` | same | (Modified) |
| JPEG | Truncate 60 % | `COR-TRUNCATED` | `COR-PARTIAL-IMAGE` | same | (Modified) |
| JPEG | Zero 4 KiB | — | `COR-PARTIAL-IMAGE` | same | `INT-ROT` |
| JPEG | Flip one byte | — | **not detected** | not detected | `INT-ROT` |
| PNG | Flip one byte | — | `COR-CHECKSUM` | same | `INT-ROT` |
| PNG | Truncate 60 % | `COR-TRUNCATED` | `COR-DECODE` | same | (Modified) |
| WebP (lossy) | Flip three bytes | — | **not detected** | not detected | `INT-ROT` |
| WebP | Truncate 60 % | `COR-TRUNCATED` | `COR-CONTAINER` | same | (Modified) |

### Detection-rate benchmark

A single position proves little. `CorruptionBenchmark` repeats each cell over 50 seeded positions and
reports the detection rate per depth. Targets:

- Truncation: 100 % at Standard for every format; 100 % at Quick for formats with a declared length.
- Checksummed formats (FLAC, PNG, Ogg): 100 % for any mutation at Standard.
- Fingerprint path: 100 % for every format and mutation.
- Healthy fixtures: 0 findings at every depth, across the whole fixture set.
- All other cells: **measure, record in `docs/detection-rates.md`, and assert no regression** beyond
  five points. Do not invent targets for cells that have not been measured.

The benchmark is `Category=Perf` (slow) and its report is committed whenever detector or provider
logic changes.

## Classifier log corpus

`tests/MediaVerifier.Tests/Data/logs/` holds captured FFmpeg stderr as `.txt` files (`*.log` is
ignored by git), one file per scenario, with a sidecar listing the expected signal kind per line. Seed it with every sample in appendix A. Rules:

- Every pattern in the classifier table has at least one corpus line.
- Any `Unknown` line seen while running integration tests fails the test until it is added to the
  corpus and classified. This is how the pattern table grows safely.
- When supporting a new FFmpeg version, regenerate the corpus with it and diff.

## Calibration

Thresholds tagged *calibrate* in subplans 10 and 11 start from synthetic measurements. Before each
detector milestone ships:

1. **Corpus.** (a) The generated fixtures. (b) Freely licensed films available at several native
   resolutions (Big Buck Bunny, Sintel, Tears of Steel), downloaded by the calibration tool into a
   cache, never committed. (c) Optionally, a folder of the owner's own legitimately obtained files,
   passed by the `MEDIAVERIFIER_CALIBRATION_DIR` environment variable, each labelled by a small
   manifest (`native`, `upscaled-from-720`, `lossless`, `from-mp3-128`, and so on).
2. **Variants.** From each trusted original produce labelled variants: lossy round trips at several
   bitrates and codecs, upsamples, padded depths, upscales from each lower rung with bilinear, bicubic
   and Lanczos, encodes down a bitrate ladder, clipped versions at several gains.
3. **Measure.** Run the providers; write one row of features per file to CSV.
4. **Choose.** For each threshold, report the distributions of positives and negatives, the overlap,
   and the threshold that gives zero false positives on the native set with the best recall. Where the
   classes overlap, prefer fewer false positives and lower the rule's confidence instead of its
   threshold.
5. **Record.** Commit `docs/calibration/<area>.md` with the method, the corpus description, the
   numbers and the chosen values. Update `RuleDefaults` to match, and bump the rule version.

The calibration tool lives in the test project (or a small `tools/Calibrate` console) and reuses the
product's providers; it contains no detection logic of its own.

## Robustness tests

- **Parser fuzzing.** Each structural walker and the WAV, progress and JSON parsers are fed 10,000
  random buffers and 10,000 mutated real headers per run. They must return, never throw an unhandled
  exception, and never read out of bounds or loop (time-box each call).
- **Kill tests.** A helper process runs a scan over a temp library and is killed at random moments;
  the suite then opens the database and checks invariants (no orphan findings, counters consistent,
  run marked interrupted, resume completes).
- **Operation fault injection.** `IMediaFileSystem` fakes fail at each step of the safe-operation
  protocol (subplan 12).
- **Path matrix.** Unicode, emoji, 300-character paths, `%` in names, trailing spaces in folder names,
  read-only files, files locked by another process.

## Continuous integration

- `ci.yml`: build (warnings are errors), unit tests, self-check.
- Integration job: obtain FFmpeg through the app's own pinned-download code once it exists (task
  FF-04), cached with `actions/cache` keyed by the catalog hash. Until then use the runner's package
  manager.
- The release workflow runs unit tests before building.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| T-01 | Test project conventions: traits, `FfmpegFactAttribute`, temp `AppPaths`, `IClock` | A sample integration test skips cleanly on a machine without FFmpeg |
| T-02 | `FixtureFactory` with the audio, video and photo recipes and a versioned cache | Second run takes under a second; missing encoders skip only dependent fixtures |
| T-03 | `Mutate` with the five mutators | Deterministic for a given seed |
| T-04 | Matrix tests for the single-position table | Every cell behaves as listed, including the "not detected" cells (so a future improvement is noticed and the table updated) |
| T-05 | `CorruptionBenchmark` and `docs/detection-rates.md` | Report generated; targets asserted |
| T-06 | Classifier corpus seeded from appendix A, with the unknown-line gate | Corpus tests pass; an unclassified error line fails integration tests |
| T-07 | Parser fuzz harness | Runs in CI within 60 seconds |
| T-08 | Kill-test harness | 20 random kills, invariants hold |
| T-09 | Calibration tool and first reports for audio, video and photo | Reports committed; `RuleDefaults` updated |
| T-10 | Synthetic library generator shared with storage and UI (task S-10) | Deterministic for a seed; 1,000,000 files in under 60 seconds |
| T-11 | Performance suite recording the budget metrics to `docs/perf.md` | One command reproduces the table |
| T-12 | Path matrix tests | All pass on NTFS; documented exceptions for FAT/exFAT |

## Exit criteria

- CI is green and fast (unit suite under two minutes).
- The matrix, benchmark and calibration reports exist and are current with the code.
- No test depends on files outside the repository or on network access, except the explicit
  calibration download and the pinned FFmpeg download.
