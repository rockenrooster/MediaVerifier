# 11 — Video and photo quality detectors

Phase 11. Depends on: phase 7. Independent of phase 10. Milestone M2 (some rules M3).

## Goal

Decide whether a picture's resolution and bitrate are honest:

- Is the detail really there, or was a smaller picture enlarged? (`VID-UPSCALED`, `IMG-UPSCALED`)
- Is the bitrate adequate for the resolution, and does the picture show it? (`VID-STARVED`,
  `VID-BLOCKY`, `VID-INFLATED`, `IMG-HEAVY-COMPRESSION`)
- Smaller checks: interlacing, freezes, silent soundtrack, HDR signalling.

All measurements use FFmpeg filters on a handful of sampled frames; there is no custom image code.

## Effective resolution: the down-up test

**Idea.** Shrink a frame to a candidate lower resolution, enlarge it back, and compare with the
original using SSIM on the luma plane. If the round trip reproduces the frame almost perfectly, the
frame holds no detail beyond that lower resolution.

**Verified** on generated 1080p H.264 (fractal detail plus grain, CRF 20), centre crop 60 %, key
frames, bicubic both ways:

| File | Rebuilt from 720p | Rebuilt from 540p | Rebuilt from 360p* |
|------|-------------------|-------------------|--------------------|
| Native 1080p | 0.957 – 0.971 | 0.939 – 0.958 | 0.963 |
| Upscaled from 720p | **0.991 – 0.993** | 0.972 – 0.978 | 0.971 |
| Upscaled from 540p | 0.994 – 0.995 | **0.990 – 0.992** | 0.980 |

\* full-frame average from an earlier run. Bold marks the true source resolution: SSIM stays at 0.99
or above down to the source size and falls away below it. PSNR separates the same way (native about
35 dB, upscaled 42–45 dB).

### Sampling

`FrameSampleProvider`:

- Sample count `N = clamp(duration / 60 s, 8, 24)`, times spread evenly between 5 % and 95 % of the
  duration. Clips under 60 s use 6. Photos use the image itself.
- Each sample is the key frame at or before the sample time: `-noaccurate_seek -ss <t> -i <file>`
  with `trim=end_frame=1`. One decoded frame per sample.
- Interlaced streams (`field_order` not progressive) get `yadif` first; combing would count as detail.
- Crop the centre 60 % (even dimensions) to keep letterbox bars, channel logos and subtitles out.
- Candidates by container height (widths scale proportionally, rounded to even):

| Container height | Candidates |
|------------------|------------|
| 2160 | 1440, 1080, 720 |
| 1440 | 1080, 720, 540 |
| 1080 | 720, 540, 360 |
| 720 | 540, 480, 360 |
| 576, 480 | 360, 240 |
| other | 2/3, 1/2 and 1/3 of the height |

### Command

One process per file; working directory is the job temp folder so stats files are relative names.
Shown for three samples and two candidates on a 1920 × 1080 source (crop 1152 × 648):

```
ffmpeg <std> -noaccurate_seek -ss <t0> -i <file> -noaccurate_seek -ss <t1> -i <file> -noaccurate_seek -ss <t2> -i <file>
  -filter_complex "
    [0:v]trim=end_frame=1,setpts=PTS-STARTPTS[s0];
    [1:v]trim=end_frame=1,setpts=PTS-STARTPTS[s1];
    [2:v]trim=end_frame=1,setpts=PTS-STARTPTS[s2];
    [s0][s1][s2]concat=n=3:v=1:a=0,crop=1152:648,split=4[o][a][b][m];
    [a]split[a1][a2];[a2]scale=768:432:flags=bicubic,scale=1152:648:flags=bicubic[a3];
    [a1][a3]ssim=stats_file=c720.log[ao];
    [b]split[b1][b2];[b2]scale=576:324:flags=bicubic,scale=1152:648:flags=bicubic[b3];
    [b1][b3]ssim=stats_file=c540.log[bo];
    [m]siti=print_summary=0,blockdetect,blurdetect,metadata=mode=print:file=frame.log[mo]"
  -map "[o]" -f null - -map "[ao]" -f null - -map "[bo]" -f null - -map "[mo]" -f null -
```

- Each `c<height>.log` has one line per frame: `n:1 Y:0.994236 U:... V:... All:... (dB)`. Use `Y`.
- `frame.log` has per-frame `lavfi.block`, `lavfi.blur`, `lavfi.siti.si` values.
- Verified cost: about 0.3 s for three samples and two candidates at 1080p.
- Compute sizes in C# from the probe; do not use `scale2ref` (deprecated).
- `siti` is optional; when the filter is missing, treat all frames as usable.

### Decision

1. **Usable frames**: spatial information (`lavfi.siti.si`) at or above `MinSpatialInfo` (start at 25,
   *calibrate*). Dark, flat or defocused frames cannot tell an upscale from a native picture. Fewer
   than 4 usable frames → `insufficient`, no finding.
2. For each candidate take the **10th percentile** of SSIM-Y over usable frames. Using a low
   percentile means nearly every frame must be reproducible; a few natively rendered titles do not
   mask an upscaled film, and a few soft shots do not condemn a native one.
