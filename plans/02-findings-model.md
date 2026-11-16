# 02 — Findings model, severity tiers and rule catalog

Phase 2. Depends on: phase 1. Unblocks: storage, every detector, the UI.

## Goal

Define, in code and in one place, what a finding is, how severe it is, and the complete list of rules
the product can raise. Later phases implement detectors against this catalog; they do not invent rule
IDs or severities ad hoc.

## Severity tiers

Stored as an integer so that a higher number is always worse.

| Value | Tier | Definition | Typical examples | Default notification |
|-------|------|------------|------------------|----------------------|
| 4 | **Critical** | Content is lost or is being lost. The file cannot be opened, or its bytes changed without anyone editing it | Bit rot, unreadable sectors, empty or zero-filled file, missing index that makes a file unplayable, more than half the content gone | Always |
| 3 | **High** | Confirmed damage or a confirmed fake. The file opens but part of it is wrong | Decode errors, checksum mismatch, truncated tail, concealed video damage, silent track, lossless file made from a lossy source (high confidence) | Yes |
| 2 | **Medium** | Real quality defect or structural problem; no content lost | Heavy clipping, upscaled video or photo, upsampled or padded hi-res audio, starved bitrate, missing seek index, dead channel | In summary |
| 1 | **Low** | Minor, cosmetic or low-confidence | Light clipping, DC offset, low bitrate, trailing junk, timestamp oddities, suspicions below the confidence bar | No |
| 0 | None | No open findings | — | — |

Rules for assigning a tier:

1. Damage outranks quality. A quality rule can never be Critical.
2. Certainty raises the tier, doubt lowers it. Heuristic rules map confidence to severity (see each
   rule); a heuristic at confidence below 0.5 is never above Low.
3. A file's status is the worst severity among its open findings.
4. The user may override the severity of any rule or disable it. Overrides apply at evaluation time
   and are stored separately from defaults.

## Types

All in `MediaVerifier.Core.Model`. Sketches, not final signatures.

```csharp
public enum Severity : byte { None = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }
public enum MediaKind : byte { Unknown = 0, Photo = 1, Video = 2, Audio = 3 }
public enum ScanDepth : byte { None = 0, Quick = 1, Standard = 2, Deep = 3 }

public enum FindingCategory : byte
{
    Integrity = 1,     // rot, unreadable, zero-filled
    Corruption = 2,    // container and decode damage
    Authenticity = 3,  // fakes: lossless-from-lossy, upscaled, upsampled, padded
    Quality = 4,       // clipping, starved bitrate, artifacts
    Structure = 5,     // index, timestamps, extension, trailing data
}

public enum FindingStatus : byte { Open = 0, Ignored = 1, Resolved = 2 }

public sealed record Finding(
    string RuleId,
    Severity Severity,
    float Confidence,          // 1.0 for facts; 0..1 for heuristics
    string Summary,            // one line of evidence, e.g. "Hard cutoff at 16.6 kHz in a 44.1 kHz FLAC"
    IReadOnlyDictionary<string, object> Evidence); // serialised as JSON

public sealed record RuleDefinition(
    string Id,
    string Title,              // "Lossless file made from a lossy source"
    FindingCategory Category,
    MediaKindMask AppliesTo,
    ScanDepth MinDepth,
    Severity MaxSeverity,
    int Version,               // bump when logic or thresholds change; forces re-evaluation
    bool EnabledByDefault,
    string WhatItMeans,        // plain-language explanation
    string WhyItMatters,
    IReadOnlyList<string> ActionIds); // remediation actions, most recommended first (subplan 12)
```

Evidence conventions: keys are camelCase with the unit as a suffix (`cutoffHz`, `dropDb`,
`decodedSeconds`, `declaredSeconds`, `offsetBytes`, `count`). Values are numbers, strings or small
arrays. Invariant culture. Evidence must be enough for the UI to draw its evidence view without
re-running analysis.

