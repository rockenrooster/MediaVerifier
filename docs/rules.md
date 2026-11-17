# Rules

Generated from `RuleCatalog` by the test suite (`RuleCatalogTests`); do not edit by hand. Severity tiers and wording rules are in [subplan 02](../plans/02-findings-model.md).

## Integrity

| ID | Title | Severity | Depth | Applies to | On by default | Milestone |
|----|-------|----------|-------|------------|---------------|-----------|
| `GEN-UNREADABLE` | File cannot be read | Critical | Quick | all | yes | M1 |
| `GEN-EMPTY` | File is empty | Critical | Quick | all | yes | M1 |
| `GEN-ZEROFILL` | File is filled with zeros | Low – Critical | Quick | all | yes | M1 |
| `INT-ROT` | File changed without being modified (bit rot) | Critical | verification | all | yes | M1 |
| `INT-META-DRIFT` | Metadata changed without a timestamp update | Low | verification | all | yes | M2 |
| `INT-SIDECAR` | Does not match its checksum file | High | Standard | all | yes | backlog |

### `GEN-UNREADABLE` File cannot be read

- **What this means:** Windows reported a hardware error while reading part of this file. That part could not be read back.
- **Why it matters:** Read errors usually mean the drive is failing. Other files on it may be next, and the unreadable part is lost unless a copy exists.
- **What you can do:** Restore the file from a healthy copy if you have one. Copy what you can to another drive, then check this one.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-CHECK-DRIVE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `GEN-EMPTY` File is empty

- **What this means:** The file is 0 bytes long. Whatever it held is gone.
- **Why it matters:** Empty files usually come from an interrupted copy or download, or from a disk error. Nothing in it can be played or shown.
- **What you can do:** Restore it from a backup or download it again. If it is not needed, move it to quarantine.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `GEN-ZEROFILL` File is filled with zeros

- **What this means:** Parts of the file contain nothing but zero bytes where the picture or sound should be.
- **Why it matters:** Zeros replace data after a crash, a failed copy or a drive fault. The zeroed parts cannot be recovered from this file.
- **What you can do:** Restore it from a healthy copy. If there is none, a playable copy can sometimes be saved from what is left.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `criticalPercent` 50 % (1–100)

### `INT-ROT` File changed without being modified (bit rot)

- **What this means:** The file's content no longer matches the fingerprint taken earlier, although its size and modified date are unchanged. Nothing that normally edits files did this.
- **Why it matters:** Silent changes come from failing storage, memory or cables. Backups that copy the file again copy the damage too.
- **What you can do:** Restore it from a healthy copy. If you changed the file yourself with a tool that kept its date, accept the new content.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-ACCEPT-BASELINE`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `INT-META-DRIFT` Metadata changed without a timestamp update

- **What this means:** The file's tags or other metadata changed, but its picture or sound is exactly the same and its modified date was kept.
- **Why it matters:** This is usually a tagging tool that preserves dates, not damage. The new content becomes the reference automatically.
- **What you can do:** Nothing, unless you did not expect any program to change this file.
- **Actions:** `ACT-IGNORE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `INT-SIDECAR` Does not match its checksum file

- **What this means:** A checksum file next to this one (.sfv, .md5, .sha1, .sha256 or .ffp) records a different value for it.
- **Why it matters:** The file changed after the checksum was made, so it is probably not the original any more.
- **What you can do:** Restore it from a healthy copy, or replace it from the original source.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

## Corruption

| ID | Title | Severity | Depth | Applies to | On by default | Milestone |
|----|-------|----------|-------|------------|---------------|-----------|
| `GEN-UNRECOGNIZED` | Not a recognisable media file | Critical | Quick | all | yes | M1 |
| `COR-CONTAINER` | Container is broken | Critical | Quick | all | yes | M1 |
| `COR-TRUNCATED` | File is cut short | High – Critical | Quick | all | yes | M1 |
| `COR-DECODE` | Decoder errors | Low – Critical | Standard | all | yes | M1 |
| `COR-CONCEALED` | Damaged video frames (hidden by error concealment) | Medium – High | Standard | video | yes | M1 |
| `COR-CHECKSUM` | Embedded checksum mismatch | High | Standard | photo, audio | yes | M1 |
| `COR-HANG` | Decoder stalled | High | Standard | all | yes | M1 |
| `COR-PARTIAL-IMAGE` | Image decodes only partly | High | Standard | photo | yes | M1 |
| `COR-DROPOUT` | Audio dropouts | Low – High | Standard | audio | yes | M2 |
| `COR-CLICKS` | Clicks or discontinuities | Low – Medium | Standard | audio | yes | M3 |
| `COR-VISUAL` | Possible visual corruption | Medium | Standard | photo | yes | backlog |
| `AUD-SILENT` | Track is silent | High | Standard | audio | yes | M2 |

