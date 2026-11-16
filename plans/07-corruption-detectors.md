# 07 — Corruption detectors

Phase 7. Depends on: phase 5 (engine) and phase 4 (FFmpeg). Unblocks: M1.

## Goal

Find damaged files: broken containers, truncation, decoder errors, checksum mismatches and stalled
decodes, for photos, video and audio, at each scan depth. Every command here was run against real
corrupted fixtures; see [appendix A](appendix-a-ffmpeg-verified-behavior.md).

## What each layer can and cannot catch

Set expectations in code comments, tests and UI copy accordingly.

| Damage | Structure check | Decode | Embedded checksum | Fingerprint (06) |
|--------|-----------------|--------|-------------------|------------------|
| Truncated file | Yes | Yes | — | Yes |
| Missing MP4 index (`moov`) | Yes | Yes | — | Yes |
| 4 KiB of zeros inside FLAC, MP3, AAC, JPEG, PNG | — | Yes | Yes where present | Yes |
| One flipped byte in FLAC | — | **No, unless `crccheck` is on** | Yes (frame CRC, audio MD5) | Yes |
| One flipped byte in Ogg (Opus, Vorbis) | — | Yes | Yes (page CRC) | Yes |
| One flipped byte in PNG | — | Yes | Yes (chunk CRC, zlib) | Yes |
| Random bytes inside H.264 | — | **Warning only** (`corrupt decoded frame`) | — | Yes |
| One flipped byte in H.264 | — | Warning only, sometimes nothing | — | Yes |
| One flipped byte in JPEG or lossy WebP | — | **Often nothing at all** | — | Yes |

The last rows are why baselines matter and why the UI must never say "verified intact" on the
strength of a decode alone. Say "no errors found" for decode results and "unchanged since <date>" for
fingerprint results.

## Artifacts

| Artifact | Provider | Depth | Content |
|----------|----------|-------|---------|
| `StructureArtifact` | `StructureProvider` (native, no FFmpeg) | Quick | Sniffed format, declared end offset, structural issues, padding regions, format-specific header data |
| `ProbeResult` | `ProbeProvider` (subplan 04) | Quick | Streams, durations, bitrates |
| `AudioDecodeArtifact` | `AudioDecodeProvider` | Standard | Outcome, log summary, decoded sample count, FLAC MD5 verdict; in M2 also PCM statistics (subplan 10) |
| `ImageDecodeArtifact` | `ImageDecodeProvider` | Standard | Outcome, log summary, frames read, dimensions |
| `VideoDecodeArtifact` | `VideoDecodeProvider` | Standard / Deep | Outcome, log summary split per stream, decoded duration and frame count, stream hash, pass kind |

## Structure checks (Quick, native)

`StructureProvider` reads only headers and seeks; it never reads payload. Budget: under 1 ms per file
on SSD. All parsing is bounds-checked against the file length; a length field that points past the
end is a finding, never an exception.

### Signature sniffing

Read the first 64 bytes (and for MP3 the bytes after an ID3v2 tag).

| Format | Signature |
|--------|-----------|
| JPEG | `FF D8 FF` |
| PNG | `89 50 4E 47 0D 0A 1A 0A` |
| GIF | `GIF87a` / `GIF89a` |
| BMP | `BM` |
| TIFF and TIFF-based RAW | `II 2A 00` / `MM 00 2A` |
| WebP / WAV / AVI | `RIFF` + size + `WEBP` / `WAVE` / `AVI ` (also `RF64` for large WAV) |
| ISO-BMFF (MP4, MOV, M4A, 3GP, HEIC, AVIF) | `ftyp` at offset 4 with a brand; QuickTime files may start with `moov`, `mdat`, `wide` or `free` |
| JPEG XL | `FF 0A`, or `00 00 00 0C 4A 58 4C 20 0D 0A 87 0A` |
| JPEG 2000 | `00 00 00 0C 6A 50 20 20 0D 0A 87 0A` |
| Matroska / WebM | `1A 45 DF A3` |
| FLAC | `fLaC` (possibly after an ID3v2 tag) |
| Ogg (Vorbis, Opus, FLAC, Theora) | `OggS` |
| MP3 | `ID3`, or an MPEG audio frame sync (`FF` then top three bits set) confirmed by a second frame at the computed distance |
| ASF (WMV, WMA) | `30 26 B2 75 8E 66 CF 11` |
| AIFF | `FORM` + size + `AIFF` / `AIFC` |
| FLV | `FLV 01` |
| MPEG-PS | `00 00 01 BA` |
| MPEG-TS / M2TS | `47` every 188 bytes (offset 0) or every 192 bytes (offset 4); check five packets |
| Monkey's Audio, WavPack, TTA, Musepack | `MAC `, `wvpk`, `TTA1`, `MPCK` / `MP+` |
| DSF, DSDIFF | `DSD `, `FRM8` |
| AC-3, DTS, AMR, CAF, AU | `0B 77`, `7F FE 80 01`, `#!AMR`, `caff`, `.snd` |

