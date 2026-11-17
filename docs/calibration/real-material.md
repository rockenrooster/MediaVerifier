# Calibration on real material (subplan 15, corpus b; tasks T-09 and VP-01)

Run: `.\scripts\Get-CalibrationMedia.ps1`, then with `MEDIAVERIFIER_CALIBRATION_DIR` set to its folder and
`MEDIAVERIFIER_UPDATE_DOCS=1`: `dotnet test -c Release --filter FullyQualifiedName~RealMaterialCalibration`
(2026-11-17, the bundled FFmpeg 9.0.2 (mediaverifier-2) as the engine, the tooling FFmpeg for the variants). Every original is trusted
(`native` or `lossless`) and must raise nothing; each variant is made from it with the tooling FFmpeg and labelled
with what was done to it. Every number comes from the product's own providers and detectors at Standard depth.

Corpus (downloaded at calibration time, never committed):

- `sintel-0105.mkv`, `sintel-0330.mkv`, `sintel-0600.mkv`: CC BY 3.0, (c) copyright Blender Foundation | durian.blender.org
- `tears-0120.mkv`, `tears-0300.mkv`, `tears-0540.mkv`: CC BY 3.0, (CC) Blender Foundation | mango.blender.org
- `photo-01-wildlife.jpg`, `photo-02-flowers.jpg`, `photo-03-heron.jpg`, `photo-04-birds.jpg`, `photo-05-hangar-people.jpg`, `photo-06-hangar.jpg`, `photo-07-portrait.jpg`, `photo-08-portrait.jpg`, `photo-09-campus.jpg`: Public domain, NASA (NASA media usage guidelines)
- `audio-01-piano-aria.flac`, `audio-02-piano-variation.flac`, `audio-03-orchestra-grieg.flac`, `audio-04-strings-suk.flac`, `audio-05-orchestra-beethoven.flac`: Public Domain Mark 1.0 (Musopen)

## Video