### `GEN-UNRECOGNIZED` Not a recognisable media file

- **What this means:** The file has a media extension, but its content does not look like any known photo, video or audio format.
- **Why it matters:** It may be damaged beyond recognition, or it may not be a media file at all. Either way nothing can open it as media.
- **What you can do:** Restore it from a backup if it should be media. Otherwise move it to quarantine.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-CONTAINER` Container is broken

- **What this means:** The part of the file that describes its contents is missing or unreadable, so players cannot find the picture or sound. A video without its index (the moov part of an MP4) is a common case.
- **Why it matters:** Most programs refuse to open the file at all, even though much of the data may still be there.
- **What you can do:** Restore it from a healthy copy. A recording without an index can sometimes be rebuilt with a specialist tool and a reference file from the same device.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-RECOVER-INDEX-HELP`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-TRUNCATED` File is cut short

- **What this means:** The file ends earlier than its own header says it should. The end of the picture, recording or song is missing.
- **Why it matters:** This usually comes from an interrupted copy or download. The missing part cannot be recovered from this file.
- **What you can do:** Restore it from a healthy copy, or save a playable copy of the part that is left.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `criticalPercent` 50 % (5–100); `audioSeconds` 0.5 seconds (0.1–60); `audioPercent` 1 % (0.1–50); `mp3EstimatePercent` 3 % (0.5–50); `videoSeconds` 2 seconds (0.5–600); `videoPercent` 2 % (0.1–50); `keyFrameSeconds` 12 seconds (1–600); `secondsPerError` 1.5 seconds (0.1–60)

### `COR-DECODE` Decoder errors

- **What this means:** While playing the file from start to end, the decoder hit damaged data and reported errors.
- **Why it matters:** Damaged parts show up as glitches, blocks, skips or noise, and every copy made from this file carries them.
- **What you can do:** Restore it from a healthy copy. Otherwise save a playable copy that drops the damaged parts.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `highErrors` 5 errors (1–100000); `audioHighSeconds` 0.5 seconds (0.1–600); `videoHighSeconds` 2 seconds (0.1–600); `frameShortfall` 2 frames (1–100000); `frameShortfallPercent` 0.1 % (0.01–50)

### `COR-CONCEALED` Damaged video frames (hidden by error concealment)

- **What this means:** The decoder found broken video frames and patched over them so playback carries on. You may see smearing or blocks at those moments.
- **Why it matters:** The damage is real even where it is hard to notice, and copies made from this file carry it.
- **What you can do:** Restore it from a healthy copy, or save a playable copy.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `highFrames` 10 frames (1–100000)

### `COR-CHECKSUM` Embedded checksum mismatch

- **What this means:** The file carries its own checksums, as FLAC, PNG and Ogg files do, and part of the content no longer matches them.
- **Why it matters:** A mismatch proves the data changed after it was written, even where playback sounds or looks normal.
- **What you can do:** Restore it from a healthy copy. A FLAC that otherwise decodes cleanly can be rewritten so its checksum is correct again.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-REENCODE-FLAC`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-HANG` Decoder stalled

- **What this means:** Decoding this file stopped making progress and had to be stopped.
- **Why it matters:** Damaged data can send players into a loop or a freeze, and the file may crash other programs.
- **What you can do:** Restore it from a healthy copy, or save a playable copy.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-PARTIAL-IMAGE` Image decodes only partly