### Per-format checks

| Format | Checks | Findings | Ms |
|--------|--------|----------|----|
| ISO-BMFF | Walk top-level boxes (32-bit size, `1` = 64-bit size follows, `0` = to end of file). Record `free`/`skip` as padding | No `moov` → `COR-CONTAINER` ("index missing; typical of an interrupted recording or download"); fragmented files still carry a `moov`. A box extending past the end → `COR-TRUNCATED` with the missing byte count and which box. Bytes after the last box that are not a valid box → `COR-TRAILING` | M1 |
| RIFF | Compare the RIFF size + 8 and the `data`/`movi` chunk extent with the file length | Declared beyond the end → `COR-TRUNCATED`. Sizes of 0 or `FFFFFFFF` (never finalised) → `COR-INDEX` Low | M1 |
| FLAC | Skip ID3v2 if present; walk the metadata block chain; parse STREAMINFO (sample rate, channels, bits per sample, total samples, 16-byte MD5 at file offset 26 when STREAMINFO is first); record `PADDING` blocks; confirm a frame sync follows | Bad block chain → `COR-CONTAINER`. STREAMINFO values feed truncation and MD5 checks | M1 |
| JPEG | Walk markers: segments with lengths until SOS, then scan entropy data for the next marker that is not a stuffed `FF 00` or `RST0–7`, across all scans, until EOI. Record SOF dimensions, quantisation tables (for subplan 11), scan count, bytes after EOI | No EOI → `COR-TRUNCATED`. Segment length past the end → `COR-TRUNCATED`. Bytes after EOI are fine when they start with another `FF D8` (multi-picture formats), contain `ftyp` at +4 (motion photos), or are all zero / `FF` padding; otherwise `COR-TRAILING` | M1 |
| PNG | Walk chunks by length without reading data: IHDR first, IEND last | Chunk past the end or no IEND → `COR-TRUNCATED`. CRCs are verified at Standard depth by the decoder | M1 |
| Matroska | Validate the EBML header; read the Segment size | Known segment size past the end → `COR-TRUNCATED` | M1 |
| Matroska (index) | Walk top-level elements, look for Cues (directly or through SeekHead) | No Cues in a file with video → `COR-INDEX` | M2 |
| MP3 | ID3v2 size, first frame, Xing/Info/VBRI frame and byte counts; subtract ID3v1 and APE tags | Audio bytes fewer than the header declares by more than 1 % → `COR-TRUNCATED`; more → `COR-INDEX` | M2 |
| TIFF-based RAW | IFD chain and strip/tile offsets inside the file | Out of bounds → `COR-TRUNCATED` | M3 |
| MPEG-TS | Sync loss and continuity-counter jumps | `COR-DECODE` Low–Medium by count | M3 |

### Extension mismatch (`GEN-EXT-MISMATCH`)

Compare the extension's family with the sniffed format (and, for ISO-BMFF and Ogg, the brand or
codec reported by the probe).

- Same family (`jpg`/`jpeg`/`jfif`; `tif`/`tiff`; `mp4`/`m4v`; `mkv`/`mka`): no finding.
- Image named as another image type (PNG called `.jpg`): Low.
- Audio or video container named as a different container (FLAC called `.mp3`, MKV called `.mp4`):
  Medium, because many players choose the decoder by extension.
- Media kind differs (a video in a `.jpg`, audio-only MP4 named `.mp4` is fine but a still image in
  `.mp4` is not): Medium.
- No signature and FFmpeg cannot open it: `GEN-UNRECOGNIZED` (Critical) instead.

## Decode checks

`<std>` and `<dec>` are the standard and decode argument sets from subplan 04.

### Audio — Standard and Deep

One FFmpeg process per file; the probe runs concurrently.

```
ffmpeg <std> <dec> -i <file> -map 0:a:0 -map_metadata -1 -fflags +bitexact -c:a pcm_f32le -f wav -
```

`AudioDecodeProvider` reads the float stream through `WavPipeReader` and pushes blocks to a list of
`IPcmAnalyzer` instances. M1 ships two analyzers; subplan 10 adds the rest without another decode.

- `SampleCounter`: decoded sample frames → decoded seconds.
- `FlacMd5Analyzer` (FLAC sources with 24 or fewer bits and a non-zero stored MD5): rebuild each
  sample as an integer (`value × 2^(bits−1)`, exact for these depths), pack little-endian in
  `ceil(bits / 8)` bytes, interleaved, and feed `IncrementalHash` MD5. Compare with STREAMINFO.
  Verified equivalent to `ffmpeg -c:a pcm_s16le -f md5` (16-bit) and `pcm_s24le` (24-bit). For
  32-bit FLAC run that FFmpeg command as a second pass instead.