| Variant | Truth | Measures | Raised |
|---------|-------|----------|--------|
| `sintel-0105-original.mkv` | native | SSIM 546: 0.9583, 408: 0.9297, 272: 0.8879; SI 21.2 (2/6 usable); block 1.18; bpp 0.280 | nothing |
| `sintel-0105-crf18.mp4` | native | SSIM 546: 0.9607, 408: 0.9339, 272: 0.8941; SI 19.6 (1/6 usable); block 2.17; bpp 0.157 | nothing |
| `sintel-0105-up546-bilinear.mp4` | upscaled-from-546 | SSIM 546: 0.9936, 408: 0.9878, 272: 0.9704; SI 14.2 (1/6 usable); block 2.54; bpp 0.105 | nothing |
| `sintel-0105-up546-bicubic.mp4` | upscaled-from-546 | SSIM 546: 0.9921, 408: 0.9832, 272: 0.9587; SI 15.8 (1/6 usable); block 2.46; bpp 0.115 | nothing |
| `sintel-0105-up546-lanczos.mp4` | upscaled-from-546 | SSIM 546: 0.9915, 408: 0.9811, 272: 0.9536; SI 16.3 (1/6 usable); block 2.53; bpp 0.118 | nothing |
| `sintel-0105-up408-bilinear.mp4` | upscaled-from-408 | SSIM 546: 0.9953, 408: 0.9926, 272: 0.9803; SI 12.8 (1/6 usable); block 2.70; bpp 0.099 | nothing |
| `sintel-0105-up408-bicubic.mp4` | upscaled-from-408 | SSIM 546: 0.9948, 408: 0.9907, 272: 0.9722; SI 14.3 (1/6 usable); block 2.62; bpp 0.110 | nothing |
| `sintel-0105-up408-lanczos.mp4` | upscaled-from-408 | SSIM 546: 0.9947, 408: 0.9898, 272: 0.9682; SI 14.7 (1/6 usable); block 2.61; bpp 0.112 | nothing |
| `sintel-0105-x1.5-lanczos.mp4` | upscaled-from-818 | SSIM 818: 0.9901, 614: 0.9731, 410: 0.9357; SI 14.3 (1/6 usable); block 2.72; bpp 0.129 | nothing |
| `sintel-0105-bpp0.008.mp4` | starved | SSIM 546: 0.9876, 408: 0.9764, 272: 0.9538; SI 15.0 (1/6 usable); block 2.91; bpp 0.011 | VID-STARVED/Medium |
| `sintel-0105-bpp0.016.mp4` | starved | SSIM 546: 0.9792, 408: 0.9622, 272: 0.9323; SI 17.7 (1/6 usable); block 2.55; bpp 0.021 | VID-STARVED/Low |
| `sintel-0105-bpp0.024.mp4` | starved | SSIM 546: 0.9740, 408: 0.9542, 272: 0.9211; SI 18.3 (1/6 usable); block 2.67; bpp 0.032 | VID-STARVED/Low |
| `sintel-0105-bpp0.060.mp4` | native | SSIM 546: 0.9627, 408: 0.9372, 272: 0.8987; SI 19.4 (1/6 usable); block 2.39; bpp 0.077 | nothing |
| `sintel-0105-bpp0.150.mp4` | native | SSIM 546: 0.9610, 408: 0.9342, 272: 0.8942; SI 19.6 (1/6 usable); block 1.90; bpp 0.188 | nothing |
| `sintel-0330-original.mkv` | native | SSIM 546: 0.9712, 408: 0.9492, 272: 0.9148; SI 61.1 (5/6 usable); block 1.53; bpp 0.351 | nothing |
| `sintel-0330-crf18.mp4` | native | SSIM 546: 0.9725, 408: 0.9515, 272: 0.9182; SI 52.8 (5/6 usable); block 1.99; bpp 0.214 | nothing |
| `sintel-0330-up546-bilinear.mp4` | upscaled-from-546 | SSIM 546: 0.9963, 408: 0.9923, 272: 0.9785; SI 45.5 (4/6 usable); block 2.14; bpp 0.159 | VID-UPSCALED/Low |
| `sintel-0330-up546-bicubic.mp4` | upscaled-from-546 | SSIM 546: 0.9958, 408: 0.9903, 272: 0.9727; SI 48.6 (4/6 usable); block 2.12; bpp 0.171 | VID-UPSCALED/Low |
| `sintel-0330-up546-lanczos.mp4` | upscaled-from-546 | SSIM 546: 0.9955, 408: 0.9894, 272: 0.9701; SI 46.9 (4/6 usable); block 2.29; bpp 0.173 | VID-UPSCALED/Medium |
| `sintel-0330-up408-bilinear.mp4` | upscaled-from-408 | SSIM 546: 0.9968, 408: 0.9948, 272: 0.9841; SI 42.2 (4/6 usable); block 2.15; bpp 0.152 | VID-UPSCALED/Low |
| `sintel-0330-up408-bicubic.mp4` | upscaled-from-408 | SSIM 546: 0.9965, 408: 0.9938, 272: 0.9782; SI 44.1 (3/6 usable); block 2.35; bpp 0.164 | nothing |
| `sintel-0330-up408-lanczos.mp4` | upscaled-from-408 | SSIM 546: 0.9965, 408: 0.9932, 272: 0.9758; SI 45.2 (3/6 usable); block 2.18; bpp 0.167 | nothing |
| `sintel-0330-x1.5-lanczos.mp4` | upscaled-from-818 | SSIM 818: 0.9931, 614: 0.9784, 410: 0.9464; SI 41.9 (4/6 usable); block 2.21; bpp 0.170 | VID-UPSCALED/Low |
| `sintel-0330-bpp0.008.mp4` | starved | SSIM 546: 0.9949, 408: 0.9910, 272: 0.9816; SI 45.3 (4/6 usable); block 3.50; bpp 0.008 | VID-BLOCKY/Medium VID-STARVED/Medium VID-UPSCALED/Low |
| `sintel-0330-bpp0.016.mp4` | starved | SSIM 546: 0.9935, 408: 0.9874, 272: 0.9730; SI 48.3 (4/6 usable); block 3.15; bpp 0.015 | VID-STARVED/Medium VID-BLOCKY/Low VID-UPSCALED/Low |
| `sintel-0330-bpp0.024.mp4` | starved | SSIM 546: 0.9854, 408: 0.9733, 272: 0.9513; SI 49.7 (5/6 usable); block 2.83; bpp 0.023 | VID-STARVED/Medium VID-BLOCKY/Low |
| `sintel-0330-bpp0.060.mp4` | native | SSIM 546: 0.9776, 408: 0.9599, 272: 0.9304; SI 51.9 (5/6 usable); block 2.64; bpp 0.059 | nothing |
| `sintel-0330-bpp0.150.mp4` | native | SSIM 546: 0.9725, 408: 0.9514, 272: 0.9179; SI 52.7 (5/6 usable); block 2.16; bpp 0.151 | nothing |
| `sintel-0600-original.mkv` | native | SSIM 546: 0.9460, 408: 0.9013, 272: 0.8302; SI 29.2 (1/6 usable); block 1.46; bpp 0.316 | nothing |
| `sintel-0600-crf18.mp4` | native | SSIM 546: 0.9528, 408: 0.9138, 272: 0.8508; SI 32.4 (2/6 usable); block 1.86; bpp 0.187 | nothing |
| `sintel-0600-up546-bilinear.mp4` | upscaled-from-546 | SSIM 546: 0.9981, 408: 0.9967, 272: 0.9927; SI 28.0 (1/6 usable); block 1.93; bpp 0.128 | nothing |
| `sintel-0600-up546-bicubic.mp4` | upscaled-from-546 | SSIM 546: 0.9978, 408: 0.9958, 272: 0.9906; SI 29.9 (1/6 usable); block 1.97; bpp 0.139 | nothing |
| `sintel-0600-up546-lanczos.mp4` | upscaled-from-546 | SSIM 546: 0.9977, 408: 0.9954, 272: 0.9898; SI 30.2 (1/6 usable); block 1.93; bpp 0.141 | nothing |
| `sintel-0600-up408-bilinear.mp4` | upscaled-from-408 | SSIM 546: 0.9983, 408: 0.9975, 272: 0.9946; SI 25.5 (1/6 usable); block 1.98; bpp 0.122 | nothing |
| `sintel-0600-up408-bicubic.mp4` | upscaled-from-408 | SSIM 546: 0.9982, 408: 0.9972, 272: 0.9931; SI 28.6 (1/6 usable); block 2.00; bpp 0.134 | nothing |
| `sintel-0600-up408-lanczos.mp4` | upscaled-from-408 | SSIM 546: 0.9982, 408: 0.9971, 272: 0.9923; SI 29.1 (1/6 usable); block 1.98; bpp 0.136 | nothing |
| `sintel-0600-x1.5-lanczos.mp4` | upscaled-from-818 | SSIM 818: 0.9907, 614: 0.9669, 410: 0.9099; SI 23.2 (2/6 usable); block 2.15; bpp 0.144 | nothing |
| `sintel-0600-bpp0.008.mp4` | starved | SSIM 546: 0.9987, 408: 0.9980, 272: 0.9964; SI 25.6 (1/6 usable); block 2.97; bpp 0.007 | VID-STARVED/Medium VID-BLOCKY/Low |
| `sintel-0600-bpp0.016.mp4` | starved | SSIM 546: 0.9801, 408: 0.9607, 272: 0.9214; SI 29.6 (2/6 usable); block 2.51; bpp 0.014 | VID-STARVED/Medium VID-BLOCKY/Low |
| `sintel-0600-bpp0.024.mp4` | starved | SSIM 546: 0.9703, 408: 0.9434, 272: 0.8948; SI 30.5 (2/6 usable); block 2.31; bpp 0.022 | VID-STARVED/Low |
| `sintel-0600-bpp0.060.mp4` | native | SSIM 546: 0.9578, 408: 0.9222, 272: 0.8626; SI 32.1 (2/6 usable); block 2.11; bpp 0.056 | nothing |
| `sintel-0600-bpp0.150.mp4` | native | SSIM 546: 0.9526, 408: 0.9136, 272: 0.8508; SI 32.3 (2/6 usable); block 1.80; bpp 0.144 | nothing |
| `tears-0120-original.mkv` | native | SSIM 356: 0.9916, 268: 0.9833, 178: 0.9595; SI 38.1 (2/6 usable); block 2.05; bpp 0.247 | nothing |
| `tears-0120-crf18.mp4` | native | SSIM 356: 0.9908, 268: 0.9823, 178: 0.9576; SI 37.2 (3/6 usable); block 2.08; bpp 0.146 | nothing |
| `tears-0120-up356-bilinear.mp4` | upscaled-from-356 | SSIM 356: 0.9964, 268: 0.9931, 178: 0.9792; SI 32.5 (2/6 usable); block 2.01; bpp 0.118 | nothing |
| `tears-0120-up356-bicubic.mp4` | upscaled-from-356 | SSIM 356: 0.9960, 268: 0.9915, 178: 0.9735; SI 34.6 (2/6 usable); block 2.01; bpp 0.126 | nothing |
| `tears-0120-up356-lanczos.mp4` | upscaled-from-356 | SSIM 356: 0.9958, 268: 0.9907, 178: 0.9712; SI 34.9 (2/6 usable); block 1.99; bpp 0.128 | nothing |
| `tears-0120-up268-bilinear.mp4` | upscaled-from-268 | SSIM 356: 0.9966, 268: 0.9942, 178: 0.9846; SI 30.6 (2/6 usable); block 2.04; bpp 0.113 | nothing |
| `tears-0120-up268-bicubic.mp4` | upscaled-from-268 | SSIM 356: 0.9966, 268: 0.9932, 178: 0.9796; SI 33.2 (2/6 usable); block 1.99; bpp 0.121 | nothing |
| `tears-0120-up268-lanczos.mp4` | upscaled-from-268 | SSIM 356: 0.9966, 268: 0.9931, 178: 0.9773; SI 33.8 (2/6 usable); block 2.02; bpp 0.122 | nothing |
| `tears-0120-x1.5-lanczos.mp4` | upscaled-from-534 | SSIM 534: 0.9960, 400: 0.9917, 266: 0.9793; SI 27.9 (1/6 usable); block 2.20; bpp 0.119 | nothing |
| `tears-0120-bpp0.008.mp4` | starved | SSIM 356: 0.9965, 268: 0.9942, 178: 0.9853; SI 32.0 (2/6 usable); block 2.55; bpp 0.008 | VID-STARVED/Medium |
| `tears-0120-bpp0.016.mp4` | starved | SSIM 356: 0.9956, 268: 0.9919, 178: 0.9786; SI 33.3 (2/6 usable); block 2.22; bpp 0.015 | VID-STARVED/Medium |
| `tears-0120-bpp0.024.mp4` | starved | SSIM 356: 0.9949, 268: 0.9903, 178: 0.9741; SI 34.8 (2/6 usable); block 2.07; bpp 0.023 | VID-STARVED/Low |
| `tears-0120-bpp0.060.mp4` | native | SSIM 356: 0.9929, 268: 0.9859, 178: 0.9641; SI 36.7 (2/6 usable); block 1.86; bpp 0.058 | nothing |
| `tears-0120-bpp0.150.mp4` | native | SSIM 356: 0.9907, 268: 0.9822, 178: 0.9577; SI 37.2 (3/6 usable); block 2.05; bpp 0.148 | nothing |
| `tears-0300-original.mkv` | native | SSIM 356: 0.9815, 268: 0.9593, 178: 0.8991; SI 50.7 (5/6 usable); block 1.36; bpp 0.244 | nothing |
| `tears-0300-crf18.mp4` | native | SSIM 356: 0.9725, 268: 0.9442, 178: 0.8818; SI 72.0 (6/6 usable); block 1.63; bpp 0.218 | nothing |
| `tears-0300-up356-bilinear.mp4` | upscaled-from-356 | SSIM 356: 0.9932, 268: 0.9832, 178: 0.9479; SI 53.8 (5/6 usable); block 1.54; bpp 0.178 | VID-UPSCALED/Low |
| `tears-0300-up356-bicubic.mp4` | upscaled-from-356 | SSIM 356: 0.9920, 268: 0.9779, 178: 0.9323; SI 59.7 (5/6 usable); block 1.53; bpp 0.191 | VID-UPSCALED/Low |
| `tears-0300-up356-lanczos.mp4` | upscaled-from-356 | SSIM 356: 0.9914, 268: 0.9753, 178: 0.9252; SI 61.3 (5/6 usable); block 1.52; bpp 0.195 | VID-UPSCALED/Low |
| `tears-0300-up268-bilinear.mp4` | upscaled-from-268 | SSIM 356: 0.9947, 268: 0.9887, 178: 0.9637; SI 48.9 (5/6 usable); block 1.50; bpp 0.175 | VID-UPSCALED/Low |
| `tears-0300-up268-bicubic.mp4` | upscaled-from-268 | SSIM 356: 0.9947, 268: 0.9865, 178: 0.9521; SI 54.6 (5/6 usable); block 1.46; bpp 0.186 | VID-UPSCALED/Low |
| `tears-0300-up268-lanczos.mp4` | upscaled-from-268 | SSIM 356: 0.9949, 268: 0.9860, 178: 0.9461; SI 56.1 (5/6 usable); block 1.48; bpp 0.188 | VID-UPSCALED/Low |
| `tears-0300-x1.5-lanczos.mp4` | upscaled-from-534 | SSIM 534: 0.9935, 400: 0.9801, 266: 0.9396; SI 56.7 (5/6 usable); block 1.67; bpp 0.179 | VID-UPSCALED/Low |
| `tears-0300-bpp0.008.mp4` | starved | SSIM 356: 0.9924, 268: 0.9868, 178: 0.9630; SI 51.6 (5/6 usable); block 2.74; bpp 0.008 | VID-STARVED/Medium VID-BLOCKY/Low VID-UPSCALED/Low |
| `tears-0300-bpp0.016.mp4` | starved | SSIM 356: 0.9849, 268: 0.9720, 178: 0.9391; SI 60.7 (5/6 usable); block 1.88; bpp 0.016 | VID-STARVED/Medium |
| `tears-0300-bpp0.024.mp4` | starved | SSIM 356: 0.9800, 268: 0.9618, 178: 0.9178; SI 64.2 (5/6 usable); block 1.61; bpp 0.024 | VID-STARVED/Low |
| `tears-0300-bpp0.060.mp4` | native | SSIM 356: 0.9751, 268: 0.9506, 178: 0.8945; SI 70.2 (6/6 usable); block 1.46; bpp 0.058 | nothing |
| `tears-0300-bpp0.150.mp4` | native | SSIM 356: 0.9724, 268: 0.9446, 178: 0.8829; SI 71.7 (6/6 usable); block 1.42; bpp 0.144 | nothing |
| `tears-0540-original.mkv` | native | SSIM 356: 0.9838, 268: 0.9653, 178: 0.9187; SI 72.5 (6/6 usable); block 1.35; bpp 0.247 | nothing |
| `tears-0540-crf18.mp4` | native | SSIM 356: 0.9869, 268: 0.9702, 178: 0.9246; SI 69.6 (6/6 usable); block 1.54; bpp 0.179 | nothing |
| `tears-0540-up356-bilinear.mp4` | upscaled-from-356 | SSIM 356: 0.9954, 268: 0.9896, 178: 0.9653; SI 54.0 (6/6 usable); block 1.46; bpp 0.134 | VID-UPSCALED/Medium |
| `tears-0540-up356-bicubic.mp4` | upscaled-from-356 | SSIM 356: 0.9949, 268: 0.9866, 178: 0.9547; SI 60.5 (6/6 usable); block 1.41; bpp 0.147 | VID-UPSCALED/Medium |
| `tears-0540-up356-lanczos.mp4` | upscaled-from-356 | SSIM 356: 0.9945, 268: 0.9850, 178: 0.9501; SI 62.1 (6/6 usable); block 1.46; bpp 0.151 | VID-UPSCALED/Medium |
| `tears-0540-up268-bilinear.mp4` | upscaled-from-268 | SSIM 356: 0.9955, 268: 0.9919, 178: 0.9746; SI 48.5 (6/6 usable); block 1.54; bpp 0.131 | VID-UPSCALED/Medium |
| `tears-0540-up268-bicubic.mp4` | upscaled-from-268 | SSIM 356: 0.9957, 268: 0.9907, 178: 0.9667; SI 54.8 (6/6 usable); block 1.47; bpp 0.142 | VID-UPSCALED/Medium |
| `tears-0540-up268-lanczos.mp4` | upscaled-from-268 | SSIM 356: 0.9959, 268: 0.9903, 178: 0.9626; SI 56.8 (6/6 usable); block 1.46; bpp 0.144 | VID-UPSCALED/Medium |
| `tears-0540-x1.5-lanczos.mp4` | upscaled-from-534 | SSIM 534: 0.9953, 400: 0.9888, 266: 0.9633; SI 53.5 (6/6 usable); block 1.57; bpp 0.150 | VID-UPSCALED/Medium |
| `tears-0540-bpp0.008.mp4` | starved | SSIM 356: 0.9936, 268: 0.9880, 178: 0.9665; SI 52.6 (5/6 usable); block 2.46; bpp 0.006 | VID-STARVED/Medium VID-UPSCALED/Low |
| `tears-0540-bpp0.016.mp4` | starved | SSIM 356: 0.9907, 268: 0.9798, 178: 0.9456; SI 62.5 (6/6 usable); block 1.73; bpp 0.013 | VID-STARVED/Medium VID-UPSCALED/Low |
| `tears-0540-bpp0.024.mp4` | starved | SSIM 356: 0.9894, 268: 0.9768, 178: 0.9390; SI 66.0 (6/6 usable); block 1.55; bpp 0.020 | VID-STARVED/Medium |
| `tears-0540-bpp0.060.mp4` | native | SSIM 356: 0.9879, 268: 0.9722, 178: 0.9281; SI 69.0 (6/6 usable); block 1.45; bpp 0.052 | nothing |
| `tears-0540-bpp0.150.mp4` | native | SSIM 356: 0.9872, 268: 0.9705, 178: 0.9251; SI 69.6 (6/6 usable); block 1.41; bpp 0.142 | nothing |

