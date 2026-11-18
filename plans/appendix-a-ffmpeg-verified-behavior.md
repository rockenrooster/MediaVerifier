# Appendix A — FFmpeg behaviour verified by experiment

Everything in this appendix was run, not assumed. First run: Windows 11, FFmpeg 8.0.1
(`ffmpeg version 8.0.1-full_build-www.gyan.dev`), November 2026, fixtures generated with the recipes in
subplan 15 and damaged with `dd`. Sections 2–4 and 7–12 were repeated on 2026-11-17 by
`AppendixAMeasurements` (task FFB-05) with the bundle the app ships, **`ffmpeg-bundle-9.0.2-mv2`**
(`ffmpeg version 9.0.2-mediaverifier-2`), and with the 8.0.1 build on the same fixtures, on a GitHub
Windows runner (Windows Server 2025); the damage came from the `Mutate` helpers. Where 9.0.2 behaves
differently, the text says so; everything else held on both. Hex addresses in log lines are replaced
by `X`.

x264's output depends on the machine (its thread count follows the core count), so the video fixtures,
and the images taken from them, differ in their bytes between machines, and the same damage can land in
different data. Compare FFmpeg versions on the same fixtures, as the measurement does.

Re-run these experiments when changing the supported FFmpeg version, and update this file. The test
`AppendixAMeasurements` repeats sections 2–4 and 7–12 and writes what FFmpeg printed to a report:
run CI by hand with **appendix** ticked (the report is printed in the log and kept as an artifact),
or set `MEDIAVERIFIER_APPENDIX_REPORT` to a file path and run that test locally.

## 1. Exit codes cannot be trusted

| Case | Tool | Exit code |
|------|------|-----------|
| FLAC with one corrupted byte | `ffmpeg ... -f null -` | 0 |
| FLAC with 4 KiB zeroed (decoder errors printed) | `ffmpeg` | 0 |
| H.264 with 2,000 random bytes mid-file | `ffmpeg` | 0 |
| Truncated JPEG, corrupted PNG, truncated WebP | `ffprobe -count_frames` | 0 |
| Text file named `.mp3` | `ffprobe` | 0 (prints `{}` and an error line) |
| MP4 without `moov` | `ffprobe` | 1 |
| MP4 without `moov` | `ffmpeg` | 183 |
| Any decode error with `-xerror` and `-err_detect ...+explode` | `ffmpeg` | 183 |

Conclusion: classify the log and compare decoded length with declared length. Never branch on the
exit code alone.

## 2. Log level and error-detection flags decide what is visible

### FLAC, one byte changed mid-file

```
ffmpeg -v error -i bad1.flac -f null -                          -> no output, exit 0
ffmpeg -v error -err_detect crccheck -i bad1.flac -f null -     -> [flac @ X] CRC error at PTS -9223372036854775808
```

`ffmpeg -h full` confirms the defaults: decoder `err_detect` default is `0`; demuxer `err_detect`
default is `crccheck`. The same on 9.0.2.

### H.264, 2,000 random bytes at 50 % (video-only MP4)

```
ffmpeg -v error ... -i badmid.mp4 -map 0:v:0 -f null -                     -> no output, exit 0
ffmpeg -v repeat+level+warning ... -i badmid.mp4 -map 0:v:0 -f null -
    [vist#0:0/h264 @ X] [dec:h264 @ X] [warning] corrupt decoded frame
```

The same on 9.0.2. A single flipped byte at 30 % gave the same single warning on 8.0.1; on 9.0.2 the
same damaged file printed nothing at all. Over 50 positions, though, the corruption benchmark finds a
single flipped byte in H.264 as often on 9.0.2 as on 8.0.1 with the same fixtures (6 % at Standard,
100 % at Deep). With `-skip_frame nokey` neither case printed anything on either version: the damage
was in non-key frames.

### PNG, one byte changed

Without `-max_error_rate 1`:

```
[png @ X] [error] inflate returned error -3
[vist#0:0/png @ X] [dec:png @ X] [fatal] Decode error rate 1 exceeds maximum 0.666667
```

The run ends with exit code 69. With `-max_error_rate 1 -err_detect crccheck+bitstream+buffer`: the
errors are listed (`CRC mismatch in chunk, skipping`, `inflate returned error -3`,
`Decoding error: Generic error in an external library`) and FFmpeg carries on, exit 0. The same on
9.0.2.

## 3. Line format with `-loglevel repeat+level+warning`

