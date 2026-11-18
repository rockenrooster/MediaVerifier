# 13 — Scheduling and notifications

Phase 13. Depends on: phase 9 (UI) and phase 5 (headless host). Independent of phase 12. Milestone M4.

## Goal

Scans run on their own, without the app being open and without a resident background process, and
the user hears about it only when something needs attention.

## Plans

A plan is a saved scan definition with a trigger. Stored in `plans.definition_json`:

```json
{
  "roots": "all",                       // or [1, 4]
  "depth": "Standard",                  // Quick | Standard | Deep
  "trigger": { "kind": "Weekly", "time": "03:00", "daysOfWeek": ["Sun"], "dayOfMonth": null, "everyDays": null },
  "budgetMinutes": 120,                 // null = no limit
  "includeVerification": true,          // scrub according to the integrity settings
  "conditions": {
    "onlyOnAcPower": true,
    "skipWhenFullScreen": true,
    "runMissedAsSoonAsPossible": true,
    "wakeComputer": false,
    "stopAfterMinutes": 480
  },
  "notify": { "when": "NewFindings", "minSeverity": "Medium" }   // Always | NewFindings | Never
}
```

Trigger kinds: `Daily`, `Weekly` (one or more weekdays), `Monthly` (day 1–28 or `Last`), `EveryDays`
(interval). One time of day per plan, local time.

Templates offered in the UI: *Nightly verification* (Quick, daily 03:00, 2 h budget), *Weekly check*
(Standard, Sunday 03:00), *Monthly deep scan* (Deep, first Saturday 01:00). After the first completed
manual scan, the dashboard shows an info bar: "Keep your library checked automatically" with
*Create schedule*, which adds the first two templates.

## Windows Task Scheduler backend

Each enabled plan has one per-user scheduled task that runs
`"<path>\MediaVerifier.exe" --run-plan <id>`.

- Access through the COM automation object `Schedule.Service` (late bound; no extra package). Wrap
  it behind `IScheduleBackend` so tests use a fake.
- Register by XML: `folder.RegisterTask(name, xml, 6 /* create or update */, null, null, 3 /* interactive token */)`. Tasks run only while the user is logged on, with normal privileges,
  and need no elevation or stored password.
- Task path `\MediaVerifier\Plan <id>`. If creating the folder is refused, fall back to the root
  folder with the name `MediaVerifier Plan <id>` and remember it in `plans.task_name`.
- `ScheduleXmlBuilder` produces the XML (schema version 1.2). It is pure and unit-tested.

```xml
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>MediaVerifier scheduled scan: Weekly check</Description></RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>2026-01-04T03:00:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByWeek><DaysOfWeek><Sunday /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT8H</ExecutionTimeLimit>
    <Priority>7</Priority>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>C:\Users\me\AppData\Local\Programs\MediaVerifier\MediaVerifier.exe</Command><Arguments>--run-plan 3</Arguments></Exec>
  </Actions>
</Task>
```

Daily uses `<ScheduleByDay><DaysInterval>n</DaysInterval></ScheduleByDay>`; monthly uses
`<ScheduleByMonth><DaysOfMonth><Day>1</Day></DaysOfMonth><Months>` with all twelve months `</Months> </ScheduleByMonth>`. Escape the description and paths as XML text.

Reconciliation at app start and whenever plans change: for every plan make the task exist with the
current XML and the **current executable path** (the path changes if a portable exe is moved); delete
tasks whose plan no longer exists. `MediaVerifier.exe --cleanup` removes all tasks and per-user
registrations; the uninstaller calls it (subplan 14).

Next-run times shown in the UI are computed by `ScheduleCalculator` (pure, tested with a fake clock),
not read back from Task Scheduler.

## Headless run

`--run-plan <id>` (host from task E-13):

1. If a GUI instance answers on the IPC pipe, forward `run-plan` and exit with code 3. The GUI runs
   the plan in its own engine and shows progress.
2. Otherwise acquire the engine mutex. If another headless run holds it, log and exit 3.
3. Load the plan. Missing or disabled → remove the orphan task, exit 1.
4. Check conditions. On battery with `onlyOnAcPower` → skip. Full-screen or presentation
   (`SHQueryUserNotificationState` returns busy, D3D full screen or presentation mode) with
   `skipWhenFullScreen` → re-check every 5 minutes for up to an hour, then skip. A skip is recorded
   in the runs table and the notification centre, not toasted.
