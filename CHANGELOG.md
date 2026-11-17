# Changelog

All notable changes to MediaVerifier are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.
- Windows notifications: scheduled scans report in the Windows notification centre, where the
  notification stays after MediaVerifier exits; Review opens the run. A newer run replaces an unread
  older notification. Quiet hours post silently, with From and To times in Settings → Notifications,
  which also has Send a test notification and honours Notify about for scheduled scans.
- Webhook: Settings → Notifications can post a summary of each scheduled scan (counts only, no file
  names) to an HTTPS address, or plain HTTP on the local network after confirming; Send test checks
  it. The payload is described in docs/webhook.md.
- Links of the form `mediaverifier://run/<id>`, `mediaverifier://finding/<id>` and
  `mediaverifier://issues` open MediaVerifier at that view; anything else is ignored.
- While a scheduled scan runs in the background, the open window shows its progress, the Scan button
  says why it is unavailable, and Stop ends the background scan. A schedule that fires while the
  window is open runs in the window.
- Audio quality checks: lossless files made from MP3, AAC or Opus; hi-res files upsampled from CD
  rates; padded bit depth; MP3s re-encoded at a higher bitrate; low bitrates; clipping and samples
  above full scale; DC offset, silent or out-of-phase channels, silent tracks, long silences and
  dropouts. The drawer shows the file's spectrogram with the cutoff marked, and a waveform with the
  clipped spots and dropouts; click a mark to see the 20 ms around it.
- Experimental click detection (off by default; turn it on in Settings → Rules).
- Updates: Settings → Updates checks for a signed new version and installs it after verifying the
  download. Portable zip and installer builds (the installer checks for the .NET 10 Desktop Runtime).
- Schedule editor: name, folders, depth, repeat, days, time, time limit and conditions, with a
  summary of what will run and when.
- Dashboard recommendations group what is worth doing (files that changed on their own, missing
  seek indexes, wrong extensions, lossless files made from MP3, upscaled video, overdue verification,
  no backup folder or schedule, read errors, an interrupted scan) with a button that does it.
- Tools → Quarantine restores or deletes kept originals; Tools → Operations undoes a change;
  Ctrl+Z undoes the last change.
- More repairs: extract the embedded preview of a damaged camera photo, make an experimental
  de-clipped copy of lossless audio, and move files to the Recycle Bin.
- Schedules: scans run on their own through Windows Task Scheduler, even when MediaVerifier is
  closed, with nightly, weekly and monthly templates; they wait while on battery or while a game or
  full-screen app runs, and report new problems in the notification area and the bell.
- Repairs from the Issues page: rebuild a damaged index, rename files with the wrong extension, store
  padded audio as 16-bit, rewrite FLAC files with a wrong checksum, restore from a verified backup
  copy, save a playable copy of damaged audio and video, and move files to quarantine. Every change
  is checked before it replaces anything, keeps the original in quarantine and can be undone.
- The detail drawer shows a thumbnail (photo, video frame or cover art); upscale findings show the
  sampled frames, and a press-and-hold comparison with the copy rebuilt from the lower resolution;
  bitrate findings show where the file sits against the limits.
- Video checks: interlacing marked wrongly (combing), a picture that stands still, long black sections
  (off by default) and a silent soundtrack.
- Fixed: a video with black frames could fail to be analysed.
- Video and photo quality checks: videos and photos enlarged from a smaller source, videos with too
  little bitrate for their resolution, heavy block artifacts, high bitrates on weak pictures, HDR
  marked on 8-bit video, heavily compressed JPEGs and (off by default) low-resolution photos.
- Fixed: Settings did not open after the audio rules were added. Rule settings with decimals (for
  example 0.5 seconds) keep their decimals when edited.
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