```
[flac @ X] [error] CRC error at PTS -9223372036854775808
[vist#0:0/h264 @ X] [dec:h264 @ X] [warning] corrupt decoded frame
[aist#0:1/aac @ X] [dec:aac @ X] [error] Error submitting packet to decoder: Invalid data found when processing input
[mov,mp4,m4a,3gp,3g2,mj2 @ X] [error] moov atom not found
[error] trunc_nomoov.mp4: Invalid data found when processing input          (ffprobe, file level)
[in#0 @ X] [error] Error opening input: Invalid data found when processing input   (ffmpeg)
[error] Error opening input file trunc_nomoov.mp4.
[fatal] Error opening input files: Invalid data found when processing input
```

FFmpeg 9.0.2's `ffmpeg` logs demuxer messages under the input: `[in#0 @ X]` while it opens the file and
`[in#0/<demuxer> @ X]` once the demuxer runs. ffprobe still names the demuxer:

```
[in#0 @ X] [error] moov atom not found                          (ffmpeg 9.0.2; ffprobe: [mov,mp4,m4a,3gp,3g2,mj2 @ X])
[in#0/ogg @ X] [error] CRC mismatch!                            (ffmpeg 9.0.2; 8.0.1: [ogg @ X])
[in#0/matroska,webm @ X] [error] File ended prematurely         (ffmpeg 9.0.2; 8.0.1: [matroska,webm @ X])
```

Filters log as `[Parsed_<filter>_<n> @ X]` on 9.0.2, including `freezedetect` and `blackdetect`, which
8.0.1 logged as `[freezedetect @ X]` and `[blackdetect @ X]`. The classifier matches on the message, not
on these prefixes; the corpus has the 9.0.2 forms (`ffmpeg9-input-contexts`). On Windows, text on stdout
ends its lines with CRLF on both versions; the parsers split on either.

## 4. Messages per format and damage

