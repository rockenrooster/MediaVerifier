# Video rule calibration (subplan 11; task T-09)

Run: `MEDIAVERIFIER_UPDATE_DOCS=1 dotnet test -c Release --filter FullyQualifiedName~VideoPhotoCalibration`
(2026-11-17, the bundled FFmpeg 9.0.2 (mediaverifier-2) as the engine, the tooling FFmpeg for the variants). Corpus: generated video
variants of a noisy Mandelbrot zoom (fine detail everywhere, like a sharp natural source): native, upscaled from
each lower rung with bilinear, bicubic and Lanczos, and encoded down a ladder. Every number comes from the
product's own providers at Standard depth. Real material (subplan 15, corpus b and c) is measured by
`RealMaterialCalibration`: see [real-material.md](real-material.md).

| Variant | Truth | P10 SSIM after down-up, per rung | H.264-equivalent bpp | Median block score | Raised |
|---------|-------|-----------------------------------|----------------------|--------------------|--------|
| `native-1080.mp4` | native | 720: 0.9566, 540: 0.9395, 360: 0.9185 | 0.235 | 1.23 | nothing |
| `up900-bilinear.mp4` | upscaled-from-900 (between rungs) | 720: 0.9872, 540: 0.9756, 360: 0.9554 | 0.163 | 1.78 | nothing |
| `up900-bicubic.mp4` | upscaled-from-900 (between rungs) | 720: 0.9823, 540: 0.9673, 360: 0.9444 | 0.199 | 1.63 | nothing |
| `up900-lanczos.mp4` | upscaled-from-900 (between rungs) | 720: 0.9804, 540: 0.9635, 360: 0.9398 | 0.213 | 1.66 | nothing |
| `up720-bilinear.mp4` | upscaled-from-720 | 720: 0.9945, 540: 0.9824, 360: 0.9625 | 0.170 | 1.76 | VID-UPSCALED/Medium |
| `up720-bicubic.mp4` | upscaled-from-720 | 720: 0.9926, 540: 0.9752, 360: 0.9498 | 0.214 | 1.72 | VID-UPSCALED/Medium VID-INFLATED/Low |
| `up720-lanczos.mp4` | upscaled-from-720 | 720: 0.9913, 540: 0.9723, 360: 0.9443 | 0.228 | 1.67 | VID-UPSCALED/Medium VID-INFLATED/Low |
| `up540-bilinear.mp4` | upscaled-from-540 | 720: 0.9953, 540: 0.9928, 360: 0.9729 | 0.210 | 1.86 | VID-UPSCALED/Medium VID-INFLATED/Low |
| `up540-bicubic.mp4` | upscaled-from-540 | 720: 0.9941, 540: 0.9903, 360: 0.9609 | 0.238 | 1.75 | VID-UPSCALED/Medium VID-INFLATED/Low |
| `up540-lanczos.mp4` | upscaled-from-540 | 720: 0.9945, 540: 0.9892, 360: 0.9577 | 0.238 | 1.68 | VID-UPSCALED/Medium VID-INFLATED/Low |
| `rate250k.mp4` | starved | no usable frames | 0.008 | 3.06 | VID-STARVED/Medium |
| `rate500k.mp4` | starved | 720: 0.9812, 540: 0.9733, 360: 0.9606 | 0.016 | 2.49 | VID-STARVED/Medium |
| `rate1000k.mp4` | starved | 720: 0.9702, 540: 0.9578, 360: 0.9401 | 0.024 | 2.12 | VID-STARVED/Low |
| `rate2500k.mp4` | native | 720: 0.9673, 540: 0.9533, 360: 0.9340 | 0.060 | 1.81 | nothing |
| `rate6000k.mp4` | native | 720: 0.9605, 540: 0.9447, 360: 0.9242 | 0.154 | 1.50 | nothing |
| `rate20000k.mp4` | native | 720: 0.9570, 540: 0.9400, 360: 0.9191 | 0.476 | 1.33 | nothing |
| `crf35.mp4` | heavily-compressed | 720: 0.9704, 540: 0.9580, 360: 0.9402 | 0.021 | 2.26 | VID-STARVED/Low |
| `crf45.mp4` | heavily-compressed | no usable frames | 0.017 | 3.03 | VID-STARVED/Medium |
| `crf51.mp4` | heavily-compressed | no usable frames | 0.009 | 3.68 | VID-STARVED/Medium |

## Findings

- Upscale separation: native variants reach at most 0.9673 at any rung; variants upscaled from a candidate rung reach 0.9892 to 0.9945 at that rung. The threshold 0.990 comes from real material, where soft natives come closer (real-material.md); it finds 6 of these 6 upscales.
- Bitrate: starved encodes reach at most 0.024 bits per pixel and adequate ones at least 0.060; `VID-STARVED` starts below 0.035.
- An upscale from 900 lines lies between the candidate rungs (720, 540, 360 for a 1080-line frame): a soft scaler still shows at 720, sharper ones do not. This is a limit of the method, not of the threshold.
- Heavily compressed encodes (CRF 45 and 51, 250 kbit/s) lose detail the way an upscale does, so they can also be called upscaled; that finding has lower confidence and reads "upscaled or heavily compressed" next to `VID-STARVED` and `VID-BLOCKY`.
- Upscales at a normal bitrate also raise `VID-INFLATED` (a high bitrate on a weak picture), as intended.

## Thresholds

Current values (the upscale thresholds chosen on real material, see real-material.md):

- `VID-UPSCALED` ssim = 0.99 (Rebuilt picture at least this similar (SSIM))
- `VID-UPSCALED` minSpatialInfo = 40 (Use frames with at least this much detail)
- `VID-STARVED` lowBpp = 0.035 (Low below)
- `VID-STARVED` mediumBpp = 0.02 (Medium below)
- `VID-BLOCKY` low = 2.6 (Low from a block score of)
- `VID-BLOCKY` medium = 3.4 (Medium from a block score of)
- `VID-BLOCKY` blur = 8 (or blur score of)