3. A candidate **passes** when that percentile is at or above `UpscaleSsim` (start at 0.985,
   *calibrate*).
4. **Effective height** = the smallest passing candidate, provided the next smaller candidate fails
   or drops by at least 0.008 (the fall-off that marks the true source size). If every candidate
   passes with no fall-off, report the smallest and lower the confidence.
5. Confidence: 0.85 when the margin over the threshold is 0.005 or more, else 0.65; −0.20 with fewer
   than 6 usable frames; −0.15 when the file is also bitrate-starved (softness may be compression).
6. Severity for `VID-UPSCALED`, with `r = effective height / container height`:
   `r ≤ 0.55` and confidence ≥ 0.80 → High; `r ≤ 0.80` and confidence ≥ 0.50 → Medium; otherwise Low.

Summary: *"Likely upscaled: 2160p file with detail equivalent to about 1080p (12 of 12 sampled frames
are reproducible from 1080p)."* When the file is also starved, say "upscaled or heavily compressed".

### Limits to state in the rule text

- Learned (AI) upscalers and added synthetic grain create new high-frequency content and can pass.
- Deliberately soft material (animation with flat shading, heavy diffusion, old film transfers) has
  little fine detail; the finding then reads as "no more detail than 720p", which is true and still
  useful, but it is not proof of an upscale. Hence "likely", never "is".

## Bitrate adequacy

### `VID-STARVED` (Quick)

`bpp = video bitrate / (width × height × frames per second)`. Video bitrate: the stream's `bit_rate`,
else the format bitrate minus audio bitrates, else `size × 8 / duration` minus audio.

Normalise to an H.264 equivalent by dividing by a codec factor:

| Codec | Factor |
|-------|--------|
| H.264 | 1.0 |
| HEVC | 0.6 |
| VP9 | 0.65 |
| AV1 | 0.5 |
| VVC | 0.4 |
| VC-1, WMV3 | 1.2 |
| MPEG-4 ASP (Xvid, DivX) | 1.6 |
| MPEG-2 | 2.2 |
| ProRes, DNxHD/HR, MJPEG, FFV1, uncompressed | rule not applied |

Equivalent bpp below `StarvedLow` (start 0.035) → Low. Below `StarvedMedium` (start 0.020), or
below `StarvedLow` with `VID-BLOCKY` also raised → Medium. For reference, 1080p24 H.264 at 1.75 Mbps
is 0.035 bpp. Low-motion content looks fine well below these numbers, which is why the Quick-only
result is Low.

### `VID-BLOCKY` (Standard)

Median `lavfi.block` over usable sampled frames. Verified on the generated 1080p clip: 1.5 at CRF 20,
2.9 when starved to about 450 kbps; `lavfi.blur` moved from 5.8 to 9.5. The scale depends on content,
so thresholds must come from calibration on real footage: start at Low 2.6, Medium 3.4, and require
either equivalent bpp under twice `StarvedLow` or median blur above its own threshold before raising.

### `VID-INFLATED` (Standard)

Equivalent bpp at or above `InflatedBpp` (start 0.20) together with `VID-UPSCALED` at confidence 0.65
or more, or with `VID-BLOCKY`. Medium at confidence 0.7 or more, else Low. Summary: *"High bitrate
(18 Mbps) but detail equivalent to 720p: probably re-encoded from a lower-quality source."*

## Other video rules (M3)

| Rule | Method | Condition and severity |
|------|--------|------------------------|
| `VID-INTERLACE` | `idet` on 200 frames at three positions, log level `info`. Parse `Multi frame detection: TFF: n BFF: n Progressive: n Undetermined: n` | Interlaced frames ≥ 70 % of determined while the stream is flagged progressive: Medium ("combing artifacts"). Flagged interlaced but ≥ 90 % progressive: Low |
| `VID-FROZEN` | Deep pass `freezedetect=n=0.003:d=10`; parse `lavfi.freezedetect.freeze_start/duration/end` | A freeze of 10 s or more that is not within the first or last 60 s and is not black: Low. Frozen time ≥ 10 % of the duration: Medium |
| `VID-BLACK` | Deep pass `blackdetect=d=30:pix_th=0.10`; parse `black_start: black_end: black_duration:` | Disabled by default. Low |
| `VID-AUDIO-SILENT` | Add `-filter:a:0 volumedetect` to the Standard pass and raise its log level to `info`; parse `max_volume: -91.0 dB` | Maximum below −60 dB for the default audio stream: Medium. Re-run the classifier corpus when changing the log level |
| `VID-HDR` | Probe fields | PQ or HLG transfer (`smpte2084`, `arib-std-b67`) with an 8-bit pixel format: Medium. HDR transfer without BT.2020 primaries: Low |

## Photos

### `IMG-UPSCALED` (Standard)

The same down-up test on the image itself: centre crop 80 %, candidates at 2/3, 1/2, 1/3 and 1/4 of
the long edge, same decision rules with a single frame (percentile = the value). Conditions: at least
1 megapixel; not palette-based; spatial information above the minimum (skips screenshots, scans of
text, flat graphics). Severity: `r ≤ 0.5` and confidence ≥ 0.80 → Medium; otherwise Low. For
low-quality JPEGs say "enlarged or over-compressed".

