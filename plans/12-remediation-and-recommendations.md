# 12 — Recommendations and remediation tools

Phase 12. Depends on: phase 9 (UI), phases 6 and 7 (findings to act on). Milestone M3.

## Goal

Turn findings into a short list of things worth doing, and provide built-in tools that do them
safely. Two parts:

1. **Recommendations**: group findings into actionable advice, ordered by importance.
2. **Tools**: a catalogue of actions, each following one safe-operation protocol with a journal and
   undo.

Honesty rule: many findings cannot be fixed, only replaced (a fake lossless file, an upscale, a
starved encode, a truncated photo). The product says so plainly and helps with the replacement
workflow instead of offering a fake cure.

## Recommendation engine

`RecommendationEngine.Build()` reads open findings and library state and returns at most five
`Recommendation` items for the dashboard (all of them on a *Recommendations* view).

```csharp
sealed record Recommendation(
    string Id, Severity Severity, string Title, string Detail,
    string? ActionId,            // null for advice-only
    FindingQuery Scope,          // which findings/files it covers
    int FileCount, long TotalBytes,
    ActionClass Class);          // Automatic, Assisted, Advice
```

Sources:

- **Per rule and action**: group open findings by rule and by the best available action for each
  file ("38 videos have no seek index → Rebuild index"). A file whose best action differs (a rotted
  file with a healthy copy versus one without) lands in different groups.
- **Library level**:
  - Protection coverage: files not verified within the scrub period, with the budget arithmetic.
  - No backup root configured while integrity findings exist: "Add a backup folder so damaged files
    can be restored automatically."
  - Read errors on a volume: "Drive D: returned read errors on 4 files. Copy what you can elsewhere
    first, then check the drive." Action opens the drive's Properties dialog.
  - No scheduled plan exists.
  - FFmpeg older than the supported minimum or missing optional filters.
  - Interrupted run waiting to be resumed.

Ordering: severity, then file count. Recommendations are recomputed on `LibraryChanged`, debounced.

## Action catalogue

Class: **Lossless** (bit-identical media payload, verified), **Copy** (creates a new file beside the
original and never replaces it), **Move** (relocates the file), **Meta** (changes app state only),
**Advice** (explains and assists; changes nothing).

| ID | Title | Class | What it does | Verification before commit |
|----|-------|-------|--------------|---------------------------|
| `ACT-RESTORE-COPY` | Restore from healthy copy | Lossless | Copies a fingerprint-verified copy from another root over the damaged file | Source fingerprint equals the damaged file's baseline, checked before and after copying |
| `ACT-REMUX` | Rebuild container and index | Lossless | `ffmpeg -i in -map 0 -c copy -map_metadata 0 [container flags] out` in the same container. MP4/MOV add `-movflags +faststart`; damaged timestamps add `-fflags +genpts`; unknown stream types add `-ignore_unknown` | Opens; same stream count and codecs; **stream hashes identical to the input**; duration within tolerance; the rule that triggered is gone on re-check |
| `ACT-FIX-EXT` | Rename to the correct extension | Move | Renames to the canonical extension of the detected format; resolves name collisions with a suffix | New name does not exist |
| `ACT-SHRINK-BITDEPTH` | Store as 16-bit (lossless) | Lossless | For `AUD-PADDED-BITS`: re-encode FLAC/ALAC/WAV at 16 bits. The dropped bits are all zero | Decoded PCM of output equals decoded PCM of input after scaling (compare MD5 of `pcm_s16le` from both) |
| `ACT-REENCODE-FLAC` | Rewrite FLAC | Lossless | For a FLAC whose stored MD5 is wrong but which decodes without errors: decode and re-encode | Decoded PCM MD5 of output equals decoded PCM MD5 of input |
| `ACT-SALVAGE` | Save a playable copy | Copy | For damaged audio and video: `ffmpeg -err_detect ignore_err -fflags +discardcorrupt+genpts -i in -map 0 -c copy out` writes `name.salvaged.ext`. Damaged packets are dropped | Output opens and decodes with no errors at Standard depth; report seconds lost |
| `ACT-EXTRACT-PREVIEW` | Extract embedded preview | Copy | For damaged JPEG and RAW files: write the largest intact embedded preview image as `name.preview.jpg` | Preview decodes cleanly; report its dimensions |
| `ACT-DECLIP-COPY` | Make a de-clipped copy (experimental) | Copy | Lossless sources only: `-af "volume=-6dB,adeclip"` to a 24-bit FLAC `name.declipped.flac` | Decodes; no samples above full scale. Labelled as an estimate, never replaces the original |
| `ACT-QUARANTINE` | Move to quarantine | Move | Moves the file to the quarantine folder, preserving its relative path | — |
| `ACT-RECYCLE` | Move to Recycle Bin | Move | Shell delete with undo allowed. Not offered on volumes without a Recycle Bin | — |
| `ACT-ACCEPT-BASELINE` | Accept current content | Meta | For `INT-ROT` the user confirms the change was intended; the current fingerprint becomes the baseline | Confirmation dialog requires ticking "I changed this file myself" |
| `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE` | Ignore | Meta | Sets finding status, or adds an ignore for a rule within a folder | — |
| `ACT-RESCAN`, `ACT-VERIFY` | Scan again, verify fingerprint | Meta | Queues a targeted run | — |
| `ACT-EXPORT-LIST` | Export replacement list | Advice | Writes the affected files grouped by folder (CSV or text) for re-acquiring from a better source | — |
| `ACT-CHECK-DRIVE` | Check the drive | Advice | Explains the risk, opens the volume's Properties dialog, links to the help page | — |
| `ACT-RECOVER-INDEX-HELP` | How to recover a recording without an index | Advice | Explains that an MP4/MOV without `moov` can sometimes be rebuilt with a reference file from the same device, and names the kind of tool to use | — |
| `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH` | Open, show in folder, copy path | Meta | — | — |

