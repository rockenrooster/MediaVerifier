# 01 — Foundation

Phase 1. Depends on: nothing (the repository and plans exist). Unblocks: everything.

## Goal

Create the solution and turn it into a real application shell: one window with navigation and empty
pages, settings that persist, logging, a single running instance with a command channel, a headless
entry point, and a self-check. No scanning yet.

## Starting point

The repository contains documents only. This phase writes the first code.

### Solution skeleton (task F-01)

| Item | Specification |
|------|---------------|
| `MediaVerifier.sln` | Contains the three projects below |
| `Directory.Build.props` | `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `DebugType=embedded`, `AssemblyVersion` and `FileVersion` `0.1.0.0` (the single place the version lives; subplan 14) |
| `src/MediaVerifier/MediaVerifier.csproj` | `OutputType=WinExe`, `TargetFramework=net10.0-windows`, `UseWindowsForms=true`, `RuntimeIdentifier=win-x64`, `SelfContained=true`, `PublishSingleFile=true`, `PublishReadyToRun=true`, `EnableCompressionInSingleFile=false`, `IncludeNativeLibrariesForSelfExtract=false`, `PublishTrimmed=false`, `ApplicationHighDpiMode=PerMonitorV2`; references Core. Otherwise the same shape as FastTaskMgr's project file. The publish output is `MediaVerifier.exe` plus `e_sqlite3.dll` beside it (D-01; measured in the master plan) |
| `src/MediaVerifier.Core/MediaVerifier.Core.csproj` | Class library, `net10.0-windows`, no WinForms; `InternalsVisibleTo` the test project |
| `tests/MediaVerifier.Tests/MediaVerifier.Tests.csproj` | xUnit (`dotnet new xunit`), references both projects |
| `Program.cs` | `[STAThread] Main`: `--self-check` returns 0; otherwise shows an empty `MainForm` titled `MediaVerifier <version>` |
| `CHANGELOG.md` | `# Changelog`, an `## Unreleased` section with the placeholder line used by `release.ps1` (subplan 14) |
| `.github/workflows/ci.yml` | Build, test, self-check (subplan 14, task U-07; create it here so every later commit is checked) |