`RuleCatalog` is a static, immutable registry built at startup. `RuleDefaults` holds every tunable
threshold with its default, minimum and maximum, keyed `ruleId.paramName`. `RuleSettings` overlays
user overrides (enabled, severity cap, parameters).

## Finding lifecycle

- A finding is unique per `(file, rule)`. Re-detection updates evidence, `last seen` and severity.
- When a file is re-scanned at a depth at least equal to the rule's `MinDepth` and the detector no
  longer raises it, the finding becomes `Resolved` with the reason `not reproduced`. A scan at a
  shallower depth leaves it untouched.
- A remediation that fixes the cause resolves the finding with the operation ID as the reason.
- `Ignored` findings stay ignored across rescans until the file content changes (new fingerprint), at
  which point they reopen if raised again. Ignores can also be scoped to a rule for a folder (subplan 03).
- When a file is modified legitimately, all its findings are re-evaluated from scratch.

## Wording rules

| Confidence | Wording in titles and summaries |
|------------|--------------------------------|
| 1.0 (fact) | Plain statement: "Decoder reported 14 errors" |
| 0.8 – 0.99 | "Likely ..." |
| 0.5 – 0.79 | "Possibly ..." |
| below 0.5 | "Suspicious: ..." and never above Low |

Every rule's explanation has three short parts shown in the detail view: *What this means*, *Why it
matters*, *What you can do*. Write them for a non-expert. No jargon without a gloss.

## Health metrics

- **Healthy %** = files with no open finding at Medium or above ÷ files scanned.
- **Protected %** = files whose fingerprint was verified within the scrub period ÷ files with a baseline.
- Counts of open findings per tier and per media kind.

No opaque composite score.

## Rule catalog

`Depth` is the minimum depth at which the rule can be evaluated. `Ms` is the milestone that
implements it. Severity ranges are resolved by the formula in the owning subplan.

### General (all media) — subplans 05, 06, 07

| ID | Title | Severity | Depth | Ms | Trigger |
|----|-------|----------|-------|----|---------|
| GEN-UNREADABLE | File cannot be read | Critical | Quick | M1 | The OS returned a device, CRC or sector error while reading. Evidence: Win32 code, offset |
| GEN-EMPTY | File is empty | Critical | Quick | M1 | Size is 0 bytes |
| GEN-ZEROFILL | File is filled with zeros | Low – Critical | Quick | M1 | Zero-filled 4 KiB blocks. At least 50 % of the file: Critical. Smaller zero regions in a compressed format: Low on their own, and attached as evidence to any corruption finding on the same file |
| GEN-UNRECOGNIZED | Not a recognisable media file | Critical | Quick | M1 | No known signature and FFmpeg cannot identify any stream |
| GEN-EXT-MISMATCH | Extension does not match content | Low – Medium | Quick | M1 | Detected format belongs to a different family than the extension. Medium when the media kind differs or common players would refuse it |
| GEN-PATH | Path may cause problems | Low | Quick | M3 | Path over 259 characters, trailing dot or space, or a reserved device name |

### Integrity — subplan 06

| ID | Title | Severity | Depth | Ms | Trigger |
|----|-------|----------|-------|----|---------|
| INT-ROT | File changed without being modified (bit rot) | Critical | verification | M1 | Fingerprint differs from baseline while size and modified time are unchanged, and the stream hash also differs or is unavailable |
| INT-META-DRIFT | Metadata changed without a timestamp update | Low | verification | M2 | Fingerprint differs, size and time unchanged, stream hash identical. Re-baselined automatically |
| INT-SIDECAR | Does not match its checksum file | High | Standard | backlog | Mismatch against a `.sfv`, `.md5`, `.sha1`, `.sha256` or `.ffp` sidecar |

Run-level alerts (not per-file findings; shown in the run summary and notifications):

| ID | Title | Severity | Trigger |
|----|-------|----------|---------|
| RUN-MASS-MISSING | Many files disappeared | High | A root that is online lost at least 20 % and at least 50 of its files since the previous run |
| RUN-ROOT-OFFLINE | Folder unavailable | Medium | A root could not be reached; its files are left untouched, not marked missing |
| RUN-READ-ERRORS | Storage read errors | Critical | Any `GEN-UNREADABLE` in the run; recommends checking the drive |

