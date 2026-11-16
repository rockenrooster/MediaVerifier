# 04 — FFmpeg toolchain

Phase 4. Depends on: phase 1. Unblocks: scan engine, all media analysis, remediation.

## Goal

A small, well-tested layer that finds FFmpeg, runs `ffmpeg.exe` and `ffprobe.exe` safely on
untrusted and possibly corrupt files, and turns their output into typed data. Nothing above this
layer starts a process or parses FFmpeg text.

Read [appendix A](appendix-a-ffmpeg-verified-behavior.md) before starting. It records what FFmpeg 8.0.1
actually does with corrupt inputs, including several cases where the obvious command reports nothing.

## The bundled build

MediaVerifier uses only the FFmpeg it ships with: FFmpeg 9.0.2, built by this repository with a
fixed set of components and no network code ([subplan 17](17-bundled-ffmpeg.md)). The user never
installs, chooses, downloads or updates FFmpeg; it is updated with the app.

- Every component the app relies on is guaranteed by the bundle's verification (subplan 17), so there
  are no optional features and no capability-dependent rules.
- A format with no decoder in the bundle is reported as *not supported* (file state `Skipped`, reason
  `UnsupportedByFfmpeg`), never as corrupt. With every decoder built in this should only happen for
  formats FFmpeg itself cannot read.

## Locating and checking the bundle

`FfmpegLocator` resolves exactly one folder:

1. If the environment variable `MEDIAVERIFIER_FFMPEG_DIR` is set (developers only, subplan 17), that
   folder, logged as a warning at startup. Its integrity is not checked.