5. Run the engine at below-normal process priority with the plan's depth, scope and budget.
6. Persist the run summary, apply the notification policy, exit 0 (1 on failure).

Never show a window or message box. All output goes to the log.

**GUI opened during a headless run.** The GUI sees the engine mutex is held by another process. It
shows "A scheduled scan is running in the background" with progress read from the run row (which the
engine checkpoints every 5 seconds), disables its own Scan button with that reason, and offers
*Stop*, which signals the named event `Local\MediaVerifier.StopRun`. The headless engine treats the
event as Cancel. The GUI detects the run by opening that event (the headless run creates it before
checking conditions), never by probing the engine mutex, which would briefly own it.

## Notification policy

`NotificationPolicy.Decide(run)` is pure and returns zero or one notification.

| Situation | Result |
|-----------|--------|
| New Critical findings | Toast, regardless of `minSeverity` (unless `when` is `Never`) |
| New findings at or above `minSeverity` | One toast summarising counts by tier and media kind |
| Run-level alerts (`RUN-READ-ERRORS`, `RUN-MASS-MISSING`) | Toast |
| `RUN-ROOT-OFFLINE` | Toast at most once per root per 7 days; always recorded in-app |
| Run failed | Toast |
| Nothing new | No toast unless `when` is `Always` |
| Quiet hours (optional setting, default off) | Posted silently to the notification centre |

One toast per run. All run toasts share one tag (`scan`, group `runs`) so a newer run replaces an
unread older one; the run id travels in the launch link. Every notification is also written to the
`notifications` table (the in-app bell). Manual runs with the window focused use in-app toasts
instead of system toasts.

Text: title states the outcome ("3 new problems found", "Scheduled scan could not run"); body gives
counts and context ("1 critical, 2 high in Music. Scan finished 03:12."). No file names in system
toasts (they appear on lock screens).

## System toasts

Channel order: `ToastNotifier` → `BalloonNotifier` (only when a tray icon exists) → in-app only.

### Registration (per user, at startup, idempotent)

- App user model ID `rockenrooster.MediaVerifier`. Call `SetCurrentProcessExplicitAppUserModelID`
  first thing in `Main`. The installer sets the same ID on its shortcuts.
- `HKCU\Software\Classes\AppUserModelId\rockenrooster.MediaVerifier` with `DisplayName` and `IconUri`
  (a PNG written to the data folder). This is what allows an unpackaged app to raise toasts without a
  Start-menu shortcut.
