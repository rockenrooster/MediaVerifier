# 06 — Integrity and bit rot

Phase 6. Depends on: phase 5. Unblocks: M1.

## Goal

Detect silent change. Record a fingerprint of every file when it is first seen (the baseline),
re-read files on a schedule, and tell the difference between a file someone edited and a file whose
bytes changed on their own.

This is the only check that catches every kind of damage. Decoding cannot: a flipped byte in a JPEG,
a WebP or an H.264 stream frequently decodes without a single error (appendix A). Fingerprints can.

## Reading a file once

`IntegrityReader.ReadAsync(path)` performs a single sequential pass and produces:

- **Fingerprint**: XXH3-128 (`System.IO.Hashing.XxHash128`) over the whole file.
- **Zero-block map**: count and first offsets of 4 KiB-aligned blocks that are entirely zero, and the
  longest run of consecutive zero blocks. Use `Span<byte>.IndexOfAnyExcept((byte)0)`, which is
  vectorised.
- **Read errors**: if the OS fails a read, capture the Win32 error code and file offset, then stop.

Implementation notes:

- Open with `FileShare.ReadWrite | FileShare.Delete`, `FileOptions.SequentialScan`, and a 1 MiB
  buffer rented from `ArrayPool`. Do not use unbuffered I/O; the OS cache is wanted for the
  analysis that follows (subplan 05, read-once rule).
- Re-stat the file after reading. If size or modified time changed during the read, discard the
  result and mark the file `InProgress`.
- Throughput target: limited by storage. Verify on NVMe that the hash and zero scan together cost
  less than 15 % of one core at 1 GB/s.

### Read errors → `GEN-UNREADABLE`

Map `IOException.HResult & 0xFFFF`:

| Win32 code | Meaning | Result |
|-----------|---------|--------|
| 23 `ERROR_CRC`, 27 `ERROR_SECTOR_NOT_FOUND`, 30 `ERROR_READ_FAULT`, 1117 `ERROR_IO_DEVICE`, 1127 `ERROR_DISK_OPERATION_FAILED`, 1392 `ERROR_FILE_CORRUPT`, 1393 `ERROR_DISK_CORRUPT` | The storage could not deliver the data | `GEN-UNREADABLE` (Critical) with code and offset; also raises the run alert `RUN-READ-ERRORS` |
| 32 `ERROR_SHARING_VIOLATION`, 33 `ERROR_LOCK_VIOLATION` | In use | Skipped: `Locked` |
| 5 `ERROR_ACCESS_DENIED` | Permissions | Skipped: `AccessDenied` |
| 21 `ERROR_NOT_READY`, 53, 59, 64, 1167 `ERROR_DEVICE_NOT_CONNECTED` | Device or share went away | Abort this root for the run (`RUN-ROOT-OFFLINE`); do not flag files |

Never retry a CRC or device error in a tight loop; one retry after 500 ms, then record it.

### Zero-filled content → `GEN-ZEROFILL`