- **What this means:** Only part of the picture could be decoded. The rest is missing, grey or garbled.
- **Why it matters:** The lost part of the picture cannot be recovered from this file.
- **What you can do:** Restore it from a healthy copy. Many cameras keep a smaller preview inside the file, which can be extracted.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-DROPOUT` Audio dropouts

- **What this means:** The sound cuts to complete digital silence for short moments, with abrupt edges, in the middle of the recording.
- **Why it matters:** Gaps like these come from read errors while ripping a CD or from damaged data. Music does not stop that way on its own.
- **What you can do:** Replace it from a healthy copy or a fresh rip.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-SALVAGE`, `ACT-EXTRACT-PREVIEW`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-CLICKS` Clicks or discontinuities

- **What this means:** There are isolated, sudden jumps in the waveform far above the surrounding sound. They are heard as clicks.
- **Why it matters:** They often come from small read or transfer errors. This check is experimental and can mistake sharp sounds for clicks.
- **What you can do:** Listen to the marked spots. If you hear clicks, replace the file from a healthy copy.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-VISUAL` Possible visual corruption

- **What this means:** Part of the photo has a sharp, block-shaped break although it decodes without errors.
- **Why it matters:** This can be damage the format cannot detect on its own. The check is experimental.
- **What you can do:** Look at the photo. If it is damaged, restore it from a healthy copy.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-IGNORE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-SILENT` Track is silent

- **What this means:** The whole file is silent or close to it.
- **Why it matters:** A silent music file almost always means the recording failed or the audio was lost.
- **What you can do:** Restore it from a healthy copy or replace it.
- **Actions:** `ACT-RESTORE-COPY`, `ACT-EXPORT-LIST`, `ACT-QUARANTINE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

## Authenticity

| ID | Title | Severity | Depth | Applies to | On by default | Milestone |
|----|-------|----------|-------|------------|---------------|-----------|
| `AUD-FAKE-LOSSLESS` | Lossless file made from a lossy source | Low – High | Standard | audio | yes | M2 |
| `AUD-UPSAMPLED` | Hi-res file upsampled from a lower rate | Low – Medium | Standard | audio | yes | M2 |
| `AUD-PADDED-BITS` | Bit depth is padded | Medium | Standard | audio | yes | M2 |
| `AUD-BITRATE-INFLATED` | Bitrate higher than the audio quality | Low – Medium | Standard | audio | yes | M2 |
| `VID-UPSCALED` | Resolution is higher than the detail | Low – High | Standard | video | yes | M2 |
| `VID-INFLATED` | Bitrate higher than the picture quality | Low – Medium | Standard | video | yes | M2 |
| `IMG-UPSCALED` | Image is larger than its detail | Low – Medium | Standard | photo | yes | M2 |

### `AUD-FAKE-LOSSLESS` Lossless file made from a lossy source

- **What this means:** The audio has the hard frequency cutoff that {sourceName} encoders leave, but the file is stored as {formatName}. It was probably converted from {sourcePhrase}.
- **Why it matters:** The file takes the space of a lossless file but sounds no better than the lossy file it came from.
- **What you can do:** Replace it from a genuine lossless source. Nothing can restore what the {sourceName} encoder removed.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-UPSAMPLED` Hi-res file upsampled from a lower rate

- **What this means:** The file uses a high sample rate such as 96 kHz, but its sound stops where a normal 44.1 or 48 kHz recording stops. It was probably converted up from a lower rate.
- **Why it matters:** It takes two or more times the space of the original without any gain in quality, and it is not the hi-res recording it claims to be.
- **What you can do:** Replace it from a genuine hi-res source if you want one. Otherwise keep it as it is.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-PADDED-BITS` Bit depth is padded

- **What this means:** The file is stored with more than 16 bits per sample, but only the top 16 carry sound. The rest are always zero.
- **Why it matters:** It is a 16-bit recording presented as hi-res, and it uses more space than it needs.
- **What you can do:** Store it as 16-bit. This is lossless: the sound stays bit for bit the same.
- **Actions:** `ACT-SHRINK-BITDEPTH`, `ACT-IGNORE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-BITRATE-INFLATED` Bitrate higher than the audio quality

