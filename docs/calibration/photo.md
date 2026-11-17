# Photo rule calibration (subplan 11; task T-09)

Run: `MEDIAVERIFIER_UPDATE_DOCS=1 dotnet test -c Release --filter FullyQualifiedName~VideoPhotoCalibration`
(2026-11-17, the bundled FFmpeg 9.0.2 (mediaverifier-2) as the engine, the tooling FFmpeg for the variants). Corpus: generated photo
variants of a noisy Mandelbrot zoom (fine detail everywhere, like a sharp natural source): native, upscaled from
each lower rung with bilinear, bicubic and Lanczos, and encoded down a ladder. Every number comes from the
product's own providers at Standard depth. Real material (subplan 15, corpus b and c) is measured by
`RealMaterialCalibration`: see [real-material.md](real-material.md).

| Variant | Truth | SSIM after down-up, per rung | Estimated JPEG quality | Raised |
|---------|-------|------------------------------|------------------------|--------|
| `native.jpg` | native | 720: 0.9761, 540: 0.9653, 360: 0.9497, 270: 0.9381 | 91 | nothing |
| `up720-bilinear.jpg` | upscaled-from-720 | 720: 0.9967, 540: 0.9924, 360: 0.9820, 270: 0.9692 | 91 | IMG-UPSCALED/Low |
| `up720-bicubic.jpg` | upscaled-from-720 | 720: 0.9961, 540: 0.9896, 360: 0.9765, 270: 0.9615 | 91 | IMG-UPSCALED/Low |
| `up720-lanczos.jpg` | upscaled-from-720 | 720: 0.9957, 540: 0.9884, 360: 0.9740, 270: 0.9588 | 91 | nothing |
| `up540-bilinear.jpg` | upscaled-from-540 | 720: 0.9971, 540: 0.9963, 360: 0.9867, 270: 0.9760 | 91 | IMG-UPSCALED/Low |
| `up540-bicubic.jpg` | upscaled-from-540 | 720: 0.9967, 540: 0.9952, 360: 0.9810, 270: 0.9673 | 91 | IMG-UPSCALED/Low |
| `up540-lanczos.jpg` | upscaled-from-540 | 720: 0.9968, 540: 0.9946, 360: 0.9787, 270: 0.9639 | 91 | IMG-UPSCALED/Low |
| `q02.jpg` | quality-ladder | 720: 0.9761, 540: 0.9653, 360: 0.9497, 270: 0.9381 | 91 | nothing |
| `q05.jpg` | quality-ladder | 720: 0.9765, 540: 0.9660, 360: 0.9508, 270: 0.9398 | 76 | nothing |
| `q10.jpg` | quality-ladder | 720: 0.9771, 540: 0.9668, 360: 0.9496, 270: 0.9380 | 52 | nothing |
| `q15.jpg` | quality-ladder | 720: 0.9791, 540: 0.9696, 360: 0.9503, 270: 0.9380 | 35 | IMG-HEAVY-COMPRESSION/Low |
| `q20.jpg` | quality-ladder | 720: 0.9815, 540: 0.9727, 360: 0.9520, 270: 0.9392 | 26 | IMG-HEAVY-COMPRESSION/Medium |
| `q25.jpg` | quality-ladder | 720: 0.9834, 540: 0.9758, 360: 0.9541, 270: 0.9410 | 21 | IMG-HEAVY-COMPRESSION/Medium |
| `q31.jpg` | quality-ladder | 720: 0.9858, 540: 0.9799, 360: 0.9578, 270: 0.9447 | 17 | IMG-HEAVY-COMPRESSION/Medium |

## Findings

- Upscale separation: native variants reach at most 0.9761 at any rung; variants upscaled from a candidate rung reach 0.9946 to 0.9967 at that rung. The threshold 0.996 comes from real material, where soft natives come closer (real-material.md); it finds 5 of these 6 upscales.
- JPEG quality: FFmpeg's `-q:v` 2, 5 and 10 estimate as about 91, 76 and 52 and raise nothing; 15 and above estimate below 50 and raise `IMG-HEAVY-COMPRESSION` (Medium below 30).
- The most compressed photo (quality about 17) also looks enlarged; since this calibration that verdict carries lower confidence, as for starved video, and reads "enlarged or over-compressed".

## Thresholds

Current values (the upscale thresholds chosen on real material, see real-material.md):

- `IMG-UPSCALED` ssim = 0.996 (Rebuilt image at least this similar (SSIM))
- `IMG-UPSCALED` minSpatialInfo = 25 (Skip images with less detail than)
- `IMG-HEAVY-COMPRESSION` lowQuality = 50 (Low below JPEG quality)
- `IMG-HEAVY-COMPRESSION` mediumQuality = 30 (Medium below JPEG quality)

