# 14 — Updater and release pipeline

Phase 14. Depends on: phase 1. Independent of all scanning work. Required before the first public
release (M1).

## Goal

The same signed, fail-closed updater and release flow as FastTaskMgr: tag a version, GitHub Actions
builds the installer and a signed manifest, the app verifies the manifest with a baked-in public key
before it trusts or downloads anything.

The flow is specified in [`docs/updater.md`](../docs/updater.md) (the owner's project-agnostic
description). Read it first. This subplan covers only what is specific to MediaVerifier.

## Source to port

Repository <https://github.com/rockenrooster/FastTaskMgr>, tag `v0.1.0.45`:

| FastTaskMgr file | Becomes | Change |
|------------------|---------|--------|
| `src/FastTaskMgr/Core/Updates/UpdateService.cs` | `src/MediaVerifier.Core/Updates/UpdateService.cs` (split `UpdateTrust`, records and service into separate files if preferred) | Constants below; otherwise keep the logic line for line |
| `build.ps1` | `build.ps1` | Names, version source, secret name |
| `release.ps1` | `release.ps1` | Names, version source |
| `.github/workflows/release.yml` | `.github/workflows/release.yml` | Names, secret name, test step |
| `installer/FastTaskMgr.iss` | `installer/MediaVerifier.iss` | New `AppId`, per-user install, shortcuts, uninstall hook |
| `Diagnostics/SelfCheck.cs` → `UpdateTrustSelfCheck` | Same check in MediaVerifier's self-check | None |
| `UI/Pages/SettingsPage.cs` update section | Settings → Updates card (kit controls) | Same state model |

Do not redesign the updater. Differences from the source must be listed in the deviations log.

### Constants

| Item | FastTaskMgr | MediaVerifier |
|------|-------------|---------------|
| Repository | `rockenrooster/FastTaskMgr` | `rockenrooster/MediaVerifier` |
| Installer asset | `FastTaskMgr-Setup.exe` | `MediaVerifier-Setup.exe` |
| Manifest asset | `FastTaskMgr-Setup.exe.manifest.json` | `MediaVerifier-Setup.exe.manifest.json` |
| Signature asset | `FastTaskMgr-Setup.exe.manifest.sig` | `MediaVerifier-Setup.exe.manifest.sig` |
| Portable asset | `FastTaskMgr.exe` (+ `.sha256`) | `MediaVerifier-portable.zip` (+ `.sha256`): the whole app folder, because the app is more than one file |
| User agent | `FastTaskMgr/<version>` | `MediaVerifier/<version>` |
| Download folder | `%LOCALAPPDATA%\FastTaskMgr\Updates` | `AppPaths.Updates` |
| CI secret | `FASTTASKMGR_UPDATE_SIGNING_PRIVATE_KEY_PEM` | `MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM` |
| Local key file env | `..._PEM_FILE` | `MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM_FILE` |
| Expected download URL path | `/rockenrooster/FastTaskMgr/releases/download/<tag>/FastTaskMgr-Setup.exe` | `/rockenrooster/MediaVerifier/releases/download/<tag>/MediaVerifier-Setup.exe` |
| Public key | FastTaskMgr key | **New key pair** (task U-01) |

Signature scheme is unchanged: RSA, PKCS#1 v1.5, SHA-256, over the exact UTF-8 bytes of the manifest.
Manifest schema is unchanged (`schemaVersion` 1).

### Behaviour to preserve exactly

- One in-flight check at a time; duplicate callers get the same task.
- `CanInstall` (signed installer valid) is separate from `CanDownload` (valid **and** newer). A
  portable copy can install itself from the latest signed installer even when already current.
- Manifest validation: schema version, tag equals release tag, version equals release version, asset
  name, size equals the release asset size, 64-hex SHA-256, parseable `createdUtc`, absolute HTTPS
  download URL that equals the asset URL and matches the expected GitHub path.
- Download to `*.download`, compare `Content-Length` early, hash while streaming, verify size and
  hash, then move into place. Verify size and hash again immediately before launching the installer.
- Installer arguments `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`, then exit the app.
- Nothing downloads or installs without a user action. A newer release with missing or invalid signed
  metadata shows a status message and enables nothing.

## MediaVerifier-specific behaviour

- **When to check.** Once per GUI launch, about 10 seconds after the window is shown, if
  *Check for updates on startup* is on. Never from a headless run. Manual *Check now* always works.
- **Where it shows.** Settings → Updates card: current version, status text, *Check now*, *Download
  and install*, progress, link to release notes. A dot on the nav rail when an update is available.
- **Active scan.** If a run is in progress when the user starts an install, say so: "A scan is
  running. It will be interrupted and can be resumed after the update." Proceed only on confirmation;
  cancel the run cleanly before launching the installer.