- **What this means:** The file uses a high bitrate, but its sound has the frequency cutoff of a much lower one. It was probably re-encoded from a lower-quality copy.
- **Why it matters:** Re-encoding cannot add back detail. The file only takes more space than its quality deserves.
- **What you can do:** Replace it from a better source if quality matters to you.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-UPSCALED` Resolution is higher than the detail

- **What this means:** The video is stored at a high resolution, but its pictures carry no more detail than a lower resolution would. It was probably enlarged from a smaller source.
- **Why it matters:** An upscaled file looks no sharper than its source and takes much more space. This check finds ordinary enlargements; it cannot reliably detect AI upscaling.
- **What you can do:** Replace it from a genuine source at this resolution if quality matters to you.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-INFLATED` Bitrate higher than the picture quality

- **What this means:** The video uses a high bitrate, but the picture is upscaled or heavily compressed. It was probably re-encoded from a weaker copy.
- **Why it matters:** The extra bits cannot bring back lost detail; the file is just larger than its quality deserves.
- **What you can do:** Replace it from a better source if quality matters to you.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `IMG-UPSCALED` Image is larger than its detail

- **What this means:** The photo is stored at a large size, but it carries no more detail than a smaller version would. It was probably enlarged.
- **Why it matters:** It looks no sharper than the smaller original and takes more space. This check cannot reliably detect AI upscaling.
- **What you can do:** Look for the original photo if you need the full detail.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

## Quality

| ID | Title | Severity | Depth | Applies to | On by default | Milestone |
|----|-------|----------|-------|------------|---------------|-----------|
| `AUD-LOW-BITRATE` | Low bitrate | Low | Quick | audio | yes | M2 |
| `AUD-CLIPPING` | Clipping | Low – High | Standard | audio | yes | M2 |
| `AUD-OVERS` | Decodes above full scale | Low | Standard | audio | yes | M2 |
| `AUD-DC` | DC offset | Low – Medium | Standard | audio | yes | M2 |
| `AUD-DEAD-CHANNEL` | A channel is silent | Medium | Standard | audio | yes | M2 |
| `AUD-PHASE` | Channels are out of phase | Medium | Standard | audio | yes | M2 |
| `AUD-LONG-SILENCE` | Long silence at start or end | Low | Standard | audio | yes | M3 |
| `AUD-DUAL-MONO` | Stereo file with identical channels | Low | Standard | audio | no | M3 |
| `AUD-DYNAMICS` | Very low dynamic range | Low | Standard | audio | no | backlog |
| `VID-STARVED` | Bitrate too low for the resolution | Low – Medium | Quick | video | yes | M2 |
| `VID-BLOCKY` | Heavy compression artifacts | Low – Medium | Standard | video | yes | M2 |
| `VID-FROZEN` | Frozen picture | Low – Medium | Deep | video | yes | M3 |
| `VID-BLACK` | Long black section | Low | Deep | video | no | M3 |
| `VID-AUDIO-SILENT` | Soundtrack is silent | Medium | Standard | video | yes | M3 |
| `IMG-HEAVY-COMPRESSION` | Heavily compressed | Low – Medium | Quick | photo | yes | M2 |
| `IMG-LOW-RES` | Low resolution | Low | Quick | photo | no | M3 |

### `AUD-LOW-BITRATE` Low bitrate

- **What this means:** The file is compressed at a bitrate below what this format normally needs to sound clean.
- **Why it matters:** Low bitrates remove detail and can add swishing or ringing sounds, especially on headphones.
- **What you can do:** Replace it from a better source if you notice the difference.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-CLIPPING` Clipping

- **What this means:** Peaks of the waveform are cut flat because the sound was louder than the format can hold.
- **Why it matters:** Clipping is heard as harsh distortion on loud passages. It happened during recording or mastering, so every copy of this release has it.
- **What you can do:** Look for a better master of the same recording. A de-clipped copy can be made as an estimate; it never replaces the original.
- **Actions:** `ACT-DECLIP-COPY`, `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `lowPerMinute` 5 clipped peaks per minute (1–10000); `mediumPerMinute` 60 clipped peaks per minute (1–10000); `highPerMinute` 600 clipped peaks per minute (1–100000)

### `AUD-OVERS` Decodes above full scale