Setting *Check photos for upscaling* (default on) lets users with very large photo libraries skip the
extra work. When the structure reader already knows the dimensions (JPEG, PNG, WebP, GIF, BMP), run
decode check and down-up test in one `ffmpeg` process instead of `ffprobe` plus `ffmpeg`.

### `IMG-HEAVY-COMPRESSION` (Quick)

JPEG only in M2. Estimate quality from the luminance quantisation table found by the JPEG walker.
De-zigzag the 64 values, then with the standard table `B`:

```
S = (100 / 64) × Σ table[i] / B[i]
Q = S ≤ 100 ? (200 − S) / 2 : 5000 / S
```

Standard luminance table (row order):

```
16 11 10 16  24  40  51  61
12 12 14 19  26  58  60  55
14 13 16 24  40  57  69  56
14 17 22 29  51  87  80  62
18 22 37 56  68 109 103  77
24 35 55 64  81 104 113  92
49 64 78 87 103 121 120 101
72 92 95 98 112 100 103  99
```

`Q < 50` → Low; `Q < 30` → Medium. Mark the estimate approximate when the table is not a scaled copy
of the standard one (cameras and Photoshop use their own). Evidence: estimated quality, bits per pixel.

### `IMG-LOW-RES` (Quick)

Below 0.3 megapixels. Disabled by default.

## Evidence views

- **Frame strip**: thumbnails of the sampled frames with each frame's SSIM at the effective
  resolution and its blockiness; unusable frames greyed out with the reason.
- **Compare**: for a chosen frame, the cropped original and the version rebuilt from the effective
  resolution, with a press-and-hold toggle and a 2× zoom. Both images are produced on demand by one
  FFmpeg call and cached. If the user cannot see a difference, the finding has explained itself.
- **Bitrate card**: resolution, frame rate, codec, bitrate, equivalent bits per pixel on a scale
  with the thresholds marked.

## Calibration

Thresholds here are starting points measured on synthetic material. Before M2 ships, run the
calibration procedure in subplan 15 on real footage: freely licensed films (Big Buck Bunny, Sintel,
Tears of Steel are available at several native resolutions) and photos, each processed into known
upscales and bitrate ladders. Downloaded at calibration time, never committed. Record distributions,
chosen thresholds and the resulting error rates in `docs/calibration/video.md` and
`docs/calibration/photo.md`.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| V-01 | `FrameSampleProvider`: sample times, filter graph builder, log parsers, `FrameSamples` artifact | Unit tests build the exact graph for given dimensions; parsers handle the verified log formats; integration reproduces the verified table within 0.01 |
| V-02 | `VID-UPSCALED` decision logic | Pure tests over hand-built artifacts for each decision step; fixtures: native not flagged, 720→1080 flagged at 720, 540→1080 flagged at 540 |
| V-03 | `VID-STARVED` with bitrate derivation and codec factors | Tests for each bitrate source and codec; the starved fixture is flagged |
| V-04 | `VID-BLOCKY`, `VID-INFLATED` | Fixture tests; thresholds read from `RuleDefaults` |
| V-05 | Evidence: frame strip, compare view, bitrate card | Compare images generated on demand in under 2 s and cached |
| V-06 | `VID-INTERLACE`, `VID-HDR` (M3) | Fixtures: interlaced clip flagged progressive (make with `tinterlace`); PQ-tagged 8-bit clip |
| V-07 | `VID-FROZEN`, `VID-BLACK`, `VID-AUDIO-SILENT` (M3) | Fixtures from `lavfi` colour and frozen sources; parsers match the verified line formats |
| P-01 | `IMG-UPSCALED` including the single-process path | Fixtures: native photo not flagged; 2× enlargement flagged; screenshot skipped |
| P-02 | `IMG-HEAVY-COMPRESSION` with the quality estimator | Estimated Q within ±3 of the encoder setting for JPEGs written at quality 20–95 with standard tables |
| P-03 | `IMG-LOW-RES` | Disabled by default; works when enabled |
| VP-01 | Calibration on real material and threshold update | Reports committed; no false positives on the native calibration set at the chosen thresholds |
| VP-02 | Performance | Sampling adds under 5 s per film at 2160p and under 0.3 s per photo on the reference machine |

## Exit criteria

- Verified tables reproduce in integration tests.
- No quality finding on native, well-encoded fixtures.
- Rule explanations include the limits listed above.

## Pitfalls

- SSIM is averaged over the frame: black bars and flat areas score 1.0 and inflate the result. Always
  crop, and always filter by spatial information.
- Seeking with `-ss` before `-i` lands on a key frame only with `-noaccurate_seek`; without it FFmpeg
  decodes up to the requested time and a long GOP makes sampling slow.
- Filter-graph strings contain no file paths (stats files are relative), so no escaping is needed.
  Keep it that way.
- Variable frame rate: use `avg_frame_rate` for bits per pixel, and guard against 0/0.
- Cover art and thumbnails are video streams; sample the stream chosen by the probe as the main video.