- Protocol handler `HKCU\Software\Classes\mediaverifier`: default value `URL:MediaVerifier`, empty
  `URL Protocol` value, `shell\open\command` = `"<exe>" "%1"`. `<exe>` is the **registration
  executable**, as for the Run value of Start with Windows: the installed copy's `MediaVerifier.exe`
  (Inno Setup's `InstallLocation`) when that file exists, otherwise the current executable path.
  (Every start used the running exe, so a development build or a second portable copy took over the
  installed app's notification links and Run value.)

### Raising a toast

`ToastInterop` calls the WinRT notification API through COM without the Windows SDK projection (it
would add about 25 MB to the exe): `RoGetActivationFactory` for
`Windows.UI.Notifications.ToastNotificationManager`, an `XmlDocument` from
`Windows.Data.Xml.Dom`, `CreateToastNotifierWithId(aumid)`, `Show`. Declare the interfaces as
`IUnknown`-based with three placeholder slots for the `IInspectable` methods (the built-in COM
marshaller no longer supports `IInspectable` directly) or call the same v-table slots through
function pointers (the implementation does this; slot 6 is each interface's first own method), manage `HSTRING`s with
`WindowsCreateString`/`WindowsDeleteString`, and take every interface ID from the Windows SDK IDL
files rather than from memory. Every failure is caught, logged once, and falls through to the next
channel.

Toast payload:

```xml
<toast launch="mediaverifier://run/123" activationType="protocol">
  <visual><binding template="ToastGeneric">
    <text>3 new problems found</text>
    <text>1 critical, 2 high in Music. Scan finished 03:12.</text>
  </binding></visual>
  <actions><action content="Review" activationType="protocol" arguments="mediaverifier://run/123" /></actions>
</toast>
```

### Activation URIs

Clicking a toast launches `MediaVerifier.exe "mediaverifier://run/123"`, which forwards to the running
instance or starts the GUI. `ActivationUri.Parse` accepts exactly: `mediaverifier://run/<id>`,
`mediaverifier://finding/<id>`, `mediaverifier://issues`. Anything else is rejected and logged. URIs
are untrusted input: they select a view by integer ID and can never name a path or trigger an action.

### Fallback when interop is unavailable

When `ToastInterop` fails, a headless run shows the notification as a tray balloon from its own process
(`SystemToast`: a temporary notification-area icon for a few seconds); the open window falls back to its
in-app toast. (A `--minimized --notify <run id>` launch was planned for this; nothing used it and it was
removed in the 2026-11-18 audit.)

## Optional webhook (stretch)

Per plan or global: POST a JSON run summary (counts by tier and kind, run id, duration, no file
names by default) to a user-supplied HTTPS URL when a run completes. 10-second timeout, two retries,
*Send test* button. Plain HTTP is allowed only for private-range addresses and after an explicit
confirmation. Implement only after everything else in this phase is done.

## UI

**Schedule page.** List of plans: name, scope, depth, schedule in words, budget, next run, last
result with a link to the run, enabled toggle; *Run now*, *Edit*, *Delete*. Below it, the scrub-policy
card from subplan 06 with coverage arithmetic.

**Plan editor.** Name; What (all roots or a selection; depth); When (kind, weekdays or day, time);
Limits (budget, stop after); Conditions; Verification; Notify. A live sentence summarises the plan:
"Every Sunday at 03:00, Standard scan of all folders, up to 2 h. Next run: Sunday 03:00." Saving
registers the task; a failure to register is shown inline with the system's message and the plan is
saved disabled.

**Settings → Notifications.** Enable system toasts, minimum severity, quiet hours, *Send test
notification*, webhook (if implemented).

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| SCH-01 | Plan model, JSON (source generated), validation | Round-trip and validation tests (bad times, empty roots, day 31) |
| SCH-02 | `ScheduleCalculator` | Fake-clock tests incl. month ends, DST transitions, weekday sets |
| SCH-03 | `ScheduleXmlBuilder` | XML validates against the task schema in tests; snapshot tests per trigger kind; special characters escaped |
| SCH-04 | `IScheduleBackend` COM implementation and reconciliation, `--cleanup` | Integration test (Windows only, tagged): register, read back, update path, delete. Works without elevation |
| SCH-05 | Headless runner flow with conditions, forwarding, stop event | Tests with a fake engine: forwarding to GUI, mutex contention, skip paths, stop event cancels |
| SCH-06 | GUI behaviour while a headless run is active | Progress shown from run checkpoints; Scan disabled with reason; Stop works |
| SCH-07 | Schedule page, plan editor, templates, first-scan info bar | Keyboard-complete; live summary sentence matches the calculator |
| N-01 | `NotificationPolicy` | Table-driven tests for every row of the policy table, incl. replacement and offline-root throttling |
| N-02 | Per-user registration of AUMID and protocol; `ActivationUri` | Registration idempotent; fuzzed URIs never produce an action; valid ones navigate |
| N-03 | `ToastInterop` with fallback chain | Manual: toast appears from a headless run, persists in the notification centre after exit, click opens the right view. Self-check acquires the activation factory without showing a toast |
| N-04 | In-app notification store and bell wiring (extends task UI-10) | Every system toast has a matching in-app entry |
| N-05 | Settings → Notifications with test button | Test notification arrives |
| N-06 | Webhook (stretch) | Local listener receives the documented payload; failures are logged, never block a run |

## Exit criteria

- With the app closed, a plan fires at its time, scans, and leaves a toast that survives the process.
- No MediaVerifier process is running between scheduled runs.
- Moving a portable exe and starting it once repairs all tasks.
- Uninstalling leaves no scheduled tasks or registry entries behind.

## Pitfalls

- Task XML `StartBoundary` without an offset is local time; that is intended so the plan follows the
  wall clock across DST.
- `StartWhenAvailable` runs a missed task minutes after logon; honour the full-screen condition so it
  does not land in the middle of a game.
- The description and path in the XML must be escaped; plan names are user input.
- A toast raised without the AUMID registration silently does nothing. Register before the first toast.
- Late-bound COM needs the dynamic binder; keep trimming and AOT off for the app (they already are).
- Do not call the Task Scheduler on the UI thread; it can block for seconds.
