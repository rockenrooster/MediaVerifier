# Audio rule calibration (subplan 10, task A-10)

Run: `dotnet test -c Release --filter "FullyQualifiedName~AudioQualityFixtureTests|FullyQualifiedName~EvidenceAndClickFixtureTests"`,
2026-11-16, FFmpeg 8.0.1 (engine and fixture tooling). Corpus: the generated audio fixtures of
subplan 15 (20-second pink-noise sources and their lossy, resampled, padded, clipped and filtered
derivatives). No real recordings have been provided yet; when the owner supplies legally obtained
samples, add them to these tables with the same columns and re-check every threshold below.

## Results per fixture

| Fixture | Expected | Raised | Knee | Drop | Notes |
|---------|----------|--------|------|------|-------|
| `src16.flac` | nothing | nothing | — | — | Full band |
| `pad24.flac` | `AUD-PADDED-BITS` | `AUD-PADDED-BITS` | — | — | 16 significant bits of 24 |
| `true96.flac` | nothing | nothing | — | — | 24 significant bits, full band |
| `up96.flac` | `AUD-UPSAMPLED` | `AUD-UPSAMPLED` | 23.2 kHz | 47.7 dB | |
| `fake128.flac` | `AUD-FAKE-LOSSLESS` | `AUD-FAKE-LOSSLESS` | 16.55 kHz | 67.5 dB | 134.4 dB below full scale above the knee: rounding noise only |
| `fake320.flac` | `AUD-FAKE-LOSSLESS` (possible) | `AUD-FAKE-LOSSLESS` | 20.0 kHz | 68.0 dB | Lower confidence by design |
| `fakeAac.flac` | `AUD-FAKE-LOSSLESS` | `AUD-FAKE-LOSSLESS` | 17.2 kHz | 68.5 dB | |
| `fakeOpus.flac` | `AUD-FAKE-LOSSLESS` | `AUD-FAKE-LOSSLESS` | 20.0 kHz | 57.2 dB | |
| `fakeV0.flac` | nothing (known limitation) | nothing | — | — | LAME V0 keeps the full band |
| `infl320.mp3` | `AUD-BITRATE-INFLATED` | `AUD-BITRATE-INFLATED` | 16.55 kHz | 84.0 dB | |
| `m320.mp3` | nothing | nothing | 20.0 kHz | — | The normal 320 kbps lowpass |
| `natural.flac` | nothing | nothing | — | — | A gentle roll-off is not a cutoff |
| `dull.flac` | nothing | nothing | — | — | Steep but not terminal |
| `clip.flac` | `AUD-CLIPPING` | `AUD-CLIPPING` High | — | — | 203 runs per channel, 0.095 % clipped, longest 14 samples |
| `clipatt.flac` | `AUD-CLIPPING` | `AUD-CLIPPING` | — | — | The same runs found at −3 dBFS |
| `clip320.mp3` | `AUD-OVERS` | `AUD-OVERS` | — | — | 2,550 samples above full scale, peak +0.7 dBFS |
| `clicks.flac` | `COR-CLICKS` (when enabled) | `COR-CLICKS` Low, 5 clicks | — | — | Five single-sample errors of 0.5 in noise at −12 dB |
| `drums.flac` | nothing | nothing | — | — | Synthetic kick and snare bursts |

## Counts per rule

| Rule | True positives | False positives | False negatives | Threshold status |
|------|----------------|-----------------|-----------------|------------------|
| `AUD-FAKE-LOSSLESS` | 4 | 0 | 1 (`fakeV0.flac`, a documented limitation) | Starting values kept (drop 30 dB; knees 19.2 and 20.6 kHz) |
| `AUD-UPSAMPLED` | 1 | 0 | 0 | Kept (knee up to 24.5 kHz) |
| `AUD-BITRATE-INFLATED` | 1 | 0 | 0 | Kept (margin 1.5 kHz) |
| `AUD-PADDED-BITS` | 1 | 0 | 0 | Exact rule, no threshold |
| `AUD-CLIPPING` | 2 | 0 | 0 | Kept; tiers are judged on the worst channel |
| `AUD-OVERS` | 1 | 0 | 0 | Kept |
| `COR-DROPOUT` | Unit signals only (588-sample gap: confidence 0.9; faded gap: nothing) | 0 | 0 | Kept |
| `COR-CLICKS` | 1 | 0 | 0 | Ratio 20, minimum step 0.05 of full scale; stays disabled by default |

`AUD-DC`, `AUD-DEAD-CHANNEL`, `AUD-PHASE`, `AUD-SILENT` and `AUD-LONG-SILENCE` are checked by unit
tests on synthetic signals; none of them fired on any fixture above.

## Adjustments made

- `COR-CLICKS`: the local level is the highest of the three block medians around a sample, not their
  median. With the median, the edges of loud noise bursts next to silence were taken for clicks (9 on
  `drums.flac`); with the highest, none, and the five real errors in `clicks.flac` are still found.
- No other threshold changed. The corpus is small and synthetic: treat these values as starting points
  until real recordings have been run.
