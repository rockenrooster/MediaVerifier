# 17 — Bundled FFmpeg

Phase 17. Depends on: nothing; start any time. Required before the first public release (M1).
Unblocks: release pipeline (14), integration tests against the shipped engine (15).

## Goal

MediaVerifier ships its own FFmpeg. It is built from source by this repository's own workflow,
licensed LGPL version 2.1 or later, contains everything needed to read the media the app scans and
nothing it does not use (no network code, no capture devices, almost no encoders), and is pinned and
verified at every step. The app never downloads FFmpeg and never uses another copy, apart from a
developer override.

## Decisions and why

| Decision | Reason |
|----------|--------|
| Build our own instead of shipping a prebuilt download | The common prebuilt Windows builds are either GPL (gyan.dev) or LGPL **version 3** with dozens of extra libraries, network protocols and capture devices (BtbN `lgpl` variants configure with `--enable-version3`; their `win64-lgpl-shared-9.0` zip is 76.5 MB compressed). Each extra library is another licence obligation and more attack surface for an app that opens untrusted files |
| FFmpeg 9.0.2 (tag `n9.0.2`) | Current release (tag verified to exist on 2026-11-16). The behaviour in appendix A was measured on 8.0.1; task FFB-05 re-measures it on this build |
| LGPL v2.1 or later; no `--enable-gpl`, `--enable-version3`, `--enable-nonfree` | Only FFmpeg's own source then needs to be offered. Every filter, decoder, encoder and muxer listed below was checked against the tag's `configure`, `allcodecs.c`, `allformats.c` and `allfilters.c`: none is GPL-only (the GPL-only filters include `cropdetect` and `tinterlace`, which the app does not use) |
| External libraries: zlib, dav1d, libjxl only | zlib: PNG, compressed MOV headers, Matroska compression. dav1d (BSD-2-Clause): AV1 and AVIF; FFmpeg's own `av1` decoder needs hardware. libjxl (BSD-3-Clause): JPEG XL; FFmpeg has no native JPEG XL decoder. None of these needs `--enable-version3` |
| Shared libraries (DLLs) plus `ffmpeg.exe` and `ffprobe.exe` | The code is stored once for both programs, and the LGPL requirement that users can replace the library is met by replaceable DLLs |
| Every decoder, demuxer, parser, bitstream filter and muxer | A verifier must open whatever is in the user's files, including old codecs inside AVI and MKV. Muxers are small and every remux target needs its own |
| No network, devices, hardware acceleration or autodetected libraries | Scanned files can never cause network access, whatever they contain. Software decoding reports errors consistently (subplan 07) |
| Built in BtbN's public Docker build image | It already contains the mingw-w64 cross toolchain and static builds of dav1d, libjxl and zlib with pkg-config files. Using it with our own configure options avoids maintaining a toolchain |

## Source

- FFmpeg 9.0.2 source: the archive of tag `n9.0.2` from `https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n9.0.2.tar.gz`.
- GitHub does not promise that generated archives stay byte-identical, so the first build run mirrors
  the archive into this repository's bundle release (below) as `ffmpeg-9.0.2-source.tar.gz` and
  records its SHA-256 in the lock file. Every later build uses the mirrored copy and verifies the hash.

## Build environment

- Runner: `ubuntu-latest` (Docker is preinstalled).
- Image: `ghcr.io/btbn/ffmpeg-builds/win64-lgpl-shared-9.0`, public (its `latest` tag was verified on
  2026-11-16). Pin it by digest (`@sha256:…`) in the lock file; record the digest the first time the
  workflow runs. If a pinned digest has been deleted, take the current `latest`, record the new digest
  and rebuild; the previous bundle stays available in its release meanwhile.
- The image provides the environment variables used below (`FFBUILD_TARGET_FLAGS`, `CC`, `CXX`, `AR`,
  `RANLIB`, `NM`, `FF_CFLAGS`, `FF_CXXFLAGS`, `FF_LDFLAGS`, `FF_LDEXEFLAGS`, `FF_LIBS`). This is
  exactly how BtbN's own `build.sh` invokes configure; the only difference is that our component
  options replace their `$FF_CONFIGURE`.

## Build script

`deps/ffmpeg/build-ffmpeg.sh`, run inside the container with the source extracted to
`/ffbuild/ffmpeg` and the output going to `/ffbuild/prefix`:

