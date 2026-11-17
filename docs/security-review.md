# Security review checklist (subplan 16, task H-04)

Reviewed 2026-11-16 on branch `implementation`. Each row names how it is enforced; *open* rows are
not done yet and block a public release.

| Item | Status | How it is enforced |
|------|--------|--------------------|
| FFmpeg and ffprobe are started without a shell, arguments only through `ProcessStartInfo.ArgumentList` | Done | `FfRunner` (subplan 04); no `UseShellExecute` and no argument strings anywhere in the engine |
| Filter graphs hold no file paths | Done | Frame sampling writes stats files by relative name in the job folder (`FrameSampling.Graph`) |
| Only the bundled FFmpeg is used | Done | `FfmpegLocator`; the development override is an environment variable that is logged, never offered in the UI |
| Nothing is downloaded or installed at run time except a verified update the user accepts | Done | `UpdateService`: manifest signature first, then size and SHA-256 while streaming and again before launch |
| Update signing private key absent from the repository | Done | `UpdateTrust.PublicKeyPem` is empty until the owner adds the public key; `New-UpdateSigningKey.ps1` refuses to write inside the repository. Scan the history again before the first public release |
| User media is changed only by the operation runner | Done | `OperationRunner` is the only writer; temporary output in the same folder, verification before the swap, rollback of every completed step, journal before any file step (fault-injection tests) |
| Permanent deletion only after confirmation | Done | `PurgeAsync` is the only deletion of user media; dialog B asks first |
| Backup roots are never written | Done | Precondition in `OperationRunner.CheckPreconditions` |
| Parsers of untrusted files validate lengths and ranges | Done | Structure readers (subplan 07) with the fuzz harness (`StructureFuzzTests`); evidence cache reader bounds every length |
| Downloaded archives stay inside their target folder | Done | `Get-FfmpegBundle.ps1` extracts to a fresh temporary folder and checks every file against `bundle.json` before moving it into place |
| IPC pipe restricted to the current user, message size capped | Done | `IpcServer` (subplan 01) |
| No elevation | Done | Per-user installer (`PrivilegesRequired=lowest`), scheduled tasks with `LeastPrivilege` and the interactive token |
| No telemetry | Done | No network access other than the update check; the FFmpeg bundle is built without networking (verified in `ffmpeg-bundle.yml`) |
| Logs hold paths, never file contents | Done | Logging calls pass paths and messages only |
| Notifications on the lock screen hold no file names | Done | `NotificationPolicy` texts are counts and folders only |
| The system report redacts the user name in paths | Open | Settings → Advanced → Copy report copies the version lines only today; redaction is needed once it includes paths |
| Code signing of the executable and installer | Open | Decided by the owner before the first public release |