### Rule → actions (most recommended first)

| Rule | Actions |
|------|---------|
| `INT-ROT`, `GEN-UNREADABLE`, `GEN-ZEROFILL` | `ACT-RESTORE-COPY` (when a healthy copy exists), `ACT-SALVAGE` or `ACT-EXTRACT-PREVIEW` by media kind, `ACT-ACCEPT-BASELINE` (rot only), `ACT-QUARANTINE`; plus `ACT-CHECK-DRIVE` for unreadable |
| `COR-CONTAINER` | `ACT-RESTORE-COPY`, `ACT-RECOVER-INDEX-HELP` (MP4/MOV), `ACT-QUARANTINE` |
| `COR-TRUNCATED`, `COR-DECODE`, `COR-CONCEALED`, `COR-CHECKSUM`, `COR-HANG`, `COR-DROPOUT` | `ACT-RESTORE-COPY`, `ACT-SALVAGE` (audio, video), `ACT-EXTRACT-PREVIEW` (photos), `ACT-QUARANTINE`. `COR-CHECKSUM` on a FLAC that decodes cleanly: `ACT-REENCODE-FLAC` |
| `COR-PARTIAL-IMAGE` | `ACT-RESTORE-COPY`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE` |
| `COR-INDEX`, `COR-TIMESTAMPS`, `COR-STREAM-LENGTH` | `ACT-REMUX` |
| `GEN-EXT-MISMATCH` | `ACT-FIX-EXT` |
| `AUD-PADDED-BITS` | `ACT-SHRINK-BITDEPTH`, `ACT-IGNORE` |
| `AUD-CLIPPING` | `ACT-DECLIP-COPY` (lossless only), `ACT-EXPORT-LIST`, `ACT-IGNORE` |
| `AUD-FAKE-LOSSLESS`, `AUD-UPSAMPLED`, `AUD-BITRATE-INFLATED`, `AUD-LOW-BITRATE`, `VID-*`, `IMG-*` quality rules | `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE` |
| every rule | `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH` |

## Safe-operation protocol

Every action that touches a media file goes through `OperationRunner`. No other code writes, moves or
deletes user media.

### Steps

1. **Plan.** Build an `OperationPlan` per file: source, temporary output, final name, where the
   original goes, estimated output size. Check preconditions:
   - the file's size and modified time still equal the index row (otherwise: "changed since the last
     scan, rescan first");
   - the root is online and writable; the file is not read-only and can be opened exclusively;
   - free space on the volume ≥ estimated output + 5 % or 100 MB, whichever is larger;
   - the file is not in a `Backup` root (those are sources, never targets).
2. **Preview.** A dialog lists what will happen: action, file count, total size, where originals go,
   whether it can be undone, and anything skipped with the reason. Nothing runs without this
   confirmation. Batches are allowed only when every selected item supports the action.
3. **Execute**, one file at a time per volume:
   1. Journal row `Running`.
   2. Write the output to `<same folder>\<name>.mvtmp-<operation id><ext>`. Same folder means same
      volume (the later rename is atomic) and the extension keeps FFmpeg's muxer choice correct.
   3. **Verify** the output with the action's verification. On failure delete the temporary file,
      journal `Failed` with the reason, leave the original untouched.
   4. **Swap.** Move the original to the backup location, then rename the temporary file to the
      final name. If the second step fails, move the original back.
   5. Update the index (new stat, new baseline, `Repaired` or `Restored` event), queue a Standard
      rescan of the file, and resolve the finding with the operation id once the rescan confirms.
   6. Journal `Done` with the undo data.
4. **Report.** A toast and a batch summary: done, failed with reasons, *Undo*.

Cancellation between files is immediate; cancelling during a file kills the child process and
deletes the temporary file. A crash leaves at most `*.mvtmp-*` files, which are found and removed at
the next start (the journal identifies them; never delete by pattern alone).

### Where originals go

Setting *Keep originals in*: **Quarantine folder** (default), Recycle Bin, or a custom folder.

- Quarantine lives inside each root: `<root>\.mediaverifier-quarantine\<operation id>\<relative
  path>`, hidden attribute set, excluded from scans. Same volume, so moves are instant, undo is exact,
  and it works on network shares that have no Recycle Bin.
- Items are purged after `QuarantineDays` (default 30) by the next run, or manually from the Tools
  page. Purging is the only permanent deletion in the product and always shows count and size first.
- Recycle Bin mode cannot offer in-app undo; say so in the preview.

### Undo

For operations with a quarantined original: move the current file to quarantine (as a new operation),
move the original back, restore the index row and baseline, reopen the finding. Undo is itself
journaled. `Ctrl+Z` undoes the most recent undoable batch.

### Interaction with scans

An operation needs the engine to be idle on that root. If a run is active, the preview offers
"Pause the scan and continue"; the runner pauses the engine, executes, and resumes it.

## Reports

From Issues (current filter), a run, or the Tools page:

- **HTML**: one self-contained file with a summary header, counts by tier, and findings grouped by
  tier and folder, with explanations. Printable; no external resources.
- **CSV**: UTF-8 with BOM. Columns: severity, confidence, rule id, rule title, summary, path, size,
  media kind, first seen, last seen, status.
- **JSON**: the same data plus evidence objects.

## Tools page views

- **Quarantine**: items grouped by operation with original path, size, date; *Restore*, *Delete
  permanently*, *Open folder*; total size and days until purge.
- **Operations**: the journal, newest first, with status, files, result and *Undo* where available.
- **Reports**: the exports above.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| R-01 | `ActionCatalog` and rule-to-action mapping; applicability evaluation per finding | Tests: each rule returns the documented actions in order; `ACT-RESTORE-COPY` only when a healthy copy exists; `ACT-RECYCLE` hidden on a share |
| R-02 | `OperationRunner`: plan, preconditions, temp naming, verify hook, swap with rollback, journal, crash cleanup | Fault-injection tests: failure at each step leaves the original intact and no stray files; kill mid-operation then restart cleans up exactly the journaled temp file |
| R-03 | Quarantine store, purge, restore; undo engine | Round trip: act → undo → file bytes and index identical to the start |
| R-04 | `ACT-RESTORE-COPY` | Rotted fixture restored from a backup root; fingerprints match; finding resolved; a changed "healthy" copy is rejected |
| R-05 | `ACT-REMUX` with stream-hash equality check | MKV without cues gains cues; MP4 gains fast start; stream hashes equal; a deliberately altered output fails verification |
| R-06 | `ACT-FIX-EXT` | Collision handling; index row updated, not duplicated |
| R-07 | `ACT-SHRINK-BITDEPTH`, `ACT-REENCODE-FLAC` with PCM-equality verification | Output PCM MD5 equals input; a truncated encode fails verification |
| R-08 | `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW` | Salvaged copy of a mid-file-damaged MP4 decodes clean; preview extracted from a truncated camera JPEG |
| R-09 | `ACT-DECLIP-COPY` (optional; requires `adeclip`) | Hidden when the filter is missing; output has no overs |
| R-10 | Meta and advice actions, including `ACT-ACCEPT-BASELINE` confirmation | Accepting replaces the baseline and resolves `INT-ROT` |
| R-11 | `RecommendationEngine` with finding-based and library-level sources | Table-driven tests from synthetic libraries; ordering and grouping as specified |
| R-12 | Preview dialog, batch summary, toasts with Undo; Dashboard recommendations wired | Flow in subplan 09 works by keyboard |
| R-13 | Reports: HTML, CSV, JSON | Files open in a browser, Excel and a JSON parser; non-ASCII paths intact |
| R-14 | Tools page: Quarantine and Operations views | Restore, purge, undo from the UI |

## Exit criteria

- No code path outside `Remediation/` writes to a library root (enforce with a test that scans the
  Core assembly's references to file-mutating APIs outside the allowed namespaces, or by routing all
  writes through one `IMediaFileSystem` abstraction that only the runner receives).
- Every Lossless action is proven lossless by its verification in tests.
- Interrupting any action at any point never loses or alters the original.

## Pitfalls

- `File.Move` across volumes is copy-then-delete and is not atomic. Keep temp and quarantine on the
  same volume as the file.
- A remux can silently drop streams the target container does not support. Compare stream counts and
  fail rather than lose a subtitle track.
- FFmpeg picks the muxer from the output extension; a temp name ending in `.tmp` fails. That is why
  the temp name keeps the real extension.
- Files can change between the scan and the fix. Always re-check stat (and fingerprint for restores)
  immediately before acting.
- Never write into a `Backup` root.
- Network shares: no Recycle Bin, slower renames, occasional sharing violations from indexers. Retry
  the swap briefly, then fail cleanly.
