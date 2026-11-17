# Changelog

All notable changes to MediaVerifier are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.
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
