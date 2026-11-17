# Changelog

All notable changes to MediaVerifier are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.
- Fixed: the Scan button stayed disabled after the first folder was added, so clicking it did
  nothing. A started scan now shows in the status strip at once, and a scan that cannot start says
  why.
- Fixed: toasts, such as the one after adding a folder, showed as a blank white box.
- Fixed: text in right-click menus, drop-down lists and tooltips was heavy and jagged.
- Fixed: the More button in the Issues detail panel did nothing; it opens a menu again.
- Issues: `Open file` in the right-click menu and the detail panel's More menu, and a double-click
  opens the file in its program.
- Library → Browse has a Problem column with the file's most serious finding.

## 0.1.0.4 - 2026-11-17

- Nothing changes in how MediaVerifier works: this release checks that updating from inside the app
  works.

## 0.1.0.3 - 2026-11-17

- Updates: every step of Download and install is written to the log and any failure is shown, instead
  of the button seeming to do nothing. After an update of an all-users install MediaVerifier opens as
  the signed-in user, not as administrator, and the installer closes a running MediaVerifier itself.
- Dialogs are built like Disk Monitor 2.0's: one window over MediaVerifier that shows the dimmed
  window behind it, so nothing can cover a dialog and leave the app waiting on it unseen. The
  corners are rounded on Windows 10 too, and a dialog taller than a small window is no longer cut off.
- Add folder and Create report stay open until the folder is saved or the report written, and a
  failure is shown; before, they closed at once as if it had worked. Create report says when the
  folder to save to does not exist, and a message listing changed files shows the whole list.
- Schedules: when Windows will not accept a schedule, the editor stays open and says why, and the
  schedule is saved switched off; switching on a schedule that Windows refuses says why in a toast.
  Before, the schedule looked saved but never ran.
- An action that fails (a button, a menu item, a toast's action) now shows `Something went wrong`
  and writes the details to the error log, instead of doing nothing.

## 0.1.0.2 - 2026-11-17

- Fixed: Add folder did nothing, because its dialog failed while opening. The Dashboard's first-run
  button and the Issues page's Add folder and Scan buttons work as well now.
- Fixed: the first schedule could not be created, and every start logged a maintenance error, until
  MediaVerifier had a folder in the Task Scheduler.

## 0.1.0.1 - 2026-11-17

- MediaVerifier is released under the MIT License; the licence is in the app folder and in
  Settings → About → Licences.
- MediaVerifier ships its own FFmpeg 9.0.2 (LGPL 2.1 or later), built with only the decoders,
  filters and file access it needs; it cannot open network addresses. The licences of FFmpeg and the
  libraries built into it are in THIRD-PARTY.txt beside it, and its source is published with the build.
- Windows notifications: scheduled scans report in the Windows notification centre, where the
  notification stays after MediaVerifier exits; Review opens the run. A newer run replaces an unread
  older notification. Quiet hours post silently, with From and To times in Settings → Notifications,
  which also has Send a test notification and honours Notify about for scheduled scans.
- Settings → Updates shows the version, what the last check found and its progress, with Check now,
  Download and install (Install MediaVerifier on a portable copy) and Release notes; the rail shows
  Update available when a newer release exists. MediaVerifier checks about 10 seconds after it starts
  (when allowed), and a running scan is interrupted, after asking, before an update installs.
- Narrator reads the Settings sections (and which one is selected), the Issues and scan-history
  lists, the filter drop-downs with their current choice, and the search boxes.
- Settings → Scanning: edit the file types and the folder names scans always skip. Settings →
  Advanced: Run self-check. Settings → About: website, release notes and licences.
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
- The upscaling checks were calibrated on real films and photos: soft-focus films and high-resolution
  or shallow-focus photos are no longer called upscaled, and only clear enlargements are reported.
- Uninstalling removes MediaVerifier's scheduled tasks and their folder in the Task Scheduler, the
  start-with-Windows entry and the notification registration; your library and settings stay.
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
- Fixed: a Deep scan of videos already scanned at Standard depth reused the Standard pass, so hidden
  damage, frozen pictures and black sections were only found in videos never scanned before.
- Fixed: when another program held the library database locked, changes waited forever; they now
  fail after about 10 seconds with "database is locked". A full disk now ends a scan with "database or
  disk is full" instead of an unrelated message, and a clock set back no longer gives runs that end
  before they start.
- Fixed: when MediaVerifier ran as administrator, starting it again (or opening a link) did nothing
  instead of bringing the open window forward.
- Fixed: files in folders whose names end in a space or a dot (common on copies from a NAS, macOS
  or Linux) were skipped by scans.
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