2. Otherwise `<folder of MediaVerifier.exe>\ffmpeg\`.

`BundleIntegrity` runs on a background thread after the main window is shown and again in
`--self-check`; a scan waits for its result:

| Result | Condition | Behaviour |
|--------|-----------|-----------|
| OK | `bundle.json` present, every listed file present with the listed SHA-256 | Nothing shown |
| Changed | Every file present, at least one hash differs | Scans run. Tools → Media engine shows a Medium info bar `The media engine files were changed. Results may differ from a standard installation.` and the self-check reports it |
| Missing | `bundle.json` or any listed file missing, or `ffmpeg -version` fails | Scanning is disabled with the reason `The media engine is missing or damaged. Reinstall MediaVerifier.`; the Scan button's tooltip and a Critical info bar on the Dashboard say so, with the action `Open download page` (the GitHub releases page in the browser) |

Hashing the bundle (about 50 MB) takes well under a second and runs at below-normal priority.

## Process runner

`FfProcess` is the only code that calls `Process.Start`.

Requirements:

- Arguments only through `ProcessStartInfo.ArgumentList`. `UseShellExecute=false`,
  `CreateNoWindow=true`, working directory set to a per-job temp folder so filters can write relative
  stats files.
- Every child is assigned to a **Job Object** created with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so no
  FFmpeg process survives the app. One job per engine instance.
- Priority class `BelowNormal` by default; `Idle` in eco mode (subplan 05).
- Standard output and standard error are both redirected and **read concurrently** (two async loops).
  Reading them sequentially deadlocks when either pipe fills.
- Standard output is exposed as a raw `Stream` when the command emits binary data (PCM, images), or
  parsed as `key=value` progress lines.
- Standard error goes to a `LogCollector` that classifies lines as they arrive and keeps bounded
  memory: the first 200 and last 50 raw lines, plus counts per normalised message. A badly damaged
  file can emit millions of lines.
- Timeouts:
  - *Idle timeout*: no bytes on stdout and no progress for 60 s (configurable) kills the process.
  - *Hard timeout*: `max(120 s, 4 × media duration)` for Standard, `max(600 s, 20 × duration)` for
    Deep. Unknown duration uses `120 s + 1 s per MiB`.
  - A timeout produces a `ProcessOutcome.TimedOut` result that the detectors turn into `COR-HANG`.
- Cancellation kills the process tree and waits for exit before returning. No orphaned handles.
- The result records exit code, wall time, peak working set, and whether the run was cancelled, timed
  out or completed.

### Standard arguments

Prepended to every invocation (`-nostdin` for `ffmpeg` only: `ffprobe` rejects it as an unknown
option, verified on 8.0.1):

```
-hide_banner -nostdin -loglevel repeat+level+warning -protocol_whitelist file,pipe
```

and, for `ffmpeg` commands that decode:

```
-max_error_rate 1 -err_detect crccheck+bitstream+buffer
```

Why each one matters (all verified in appendix A):

| Argument | Reason |
|----------|--------|
| `-loglevel repeat+level+warning` | H.264 damage is reported only as a *warning* (`corrupt decoded frame`). At `-v error` a corrupted film decodes "cleanly". `level` prefixes every line with `[warning]`/`[error]`/`[fatal]`; `repeat` stops FFmpeg collapsing repeated lines |
| `-err_detect crccheck+bitstream+buffer` | Decoder CRC checks are off by default. A single flipped byte in a FLAC is silent without `crccheck` |
| `-max_error_rate 1` | Without it FFmpeg aborts with *Decode error rate exceeds maximum* and stops reporting |
| `-protocol_whitelist file,pipe` | Media containers and playlists can reference URLs and other files. The bundle contains no network code at all; the whitelist keeps the same guarantee when a developer override points at a full build |
| `-nostdin` | Prevents `ffmpeg` reading the console and hanging (`ffprobe` has no such option and does not read the console) |

Strict mode (Deep depth, optional setting) appends `+careful+compliant` to `-err_detect`. Do not use
`explode`; it stops at the first problem.

### Input-path rules

- Pass the path as its own argument after `-i`. Paths longer than 260 characters and Unicode names
  work (verified with a 393-character path on 8.0.1; re-verified on the bundle by task FFB-05).
- **Still images:** the `image2` demuxer treats `%d` and `%03d` in a file name as a sequence pattern
  and fails with *Could find no file with path*. For photos, select the demuxer explicitly from the
  sniffed signature: `-f jpeg_pipe`, `png_pipe`, `webp_pipe`, `bmp_pipe`, `tiff_pipe`, `gif`,
  `jpegxl_pipe` and so on. When the type is unknown and the name contains `%`, pass
  `-pattern_type none`. Never pass `-pattern_type` for non-image inputs: `ffmpeg` rejects it with
  *Option pattern_type not found*.

## Probe model

`ProbeProvider` runs:

```
ffprobe <standard args> -show_format -show_streams -show_error -of json -i <file>
```

and deserialises with source-generated `System.Text.Json` into:

```csharp
record ProbeResult(
    bool Opened,                    // false when ffprobe could not open the input
    string? FormatName,             // "mov,mp4,m4a,3gp,3g2,mj2", "flac", "image2", "png_pipe", ...
    int ProbeScore,                 // 100 = certain; below 25 = guess
    double? DurationSeconds,
    long? SizeBytes,
    long? BitRate,
    IReadOnlyDictionary<string, string> FormatTags,
    IReadOnlyList<ProbeStream> Streams,
    LogSummary Log);

record ProbeStream(
    int Index, string CodecType, string? CodecName, string? Profile,
    int? Width, int? Height, string? PixFmt, string? FieldOrder,
    Rational? RFrameRate, Rational? AvgFrameRate, long? NbFrames,
    int? SampleRate, int? Channels, string? SampleFmt, int? BitsPerRawSample,
    double? DurationSeconds, long? BitRate,
    string? ColorTransfer, string? ColorPrimaries, string? ColorSpace,
    bool IsAttachedPicture, bool IsDefault,
    IReadOnlyDictionary<string, string> Tags);