```bash
#!/bin/bash
set -euo pipefail
cd /ffbuild/ffmpeg

./configure \
  --prefix=/ffbuild/prefix \
  --pkg-config-flags="--static" \
  $FFBUILD_TARGET_FLAGS \
  --cc="$CC" --cxx="$CXX" --ar="$AR" --ranlib="$RANLIB" --nm="$NM" \
  --extra-cflags="$FF_CFLAGS" --extra-cxxflags="$FF_CXXFLAGS" \
  --extra-ldflags="$FF_LDFLAGS" --extra-ldexeflags="$FF_LDEXEFLAGS" --extra-libs="$FF_LIBS" \
  --extra-version="mediaverifier-1" \
  --enable-shared --disable-static \
  --disable-debug --disable-doc --disable-ffplay --disable-avdevice \
  --disable-autodetect --disable-network --disable-everything \
  --enable-w32threads --enable-zlib --enable-libdav1d --enable-libjxl \
  --enable-protocol=file,pipe \
  --enable-decoder='*' \
  --enable-demuxer='*' \
  --enable-parser='*' \
  --enable-bsf='*' \
  --enable-muxer='*' \
  --enable-encoder=wrapped_avframe,flac,alac,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_s16be,pcm_s24be,pcm_s32be,png,mjpeg,bmp \
  --enable-filter=scale,crop,split,asplit,concat,trim,atrim,setpts,asetpts,null,anull,format,aformat,aresample,metadata,ametadata,ssim,psnr,siti,blockdetect,blurdetect,idet,yadif,bwdif,freezedetect,blackdetect,volumedetect,ebur128,astats,adeclip,adeclick,select,fps,pad,transpose,hflip,vflip,rotate,showinfo \
  || { cat ffbuild/config.log; exit 1; }

make -j"$(nproc)"
make install
```