- Originals: 12, accused: 0.
- Upscales (`VID-UPSCALED` or `IMG-UPSCALED`): 19 of 42 found.
- `VID-STARVED`: 18 of 18 found.
- Adequate re-encodes (expected to raise nothing): 12, accused: 0.

## Photos

| Variant | Truth | Measures | Raised |
|---------|-------|----------|--------|
| `photo-01-wildlife-original.jpg` | native | SSIM 2874: 0.9591, 2156: 0.9253, 1438: 0.8751, 1078: 0.8303; SI 41.8 (1/1 usable); quality 99 | nothing |
| `photo-01-wildlife-q2.jpg` | native | SSIM 2874: 0.9679, 2156: 0.9392, 1438: 0.8929, 1078: 0.8492; SI 41.7 (1/1 usable); quality 91 | nothing |
| `photo-01-wildlife-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 2874: 0.9953, 2156: 0.9903, 1438: 0.9708, 1078: 0.9394; SI 36.3 (1/1 usable); quality 91 | nothing |
| `photo-01-wildlife-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 2874: 0.9938, 2156: 0.9857, 1438: 0.9596, 1078: 0.9211; SI 38.8 (1/1 usable); quality 91 | nothing |
| `photo-01-wildlife-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 2874: 0.9928, 2156: 0.9829, 1438: 0.9535, 1078: 0.9135; SI 39.2 (1/1 usable); quality 91 | nothing |
| `photo-01-wildlife-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 2874: 0.9960, 2156: 0.9933, 1438: 0.9793, 1078: 0.9532; SI 34.1 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-01-wildlife-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 2874: 0.9949, 2156: 0.9902, 1438: 0.9683, 1078: 0.9337; SI 37.6 (1/1 usable); quality 91 | nothing |
| `photo-01-wildlife-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 2874: 0.9947, 2156: 0.9891, 1438: 0.9634, 1078: 0.9255; SI 38.4 (1/1 usable); quality 91 | nothing |
| `photo-01-wildlife-q15.jpg` | heavily-compressed | SSIM 2874: 0.9919, 2156: 0.9847, 1438: 0.9608, 1078: 0.9267; SI 42.1 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-02-flowers-original.jpg` | native | no usable frames; quality 97; also COR-TRAILING | nothing |
| `photo-02-flowers-q2.jpg` | native | no usable frames; SI 16.8 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-up2-3-bilinear.jpg` | upscaled-from-2-3 | no usable frames; SI 15.5 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-up2-3-bicubic.jpg` | upscaled-from-2-3 | no usable frames; SI 16.0 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-up2-3-lanczos.jpg` | upscaled-from-2-3 | no usable frames; SI 16.1 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-up1-2-bilinear.jpg` | upscaled-from-1-2 | no usable frames; SI 15.1 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-up1-2-bicubic.jpg` | upscaled-from-1-2 | no usable frames; SI 15.9 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-up1-2-lanczos.jpg` | upscaled-from-1-2 | no usable frames; SI 16.1 (0/1 usable); quality 91 | nothing |
| `photo-02-flowers-q15.jpg` | heavily-compressed | no usable frames; SI 17.4 (0/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-03-heron-original.jpg` | native | SSIM 1334: 0.9843, 1000: 0.9729, 666: 0.9533, 500: 0.9345; SI 31.7 (1/1 usable); quality 99 | nothing |
| `photo-03-heron-q2.jpg` | native | SSIM 1334: 0.9911, 1000: 0.9837, 666: 0.9675, 500: 0.9502; SI 31.8 (1/1 usable); quality 91 | nothing |
| `photo-03-heron-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 1334: 0.9964, 1000: 0.9948, 666: 0.9881, 500: 0.9768; SI 28.9 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-03-heron-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 1334: 0.9958, 1000: 0.9934, 666: 0.9841, 500: 0.9700; SI 30.4 (1/1 usable); quality 91 | nothing |
| `photo-03-heron-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 1334: 0.9955, 1000: 0.9926, 666: 0.9821, 500: 0.9675; SI 30.5 (1/1 usable); quality 91 | nothing |
| `photo-03-heron-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 1334: 0.9966, 1000: 0.9955, 666: 0.9902, 500: 0.9810; SI 27.5 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-03-heron-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 1334: 0.9958, 1000: 0.9939, 666: 0.9852, 500: 0.9717; SI 30.1 (1/1 usable); quality 91 | nothing |
| `photo-03-heron-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 1334: 0.9955, 1000: 0.9932, 666: 0.9829, 500: 0.9680; SI 30.5 (1/1 usable); quality 91 | nothing |
| `photo-03-heron-q15.jpg` | heavily-compressed | SSIM 1334: 0.9949, 1000: 0.9931, 666: 0.9840, 500: 0.9740; SI 32.8 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-04-birds-original.jpg` | native | SSIM 1334: 0.9937, 1000: 0.9859, 666: 0.9534, 500: 0.8915; SI 45.6 (1/1 usable); quality 99 | nothing |
| `photo-04-birds-q2.jpg` | native | SSIM 1334: 0.9952, 1000: 0.9888, 666: 0.9577, 500: 0.8968; SI 45.4 (1/1 usable); quality 91 | nothing |
| `photo-04-birds-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 1334: 0.9972, 1000: 0.9946, 666: 0.9756, 500: 0.9281; SI 40.2 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-04-birds-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 1334: 0.9973, 1000: 0.9938, 666: 0.9703, 500: 0.9161; SI 42.7 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-04-birds-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 1334: 0.9972, 1000: 0.9932, 666: 0.9680, 500: 0.9124; SI 43.1 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-04-birds-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 1334: 0.9973, 1000: 0.9958, 666: 0.9802, 500: 0.9393; SI 38.1 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-04-birds-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 1334: 0.9971, 1000: 0.9952, 666: 0.9733, 500: 0.9218; SI 42.1 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-04-birds-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 1334: 0.9973, 1000: 0.9945, 666: 0.9703, 500: 0.9149; SI 42.9 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-04-birds-q15.jpg` | heavily-compressed | SSIM 1334: 0.9900, 1000: 0.9844, 666: 0.9520, 500: 0.8953; SI 45.0 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-05-hangar-people-original.jpg` | native | SSIM 2384: 0.8971, 1788: 0.8283, 1192: 0.7727, 894: 0.7503; SI 30.6 (1/1 usable); quality 99 | nothing |
| `photo-05-hangar-people-q2.jpg` | native | SSIM 2384: 0.9122, 1788: 0.8509, 1192: 0.7995, 894: 0.7781; SI 30.3 (1/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-up2-3-bilinear.jpg` | upscaled-from-2-3 | no usable frames; SI 21.8 (0/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-up2-3-bicubic.jpg` | upscaled-from-2-3 | no usable frames; SI 24.0 (0/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-up2-3-lanczos.jpg` | upscaled-from-2-3 | no usable frames; SI 24.6 (0/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-up1-2-bilinear.jpg` | upscaled-from-1-2 | no usable frames; SI 19.9 (0/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-up1-2-bicubic.jpg` | upscaled-from-1-2 | no usable frames; SI 22.2 (0/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-up1-2-lanczos.jpg` | upscaled-from-1-2 | no usable frames; SI 22.9 (0/1 usable); quality 91 | nothing |
| `photo-05-hangar-people-q15.jpg` | heavily-compressed | SSIM 2384: 0.9893, 1788: 0.9837, 1192: 0.9715, 894: 0.9608; SI 27.3 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-06-hangar-original.jpg` | native | SSIM 2728: 0.9623, 2046: 0.9369, 1364: 0.9097, 1024: 0.8923; SI 44.8 (1/1 usable); quality 99 | nothing |
| `photo-06-hangar-q2.jpg` | native | SSIM 2728: 0.9747, 2046: 0.9545, 1364: 0.9300, 1024: 0.9132; SI 44.8 (1/1 usable); quality 91 | nothing |
| `photo-06-hangar-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 2728: 0.9957, 2046: 0.9929, 1364: 0.9839, 1024: 0.9720; SI 39.8 (1/1 usable); quality 91 | nothing |
| `photo-06-hangar-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 2728: 0.9945, 2046: 0.9899, 1364: 0.9766, 1024: 0.9622; SI 42.1 (1/1 usable); quality 91 | nothing |
| `photo-06-hangar-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 2728: 0.9939, 2046: 0.9881, 1364: 0.9729, 1024: 0.9579; SI 42.5 (1/1 usable); quality 91 | nothing |
| `photo-06-hangar-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 2728: 0.9967, 2046: 0.9945, 1364: 0.9887, 1024: 0.9795; SI 37.7 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-06-hangar-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 2728: 0.9963, 2046: 0.9933, 1364: 0.9846, 1024: 0.9721; SI 40.0 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-06-hangar-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 2728: 0.9961, 2046: 0.9927, 1364: 0.9823, 1024: 0.9686; SI 40.5 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-06-hangar-q15.jpg` | heavily-compressed | SSIM 2728: 0.9926, 2046: 0.9867, 1364: 0.9735, 1024: 0.9591; SI 45.3 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-07-portrait-original.jpg` | native | SSIM 5346: 0.9900, 4010: 0.9795, 2672: 0.9489, 2004: 0.9225; SI 46.6 (1/1 usable); quality 84 | nothing |
| `photo-07-portrait-q2.jpg` | native | SSIM 5346: 0.9905, 4010: 0.9809, 2672: 0.9528, 2004: 0.9280; SI 46.4 (1/1 usable); quality 91 | nothing |
| `photo-07-portrait-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 5346: 0.9954, 4010: 0.9925, 2672: 0.9796, 2004: 0.9633; SI 39.2 (1/1 usable); quality 91 | nothing |
| `photo-07-portrait-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 5346: 0.9947, 4010: 0.9907, 2672: 0.9729, 2004: 0.9527; SI 42.4 (1/1 usable); quality 91 | nothing |
| `photo-07-portrait-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 5346: 0.9944, 4010: 0.9896, 2672: 0.9699, 2004: 0.9489; SI 43.1 (1/1 usable); quality 91 | nothing |
| `photo-07-portrait-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 5346: 0.9962, 4010: 0.9944, 2672: 0.9859, 2004: 0.9734; SI 36.3 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-07-portrait-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 5346: 0.9958, 4010: 0.9934, 2672: 0.9812, 2004: 0.9644; SI 39.7 (1/1 usable); quality 91 | nothing |
| `photo-07-portrait-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 5346: 0.9957, 4010: 0.9930, 2672: 0.9789, 2004: 0.9605; SI 40.6 (1/1 usable); quality 91 | nothing |
| `photo-07-portrait-q15.jpg` | heavily-compressed | SSIM 5346: 0.9932, 4010: 0.9913, 2672: 0.9764, 2004: 0.9599; SI 45.8 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-08-portrait-original.jpg` | native | SSIM 5712: 0.9932, 4284: 0.9874, 2856: 0.9681, 2142: 0.9476; SI 45.9 (1/1 usable); quality 84 | nothing |
| `photo-08-portrait-q2.jpg` | native | SSIM 5712: 0.9934, 4284: 0.9881, 2856: 0.9701, 2142: 0.9507; SI 45.7 (1/1 usable); quality 91 | nothing |
| `photo-08-portrait-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 5712: 0.9961, 4284: 0.9943, 2856: 0.9851, 2142: 0.9717; SI 38.7 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-08-portrait-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 5712: 0.9956, 4284: 0.9933, 2856: 0.9811, 2142: 0.9650; SI 41.8 (1/1 usable); quality 91 | nothing |
| `photo-08-portrait-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 5712: 0.9954, 4284: 0.9927, 2856: 0.9794, 2142: 0.9628; SI 42.4 (1/1 usable); quality 91 | nothing |
| `photo-08-portrait-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 5712: 0.9966, 4284: 0.9954, 2856: 0.9891, 2142: 0.9787; SI 35.9 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-08-portrait-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 5712: 0.9963, 4284: 0.9947, 2856: 0.9859, 2142: 0.9719; SI 39.4 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-08-portrait-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 5712: 0.9963, 4284: 0.9945, 2856: 0.9844, 2142: 0.9692; SI 40.3 (1/1 usable); quality 91 | IMG-UPSCALED/Low |
| `photo-08-portrait-q15.jpg` | heavily-compressed | SSIM 5712: 0.9935, 4284: 0.9920, 2856: 0.9787, 2142: 0.9667; SI 45.0 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |
| `photo-09-campus-original.jpg` | native | SSIM 2554: 0.9747, 1916: 0.9431, 1276: 0.8729, 958: 0.8074; SI 69.4 (1/1 usable); quality 99 | nothing |
| `photo-09-campus-q2.jpg` | native | SSIM 2554: 0.9791, 1916: 0.9510, 1276: 0.8842, 958: 0.8200; SI 69.0 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-up2-3-bilinear.jpg` | upscaled-from-2-3 | SSIM 2554: 0.9944, 1916: 0.9860, 1276: 0.9514, 958: 0.9005; SI 53.6 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-up2-3-bicubic.jpg` | upscaled-from-2-3 | SSIM 2554: 0.9935, 1916: 0.9817, 1276: 0.9369, 958: 0.8775; SI 59.3 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-up2-3-lanczos.jpg` | upscaled-from-2-3 | SSIM 2554: 0.9929, 1916: 0.9792, 1276: 0.9299, 958: 0.8689; SI 60.7 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-up1-2-bilinear.jpg` | upscaled-from-1-2 | SSIM 2554: 0.9949, 1916: 0.9898, 1276: 0.9649, 958: 0.9231; SI 48.0 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-up1-2-bicubic.jpg` | upscaled-from-1-2 | SSIM 2554: 0.9951, 1916: 0.9884, 1276: 0.9545, 958: 0.9024; SI 54.8 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-up1-2-lanczos.jpg` | upscaled-from-1-2 | SSIM 2554: 0.9953, 1916: 0.9876, 1276: 0.9489, 958: 0.8926; SI 56.8 (1/1 usable); quality 91 | nothing |
| `photo-09-campus-q15.jpg` | heavily-compressed | SSIM 2554: 0.9849, 1916: 0.9652, 1276: 0.9160, 958: 0.8589; SI 67.0 (1/1 usable); quality 35 | IMG-HEAVY-COMPRESSION/Low |

- Originals: 18, accused: 0.
- Upscales (`VID-UPSCALED` or `IMG-UPSCALED`): 17 of 54 found.
- `IMG-HEAVY-COMPRESSION`: 9 of 9 found.

## Audio

| Variant | Truth | Measures | Raised |
|---------|-------|----------|--------|
| `audio-01-piano-aria-original.flac` | lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | AUD-PADDED-BITS/Medium |
| `audio-01-piano-aria-fake128.flac` | fake-lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-01-piano-aria-fake320.flac` | fake-lossless (320 kbit/s) | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-01-piano-aria-fakeaac.flac` | fake-lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-01-piano-aria-fakeopus.flac` | fake-lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-01-piano-aria-inflated320.mp3` | inflated | knee none, drop 28.5 dB, bits -1, clipped runs 0 | nothing |
| `audio-01-piano-aria-up96.flac` | upsampled | knee none, drop 20.1 dB, bits 24, clipped runs 0 | nothing |
| `audio-01-piano-aria-clip.flac` | clipped | knee none, drop 0.0 dB, bits 16, clipped runs 162 | AUD-CLIPPING/Medium |
| `audio-02-piano-variation-original.flac` | lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | AUD-PADDED-BITS/Medium |
| `audio-02-piano-variation-fake128.flac` | fake-lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-02-piano-variation-fake320.flac` | fake-lossless (320 kbit/s) | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-02-piano-variation-fakeaac.flac` | fake-lossless | knee none, drop 15.3 dB, bits 16, clipped runs 0 | nothing |
| `audio-02-piano-variation-fakeopus.flac` | fake-lossless | knee none, drop 0.0 dB, bits 16, clipped runs 0 | nothing |
| `audio-02-piano-variation-inflated320.mp3` | inflated | knee none, drop 0.0 dB, bits -1, clipped runs 0 | nothing |
| `audio-02-piano-variation-up96.flac` | upsampled | knee none, drop 0.0 dB, bits 24, clipped runs 0 | nothing |
| `audio-02-piano-variation-clip.flac` | clipped | knee none, drop 0.0 dB, bits 16, clipped runs 10432 | AUD-CLIPPING/High |
| `audio-03-orchestra-grieg-original.flac` | lossless | knee none, drop 0.0 dB, bits 24, clipped runs 3 | nothing |
| `audio-03-orchestra-grieg-fake128.flac` | fake-lossless | knee 16.5 kHz, drop 63.7 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/High |
| `audio-03-orchestra-grieg-fake320.flac` | fake-lossless (320 kbit/s) | knee 20.3 kHz, drop 60.9 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/Medium |
| `audio-03-orchestra-grieg-fakeaac.flac` | fake-lossless | knee 18.0 kHz, drop 44.5 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/High |
| `audio-03-orchestra-grieg-fakeopus.flac` | fake-lossless | knee 20.0 kHz, drop 48.3 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/Medium |
| `audio-03-orchestra-grieg-inflated320.mp3` | inflated | knee 16.5 kHz, drop 75.2 dB, bits -1, clipped runs 0 | AUD-BITRATE-INFLATED/Medium |
| `audio-03-orchestra-grieg-up96.flac` | upsampled | knee none, drop 23.8 dB, bits 24, clipped runs 0 | nothing |
| `audio-03-orchestra-grieg-clip.flac` | clipped | knee none, drop 0.0 dB, bits 16, clipped runs 48766 | AUD-CLIPPING/High |
| `audio-04-strings-suk-original.flac` | lossless | knee none, drop 22.2 dB, bits 24, clipped runs 0 | nothing |
| `audio-04-strings-suk-fake128.flac` | fake-lossless | knee none, drop 23.2 dB, bits 16, clipped runs 0 | nothing |
| `audio-04-strings-suk-fake320.flac` | fake-lossless (320 kbit/s) | knee none, drop 22.1 dB, bits 16, clipped runs 0 | nothing |
| `audio-04-strings-suk-fakeaac.flac` | fake-lossless | knee none, drop 22.5 dB, bits 16, clipped runs 0 | nothing |
| `audio-04-strings-suk-fakeopus.flac` | fake-lossless | knee none, drop 19.9 dB, bits 16, clipped runs 0 | nothing |
| `audio-04-strings-suk-inflated320.mp3` | inflated | knee none, drop 23.1 dB, bits -1, clipped runs 0 | nothing |
| `audio-04-strings-suk-up96.flac` | upsampled | knee none, drop 22.2 dB, bits 24, clipped runs 0 | nothing |
| `audio-04-strings-suk-clip.flac` | clipped | knee none, drop 0.0 dB, bits 16, clipped runs 54366 | AUD-CLIPPING/High |
| `audio-05-orchestra-beethoven-original.flac` | lossless | knee none, drop 0.0 dB, bits 24, clipped runs 5 | nothing |
| `audio-05-orchestra-beethoven-fake128.flac` | fake-lossless | knee 16.1 kHz, drop 32.3 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/High |
| `audio-05-orchestra-beethoven-fake320.flac` | fake-lossless (320 kbit/s) | knee none, drop 26.3 dB, bits 16, clipped runs 0 | nothing |
| `audio-05-orchestra-beethoven-fakeaac.flac` | fake-lossless | knee 17.2 kHz, drop 34.5 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/High |
| `audio-05-orchestra-beethoven-fakeopus.flac` | fake-lossless | knee 20.0 kHz, drop 32.0 dB, bits 16, clipped runs 0 | AUD-FAKE-LOSSLESS/Medium |
| `audio-05-orchestra-beethoven-inflated320.mp3` | inflated | knee 16.1 kHz, drop 51.7 dB, bits -1, clipped runs 0 | AUD-BITRATE-INFLATED/Medium |
| `audio-05-orchestra-beethoven-up96.flac` | upsampled | knee none, drop 0.0 dB, bits 24, clipped runs 0 | nothing |
| `audio-05-orchestra-beethoven-clip.flac` | clipped | knee none, drop 0.0 dB, bits 16, clipped runs 32442 | AUD-CLIPPING/High |

- Originals: 5, accused: 0.
- `AUD-FAKE-LOSSLESS`: 7 of 20 found.
- `AUD-BITRATE-INFLATED`: 2 of 5 found.
- `AUD-UPSAMPLED`: 0 of 5 found.
- `AUD-CLIPPING`: 5 of 5 found.

