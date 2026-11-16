# Changelog

All notable changes to MediaVerifier are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.
- Scans check every photo, video and music file for damage: broken or truncated containers, decoder
  errors, hidden video damage at Deep depth, embedded checksum failures (FLAC, PNG, Ogg), stalled
  decodes, wrong seek indexes, mismatched extensions and unrecognised files.
- FLAC files are checked against the audio fingerprint stored inside them.