- Whole file empty → `GEN-EMPTY` instead.
- Zero blocks make up 50 % or more of the file → Critical ("mostly empty; typical of a failed copy or
  file-system recovery").
- Otherwise, for **compressed** formats only (everything except WAV, AIFF, BMP, uncompressed TIFF,
  raw PCM in any container, DSD): a run of 16 or more consecutive zero blocks (64 KiB) → Low on its
  own. If the same file also has a corruption finding, attach the zero-run offsets to that finding's
  evidence and say so in its summary: compressed media essentially never contains 64 KiB of zeros, so
  this explains the cause.
- Known padding must not trigger: MP4 `free`/`skip` boxes, Matroska `Void` elements, FLAC `PADDING`
  blocks, ID3v2 padding. Where the structure reader (subplan 07) has located such regions, subtract
  them. Until then the Low severity keeps the cost of a false positive small.

## Baselines and change classification

A baseline is `(hash, hash_size, hash_mtime_utc, hash_utc)` plus, for audio and video at Standard
depth or deeper, a **stream hash**.

### Stream hash

The stream hash covers only the encoded media packets, not tags or container layout:

```
ffmpeg <standard args> -i <file> -map 0:a:0 -c copy -f streamhash -hash md5 -
```

Output is one line per stream, for example `0,a,MD5=218d8bdbaa101df51f07a726ac401ee5`. Verified:
re-tagging an MP3 or a FLAC changes the file hash and leaves the stream hash identical; remuxing MP4
to MKV keeps the video stream hash identical. For video files map the first video and first audio
stream (`-map 0:v:0 -map 0:a:0?`) and store a hash of the concatenated lines. This is a demux-only
pass (no decoding) and can share the Standard video pass in subplan 07 by adding a second output.

### Decision table

Evaluated when a file with a baseline is read again. "Stat" means size and modified time (with the
tolerances from subplan 05).

| Stat vs baseline | File hash | Stream hash | Classification | Action |
|------------------|-----------|-------------|----------------|--------|
| Same | Same | — | Verified | `Verified` event; update `last_verified_utc`, `verify_count` |
| Same | Different | Different | **Rot** | `INT-ROT` (Critical), `Rot` event. Keep the old baseline. Queue the file for Standard analysis to describe the damage. Look for a healthy copy |
| Same | Different | Not available | **Rot** | As above. Evidence notes that payload comparison was not possible |
| Same | Different | Same | Metadata drift | `INT-META-DRIFT` (Low), `MetaDrift` event. A tool rewrote tags and preserved the timestamp. Accept the new baseline |
| Different | any | any | Modified | `Modified` event, new baseline, full re-analysis. No finding |

`INT-ROT` evidence: old and new fingerprints (hex), baseline date, last successful verification date,
number of successful verifications, size, and the zero-block map of the current content. Summary
example: *"Content changed since 2026-03-14 without the file being modified. Verified intact 4 times
before."*

After the user repairs or accepts a rotted file, the baseline is replaced and the finding resolved
with the operation recorded.

**Never auto-accept a changed hash when the stat is unchanged.** That is precisely the signal the
product exists to raise.

## Scrub policy

Global settings (overridable per plan):

| Setting | Default | Meaning |
|---------|---------|---------|
| `ScrubPeriodDays` | 90 | Every baselined file should be re-read at least this often |
| `ScrubBudgetMinutes` | 120 (scheduled runs), unlimited (manual *Verify now*) | Time spent on verification per run |
| `VerifyOnAnalysis` | on | Any file read for analysis is verified for free (read-once rule) |

Selection: files whose `last_verified_utc` is older than now − period, oldest first, grouped by volume
and ordered by directory within a volume. The dashboard shows **Protected %** and, when the budget is
too small to cover the library within the period, a recommendation with the numbers ("At 2 h per
night, a full pass takes 41 days").

A manual **Verify now** action on any root, folder or selection ignores the period and budget.

## Healthy copies

When rot or corruption is found, a good copy may already exist:

- `Backup` roots are fingerprinted (Quick depth only, never analysed) precisely so they can serve as
  sources.
- `HealthyCopyFinder.Find(file)` looks up `files.hash = <old baseline hash>` across all roots,
  excluding the damaged file itself, and returns candidates that exist, are online, and whose
  current stat still matches their own baseline. Before any copy is used it is re-read and its
  fingerprint compared with the damaged file's old baseline.
- If no fingerprint match exists, offer a weaker candidate list by same name and size under backup
  roots, labelled as unverified.

The recommendation and the restore action live in subplan 12.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| I-01 | `IntegrityReader`: fingerprint, zero-block map, error capture, re-stat guard | Tests against known XXH3-128 vectors; zero-map correct on synthetic files; a file appended during the read is reported `InProgress` |
| I-02 | Read-error mapping | Unit tests per code using a faulting stream wrapper; no test needs a bad disk |
| I-03 | `GEN-EMPTY`, `GEN-ZEROFILL`, `GEN-UNREADABLE` detectors and `RUN-READ-ERRORS` alert | Fixture tests: empty, 100 % zero, 60 % zero, one 64 KiB zero run in a FLAC (Low), the same in a WAV (no finding) |
| I-04 | Stream-hash provider | Integration: retagged MP3 and FLAC keep their stream hash; a payload byte flip changes it |
| I-05 | Change classifier implementing the decision table | Table-driven unit tests for every row, including timestamp tolerance cases |
| I-06 | Rot path end to end: mutate a file's bytes and restore its modified time, then verify | `INT-ROT` raised with correct evidence; the baseline is unchanged; the file is queued for analysis |
| I-07 | Metadata-drift path | Retag with the timestamp preserved → `INT-META-DRIFT`, new baseline accepted |
| I-08 | Scrub selection and budget | Tests with a fake clock: oldest first, budget respected, per-volume ordering |
| I-09 | `HealthyCopyFinder` including re-verification of the candidate | Tests: finds a copy in a backup root; rejects a copy that itself changed |
| I-10 | Backup-root handling in the engine (fingerprint only) | A backup root produces no analysis jobs and no quality findings |

## Exit criteria

- The corruption mutators from subplan 15 applied with the timestamp preserved are detected as
  `INT-ROT` in 100 % of cases, for every format in the fixture set.
- Legitimate edits (touching the file, rewriting tags normally) never produce `INT-ROT`.

## Pitfalls

- Some sync and backup tools restore modified times after copying; a file replaced by a different
  version with the same size and time is indistinguishable from rot. That is acceptable: it is still
  an unexpected content change and the wording says "changed", not "decayed".
- Fingerprints are 16 bytes; store as BLOB and compare bytes, never hex strings.
- XXH3 is not a cryptographic hash. Do not use it for download verification (subplans 04 and 14 use
  SHA-256).