- **Installed versus portable.** Installed means the exe's folder equals the `InstallLocation` in the
  uninstall registry key for the installer's `AppId` (per-user installs write under `HKCU`,
  all-users under `HKLM`). A portable copy sees *Install MediaVerifier* instead of *Download and
  install*, exactly as in FastTaskMgr.
- **After a silent update** the installer relaunches the app; the app then reconciles scheduled tasks
  and registrations (subplan 13) because its path may have changed.
- Make `ValidateManifest`, `TryParseVersion`, `NormalizeSha256` and `IsExpectedGithubDownloadUri`
  `internal static` so tests reach them through `InternalsVisibleTo`.

## Versioning

- Four-part version `X.Y.Z.W`, tags `vX.Y.Z.W`, starting at `0.1.0.0`.
- The version lives in **`Directory.Build.props`** (`AssemblyVersion` and `FileVersion`) so the exe
  and the Core library always agree. `build.ps1` and `release.ps1` edit that file where FastTaskMgr's
  scripts edit the project file.
- `build.ps1` increments the fourth part unless `-NoIncrement` or `-Version` is given.
- The window title shows the version.

## Build script (`build.ps1`)

Port with these properties:

1. Run `scripts/Get-FfmpegBundle.ps1` (subplan 17). It fails the build if the bundle cannot be fetched
   or verified.
2. Publish `src/MediaVerifier/MediaVerifier.csproj`: `-c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:EnableCompressionInSingleFile=false` into an
   intermediate folder.