- **What this means:** When decoded, the sound goes above the highest level a digital file can play.
- **Why it matters:** Most players clip those peaks, which can cause brief distortion.
- **What you can do:** Usually nothing. Volume levelling in your player avoids the distortion.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-DC` DC offset

- **What this means:** The waveform is shifted away from the centre line, as if a constant value were added to it.
- **Why it matters:** It wastes headroom and can cause clicks where tracks start, stop or are edited.
- **What you can do:** Usually nothing. An audio editor can remove the offset if the clicks bother you.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-DEAD-CHANNEL` A channel is silent

- **What this means:** One channel of this recording is silent while the others carry sound.
- **Why it matters:** You hear the music from one side only. It is usually a recording or conversion mistake.
- **What you can do:** Replace it from a better source.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-PHASE` Channels are out of phase

- **What this means:** The left and right channels are close to mirror images of each other, as if one were wired backwards.
- **Why it matters:** The sound seems to come from nowhere, the bass is thin, and played in mono the music can nearly vanish.
- **What you can do:** Replace it from a better source.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-LONG-SILENCE` Long silence at start or end

- **What this means:** The track starts or ends with a long stretch of digital silence.
- **Why it matters:** Usually harmless, but it can hide a bonus track or point to a bad rip.
- **What you can do:** Usually nothing.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-DUAL-MONO` Stereo file with identical channels

- **What this means:** Both channels of this stereo file are exactly the same.
- **Why it matters:** It is a mono recording stored as stereo. Nothing is lost, but it takes twice the space it needs.
- **What you can do:** Nothing, unless you expected a stereo recording.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `AUD-DYNAMICS` Very low dynamic range

- **What this means:** Loud and quiet passages differ very little: the music was compressed to sound loud all the time.
- **Why it matters:** Heavily compressed masters tire the ear and lose punch.
- **What you can do:** Look for a different master of the same recording.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-STARVED` Bitrate too low for the resolution

- **What this means:** The video uses very few bits per pixel for its resolution and frame rate.
- **Why it matters:** Starved encodes show blocks, banding and smearing, especially in dark or fast scenes.
- **What you can do:** Replace it from a better source if you notice the artifacts.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-BLOCKY` Heavy compression artifacts

- **What this means:** Sampled frames show strong block edges from heavy compression.
- **Why it matters:** The blocks are part of the picture now and cannot be removed.
- **What you can do:** Replace it from a better source if quality matters to you.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-FROZEN` Frozen picture

- **What this means:** The picture stops changing for a long stretch while the sound keeps playing.
- **Why it matters:** This usually points to a failed recording or a damaged conversion.
- **What you can do:** Check the marked part. Replace the file if the picture should move.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-BLACK` Long black section

- **What this means:** The video is completely black for a long stretch.
- **Why it matters:** This can be intended, such as credits or scene changes, but it can also mean a failed recording.
- **What you can do:** Check the marked part.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-AUDIO-SILENT` Soundtrack is silent

- **What this means:** The video's main soundtrack is silent from start to end.
- **Why it matters:** The sound was probably lost during recording or conversion.
- **What you can do:** Replace it from a healthy copy if it should have sound.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `IMG-HEAVY-COMPRESSION` Heavily compressed

- **What this means:** The photo was saved with very strong compression.
- **Why it matters:** Strong compression adds blocks and smears fine detail, and saving it again makes it worse.
- **What you can do:** Look for the original photo if quality matters to you.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `IMG-LOW-RES` Low resolution

- **What this means:** The photo is small compared with what cameras and phones normally produce.
- **Why it matters:** It may be a thumbnail or a reduced copy rather than the original.
- **What you can do:** Look for the original photo.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

## Structure

| ID | Title | Severity | Depth | Applies to | On by default | Milestone |
|----|-------|----------|-------|------------|---------------|-----------|
| `GEN-EXT-MISMATCH` | Extension does not match content | Low – Medium | Quick | all | yes | M1 |
| `GEN-PATH` | Path may cause problems | Low | Quick | all | yes | M3 |
| `COR-TIMESTAMPS` | Timestamp problems | Low – Medium | Standard | video, audio | yes | M2 |
| `COR-INDEX` | Seek index missing or wrong | Low – Medium | Quick | video, audio | yes | M2 |
| `COR-STREAM-LENGTH` | Audio and video lengths differ | Low – Medium | Quick | video | yes | M2 |
| `COR-TRAILING` | Unexpected data after the end | Low | Quick | all | yes | M3 |
| `VID-INTERLACE` | Interlacing mismatch | Low – Medium | Standard | video | yes | M3 |
| `VID-HDR` | HDR signalling problem | Low – Medium | Quick | video | yes | M3 |