### Corruption — subplan 07 (audio signal rules in 10)

| ID | Title | Severity | Depth | Ms | Trigger |
|----|-------|----------|-------|----|---------|
| COR-CONTAINER | Container is broken | Critical | Quick | M1 | Header or index unreadable (for example MP4 without `moov`), or no streams found |
| COR-TRUNCATED | File is cut short | High – Critical | Quick | M1 | Declared size or duration exceeds what exists or decodes. Critical when more than half is missing |
| COR-DECODE | Decoder errors | Low – Critical | Standard | M1 | Errors while decoding. Scaled by how much of the content is affected |
| COR-CONCEALED | Damaged video frames (hidden by error concealment) | Medium – High | Standard | M1 | Decoder flagged frames as corrupt while still producing output |
| COR-CHECKSUM | Embedded checksum mismatch | High | Standard | M1 | FLAC frame CRC or audio MD5, PNG chunk CRC, Ogg page CRC |
| COR-HANG | Decoder stalled | High | Standard | M1 | No decode progress within the idle timeout, or the hard timeout expired |
| COR-PARTIAL-IMAGE | Image decodes only partly | High | Standard | M1 | Missing end marker, overread, or decoder error count above zero |
| COR-TIMESTAMPS | Timestamp problems | Low – Medium | Standard | M2 | Non-monotonic or discontinuous timestamps during demux |
| COR-INDEX | Seek index missing or wrong | Low – Medium | Quick | M2 | Matroska without Cues, AVI without index, MP3 VBR header frame count wrong |
| COR-STREAM-LENGTH | Audio and video lengths differ | Low – Medium | Quick | M2 | Stream durations differ by more than max(2 s, 2 %) |
| COR-TRAILING | Unexpected data after the end | Low | Quick | M3 | Bytes after the logical end that are not a known trailer format |
| COR-DROPOUT | Audio dropouts | Low – High | Standard | M2 | Runs of exact digital silence inside programme material with abrupt edges; confidence rises when the gap length is a whole number of codec frames or CD sectors |
| COR-CLICKS | Clicks or discontinuities | Low – Medium | Standard | M3 | Isolated sample-level jumps far above the local signal (experimental) |
| COR-VISUAL | Possible visual corruption | Medium | Standard | backlog | Block-aligned discontinuity in a photo with no decoder error (experimental) |

### Audio authenticity and quality — subplan 10

| ID | Title | Severity | Depth | Ms | Trigger |
|----|-------|----------|-------|----|---------|
| AUD-FAKE-LOSSLESS | Lossless file made from a lossy source | Low – High | Standard | M2 | Lossless codec with a lossy-codec spectral cutoff. Severity follows confidence |
| AUD-UPSAMPLED | Hi-res file upsampled from a lower rate | Low – Medium | Standard | M2 | Sample rate of 88.2 kHz or more with content limited to about 24 kHz or less |
| AUD-PADDED-BITS | Bit depth is padded | Medium | Standard | M2 | Container above 16 bits, 16 or fewer significant bits |
| AUD-BITRATE-INFLATED | Bitrate higher than the audio quality | Low – Medium | Standard | M2 | Lossy file whose cutoff is far below what its bitrate normally delivers |
| AUD-LOW-BITRATE | Low bitrate | Low | Quick | M2 | Lossy bitrate under the per-codec floor |
| AUD-CLIPPING | Clipping | Low – High | Standard | M2 | Flat-topped peaks; tier by density |
| AUD-OVERS | Decodes above full scale | Low | Standard | M2 | Float or lossy decode peaks above 0 dBFS |
| AUD-DC | DC offset | Low – Medium | Standard | M2 | Mean level beyond the threshold |
| AUD-DEAD-CHANNEL | A channel is silent | Medium | Standard | M2 | One channel silent while others carry signal |
| AUD-PHASE | Channels are out of phase | Medium | Standard | M2 | Left and right strongly anti-correlated |
| AUD-SILENT | Track is silent | High | Standard | M2 | Whole file below the silence threshold |
| AUD-LONG-SILENCE | Long silence at start or end | Low | Standard | M3 | Leading or trailing digital silence beyond the threshold |
| AUD-DUAL-MONO | Stereo file with identical channels | Low | Standard | M3 | Disabled by default |
| AUD-DYNAMICS | Very low dynamic range | Low | Standard | backlog | Disabled by default |