3. Assemble the app folder `artifacts\MediaVerifier\`:

   ```
   MediaVerifier\
     MediaVerifier.exe          self-contained single file (about 125 MB)
     e_sqlite3.dll              SQLite 3.49.1 from SQLitePCLRaw.bundle_e_sqlite3
     ffmpeg\                    the bundle exactly as verified (subplan 17)
     THIRD-PARTY-NOTICES.md     licences of everything shipped (subplan 16)
   ```

   Fail if the publish output contains any file other than `MediaVerifier.exe` and `e_sqlite3.dll`
   (ignoring `.pdb`): a new native file means a dependency was added without updating the installer
   and this list.
4. Write `artifacts\MediaVerifier-portable.zip` (the folder above, at the root of the zip) and its
   `.sha256`.
5. If Inno Setup 6 is available, build `artifacts\MediaVerifier-Setup.exe` from the folder, its
   `.sha256`, the manifest and the signature. Under GitHub Actions a missing compiler, bundle or
   signing key is an error; locally it is a warning and the signed assets are skipped.
6. Check the size budgets from the master plan (app folder at most 200 MB, installer at most 90 MB)
   and print both sizes.

## Release script and workflow

- `release.ps1` as in FastTaskMgr: compute or accept the version, refuse if the tag exists locally or
  remotely, write the version, promote the `Unreleased` section of `CHANGELOG.md` to a dated version
  section, commit, push the branch, create and push the tag.
- `release.yml`: on tags `v*` and manual dispatch with a tag input. Steps: checkout the tag, set up
  .NET 10, install Inno Setup, resolve and validate the version from the tag, **run unit tests**,
  run `build.ps1 -Version <v> -NoIncrement` with the signing secret, create or update the GitHub
  release with notes taken from the matching `CHANGELOG.md` section (generated notes as the fallback),
  upload the six assets (`MediaVerifier-Setup.exe`, its `.sha256`, `.manifest.json` and
  `.manifest.sig`, `MediaVerifier-portable.zip` and its `.sha256`), and mark it as the latest release.
  The release notes end with a line naming the bundled FFmpeg version and linking to its
  `ffmpeg-bundle-…` release, where its source is.
- `ci.yml` (new, not in FastTaskMgr): on pushes and pull requests to the default branch: build
  Release with warnings as errors, run unit tests, run `--self-check` on the built exe. Launch the exe
  with `Start-Process -Wait -PassThru` and test `ExitCode`; a GUI-subsystem exe does not block a
  PowerShell pipeline or set `$LASTEXITCODE`.

## Installer (`installer/MediaVerifier.iss`)

Start from FastTaskMgr's script and change:

- New `AppId` GUID (generate once; never change it afterwards).
- Per-user by default: `PrivilegesRequired=lowest`, `PrivilegesRequiredOverridesAllowed=dialog`,
  `DefaultDirName={autopf}\MediaVerifier`. Silent updates then need no elevation.
- Registry entries use `Root: HKA` (resolves to `HKCU` or `HKLM` to match the install mode).
- Start-menu and optional desktop shortcuts carry `AppUserModelID: "rockenrooster.MediaVerifier"`
  (must equal the ID used for toasts in subplan 13).
- `SetupIconFile` and `UninstallDisplayIcon` use the icon from task F-09.
- `CloseApplications=yes` with a filter on `MediaVerifier.exe`; relaunch after a silent install
  (`skipifnotsilent` run entry) as in the source script.
- `[UninstallRun]`: `{app}\MediaVerifier.exe --cleanup` so scheduled tasks and per-user registry
  entries are removed. Leave the user's data folder in place and say so on the final page.
- Files: everything in `artifacts\MediaVerifier\`, recursively, with `ignoreversion`. An upgrade
  replaces the whole `ffmpeg\` folder (`[InstallDelete]` of `{app}\ffmpeg\*` first, so DLLs from an
  older FFmpeg with different version suffixes never linger).
- `Compression=lzma2/ultra64` and `SolidCompression=yes` (the self-contained runtime compresses well;
  the 90 MB installer budget depends on it).

## Signing key

Task U-01 adds `scripts/New-UpdateSigningKey.ps1`, which:

1. Generates an RSA-3072 key pair.
2. Writes the private key to `artifacts\MediaVerifier-update-signing-private-key.pem` (the `artifacts`
   folder and `*.pem` are ignored by git; the same convention as FastTaskMgr).
3. Prints the public key PEM for `UpdateTrust.PublicKeyPem`.
4. With `-SetGitHubSecret`, pipes the private key to
   `gh secret set MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM` for the repository.
5. Refuses to overwrite an existing key without `-Force`, and explains that replacing the key strands
   every installed copy (they can no longer verify updates).

**The script must be run by the repository owner on their own machine.** An agent working in a
sandbox must not generate the production key: the private key would be lost with the sandbox. Until
the owner has run it, keep a clearly marked placeholder public key in `UpdateTrust`; with a
placeholder every manifest fails verification, which is the correct fail-closed behaviour, and the
self-check still passes because it tests the algorithm with a throwaway key.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| U-01 | `scripts/New-UpdateSigningKey.ps1` | Dry run (`-OutDir` to a temp folder, no secret) produces a key pair whose signature round-trips through `UpdateTrust.VerifyManifestSignature`; refuses to overwrite |
| U-02 | Port `UpdateService`, `UpdateTrust`, records | Compiles with the new constants; behaviour list above holds |
| U-03 | Unit tests for trust and validation | Valid signature accepted; tampered manifest, wrong key, wrong tag, wrong version, wrong asset name, wrong size, bad hash, non-HTTPS URL, foreign host and path mismatch are each rejected |
| U-04 | Download and install path tests against a local HTTP listener | Correct file installs to the update folder; short stream, long stream, wrong hash and wrong `Content-Length` each delete the temp file and report an error |
| U-05 | `build.ps1`, `Directory.Build.props` versioning | Local run produces the `artifacts\MediaVerifier\` folder exactly as listed and the portable zip; the folder runs on a machine with no .NET installed; version increments; `-NoIncrement` and `-Version` honoured; without a key the manifest is skipped with a warning; an unexpected file in the publish output fails the build |
| U-06 | `installer/MediaVerifier.iss` | Compiles with Inno Setup 6 within the size budget; per-user install without elevation; installs and runs on a clean Windows 10 22H2 virtual machine with no .NET runtime; upgrade in place keeps settings and replaces `ffmpeg\` cleanly; uninstall removes tasks and registry entries; silent install relaunches the app |
| U-07 | `release.ps1`, `release.yml` (and `ci.yml` if task F-01 has not already added it) | CI green on a pull request. A tag on a fork or test repository with a test key produces a release with all six assets; a tag without the secret fails the workflow |
| U-08 | Settings → Updates card and nav indicator | States: never checked, checking, up to date, update available, downloading with percentage, validation failed, offline. Buttons enable exactly per `CanDownload` / `CanInstall` |
| U-09 | Startup check timing, active-scan confirmation, portable *Install* path | Manual test of all three |
| U-10 | Self-check includes the trust round trip | `--self-check` fails if signature verification is broken |
| U-11 | First release dry run | With the owner's real key in place: tag `v0.1.0.1`, install it, tag `v0.1.0.2`, update from inside the app |

## Exit criteria

- An installed build updates itself to a newer signed release with one click and no elevation prompt.
- A release whose manifest or signature is missing, altered or signed by another key never enables
  the install button.
- The private key appears nowhere in the repository, the build output, or the logs.
- The installed app and the portable zip run on a Windows machine that has never had .NET or FFmpeg
  installed.

## Pitfalls

- GitHub's API rate-limits unauthenticated requests per IP; treat HTTP 403/429 as "could not check",
  not as "no update".
- `Get-FileHash` output is upper case; the manifest and the app compare lower-case hex.
- Write the manifest as UTF-8 **without** a byte-order mark; the signature covers the exact bytes.
- A release created before its assets finish uploading is briefly visible without them; the updater
  already reports "signed update assets are missing" for that window. Do not weaken it.
- Never change the installer `AppId` or the signing key after the first public release.