### `GEN-EXT-MISMATCH` Extension does not match content

- **What this means:** The file's name says one format but its content is another, for example a PNG picture named .jpg.
- **Why it matters:** Some players and editors trust the extension and refuse the file, or open it with the wrong program.
- **What you can do:** Rename it to the correct extension. The content is not changed.
- **Actions:** `ACT-FIX-EXT`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `GEN-PATH` Path may cause problems

- **What this means:** The file's full path is longer than 259 characters, ends with a dot or a space, or uses a name Windows reserves, such as CON or AUX.
- **Why it matters:** Many programs, backup tools and older parts of Windows cannot open or copy such files.
- **What you can do:** Shorten or rename the folder or the file. MediaVerifier does not rename it for you.
- **Actions:** `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `maxPathLength` 259 characters (100–32767)

### `COR-TIMESTAMPS` Timestamp problems

- **What this means:** The timing information inside the file jumps backwards or has gaps.
- **Why it matters:** Players may stutter, lose sync between sound and picture, or show the wrong length.
- **What you can do:** Rebuild the container. This rewrites the timing without touching the picture or sound.
- **Actions:** `ACT-REMUX`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `minCount` 10 errors (1–100000); `mediumCount` 100 errors (1–1000000)

### `COR-INDEX` Seek index missing or wrong

- **What this means:** The file has no index of where each moment starts, or the index is wrong.
- **Why it matters:** Seeking is slow or jumps to the wrong place, and some players show the wrong length.
- **What you can do:** Rebuild the index. This is lossless: the picture and sound are copied unchanged.
- **Actions:** `ACT-REMUX`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `COR-STREAM-LENGTH` Audio and video lengths differ

- **What this means:** The sound track and the picture in this file have clearly different lengths.
- **Why it matters:** This often follows an interrupted recording or a bad conversion. Sound may end early or drift out of sync.
- **What you can do:** Rebuild the container, which can correct the declared lengths. If sound is really missing, replace the file.
- **Actions:** `ACT-REMUX`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`
- **Limits:** `seconds` 2 seconds (0.5–600); `percent` 2 % (0.1–50); `mediumPercent` 10 % (1–100)

### `COR-TRAILING` Unexpected data after the end

- **What this means:** There are extra bytes after the point where the format says the file ends.
- **Why it matters:** Usually harmless leftovers from a copy or a tool, but sometimes part of another file or a sign of a bad write.
- **What you can do:** Usually nothing. If the file has other problems too, restore it from a healthy copy.
- **Actions:** `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-INTERLACE` Interlacing mismatch

- **What this means:** The video is interlaced but marked as progressive, or the other way round.
- **Why it matters:** Players then show comb-like lines on movement, or blur the picture needlessly.
- **What you can do:** Replace it, or convert it with a tool that de-interlaces it properly.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

### `VID-HDR` HDR signalling problem

- **What this means:** The video is marked as HDR but has fewer than 10 bits per colour, or its colour information contradicts itself.
- **Why it matters:** Players may show washed-out or wrongly bright colours.
- **What you can do:** Replace it from a correctly made source.
- **Actions:** `ACT-EXPORT-LIST`, `ACT-IGNORE`, `ACT-IGNORE-FOLDER-RULE`, `ACT-RESCAN`, `ACT-OPEN`, `ACT-REVEAL`, `ACT-COPY-PATH`

## Run alerts

| ID | Title | Severity | Trigger |
|----|-------|----------|---------|
| `RUN-MASS-MISSING` | Many files disappeared | High | A root that is online lost at least 20 % and at least 50 of its files since the previous run |
| `RUN-ROOT-OFFLINE` | Folder unavailable | Medium | A root could not be reached; its files are left untouched, not marked missing |
| `RUN-READ-ERRORS` | Storage read errors | Critical | Any GEN-UNREADABLE in the run; recommends checking the drive |
