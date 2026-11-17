# Changelog

All notable changes to MediaVerifier are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.
- Audio quality checks: lossless files made from MP3, AAC or Opus; hi-res files upsampled from CD
  rates; padded bit depth; MP3s re-encoded at a higher bitrate; low bitrates; clipping and samples
  above full scale; DC offset, silent or out-of-phase channels, silent tracks, long silences and
  dropouts. The drawer shows the cutoff on the spectrum.
- MediaVerifier uses the .NET 10 Desktop Runtime (x64) installed on the PC instead of carrying its
  own copy: the download is much smaller, and setup asks for the runtime when it is missing.
- Scans check every photo, video and music file for damage: broken or truncated containers, decoder
  errors, hidden video damage at Deep depth, embedded checksum failures (FLAC, PNG, Ogg), stalled
  decodes, wrong seek indexes, mismatched extensions and unrecognised files.
- FLAC files are checked against the audio fingerprint stored inside them.
- New main window: Dashboard with health, protection, scans, severity tiles, media types, next steps
  and recent activity; dark, light and high-contrast themes that follow Windows.
- Issues page: severity and media-kind chips, filters, grouping by severity, folder or rule, a
  multi-select selection bar, and a detail drawer explaining each problem with its evidence.
- Library page: watched folders as cards (scan, verify, edit, remove, not-connected state), an Add
  folder dialog that checks the folder and counts its media files, and Browse with the folder tree
  and a sortable file table.
- Scans page: the running scan with progress, speed, time left, problems found and the files being
  worked on; history of runs with results; a run's details in the drawer; a confirmation with time
  estimates before a large scan.
- Settings page: General, Scanning, Rules, Integrity, Notifications, Updates, Advanced and About.
  Rules can be switched off, capped and tuned; findings are re-judged at once without a rescan.
- Tools page: quarantine, operations journal, reports (web page, CSV or JSON), media engine with
  its licence and integrity, and database maintenance (back up, compact, check, reset).
- Notifications flyout under the bell; keyboard shortcuts (Ctrl+F, /, F5, Ctrl+N, Ctrl+Shift+S, F1,
  Esc); ignoring a rule for a folder from the Issues row menu.