### Video quality — subplan 11

| ID | Title | Severity | Depth | Ms | Trigger |
|----|-------|----------|-------|----|---------|
| VID-UPSCALED | Resolution is higher than the detail | Low – High | Standard | M2 | Sampled frames are reproducible from a lower resolution. Severity follows confidence and ratio |
| VID-STARVED | Bitrate too low for the resolution | Low – Medium | Quick | M2 | Bits per pixel under the codec floor; raised to Medium when artifacts confirm |
| VID-BLOCKY | Heavy compression artifacts | Low – Medium | Standard | M2 | Blockiness of sampled frames above the threshold |
| VID-INFLATED | Bitrate higher than the picture quality | Low – Medium | Standard | M2 | High bitrate combined with an upscale or heavy artifacts |
| VID-INTERLACE | Interlacing mismatch | Low – Medium | Standard | M3 | Interlaced content flagged progressive, or the reverse |
| VID-FROZEN | Frozen picture | Low – Medium | Deep | M3 | Long freeze while audio continues |
| VID-BLACK | Long black section | Low | Deep | M3 | Disabled by default |
| VID-AUDIO-SILENT | Soundtrack is silent | Medium | Standard | M3 | Default audio stream is silent throughout |
| VID-HDR | HDR signalling problem | Low – Medium | Quick | M3 | HDR transfer function with fewer than 10 bits, or contradictory colour metadata |

### Photo quality — subplan 11

| ID | Title | Severity | Depth | Ms | Trigger |
|----|-------|----------|-------|----|---------|
| IMG-UPSCALED | Image is larger than its detail | Low – Medium | Standard | M2 | Reproducible from a lower resolution |
| IMG-HEAVY-COMPRESSION | Heavily compressed | Low – Medium | Quick | M2 | Estimated JPEG quality or bits per pixel under the threshold |
| IMG-LOW-RES | Low resolution | Low | Quick | M3 | Disabled by default |

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| M-01 | Enums and records above, with `MediaKindMask` flags | Compiles; severity comparisons are numeric |
| M-02 | `RuleCatalog` containing every rule in the tables, including explanation text and action IDs (action IDs may reference actions implemented later) | Tests: IDs unique and match `^[A-Z]{3}-[A-Z0-9-]+$`; every rule has non-empty title and three explanation parts; `MaxSeverity` for Quality and Authenticity rules is never Critical |
| M-03 | `RuleDefaults` registry for tunables with range validation | Test: out-of-range override is clamped and logged |
| M-04 | `RuleSettings` overlay: enable/disable, severity cap, parameter overrides; persisted through storage (subplan 03) | Tests: disabled rule yields no finding; cap lowers severity; override survives restart |
| M-05 | `SeverityPolicy` helpers: confidence-to-wording, heuristic cap below 0.5, file status aggregation | Table-driven tests for the wording and aggregation rules |
| M-06 | Evidence serialisation (JSON, invariant culture, stable key order) | Round-trip test with numbers, strings and arrays under a non-English culture |
| M-07 | Catalog export: `--self-check` writes nothing, but a test generates `docs/rules.md` from the catalog and fails if the committed file is stale | `docs/rules.md` matches the catalog |

## Exit criteria

- The catalog in code matches the tables above (the generated `docs/rules.md` is the cross-check).
- No detector logic exists yet; this phase is types, data and policy only.
