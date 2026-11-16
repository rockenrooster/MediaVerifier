# 10 — Audio authenticity and quality detectors

Phase 10. Depends on: phase 7 (`AudioDecodeProvider` and the `IPcmAnalyzer` pipeline). Milestone M2.

## Goal

From the same single decode used for corruption checks, measure the signal and answer:

- Is this lossless file really lossless, or was it made from an MP3/AAC/Opus? (`AUD-FAKE-LOSSLESS`)
- Is this hi-res file really hi-res, or upsampled and padded? (`AUD-UPSAMPLED`, `AUD-PADDED-BITS`)
- Does this lossy file's quality match its bitrate? (`AUD-BITRATE-INFLATED`, `AUD-LOW-BITRATE`)
- Is it clipped or otherwise distorted? (`AUD-CLIPPING`, `AUD-OVERS`, `AUD-DC`, `COR-DROPOUT`)
- Is something plainly wrong with the channels or the level? (`AUD-DEAD-CHANNEL`, `AUD-PHASE`,
  `AUD-SILENT`, `AUD-LONG-SILENCE`)

The spectral, clipping and bit-depth analysis was validated with a throwaway prototype whose listing
and output are in [appendix B](appendix-b-audio-prototype.md). Port its logic; its measured results
are in the last section of this subplan and are the first regression targets.

## Analyzer pipeline

`AudioDecodeProvider` (subplan 07) streams 32-bit float blocks to analyzers. Each analyzer is
streaming, allocation-free per block, and contributes a section to the `PcmStats` artifact.

| Analyzer | Produces |
|----------|----------|
| `LevelAnalyzer` | Per channel: peak, RMS, mean (DC), count of samples above 1.0, RMS in 50 ms windows for silence detection, sign-stability of 1 s DC windows |
| `BitDepthAnalyzer` | Whether all samples are exact multiples of 2^-23, and the OR of all scaled integers → effective bits |
| `ClippingAnalyzer` | Flat-run histogram near the peak: runs, clipped samples, longest run; peak-crowding ratio |
| `StereoAnalyzer` | Left/right correlation, identical-channel flag |
| `SpectrumAnalyzer` | Long-term average spectrum features: knee, drop, levels below and above, quantisation-floor reference, reduced 96-band spectrum |
| `DropoutAnalyzer` | Digital-zero gaps with edge levels and lengths |

`PcmStats` stores features, not spectra, so the artifact stays a few hundred bytes and rules can be
re-evaluated without decoding again (subplan 05).

## Spectral cutoff analysis

### Measurement

1. Mix to mid: `m = (L + R) / 2` for stereo; mean of front channels for multichannel; the channel
   itself for mono. If the mid signal's RMS is more than 20 dB below the left channel's (out-of-phase
   material), analyse left instead.
2. FFT size `N`: 4096 up to 50 kHz sample rate, 8192 up to 100 kHz, 16384 above. Hann window, hop `N`
   (no overlap). For files longer than 4,000 frames, raise the hop so about 4,000 frames are used,
   spread evenly.
3. Skip frames whose mean square is below 1e-7 (about −70 dBFS).
4. Accumulate the power spectrum; divide by the number of frames used. Require at least 100 frames
   and 15 seconds of non-silent audio, otherwise report `insufficient` and raise nothing.
5. Smooth in the power domain over ±100 Hz, then convert to dB.
6. **Terminal-drop search.** For each candidate `f` from 4 kHz to Nyquist − 500 Hz in 50 Hz steps:
   `below = mean dB in [f−1000, f−200]`, `above = mean dB in [f+200, min(f+1000, Nyquist−50)]`,
   `maxAbove = max dB in [f+200, Nyquist−50]`. Reject `f` unless `maxAbove ≤ below − 15` (the drop
   must be terminal, not a notch). Keep the `f` with the largest `drop = below − above`.
7. **Knee refinement.** From the winning `f`, step down 50 Hz at a time while the local slope
   `dB(f−250) − dB(f+250)` is 4 dB or more; the knee is the stopping point + 250 Hz. This lands on the
   start of the roll-off for both razor-sharp codec lowpasses and slower resampler filters.