Reference implementation for patterns in this phase: FastTaskMgr
(<https://github.com/rockenrooster/FastTaskMgr>, tag `v0.1.0.45`), files `src/FastTaskMgr/Program.cs`,
`App/AppState.cs`, `App/AppSettings.cs`, `App/SingleInstance.cs`, `App/PageBase.cs`,
`Diagnostics/ErrorLog.cs`, `Diagnostics/SelfCheck.cs`. Port the pattern, not the task-manager logic.

## Design

### Locations

| Purpose | Path |
|---------|------|
| Settings | `%APPDATA%\MediaVerifier\settings.json` |
| Database | `%LOCALAPPDATA%\MediaVerifier\library.db` (overridable in settings) |
| Logs | `%LOCALAPPDATA%\MediaVerifier\logs\` |
| Caches (thumbnails, evidence images) | `%LOCALAPPDATA%\MediaVerifier\cache\` |
| Bundled FFmpeg (read-only, part of the install) | `<folder of MediaVerifier.exe>\ffmpeg\` |
| Update downloads | `%LOCALAPPDATA%\MediaVerifier\Updates\` |
| Scratch for child processes | `%LOCALAPPDATA%\MediaVerifier\temp\<run>\<job>\` (deleted after each job and at startup) |

Expose these through a single `AppPaths` class in Core. Tests override the base directory.

### Command line

| Argument | Behaviour |
|----------|-----------|
| *(none)* | Start the GUI, or activate the running instance |
| `--minimized` | Start hidden in the tray (only when the tray option is on) |
| `--self-check` | Run diagnostics without UI; exit 0 on success, 1 on failure |
| `--run-plan <id>` | Headless run of a saved plan (subplan 13). Forwards to a running GUI instance if there is one |
| `--render-screens <dir>` | Render every page to PNG with demo data, then exit (subplan 08) |
| `--cleanup` | Remove scheduled tasks and per-user registry entries, then exit. Called by the uninstaller (subplans 13, 14) |
| `--notify <run id>` | With `--minimized`: tray fallback that announces a finished run (subplan 13) |
| `mediaverifier://...` | Activation URI from a toast (subplan 13). Forwarded to the running instance |

Parsing is hand-written and strict: unknown arguments print usage to the log and exit 1.

### Single instance and IPC

- Mutex `Local\MediaVerifier.SingleInstance` decides the GUI owner.
- Mutex `Local\MediaVerifier.Engine` is held by whichever process currently runs the scan engine (GUI
  or headless). Only one engine process may write the database.
- A named pipe `MediaVerifier.Ipc.<session id>` is served by the GUI instance. Messages are single-line
  UTF-8 JSON: `{"cmd":"activate"}`, `{"cmd":"run-plan","planId":3}`, `{"cmd":"open","uri":"mediaverifier://finding/123"}`.
  The pipe is created with an ACL restricted to the current user. Reject messages over 8 KiB.
- A second GUI launch sends `activate` and exits. A headless launch sends `run-plan` when a GUI is
  running, otherwise acquires the engine mutex and runs itself.

### Settings

`AppSettings` is a plain class serialised with `System.Text.Json` source generation
(`JsonSerializerContext`), written atomically (write `settings.json.tmp`, flush, `File.Replace`).
Loading is tolerant: unknown properties ignored, missing properties defaulted, and a file that fails
to parse is renamed to `settings.corrupt-<timestamp>.json` before defaults are used. Include a
`SchemaVersion` integer and an upgrade hook.

Initial settings (later phases add more):

| Setting | Default |
|---------|---------|
| `Theme` (`System`, `Light`, `Dark`) | `System` |
| `StartWithWindows` | `false` |
| `MinimizeToTray` | `false` |
| `CheckForUpdatesOnStartup` | `true` |
| `DatabasePath` | empty (use default) |
| `WindowPlacement` (bounds, maximised, last page) | — |

### Shell window

`MainForm` hosts a left navigation rail, a page host and a slim status strip. Pages derive from
`PageBase` (ported from FastTaskMgr): each page is created lazily on first visit, receives
`OnActivated` / `OnDeactivated`, and stops all timers while hidden. Pages in this phase are empty
placeholders with their title: Dashboard, Issues, Library, Scans, Schedule, Tools, Settings.

The window title is `MediaVerifier <version>`; the version comes from the assembly version, which
`build.ps1` maintains.

`AppState` is the composition root: it owns settings and lazily constructed services, and disposes
them in reverse order on exit. No dependency-injection container.

### Application manifest

Add `app.manifest` to the exe project with: `asInvoker` execution level, `longPathAware`, the
`supportedOS` entry for Windows 10 and 11 (both use the same ID), and Common Controls v6. Set
`ApplicationHighDpiMode` to `PerMonitorV2` in the project; that is where the DPI awareness lives. The
Windows Forms build rejects DPI settings in the manifest (error WFO0003), so the manifest has none.

### Logging

- `ErrorLog` (ported): appends unhandled exceptions to `logs\error.log`.
- `AppLog`: a small asynchronous logger writing `logs\app.log`, size-capped at 5 MB with three rolled
  files. Levels: Info, Warning, Error. One writer thread fed by a bounded channel; the UI thread never
  blocks on it. No external logging library.
- Unhandled exception policy as in FastTaskMgr: log, keep running, tell the user once per session.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| F-01 | Create the solution skeleton from the table above, with one passing smoke test | On a clean clone: `dotnet build -c Release` has no warnings, `dotnet test` passes, the exe starts and `--self-check` exits 0; CI is green |
| F-02 | `AppPaths` in Core with an overridable base directory | Unit test creates every directory under a temp root |
| F-03 | `AppSettings` with source-generated JSON, atomic save, tolerant load | Tests: round trip; unknown property ignored; corrupt file renamed and defaults returned; save is atomic (kill between temp write and replace leaves the old file intact) |
| F-04 | `AppLog` and `ErrorLog`; install exception handlers | Test: 10,000 log calls from 8 threads lose nothing and roll at the cap |
| F-05 | Argument parser and `Program.Main` dispatch | Tests for every row of the command-line table, including rejection of unknown arguments |
| F-06 | Single-instance mutex, engine mutex, named-pipe IPC | Test: second process sends `activate`; first receives it. Oversized and malformed messages are rejected without crashing |
| F-07 | `AppState` composition root with ordered disposal | Self-check constructs and disposes it without exceptions |
| F-08 | `MainForm` shell with navigation rail, lazy pages, status strip; restores placement | Manual: navigate all pages with mouse and keyboard (`Ctrl+1`..`Ctrl+7`); placement survives restart, including on a disconnected monitor (falls back to primary) |
| F-09 | App icon: generate `Assets/MediaVerifier.ico` from the geometry in subplan 08 (*App icon*; `docs/design/app-icon.svg` and `app-icon-small.svg`) with a script kept in `tools/`, at 16, 20, 24 (small version) and 32, 40, 48, 64, 128, 256 px (standard version); wire `ApplicationIcon` and the installer `SetupIconFile` | Icon shows in Explorer, taskbar, Alt+Tab and the installer, and matches the icon sheet in `docs/design/png/icons-dark.png` |
| F-10 | `app.manifest`, DPI mode, dark title bar (`DwmSetWindowAttribute`, attribute 20) | Title bar follows the theme; window is crisp at 100 %, 150 % and 200 % scale |
| F-11 | Extend `SelfCheck`: paths writable, settings round trip, log write, IPC loopback | `--self-check` exits 0 on a clean profile and 1 when the data directory is read-only |
| F-12 | Start-with-Windows toggle (HKCU `Run` value with `--minimized`), tray icon when enabled | Toggle adds and removes the value; tray menu has Open, Scan now (disabled for now), Exit |

## Exit criteria

- The app starts in under 500 ms (measure with a `Stopwatch` logged at first paint) and idles at 0 % CPU.
- All tasks' acceptance criteria pass; no warnings; tests green.
- `--self-check` passes in CI (`ci.yml` runs it after the build).

## Pitfalls

- Do not create pages eagerly; startup time depends on it.
- `File.Replace` fails when the destination does not exist; handle the first save.
- Named pipes default to an ACL that allows other users on the machine; set the security explicitly.
- A mutex abandoned by a crashed process throws `AbandonedMutexException` on acquire; treat it as acquired.
- Never show a message box from a headless run.