```

Rules:

- Every numeric field in ffprobe JSON is a string or may be absent or `"N/A"`. Parse defensively.
- ffprobe exits 0 for many broken files and prints an empty object for unopenable ones. `Opened` is
  true only when at least one stream with a codec name was returned and no file-level `[error]` line
  of the form `<name>: Invalid data found when processing input` was logged.
- A probe score below 25 with no decodable stream means *unrecognised*, even though a format name is
  present (ffprobe names an empty `.flac` file `flac`).
- `IsAttachedPicture` comes from `disposition.attached_pic`; cover art is a video stream and must not
  make an audio file look like a video.

## Log classification

`LogClassifier` turns each stderr line into a `LogSignal`.

Line shapes with `level` enabled:

```
[flac @ 000001d5ee3a8380] [error] CRC error at PTS -9223372036854775808
[vist#0:0/h264 @ 0000018f84263e00] [dec:h264 @ 0000018f845909c0] [warning] corrupt decoded frame
[error] trunc.mp4: Invalid data found when processing input
```

Parse as: zero or more `[context]` groups, one `[level]`, then the message. Strip the ` @ <hex>` part
of contexts. Normalise the message for counting by replacing digit runs with `N`. Lines that do not
match (continuation lines, hints such as *Consider increasing the value for 'analyzeduration'*) are
kept raw and classified as `Noise`.

Signal kinds:

| Kind | Meaning | Feeds |
|------|---------|-------|
| `OpenFailure` | Input could not be opened or has no usable streams | COR-CONTAINER, GEN-UNRECOGNIZED |
| `Truncation` | Data ended early | COR-TRUNCATED, COR-PARTIAL-IMAGE |
| `DecodeError` | Decoder rejected data | COR-DECODE |
| `MinorDecode` | Reference-picture bookkeeping complaints that follow real damage but also occur on cleanly cut streams | COR-DECODE at Low when no other signal is present; otherwise supporting evidence |
| `ConcealedFrame` | Decoder produced a frame it marked corrupt | COR-CONCEALED |
| `Checksum` | Embedded CRC or checksum failed | COR-CHECKSUM |
| `Timestamp` | Timestamp order or continuity problem | COR-TIMESTAMPS |
| `Unsupported` | This FFmpeg build cannot decode the stream | Skipped state, not a finding |
| `Benign` | Known harmless message | ignored |
| `Unknown` | Unmatched `[error]` or `[fatal]` line | counted as `DecodeError` with weight 0.5 and logged for corpus growth |
| `Noise` | Unmatched warning or unparsed line | ignored |

Starter pattern table (case-insensitive substring or regex on the message; first match wins). Extend
it from the log corpus in subplan 15; every pattern needs a corpus line that exercises it.

| Kind | Patterns |
|------|----------|
| OpenFailure | `moov atom not found`; `Invalid data found when processing input` on a file-level or `Error opening input` line; `EBML header parsing failed`; `Failed to find two consecutive MPEG audio frames`; `Could not find codec parameters for stream` with `unspecified size` or `unspecified sample format` |
| Truncation | `File ended prematurely`; `partial file`; `EOI missing`; `overread`; `chunk too big`; `filesize and duration do not match`; `Truncating packet of size`; `invalid new backstep`; `premature end` |
| Checksum | `CRC error`; `CRC mismatch`; `crc mismatch`; `checksum mismatch` |
| ConcealedFrame | `corrupt decoded frame` |
| DecodeError | `Invalid data found when processing input` on a decoder line; `decode_frame() failed`; `invalid residual`; `Header missing`; `error while decoding MB`; `Invalid NAL unit size`; `Error splitting the input into NAL units`; `missing picture in access unit`; `invalid band type`; `Prediction is not allowed`; `Number of bands`; `is not allocated`; `inflate returned error`; `error y=`; `error count:`; `no frame!`; `damaged`; `concealing`; `Error submitting packet to decoder`; `Decoding error` |
| MinorDecode | `mmco: unref short failure`; `co located POCs unavailable`; `reference picture missing during reorder`; `Missing reference picture`; `Increasing reorder buffer` |
| Timestamp | `Non-monotonic DTS`; `non monotonically increasing dts`; `Invalid timestamps`; `timestamp discontinuity`; `DTS .* out of order` |
| Unsupported | `Decoder .* not found`; `Unsupported codec`; `is not implemented`; `Unknown decoder` |
| Benign | `deprecated pixel format used`; `Estimating duration from bitrate`; `Guessed Channel Layout`; `detected only with low score` (handled by probe score); `Last message repeated`; `Could not find codec parameters for stream .* \(Attachment`; `Thread message queue blocking` |

`LogSummary` exposes, per kind: count, distinct normalised messages with counts (top 20), first and
last raw line, and the stream index when the context names one.

One message can mean different things by context: *Invalid data found when processing input* on a
file-level line is an open failure, on a `dec:` line a decode error. Classify on context plus message.

Truncation is decided mainly by arithmetic (decoded duration against declared duration, subplan 07),
not by log text. Messages such as *Invalid NAL unit size* appear for both a cut-off file and damage
in the middle of one, so they are decode errors here.

## Progress parsing

Commands that discard decoded output use `-progress pipe:1 -nostats -stats_period 0.5` and write media
to `-f null -`. Standard output then carries blocks of `key=value` lines ending with
`progress=continue` or `progress=end`. Parse `frame`, `out_time_us`, `dup_frames`, `drop_frames`,
`speed`. The final block gives the decoded frame count and decoded duration, which are the inputs for
truncation detection (declared 6.0 s, decoded 4.0 s on a truncated file).

## WAV pipe reader

Audio analysis decodes to 32-bit float WAV on standard output:

```
ffmpeg <standard args> <decode args> -i <file> -map 0:a:<n> -map_metadata -1 -fflags +bitexact -c:a pcm_f32le -f wav -
```

`WavPipeReader` walks RIFF chunks from a forward-only stream: accept `fmt ` with format tag 3 (IEEE
float) or `0xFFFE` (extensible, float sub-format), 32 bits per sample; skip any other chunk (FFmpeg
emits a `LIST` chunk unless `+bitexact` is set); stop at `data`. The RIFF and data sizes are
`0xFFFFFFFF` on a pipe and must be ignored. After the header the stream is interleaved little-endian
float samples until end of stream.

Why float: 32-bit float represents every integer sample up to 24 bits exactly, so one format serves
lossless bit-depth analysis and lossy decodes (which can exceed ±1.0). True 32-bit integer sources are
rare; treat their effective depth as "24 or more".

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| FF-01 | `FfmpegLocator`: bundle folder beside the executable, `MEDIAVERIFIER_FFMPEG_DIR` override | Tests with fake folders; the override is logged as a warning |
| FF-02 | `BundleIntegrity` with the OK, Changed and Missing outcomes and their UI hooks | Tests with a fake bundle: a changed DLL gives Changed and scans continue; a deleted DLL gives Missing and scans are refused with the exact message |
| FF-03 | `FfProcess` with Job Object, concurrent pipe reading, timeouts, cancellation, bounded log collection | Tests use a helper console program in the test project that sleeps, prints a set number of lines, or writes binary data (the bundle has no test-signal inputs). Cancel mid-run leaves no child process; 5 million lines keep memory flat; the idle timeout fires on the sleeping helper |
| FF-04 | Startup check that the bundle reports version `9.0.2` and its protocols are only `file` and `pipe` | Self-check fails when run against a full FFmpeg build through the override |
| FF-05 | `ProbeProvider` and JSON model | Golden-file tests over captured ffprobe JSON (healthy audio, video, photo; cover art; missing fields; `N/A` values; unopenable file) |
| FF-06 | `LogClassifier` and `LogSummary` | Every corpus line in `tests/Data/logs/` maps to the expected kind; unknown error lines are counted and logged |
| FF-07 | Progress parser | Parses a captured progress stream; tolerates a stream cut mid-block |
| FF-08 | `WavPipeReader` | Tests: plain float header, header with `LIST` chunk, extensible header (6 channels), truncated stream, non-float format rejected |
| FF-09 | Photo demuxer selection and `%` handling | Integration test: a file named `a%03d.jpg` probes and decodes |
| FF-10 | Tools → Media engine view (subplan 09): version, build, licence, location, integrity state, licence text, source link | Matches the subplan 09 table |

## Exit criteria

- Integration tests pass against the bundle fetched by `scripts/Get-FfmpegBundle.ps1`. They skip
  (never fail) when no bundle has been fetched yet and the override is not set.
- No other namespace references `System.Diagnostics.Process`.

## Pitfalls

- FFmpeg writes progress with `\n`, logs with `\r` or `\n`; split on both.
- When stdout carries binary data, never wrap it in a `StreamReader`.
- Killing a process does not kill its children unless the Job Object is used.
- `Process.PeakWorkingSet64` must be read before `Dispose`.
- Do not parse the human-readable stream banner FFmpeg prints at info level; use ffprobe JSON.