| Format | Damage | Messages (normalised) |
|--------|--------|-----------------------|
| FLAC | One byte | `CRC error at PTS N` (needs `crccheck`) |
| FLAC | 4 KiB zeros | `invalid residual` or `qlevel N not supported, maybe buggy stream`, depending on where the zeros land; `decode_frame() failed`; `Decoding error: Invalid data found when processing input` |
| MP3 | Truncated at 55 % | `[mp3] [warning] filesize and duration do not match (growing file?)`; `[mp3float] [error] invalid new backstep -N` (the 2026-11-17 cut printed only the first). ffprobe still reports 20.0 s; decode reaches 10.998617 s on both versions |
| MP3 | 3,000 random bytes | `Header missing` ×3; `Error submitting packet to decoder: Invalid data found when processing input` ×3; also `bits_left=N` (counts depend on where the bytes land). The `bits_left` lines come from `-err_detect buffer`, which the app no longer passes: with it, healthy LAME CBR files print one per granule with stuffing bits (464 to 10,272 per song on the owner's library, 2026-11-17) and those granules decode as silence. Without it the damaged copies still print `Header missing` and `Error submitting packet` |
| AAC in M4A | 1,500 random bytes | `Prediction is not allowed in AAC-LC.`; `invalid band type`; `channel element N.N is not allocated`; `Number of bands (N) exceeds limit (N).`; `Reserved bit set.`; `Error submitting packet to decoder` ×5. Final `out_time`: 20.015601 s on 8.0.1, 20.000000 s on 9.0.2 |
| Opus in Ogg | Two bytes | `[ogg] [error] CRC mismatch!` ×2, ×4 in the 2026-11-17 run (demuxer checks page CRC by default) |
| MP4, index at end | Truncated at 60 % | `moov atom not found`; cannot open |
| MP4, index at front | Truncated at 60 % | `Invalid NAL unit size (N > N).`; `missing picture in access unit with size N`; `Error splitting the input into NAL units.`; `Decoding error: Invalid data found when processing input`. ffprobe still reports 6.0 s and 144 frames; decode stops at frame 96, `out_time` 4.0 s |
| MP4 with AAC audio | 2,000 random bytes at 50 % | Standard pass (key frames + audio): `Invalid NAL unit size` ×2, `missing picture in access unit`, `Error splitting the input into NAL units.`, decoder errors on both the video and the audio stream. Deep pass adds `corrupt decoded frame` and `mmco: unref short failure`; 143 of 144 frames decoded. On the 2026-11-17 fixtures the bytes hit only non-key video frames: Standard printed nothing and Deep only `corrupt decoded frame`, 144 of 144 frames, on both versions |
| MKV | Truncated at 60 % | `[matroska,webm] [error] File ended prematurely`; ffprobe still reports 6.0 s; decode stops at frame 96 |
| JPEG | Truncated at 60 % | `[mjpeg] [warning] EOI missing, emulating` (×2 on 8.0.1, once on 9.0.2); `[mjpeg] [error] overread N` |
| JPEG | 4 KiB zeros | 8.0.1: `[mjpeg] [error] error y=N x=N` with `error count: N` or `error dc` (after `[warning] mjpeg_decode_dc: bad vlc`). 9.0.2 on the same file: only `[mjpeg] [error] overread N` |
| JPEG | One byte | **nothing** |
| PNG | One byte | `CRC mismatch in chunk, skipping`; `inflate returned error -N`; `nb_read_frames` is `N/A` |
| PNG | Truncated | `chunk too big` ×2 |
| WebP (lossy) | Three bytes | **nothing** |
| WebP | Truncated | `Could not find codec parameters for stream N (Video: webp, none): unspecified size` |
| Text named `.mp3` | — | `Format mp3 detected only with low score of 1, misdetection possible!`; `Failed to find two consecutive MPEG audio frames.` |
| Empty `.flac` | — | `Format flac detected only with low score of 1`; `Could not find codec parameters for stream 0 (Audio: flac, 0 channels): unspecified sample format`; ffprobe still returns `format_name: flac` |

The corruption benchmark (every cell of the corruption matrix at 50 positions, `docs/detection-rates.md`)
gave the same detection rates on 9.0.2 as on 8.0.1 with the same fixtures in every cell but one: a JPEG
with one flipped byte, 30 % against 32 % (one position in 50). Every target of the benchmark held on both.

## 5. FLAC audio MD5

STREAMINFO's MD5 sits at byte offset 26 of a FLAC file whose STREAMINFO block is first. It equals the
MD5 of the decoded samples packed as signed little-endian integers of `ceil(bits/8)` bytes:

```
xxd -s 26 -l 16 -p src16.flac                               -> 7115eda617cbf33bc6c35f2e11ceadb6
ffmpeg -i src16.flac -map 0:a:0 -c:a pcm_s16le -f md5 -     -> MD5=7115eda617cbf33bc6c35f2e11ceadb6
xxd -s 26 -l 16 -p pad24.flac                               -> 205467d3b97a4b4a8630c45c2ea17a93
ffmpeg -i pad24.flac -map 0:a:0 -c:a pcm_s24le -f md5 -     -> MD5=205467d3b97a4b4a8630c45c2ea17a93
```

After one corrupted byte the decoded MD5 changes (`5c4de07b...`), so the comparison catches damage
even with CRC checking off.

## 6. Stream hash ignores tags and container

```
ffmpeg -i m320.mp3  -map 0:a:0 -c copy -f streamhash -hash md5 -   -> 0,a,MD5=218d8bdbaa101df51f07a726ac401ee5
ffmpeg -i retag.mp3 -map 0:a:0 -c copy -f streamhash -hash md5 -   -> 0,a,MD5=218d8bdbaa101df51f07a726ac401ee5
```

`retag.mp3` is the same audio with different tags; its file hash differs. The same holds for FLAC.
The video stream hash of `fs.mp4` equals that of its MKV remux `t.mkv`
(`0,v,MD5=6d9893e3b31a837969f9a2cc8c5d52e2`). Damaging the payload changes the stream hash.

## 7. Combined Standard video pass

```
ffmpeg -hide_banner -nostdin -loglevel repeat+level+warning -protocol_whitelist file,pipe
       -max_error_rate 1 -err_detect crccheck+bitstream+buffer -threads 4 -skip_frame nokey -i av.mp4
       -map 0:v:0 -map 0:a? -f null -
       -map 0:v:0 -map 0:a:0? -c copy -f streamhash -hash md5 streamhash.txt
       -progress pipe:1 -nostats
```

Healthy 6-second 1080p file with AAC: about 200 ms, no stderr, final progress block
`frame=4 ... out_time_us=5916667 ... progress=end` on 8.0.1 and `out_time_us=6000000` on 9.0.2, and
`streamhash.txt` contains `0,v,MD5=...` and `1,a,MD5=...`. On 9.0.2 the final `out_time` reaches the end
of the stream: 6.000000 s for this file in both the key-frame pass and a full decode (8.0.1: 5.916667 s
and 5.990748 s).

Progress block shape (one `key=value` per line):
`frame=144 fps=0.00 stream_0_0_q=-0.0 bitrate=N/A total_size=N/A out_time_us=6000000 out_time_ms=6000000 out_time=00:00:06.000000 dup_frames=0 drop_frames=0 speed=48.2x progress=end`

## 8. Audio through a WAV pipe

```
ffmpeg ... -i pad24.flac -map 0:a:0 -c:a pcm_f32le -f wav -
```

First bytes: `RIFF ffffffff WAVE fmt  12000000 0300 0200 44ac0000 ...` then a `LIST` chunk
(`INFO ISFT Lavf62.3.100`; `Lavf63.1.102` on 9.0.2, the same size), then `data ffffffff`, then samples. Format tag 3 is IEEE float; the RIFF
and data sizes are `0xFFFFFFFF` on a pipe. `-map_metadata -1 -fflags +bitexact` removes the `LIST`
chunk, but the reader must skip unknown chunks regardless.

## 9. ffprobe JSON

Top-level format keys: `bit_rate, duration, filename, format_long_name, format_name, nb_programs, nb_stream_groups, nb_streams, probe_score, size, start_time, tags`. Video stream keys include
`avg_frame_rate, bit_rate, bits_per_raw_sample, codec_name, codec_type, coded_height, coded_width, disposition, duration, field_order, height, nb_frames, pix_fmt, profile, r_frame_rate, width`. All
numbers arrive as strings. 9.0.2 adds `mime_codec_string` and no longer writes `refs`; MediaVerifier
reads neither.

A 24-bit FLAC reports `"sample_fmt":"s32"`, `"bits_per_sample":0`, `"bits_per_raw_sample":"24"`.
An MP3 reports `probe_score` 51 (52 on 9.0.2); that is normal.

## 10. Still-image demuxers and awkward names

| Extension | Demuxer chosen |
|-----------|----------------|
| `.jpg` | `image2` |
| `.png` | `png_pipe` |
| `.webp` | `webp_pipe` |

A PNG renamed `.jpg` is identified correctly (`png`, `png_pipe`).

| File name | Plain `ffprobe` | With `-pattern_type none` |
|-----------|-----------------|---------------------------|
| `a%b.jpg`, `100% real.jpg` | Works | Works |
| `a%d.jpg`, `a%03d.jpg`, `x%d.png` | **Fails** on 8.0.1: `Could find no file with path 'a%d.jpg' and index in the range 0-4`. Works on 9.0.2 | Works |

`-pattern_type none` before a non-image input: `ffprobe` tolerates it; `ffmpeg` fails with
`Option pattern_type not found.` (exit 8). Forcing the demuxer (`-f jpeg_pipe`, or
`-f image2 -pattern_type none`) works for the `%d` names.

A path of about 400 characters containing accented Latin, Japanese and symbol characters probes
correctly on both versions. MediaVerifier still names the demuxer for photos (subplan 05), so it does not
depend on the 9.0.2 behaviour above.

`-protocol_whitelist file,pipe` works for file input and pipe output.

## 11. Quality filters

| Filter | Where the result appears |
|--------|--------------------------|
| `ssim=stats_file=NAME` | One line per frame in the file: `n:1 Y:0.994236 U:0.968169 V:0.966805 All:0.985319 (18.332549)`. Summary line at info level: `[Parsed_ssim_N @ X] SSIM Y:0.994183 (22.353202) U:... All:...` |
| `siti`, `blockdetect`, `blurdetect` + `metadata=mode=print:file=NAME` | Per frame: `frame:0 pts:0 pts_time:0`, then `lavfi.siti.si=53.83`, `lavfi.siti.ti=0.00`, `lavfi.block=2.068609`, `lavfi.blur=5.825063` |
| `idet` | Info level: `[Parsed_idet_0 @ X] Multi frame detection: TFF: 0 BFF: 0 Progressive: 48 Undetermined: 1` |
| `freezedetect` | Info level: `[freezedetect @ X] lavfi.freezedetect.freeze_start: 0`, `...freeze_duration: 3`, `...freeze_end: 3` (`[Parsed_freezedetect_0 @ X]` on 9.0.2) |
| `blackdetect` | Info level: `[blackdetect @ X] black_start:0 black_end:3 black_duration:3` (`[Parsed_blackdetect_0 @ X]` on 9.0.2) |
| `cropdetect` | Info level: `[Parsed_cropdetect_0 @ X] x1:0 x2:319 y1:0 y2:239 w:320 h:240 x:0 y:0 pts:48640 t:3.958333 limit:0.094118 crop=320:240:0:0`. Not in the bundle (`No such filter: 'cropdetect'`); the app does not use it |
| `volumedetect` | Info level: `[Parsed_volumedetect_0 @ X] [info] mean_volume: -23.5 dB`, `max_volume: ...` |
| `ebur128=peak=true:framelog=quiet` | Info level summary: `Integrated loudness: I: -6.7 LUFS ... Loudness range: LRA: 0.1 LU ... True peak: Peak: 1.1 dBFS` |
| `astats` (overall) | Info level: `DC offset: 0.003124`, `Peak level dB: -10.291644`, `Flat factor: 0.000000`, `Peak count: 2.000000`, `Bit depth: 14/16/16/16` (for the padded 24-bit file) |

Measured on the generated 1080p clips: blockiness 1.50 (CRF 20) versus 2.90 (starved to about
450 kbps); blur 5.76 versus 9.54. Over all 144 frames of the 2026-11-17 fixtures: blockiness 1.77 versus
3.25, blur 5.67 versus 9.13, on both versions. `ssim`, `siti`, `idet`, `volumedetect`, `ebur128` and
`astats` printed the same values on 8.0.1 and 9.0.2.

## 12. Down-up SSIM for upscale detection

The exact filter graph in subplan 11 (three key frames, centre crop 1152 × 648, candidates 720p and
540p) ran with no stderr output in about 0.3 s per file. The frame-sampling provider's graph as built
today (six samples from 5 % to 95 %, which in these 6-second clips land on the three key frames twice
over, and a 360p candidate as well) gave the same values to three decimals on 8.0.1 and 9.0.2, in 0.4 to
0.9 s per file on the runner; the 360p values are 0.942, 0.919, 0.921 (`native1080.mp4`), 0.956, 0.944,
0.947 (`up720.mp4`) and 0.969, 0.961, 0.963 (`up540.mp4`):

| File | Rebuilt from 720p (three frames) | Rebuilt from 540p (three frames) |
|------|----------------------------------|----------------------------------|
| `native1080.mp4` | 0.971, 0.957, 0.957 | 0.958, 0.939, 0.940 |
| `up720.mp4` | 0.993, 0.991, 0.992 | 0.978, 0.972, 0.973 |
| `up540.mp4` | 0.995, 0.994, 0.994 | 0.992, 0.990, 0.991 |

`scale2ref` works but prints a deprecation warning; use explicit sizes.

## 13. Timings on the test machine

| Operation | Time |
|-----------|------|
| `ffprobe -show_format -show_streams` | about 90 ms (process start dominates) |
| `ffprobe -count_frames` on a 1080p JPEG | about 52 ms |
| Packet copy pass, 6 s 1080p (`-c copy -f null`) | about 80 ms |
| Full decode, 6 s 1080p H.264 | about 215 ms (roughly 48× real time) |
| Key-frame decode of the same | about 157 ms |

Process start-up is the floor for per-file cost. Avoid a second process where one will do.

## 14. Capabilities of the tested build

Filters present: `adeclick adeclip aspectralstats astats ebur128 loudnorm silencedetect volumedetect bitplanenoise blackdetect blockdetect blurdetect cropdetect entropy freezedetect idet psnr scale select signalstats siti ssim vmafmotion xpsnr showspectrumpic`.

Decoders present include: `flac mp3float aac opus vorbis alac ape wavpack tak wmalossless truehd dca ac3 eac3 dsd_lsbf h264 hevc av1 libdav1d vp9 mpeg2video mpeg4 vc1 prores mjpeg png webp gif bmp tiff libjxl`. Hardware acceleration methods listed: `cuda vaapi dxva2 qsv d3d11va opencl vulkan d3d12va amf`.

Smaller builds may lack some of these; the capability probe (subplan 04) decides at run time. The
bundle (subplan 17) has only the components in its build script: of the filters above it lacks
`aspectralstats`, `loudnorm`, `silencedetect`, `bitplanenoise`, `cropdetect`, `entropy`, `signalstats`,
`vmafmotion`, `xpsnr` and `showspectrumpic`, and it has no hardware acceleration.