How the options behave (from the tag's `configure`):

- `--disable-everything` turns off every component; the `--enable-…=` lists turn chosen ones back
  on. Values are comma-separated shell patterns matched against the component lists, so `'*'` means
  all of that kind. Components whose dependencies are not enabled (network demuxers and muxers,
  hardware decoders, decoders for other external libraries) drop out automatically.
- `--disable-autodetect` stops configure from picking up any other library present in the image, so
  zlib and the threading model must be enabled explicitly (`zlib` is an autodetected library and
  `w32threads` a threading option; both are accepted by the generic `--enable-*` handler).
- Building the `ffmpeg` program automatically adds the filters it needs (`aformat`, `anull`, `atrim`,
  `crop`, `format`, `hflip`, `null`, `rotate`, `transpose`, `trim`, `vflip`); the buffer source and
  sink filters are always built.
- FFmpeg 9.0 has no libpostproc; do not pass `--disable-postproc` (configure would reject it).

What each enabled encoder and filter is for:

| Component | Used by |
|-----------|---------|
| `wrapped_avframe` encoder | `-f null` output: the default video encoder for the null muxer |
| `pcm_*` encoders | WAV pipe for audio analysis, `-f md5` checks, WAV and AIFF repairs |
| `flac`, `alac` encoders | Lossless rewrite and 16-bit conversion (subplan 12) |
| `png`, `mjpeg`, `bmp` encoders | Thumbnails and evidence images |
| `ssim`, `psnr`, `siti`, `blockdetect`, `blurdetect`, `scale`, `crop`, `split`, `concat`, `trim`, `setpts`, `metadata` | Upscale and artifact sampling (subplan 11) |
| `idet`, `yadif`, `bwdif` | Interlace detection and deinterlacing before sampling |
| `freezedetect`, `blackdetect`, `volumedetect` | Deep video checks |
| `ebur128`, `astats`, `adeclip`, `adeclick`, `aresample`, `aformat` | Audio diagnostics and the de-clipped copy |
| `select`, `fps`, `pad`, `transpose`, `hflip`, `vflip`, `rotate`, `format`, `null`, `anull`, `asplit`, `atrim`, `asetpts`, `ametadata`, `showinfo` | Frame selection, orientation, plumbing and diagnostics |

Adding a filter or encoder later is a one-line change here plus a new bundle release.

## Output and packaging

The workflow keeps only what the app uses and lays it out like this (DLL version suffixes are
whatever 9.0.2 produces):

```
ffmpeg/
  ffmpeg.exe
  ffprobe.exe
  avcodec-NN.dll
  avfilter-NN.dll
  avformat-NN.dll
  avutil-NN.dll
  swresample-N.dll
  swscale-N.dll
  LICENSE.txt          COPYING.LGPLv2.1 from the FFmpeg source
  THIRD-PARTY.txt      Licence texts for dav1d, libjxl and every library linked into libjxl, and zlib
  bundle.json
```

Everything else from the install prefix (`include/`, `lib/`, `share/`, import libraries, `.def`
files, pkg-config files) is dropped.

`bundle.json`:

```json
{
  "schemaVersion": 1,
  "ffmpegVersion": "9.0.2",
  "extraVersion": "mediaverifier-1",
  "licence": "LGPL-2.1-or-later",
  "sourceArchive": "ffmpeg-9.0.2-source.tar.gz",
  "sourceSha256": "<64 hex>",
  "buildImage": "ghcr.io/btbn/ffmpeg-builds/win64-lgpl-shared-9.0@sha256:<64 hex>",
  "buildconf": "<output of ffmpeg -hide_banner -buildconf, one string>",
  "files": { "ffmpeg.exe": "<sha256>", "ffprobe.exe": "<sha256>", "avcodec-NN.dll": "<sha256>" },
  "builtUtc": "2026-11-16T00:00:00Z"
}
```

`files` lists every file in the folder except `bundle.json` itself.

## Verification (Windows job, before anything is published)

| Check | Pass condition |
|-------|----------------|
| Version | `ffmpeg -version` first line contains `9.0.2` and `mediaverifier-1` |
| Licence | `ffmpeg -L` states the GNU Lesser General Public License version 2.1 or later; `-buildconf` contains none of `--enable-gpl`, `--enable-version3`, `--enable-nonfree` |
| Protocols | `ffmpeg -protocols` lists exactly `file` and `pipe` for input and output |
| No network | `ffprobe -i https://example.com/a.mp4` fails at once with `Protocol not found` and opens no connection |
| Required components | Present in `-decoders`, `-demuxers`, `-muxers`, `-encoders`, `-filters`: every decoder named in subplan 05's extension table's formats (at minimum `h264 hevc vvc vp8 vp9 libdav1d mpeg2video mpeg4 vc1 wmv3 prores dnxhd ffv1 theora mjpeg png apng gif bmp tiff webp jpeg2000 libjxl flac alac ape wavpack tak tta mp3float aac opus vorbis ac3 eac3 dca truehd wmalossless wmapro wmav2 dsd_lsbf dsd_msbf amrnb amrwb mpc7 mpc8`), the demuxers `mov matroska avi asf flv mpegts mpegps ogg flac mp3 wav aiff w64 caf ape wv tta dsf iff image2 image_jpeg_pipe image_png_pipe image_webp_pipe image_tiff_pipe image_bmp_pipe image_jpegxl_pipe gif`, all encoders and filters in the build script, and the muxers `null wav md5 streamhash framemd5 mp4 mov ipod matroska webm ogg opus flac mp3 adts aiff avi mpegts asf flv ac3 eac3 dts truehd w64 caf image2 apng` |
| Self-contained | The executables and DLLs import only Windows system DLLs (KERNEL32, USER32, ADVAPI32, SHELL32, OLE32, PSAPI, BCRYPT and the `api-ms-win-crt-*` Universal C Runtime that is part of Windows 10 and 11) and each other. No `libgcc`, `libstdc++`, `libwinpthread` or other DLL. Check with `objdump -p` in the Linux job |
| Size | The `ffmpeg` folder is 60 MB or less. Record the actual size in `bundle.json`'s release notes |
| Behaviour | The decode commands in appendix A sections 2–4 produce the same classified signals on fixtures made with the tooling FFmpeg (subplan 15). Differences update appendix A and the log corpus before release |

## Publishing

Workflow `.github/workflows/ffmpeg-bundle.yml`, started by hand with inputs `version` (`9.0.2`),
`revision` (`1`) and optionally `image_digest`:

1. **build** (`ubuntu-latest`): fetch the source (mirrored asset if the release exists, otherwise the
   tag archive), verify or record its SHA-256, run `build-ffmpeg.sh` in the pinned image, package as
   above, write `bundle.json`, upload a workflow artifact.
2. **verify** (`windows-latest`): run every check in the table on the artifact.
3. **publish**: create the release `ffmpeg-bundle-9.0.2-mv1` **as a pre-release and not the latest
   release** (`gh release create … --prerelease --latest=false`). The app's updater reads
   `/releases/latest`, which must always be an app release. Assets:
   - `mediaverifier-ffmpeg-9.0.2-mv1-win64.zip` and its `.sha256`
   - `ffmpeg-9.0.2-source.tar.gz` (the exact source built)
   - `build-ffmpeg.sh` (the script that built it)
   - `bundle.json`
4. Open a pull request that updates `deps/ffmpeg/bundle.lock.json`:

```json
{
  "release": "ffmpeg-bundle-9.0.2-mv1",
  "asset": "mediaverifier-ffmpeg-9.0.2-mv1-win64.zip",
  "sha256": "<64 hex>",
  "sizeBytes": 0,
  "ffmpegVersion": "9.0.2",
  "sourceSha256": "<64 hex>",
  "buildImageDigest": "sha256:<64 hex>"
}
```

## Consumption

- `scripts/Get-FfmpegBundle.ps1` reads the lock file, downloads the asset from this repository's
  release (`gh release download`, or HTTPS in CI), verifies size and SHA-256, and extracts it to
  `deps/ffmpeg/bin/` (ignored by git). It fails closed on any mismatch and never falls back to
  another source. `build.ps1`, CI and the test suite call it.
- `build.ps1` copies the folder into the publish output as `ffmpeg\` (subplan 14).
- The app finds it beside the executable (subplan 04) and checks its files against `bundle.json`
  before the first scan.

## Licence compliance

- MediaVerifier runs FFmpeg as a separate program; FFmpeg's licence does not extend to
  MediaVerifier's own code.
- LGPL 2.1 section 6: the complete corresponding source (the FFmpeg archive and the build script
  with its exact configure command) is offered from the same place as the binaries: a release of the
  same GitHub repository. The *Where to get the source* button (Tools → Media engine, subplan 09)
  opens that release page. Keep every bundle release for as long as any app release that contains it
  is downloadable, and at least three years.
- Users can replace the FFmpeg DLLs; the app's integrity check then reports the media engine as
  changed (it does not silently refuse): `The media engine files were changed. Results may differ from a standard installation.` shown as a Medium info bar on Tools → Media engine, and scanning
  continues.
- `LICENSE.txt` and `THIRD-PARTY.txt` ship inside `ffmpeg\`; the app's `THIRD-PARTY-NOTICES.md`
  (subplan 16) repeats them. The libjxl dependency list (for example highway, brotli and lcms2 or
  skcms) is taken from the build image's dependency scripts for libjxl at the pinned digest, not
  assumed.

## Updating FFmpeg

1. Change `version` (and reset `revision` to 1), run the workflow.
2. Re-run the appendix A experiments with the new bundle and update the appendix and the log corpus.
3. Run the full test suite and the corruption benchmark against the new bundle; investigate any
   detection-rate change.
4. Merge the lock-file pull request; the next app release ships it.

A configure change without a version change increments `revision` (`mv2`, `mv3`, …).

## Development before the first bundle exists

The FFmpeg locator honours the environment variable `MEDIAVERIFIER_FFMPEG_DIR` (a folder containing
`ffmpeg.exe` and `ffprobe.exe`, version 8.0 or newer). It is logged as a warning at startup and is
never shown or offered in the UI. Release builds and CI use the bundle.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| FFB-01 | `deps/ffmpeg/build-ffmpeg.sh` exactly as above, and a local wrapper that runs it in the pinned image | A local run on Linux or WSL produces the layout above |
| FFB-02 | Packaging step and `bundle.json` writer | File hashes in `bundle.json` match the files; nothing extra in the folder |
| FFB-03 | Windows verification job with every check in the table | Fails on purpose when a required filter is removed from the script or `--enable-network` is added |
| FFB-04 | `ffmpeg-bundle.yml` with source mirroring, pre-release publishing and the lock-file pull request | First run publishes `ffmpeg-bundle-9.0.2-mv1`; `/releases/latest` still points at the latest app release (or 404 if none) |
| FFB-05 | Re-run appendix A sections 2–4 and 7–12 with the bundle; update the appendix and corpus | Appendix states the bundle version it was measured with |
| FFB-06 | `scripts/Get-FfmpegBundle.ps1` | Verifies, extracts, idempotent; corrupt download fails closed |
| FFB-07 | `THIRD-PARTY.txt` contents from the image's dependency scripts | Every library linked into the DLLs is listed with its licence text |
| FFB-08 | Size and import checks recorded in the release notes | Folder at most 60 MB; imports as allowed |

## Exit criteria

- The release pipeline (subplan 14) ships the bundle and nothing else that decodes media.
- Integration tests pass against the bundle.
- The bundle release carries the exact source and build script.

## Pitfalls

- Quote the `'*'` patterns so the shell does not expand them.
- `--disable-autodetect` also removes `bcrypt` and similar system-library detections; FFmpeg falls back
  to other sources of randomness, which is fine for decoding. Do not add `--enable-…` for libraries
  the app does not need.
- A pre-release must never become the latest release, or every installed copy's update check breaks.
- The DLL version suffixes change between FFmpeg major versions; nothing in the app may hard-code
  them (the integrity check reads `bundle.json`).
