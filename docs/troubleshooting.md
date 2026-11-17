# Troubleshooting

## MediaVerifier does not start, or setup stops with a message about .NET

MediaVerifier needs the **.NET 10 Desktop Runtime (x64)**. Install it from
<https://dotnet.microsoft.com/download/dotnet/10.0> (choose *Desktop Runtime*, x64), then run setup or
the app again. MediaVerifier never downloads or installs the runtime itself.

## "The media engine is missing" or scanning is disabled

MediaVerifier decodes media with the FFmpeg programs in the `ffmpeg\` folder next to
`MediaVerifier.exe`. If that folder is missing or incomplete, reinstall MediaVerifier or extract the
portable zip again. Tools → Media engine shows which files are missing or changed. MediaVerifier does
not use any other FFmpeg on the PC.

## Scheduled scans do not run

- Scheduled scans are Windows Task Scheduler tasks under *MediaVerifier* in the Task Scheduler
  library. They run only while you are signed in.
- With *Only when plugged in*, a laptop on battery skips the scan; with *Wait if a game or
  full-screen app is running*, the scan waits up to an hour.
- If you moved the portable folder, start MediaVerifier once: it updates every task to the new path.
- Skipped runs are listed in the notification bell.

## No notifications appear

Check Settings → Notifications and Windows Settings → System → Notifications. Every notification is
also kept in the bell at the top of the window.

- **Send a test notification** (Settings → Notifications) shows whether Windows notifications work.
  When they do not, the test appears inside the window instead.
- MediaVerifier registers itself for notifications each time it starts (under
  `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\rockenrooster.MediaVerifier`). Start it once after
  moving a portable copy; `MediaVerifier.exe --self-check` reports whether the Windows notification
  service can be reached.
- Focus assist (Do not disturb) and quiet hours hold notifications back: quiet hours put them in the
  notification centre without a pop-up.
- Scheduled scans notify only about new problems at or above *Notify about* and the schedule's own
  setting; a scan that finds nothing new stays silent unless the schedule says *Always*.
- A webhook that does not answer is logged (Settings → Advanced → Open logs folder); *Send test* next
  to it checks the address.

## Scans are slow

- Hard disks and network drives are read one file at a time on purpose; SSDs in parallel.
- *Deep* decodes every video frame and is much slower than *Standard*. Use *Quick* for frequent
  checks and a monthly *Deep* scan.
- Fingerprint verification can be limited per scheduled scan (Schedule → Verification).

## A finding looks wrong (false positive)

- Open the finding: the drawer explains what was measured and shows the evidence.
- Heuristic findings (fake lossless, upscaled, clipping…) carry a confidence and say *likely* or
  *possibly*. A clean result is not proof, and a finding is not a verdict.
- Ignore one finding (Ctrl+I), ignore a rule for a folder (row menu), or adjust or disable the rule
  in Settings → Rules. Rule thresholds are explained in [`rules.md`](rules.md).

## A repair failed

Repairs never change the original until the result is verified; a failed repair leaves the original
untouched. Originals replaced by a repair are kept in the quarantine folder (`.mediaverifier-quarantine`
inside each library folder) for the configured number of days and can be restored with *Undo* or from
Tools → Quarantine.
