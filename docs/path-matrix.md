# Path matrix

What the scanner does with awkward paths (subplan 15, Path matrix; task T-12). The test
`PathMatrixTests` scans each case below with the real engine and the bundled FFmpeg on NTFS.

## NTFS (tested)

| Case | Example | Result |
|------|---------|--------|
| Unicode | `Ünïcödé 日本語 Café\Мелодия ñ.flac` | Analysed normally |
| Emoji, including a skin-tone sequence | `Emoji 🎵\🎶 song 👍🏽.mp3` | Analysed normally |
| Path over 300 characters | 120 + 120 character folders and a 60 character name | Analysed normally; `GEN-PATH` (Low) warns that other programs may not open it |
| `%` in names | `100% done\a%20b %d %s.png` | Analysed normally (arguments reach FFmpeg through `ArgumentList`, never a shell) |
| Folder name ending in a space | `Trailing \clip.mp4` | Analysed through the `\\?\` form; `GEN-PATH` (Low) |
| Read-only file | attribute set | Analysed normally (the scanner only reads) |
| File locked by another program | opened with no sharing | Counted as a run error, left unscanned and not reported as damage; analysed on the next scan once it is free |

Folder and file names ending in a space or a dot exist when files come from a NAS, macOS or Linux.
Win32 path handling removes those characters, so MediaVerifier reaches such paths with the `\\?\`
form, and only those (see `ExtendedPath`). The database and the UI keep the plain form.

## FAT32 and exFAT (documented exceptions)

These volumes are not part of the automated matrix. What differs:

| Area | FAT32 / exFAT behaviour | Effect in MediaVerifier |
|------|-------------------------|-------------------------|
| File IDs | No stable 64-bit file ID | Renames and moves are seen as one file missing and a new one found; the fingerprint still links them for healthy-copy search |
| Modified time | FAT32 stores times in 2-second steps and in local time | A file changed within 2 seconds of the scan may look unchanged until the next change; a time-zone change can make every file look modified once |
| File size | FAT32 caps a file at 4 GiB | Larger videos cannot be stored there at all |
| Names | Trailing spaces and dots cannot be created; some characters (`"*:<>?|`) are invalid | The trailing-space case does not occur |
| Long paths | exFAT and FAT32 accept long paths through the same APIs | Same behaviour as NTFS |
| Locks and read-only | Same sharing rules as NTFS | Same behaviour |