| Rule | Condition | Severity |
|------|-----------|----------|
| `COR-CONTAINER` | Probe or decode cannot open the file, or no audio stream | Critical |
| `COR-CHECKSUM` | Any `Checksum` signal, or FLAC MD5 mismatch | High |
| `COR-DECODE` | `DecodeError` signals | Lossless source, or 5 or more errors, or 0.5 s or more lost: High. Otherwise Medium. Only `MinorDecode`: Low |
| `COR-TRUNCATED` | Decoded seconds < declared − max(0.5 s, 1 %). Declared comes from FLAC STREAMINFO, else stream duration, else format duration. For MP3 without a VBR header the declared value is an estimate: use 3 % and require a `Truncation` signal or a structural finding as well | Missing half or more: Critical. Otherwise High |
| `COR-INDEX` | Decoded seconds > declared + tolerance (length header wrong) | Low |
| `COR-HANG` | Process timed out | High |

Evidence always includes declared and decoded seconds, error counts per kind, the three most frequent
normalised messages, and the first raw line.

### Photos — Standard and Deep

One ffprobe process per file does both the probe and a full decode:

```
ffprobe <std> -err_detect crccheck+bitstream+buffer -f <demuxer> -count_frames
        -show_entries stream=codec_name,width,height,pix_fmt,nb_read_frames:format=format_name -of json -i <file>
```

`<demuxer>` comes from the sniffed signature (subplan 04, input-path rules); omit `-f` for ISO-BMFF
images so the MOV demuxer is used.

| Rule | Condition | Severity |
|------|-----------|----------|
| `COR-CONTAINER` | Cannot open, or no image stream | Critical |
| `COR-DECODE` | `nb_read_frames` is 0 or `N/A` together with decode errors (nothing usable decoded) | Critical |
| `COR-PARTIAL-IMAGE` | `Truncation` signals (`EOI missing`, `overread`, `chunk too big`) or JPEG decoder error counts (`error count:`, `error y=`) while a frame was still produced | High |
| `COR-CHECKSUM` | PNG `CRC mismatch`, zlib `inflate returned error` | High |
| `COR-HANG` | Timed out | High |

Notes:

- Animated GIF, APNG and WebP are decoded frame by frame by `-count_frames`.
- HEIC with tile grids needs FFmpeg 7.1 or newer for complete verification; on older builds mark the
  file `Skipped: UnsupportedByFfmpeg`.
- RAW files: structural check only in M1 and M2; the embedded JPEG preview is decoded in M3.
- A JPEG or WebP with no decoder complaint is "no errors found", not "intact" (see the table at the top).

### Video — Standard

One FFmpeg process demuxes the entire file, decodes key frames and all audio, and computes the stream
hash. Working directory is the job temp folder.

```
ffmpeg <std> <dec> -threads <n> -skip_frame nokey -i <file>
       -map 0:v:0 -map 0:a? -f null -
       -map 0:v:0 -map 0:a:0? -c copy -f streamhash -hash md5 streamhash.txt
       -progress pipe:1 -nostats
```

This reads every byte of the file once (so it also exercises the storage) and costs little CPU.
Verified on a 6-second 1080p file with AAC audio: about 200 ms, and it reports mid-file damage in
both the video and the audio stream.

| Rule | Condition | Severity |
|------|-----------|----------|
| `COR-CONTAINER` | Cannot open; no video stream in a file classified as video and no audio either | Critical |
| `COR-DECODE` | `DecodeError` signals on the video stream or any audio stream. Evidence splits counts per stream | 5 or more errors, or 2 s or more of decoded time lost: High. Otherwise Medium. Only `MinorDecode`: Low |
| `COR-CONCEALED` | `ConcealedFrame` signals | 10 or more: High. Otherwise Medium |
| `COR-TRUNCATED` | Final `out_time` < declared duration − tolerance. Tolerance is max(2 s, 2 %) when an audio stream exists, max(12 s, 2 %) for video-only files (key-frame decoding stops at the last key frame). Also any `Truncation` signal such as `File ended prematurely` | Missing half or more: Critical. Otherwise High |
| `COR-TIMESTAMPS` | 10 or more `Timestamp` signals | 100 or more: Medium. Otherwise Low |
| `COR-STREAM-LENGTH` | From the probe: video and default audio stream durations differ by more than max(2 s, 2 %) | Above 10 %: Medium. Otherwise Low |
| `COR-HANG` | Timed out | High |

### Video — Deep

Every frame is decoded. Log level is raised to `info` so filter results and concealment details are
visible; the classifier ignores unknown info lines.