8. **Floor reference.** For integer PCM with effective depth `b`, the level of undithered
   quantisation noise per bin in this normalisation is
   `10·log10( (2^-(b-1))² / 12 · 1.5 / N )`. Store `aboveDb − floorRefDb`. A value within 6 dB means
   there is nothing above the knee but rounding noise.

Stored features: `kneeHz`, `dropDb`, `belowDb`, `aboveDb`, `aboveVsFloorDb`, `nyquistHz`,
`framesUsed`, `analysedSeconds`, `spectrum96` (96 linear bands, dB as bytes, for the UI).

Definitions used below: **brickwall** = `dropDb ≥ 30`. **Ratio** = `kneeHz / nyquistHz`.

### `AUD-FAKE-LOSSLESS`

Applies to lossless codecs (FLAC, ALAC, WavPack, Monkey's Audio, TTA, PCM in WAV/AIFF) with a sample
rate of 44.1 kHz or more. Requires a brickwall.

| Sample rate | Knee | Base confidence | Wording |
|-------------|------|-----------------|---------|
| 44.1 / 48 kHz | up to 19.2 kHz | 0.75 | "Likely made from a lossy source" |
| 44.1 / 48 kHz | 19.2 – 20.6 kHz | 0.45 | "Possibly made from a high-bitrate lossy source, or mastered with a 20 kHz filter" |
| 48 kHz | 21.3 – 22.5 kHz | — | Not lossy: raise `AUD-UPSAMPLED` at Low ("resampled from 44.1 kHz") |
| 88.2 kHz and above | up to 20.6 kHz | 0.75 | As the first row |
| any | above the ranges | — | No finding |

Confidence adjustments: +0.10 if `dropDb ≥ 45`; +0.10 if `aboveVsFloorDb ≤ 6`; −0.20 if fewer than
30 seconds were analysed. Clamp to 0.30–0.97; rows with base 0.45 are capped at 0.70.

Severity: confidence 0.80 or more → High; 0.50–0.79 → Medium; lower → Low.

Evidence summary example: *"Hard cutoff at 16.6 kHz (67 dB drop) in a 44.1 kHz FLAC; only rounding
noise above. Matches a 128 kbps MP3."* The "matches" hint comes from this table and is illustrative:

| Knee | Typical source |
|------|----------------|
| ≤ 11.5 kHz | MP3 64 kbps or lower |
| 15 – 16 kHz | MP3 96–112 kbps, older encoders at any bitrate |
| 16 – 17 kHz | MP3 128 kbps |
| 17 – 17.6 kHz | AAC 128 kbps, MP3 160 kbps |
| 18 – 19 kHz | MP3 192 kbps |
| 19 – 19.8 kHz | MP3 224–256 kbps |
| 19.8 – 20.6 kHz | MP3 320 kbps, Opus (20 kHz band limit) |

### `AUD-UPSAMPLED`

Applies to lossless files at 88.2 kHz or more. Brickwall with knee between 20.6 and 24.5 kHz →
Medium, confidence 0.80 (0.90 when `aboveVsFloorDb ≤ 6`). Summary: *"Content stops at 22.1 kHz in a
96 kHz file: upsampled from 44.1 or 48 kHz."* Name a single source rate only when the knee is within
0.5 kHz of 22.05 or 24 kHz. A brickwall between 24.5 kHz and ratio 0.93 → Low ("band-limited
below the sample rate's capability").

Genuine hi-res transfers from analogue sources roll off gradually and carry noise to high
frequencies; they have no brickwall and are not flagged.

### `AUD-BITRATE-INFLATED`

Applies to lossy stereo files at 44.1 or 48 kHz. Expected minimum knee by codec and nominal bitrate:

| Codec | Bitrate (kbps) | Expected knee at least |
|-------|----------------|------------------------|
| MP3 | 256 and up | 18.5 kHz |
| MP3 | 192 – 255 | 17.5 kHz |
| MP3 | 160 – 191 | 16.5 kHz |
| MP3 | 128 – 159 | 15.0 kHz |
| AAC-LC | 256 and up | 18.5 kHz |
| AAC-LC | 160 – 255 | 17.0 kHz |
| AAC-LC | 128 – 159 | 15.5 kHz |
| Vorbis | 192 and up | 18.0 kHz |
| Vorbis | 128 – 191 | 15.5 kHz |
| Opus | 96 and up | 15.0 kHz |

Raise when the file has a brickwall and `kneeHz ≤ expected − 1500`. Confidence 0.80 when the knee
falls in the typical range of a bitrate at least two rows lower, else 0.60. Severity Medium at 0.70
or more, else Low. Summary: *"320 kbps MP3 whose audio stops at 16.6 kHz, typical of 128 kbps."*
For mono, halve the bitrate thresholds. Skip variable-bitrate files whose average is below 128 kbps.

Material that is simply dull (old recordings, speech) has a gradual roll-off, no brickwall, and is
not flagged.

### `AUD-LOW-BITRATE`

Quick depth, from the probe. Low severity when the stereo bitrate is under: MP3 128, AAC-LC 96,
HE-AAC 48, Vorbis 96, Opus 64, WMA 128 kbps (halved for mono).

## Bit depth — `AUD-PADDED-BITS`

For lossless integer sources. If every sample is an exact multiple of 2^-23, OR all scaled integer
values together; effective bits = 24 − trailing zero count. Container depth above 16 with 16 or fewer
effective bits → Medium, confidence 1.0 (this is arithmetic, not a heuristic). Summary: *"24-bit
file with 16 significant bits; the lower 8 bits are always zero."* Lossy decodes are not integral and
are skipped. Sources with 32-bit integer samples are reported as "24 bits or more".

## Clipping — `AUD-CLIPPING` and `AUD-OVERS`

### Integer PCM (lossless sources)

Streaming, per channel. Let `lsb = 1/32768`.

1. Track a run of consecutive samples with the same sign, `|x| ≥ 0.1`, and each within `lsb` of the
   previous one.
2. When a run of 3 or more ends, compute the edge step: the larger of the jumps into and out of the
   run. Count the run only if the edge step is at least `8·lsb`. This rejects the naturally flat
   crest of a loud low-frequency tone, which eases in and out, while a clipped waveform hits the
   plateau at speed.
3. Add the run to a histogram keyed by level in 0.1 dB bins (runs, samples, longest run).
4. At the end, sum the bins within 0.2 dB of the channel's peak. Clipping that was attenuated
   afterwards is still found because the test is relative to the peak.

Tiers (per file, worst channel; all values *calibrate*):

| Tier | Condition |
|------|-----------|
| Low | At least 10 runs and 5 runs per minute |
| Medium | 60 runs per minute or more, or a run of 16 samples or more |
| High | 600 runs per minute or more, or clipped samples at 0.05 % or more, or a run of 64 samples or more |

Confidence 0.9. Evidence: runs, runs per minute, clipped sample percentage, longest run, peak dBFS,
and up to 20 positions (seconds) for the waveform view.

### Lossy and float sources

Lossy coding smears flat tops, so run detection finds nothing (verified: a clipped source encoded to
320 kbps MP3 shows zero flat runs but 2,550 samples above full scale). Use instead:

- `AUD-OVERS` (Low): any decoded sample above 1.0. Evidence: count and peak in dBFS. Summary:
  *"Decodes to +0.7 dBFS; will clip on playback without headroom."*
- `AUD-CLIPPING` at confidence 0.6 with "Possibly clipped or heavily limited" wording when the
  peak-crowding ratio (share of samples at or above 98 % of the peak) is 0.05 % or more (Low) or
  0.5 % or more (Medium). Never High for a lossy source: the evidence is indirect.

## Other signal rules

| Rule | Measurement | Condition and severity |
|------|-------------|------------------------|
| `AUD-DC` | Per-channel mean, and the sign of the mean in 1 s windows | `|mean| ≥ 0.01` with the same sign in 90 % of windows: Low. `≥ 0.05`: Medium. The window test separates a true offset from low-frequency content (pink noise alone gives 0.003) |
| `AUD-DEAD-CHANNEL` | Per-channel RMS | Stereo: one channel below −80 dBFS while the other is above −50 dBFS: Medium. Multichannel: Low, and never for LFE |
| `AUD-PHASE` | Pearson correlation of left and right over the track | Below −0.9: Medium |
| `AUD-DUAL-MONO` | Left equals right for every sample | Low. Disabled by default |
| `AUD-SILENT` | Track peak and RMS | Peak below −60 dBFS or RMS below −70 dBFS for the whole file: High |
| `AUD-LONG-SILENCE` | Leading and trailing digital silence | 10 s or more: Low |
| `COR-CLICKS` (M3, experimental, disabled by default) | Sample-to-sample step compared with the median absolute step in the surrounding ±512 samples; a click is an isolated step at least 20 times the local median and at least 0.05 full scale, not part of a clipped run or a dropout edge | 3–20 clicks: Low. More than 20: Medium. Confidence 0.5; all values *calibrate* on fixtures with spliced single-sample errors before enabling |
| `COR-DROPOUT` | Gaps where all channels are exactly zero (below 2^-20 for float) for 1–50 ms, with the 50 ms before and after above −50 dBFS and an abrupt edge on both sides | Gap length within one sample of a multiple of 588, 1024, 1152 or 4096 samples: confidence 0.9, High for three or more gaps, Medium for one or two. Otherwise confidence 0.5, Low. Evidence lists positions |

## Guards against false positives

- Fewer than 15 s of non-silent audio, or fewer than 100 spectral frames: no spectral rules.
- Sample rate 32 kHz or lower: no spectral rules.
- A terminal drop is required; gradual roll-off never qualifies, however dull the recording.
- The knee tables apply to the measured knee, not to tags; never use the encoder tag as evidence of
  quality.
- DSD sources and files the bundled FFmpeg cannot decode are skipped, not flagged.
- High-quality variable-bitrate encodes (LAME V0, AAC at 256 kbps and above) can pass the cutoff
  test. Say so in the rule's explanation: a clean result is not proof of lossless origin.

## Evidence views

Rendered on demand by `EvidenceRenderer` (background thread, disk cache keyed by file id and
fingerprint):

- **Spectrogram**: STFT `N = 2048`, hop chosen for at most 1,024 columns (max-hold within a column),
  linear frequency axis to Nyquist, −120 to 0 dB through a perceptually uniform lookup table.
  Overlays: knee line with label, Nyquist line, expected-knee line for inflated-bitrate findings.
- **Waveform**: per-column min/max envelope; clipped runs and dropouts marked; click a marker to
  show a zoomed 20 ms window around it.

## Performance

- The analyzers share one pass over the decoded stream. Target: analysis adds less than 25 % to the
  decode time of a FLAC.
- FFT: an in-house iterative radix-2 on `float`, precomputed twiddles and bit-reversal table, one
  instance per worker. Window multiply and power accumulation use `TensorPrimitives`. Keep a scalar
  path and test both produce the same features.
- Memory per job: a few FFT buffers and the histograms; independent of track length. The prototype
  buffers the whole track for simplicity; the product must not.

## Verified prototype results

FFmpeg 8.0.1, 20-second synthetic pink noise, generated as described in subplan 15.

| Fixture | How it was made | Effective bits | Knee | Drop | Result |
|---------|-----------------|----------------|------|------|--------|
| `src16.flac` | 44.1 kHz 16-bit original | 16 | 22.05 kHz | — | Full band |
| `pad24.flac` | Original stored as 24-bit | **16** | 22.05 kHz | — | Padded bit depth |
| `true96.flac` | Generated at 96 kHz, 24-bit | 24 | 48 kHz | — | Full band |
| `up96.flac` | Original resampled to 96 kHz | 24 | **23.2 kHz** | 48 dB | Upsampled |
| `fake128.flac` | MP3 128 kbps decoded to FLAC | 16 | **16.6 kHz** | 68 dB | Fake lossless |
| `fake320.flac` | MP3 320 kbps decoded to FLAC | 16 | **20.0 kHz** | 68 dB | Possible fake |
| `fakeAac.flac` | AAC 128 kbps decoded to FLAC | 16 | **17.2 kHz** | 69 dB | Fake lossless |
| `fakeOpus.flac` | Opus 128 kbps decoded to FLAC (48 kHz) | 16 | **20.0 kHz** | 57 dB | Fake lossless |
| `fakeV0.flac` | LAME V0 decoded to FLAC | 16 | 22.05 kHz | — | **Not detected** (expected limitation) |
| `infl320.mp3` | MP3 128 re-encoded at 320 kbps | float | **16.6 kHz** | 84 dB | Inflated bitrate |
| `natural.flac` | Gentle 6 dB/octave roll-off | 16 | 22.05 kHz | — | Not flagged |
| `dull.flac` | −30 dB shelf plus 12 dB/octave lowpass | 16 | 22.05 kHz | — | Not flagged |
| `clip.flac` | +14 dB then 16-bit | 16 | — | — | 406 runs, 0.095 % clipped, longest 14 |
| `clipatt.flac` | Clipped, then −3 dB | 16 | — | — | Same 406 runs found at −3 dBFS |
| `clip320.mp3` | Clipped source as MP3 320 | float | — | — | 0 runs, 2,550 overs, peak +0.72 dBFS |

Level above the knee in the fake FLACs measured −134 to −135 dB per bin against a computed 16-bit
rounding-noise floor of −135.5 dB: nothing but quantisation noise, as the floor reference predicts.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| A-01 | `LevelAnalyzer`, `BitDepthAnalyzer`, `StereoAnalyzer` | Unit tests on synthetic buffers; DC and peak agree with FFmpeg `astats` on the fixture set within 1e-4 |
| A-02 | FFT and `SpectrumAnalyzer` (measurement steps 1–8) | Unit: a synthetic brickwall at 16 kHz is located within 100 Hz. Integration: knees match the table above within 300 Hz |
| A-03 | `AUD-FAKE-LOSSLESS`, `AUD-UPSAMPLED`, `AUD-BITRATE-INFLATED`, `AUD-LOW-BITRATE` detectors | Pure tests over hand-built features for every table row and confidence adjustment; fixture results match the table |
| A-04 | `AUD-PADDED-BITS` | `pad24.flac` flagged; `true96.flac` and `src16.flac` not |
| A-05 | `ClippingAnalyzer` (integer and lossy paths), `AUD-CLIPPING`, `AUD-OVERS` | Fixture counts match the table; a full-scale 30 Hz sine is not flagged (edge test); a clipped 40 Hz sine is |
| A-06 | `AUD-DC`, `AUD-DEAD-CHANNEL`, `AUD-PHASE`, `AUD-SILENT`, `AUD-LONG-SILENCE` | Synthetic fixtures for each; pink noise does not trigger `AUD-DC` |
| A-07 | `DropoutAnalyzer` and `COR-DROPOUT` | Fixture with a 588-sample zero gap → High confidence; a musical gap with fades → no finding |
| A-08 | `PcmStats` artifact persistence and re-evaluation | Changing a clipping threshold changes findings with no decode |
| A-09 | `EvidenceRenderer`: spectrogram and waveform images, disk cache | Rendered in under 1 s for a 5-minute track; images shown in the drawer with overlays |
| A-10 | Calibration run on the generated corpus plus any real, legally obtained samples the owner provides | Report in `docs/calibration/audio.md`: per-rule true and false positive counts; thresholds adjusted and recorded |
| A-11 | Performance check | Analysis overhead below 25 % of decode time; flat memory on a 3-hour file |
| A-12 | `ClickAnalyzer` and `COR-CLICKS` (M3) | Fixture with five spliced single-sample errors is flagged; percussive music fixtures are not; stays disabled by default until calibrated |

## Exit criteria

- All rows of the verified table reproduce in integration tests.
- Zero findings from these rules on the healthy and natural-roll-off fixtures.
- Each rule's explanation states its limits in plain language.