```
ffmpeg -hide_banner -nostdin -loglevel repeat+level+info -protocol_whitelist file,pipe
       -max_error_rate 1 -err_detect crccheck+bitstream+buffer -threads <n> -i <file>
       -map 0:v:0 -map 0:a? -vf "freezedetect=n=0.003:d=10,blackdetect=d=30:pix_th=0.10" -f null -
       -progress pipe:1 -nostats
```

Additional evaluation at Deep:

- Decoded frame count against the container's `nb_frames` when known: a shortfall above
  max(2 frames, 0.1 %) is added to the `COR-DECODE` evidence ("N frames could not be decoded") and
  counts as that many errors.
- Truncation uses the 2 s / 2 % tolerance for all files.
- `freezedetect` and `blackdetect` lines feed `VID-FROZEN` and `VID-BLACK` (subplan 11).
- Optional setting *Strict decoding* appends `+careful+compliant` to `-err_detect`. Off by default
  because some healthy but non-compliant encodes trip it.

Hardware decoding (`-hwaccel d3d11va`) is an opt-in Deep setting, labelled "faster, may miss some
errors"; hardware decoders report less. Default is software.

## Severity summary for damage

1. Cannot open, or half or more of the content gone → Critical.
2. Errors that lose audible or visible content, checksum failures, stalls → High.
3. A few isolated, recoverable errors in a lossy stream → Medium.
4. Bookkeeping complaints only, wrong length headers, stray trailing bytes → Low.

All numeric limits above are entries in `RuleDefaults` under their rule ID.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| C-01 | Signature sniffer covering the table | Unit tests with minimal byte arrays per format; never throws on random input (fuzz with 10,000 random buffers) |
| C-02 | ISO-BMFF and RIFF walkers | Fixture tests: healthy, truncated at 60 %, `moov` missing, trailing junk, 64-bit box size, `free` padding recorded |
| C-03 | FLAC header reader | STREAMINFO values match `ffprobe` for the fixture set; MD5 read at the verified offset; ID3v2-prefixed FLAC handled |
| C-04 | JPEG marker walker | Fixtures: baseline, progressive, with restart markers, truncated, trailing garbage, multi-picture trailer, motion-photo trailer. Quantisation tables exposed |
| C-05 | PNG chunk walker and Matroska header check | Truncated files detected with the correct missing-byte count |
| C-06 | `GEN-EXT-MISMATCH` and `GEN-UNRECOGNIZED` | Table-driven tests for each family rule; a text file named `.mp3` is unrecognised |
| C-07 | `AudioDecodeProvider`, `IPcmAnalyzer` pipeline, `SampleCounter`, `FlacMd5Analyzer` | Integration: healthy FLAC 16/24-bit MD5 matches; one flipped byte → `COR-CHECKSUM`; 4 KiB zeroed → `COR-DECODE`; truncated MP3 → `COR-TRUNCATED` with the right seconds |
| C-08 | `ImageDecodeProvider` | Integration: truncated JPEG → `COR-PARTIAL-IMAGE`; zeroed JPEG → High; flipped PNG byte → `COR-CHECKSUM`; file named `a%03d.jpg` works |
| C-09 | `VideoDecodeProvider` Standard pass with stream hash | Integration: mid-file damage → `COR-DECODE` with per-stream counts; truncated fast-start MP4 → `COR-TRUNCATED`; truncated MKV → `COR-TRUNCATED` |
| C-10 | `VideoDecodeProvider` Deep pass | Integration: damage inside non-key frames produces `COR-CONCEALED` at Deep; frame shortfall reported |
| C-11 | Detectors mapping artifacts to the rules and severities above | Pure unit tests over hand-built artifacts for every row of every table |
| C-12 | Timeouts → `COR-HANG` | Test with a fake provider outcome; integration with a stalled child |
| C-13 | Matroska Cues check, MP3 header check (M2) | Fixtures: MKV remuxed without cues; MP3 with a wrong Xing frame count |
| C-14 | Evidence builders and summaries | Snapshot tests of summaries for the fixture set; wording follows subplan 02 |

## Exit criteria

- The corruption matrix in subplan 15 meets its detection targets at each depth.
- Zero findings on the healthy fixture set at all depths.
- A full Standard scan of the fixture set leaves no FFmpeg processes and no temp files behind.

## Pitfalls

- FFmpeg and ffprobe exit 0 for most corrupt inputs. Only the classified log and the arithmetic count.
- `out_time` in key-frame mode is the time of the last key frame, not the end of the stream.
- Cover art in audio files is a video stream; exclude attached pictures when counting video streams.
- Motion photos and multi-picture JPEGs legitimately carry megabytes after EOI.
- QuickTime files written by cameras often have `mdat` before `moov`; that is normal, not truncation.
- Fragmented MP4 keeps a small `moov` at the front and stores its sample tables in `moof` boxes, so
  many small `mdat` boxes are normal there. A file with `moof` boxes but no `moov` is a bare media
  segment and cannot be played on its own.
