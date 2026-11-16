# 09 — UI screens and flows

Phase 9. Depends on: phases 3 (queries) and 8 (kit). Pages can be built against the demo library
before the engine is finished; wire them to the real engine as phases 5–7 land.

## Goal

Seven pages, one detail drawer and a handful of dialogs that let a user: add folders, scan, see the
state of the library at a glance, understand any finding, act on it, and trust what the app tells them.

## Information architecture

| Page | Question it answers | Shortcut |
|------|--------------------|----------|
| Dashboard | Is my library healthy, and what should I do next? | `Ctrl+1` |
| Issues | What is wrong, and how bad is it? | `Ctrl+2` |
| Library | What is being watched, and what state is each folder and file in? | `Ctrl+3` |
| Scans | What is running, and what happened before? | `Ctrl+4` |
| Schedule | When do scans run on their own? | `Ctrl+5` |
| Tools | Quarantine, undo, reports, maintenance | `Ctrl+6` |
| Settings | Everything configurable | `Ctrl+,` |

## Shell

```
+-----------+---------------------------------------------------------------+
| MediaVer. |  Page title            [ Search ]      (bell)   [ Scan  v ]   |
|           |---------------------------------------------------------------|
| Dashboard |                                                               |
| Issues 128|                                                               |
| Library   |                       page content                            |
| Scans     |                                                               |
| Schedule  |                                                               |
| Tools     |                                                               |
|           |                                                               |
| Settings  |---------------------------------------------------------------|
| Update *  | Scanning 12,404 of 58,210 · 142 MB/s · about 1 h 12 min left  [Pause] [Stop] |
+-----------+---------------------------------------------------------------+
```

- **Header.** Page title, page toolbar, a global search box (searches file names; Enter opens Issues
  or Library filtered), the notification bell with unread count, and the primary **Scan** split
  button. Default action: scan all library roots at the default depth. Menu: Quick, Standard, Deep,
  *Verify fingerprints only*, *Scan a folder…*.
- **Status strip.** While a run is active: phase, progress, read rate, time left, Pause and Stop.
  Otherwise the last run's one-line result. Click opens Scans. Hidden when there is nothing to say.
- **Issues badge.** Count of open findings at Medium or above, coloured by the worst tier.
- **Update indicator.** A dot on the nav rail's update entry when an update is available (subplan 14).
- Minimum window size 960 × 600. Below 1000 px wide the nav rail collapses to icons.

## Dashboard

```
+-- Health --------------------+  +-- Protection ----------------+  +-- Scans --------------------+
| (donut) 98.7 % healthy       |  | (ring) 82 % verified          |  | Last: today 03:12, Standard |
| 12,345 of 12,507 files       |  | within 90 days                |  | 2 new issues, 1 resolved    |
+------------------------------+  +-------------------------------+  | Next: tomorrow 03:00        |
                                                                     +-----------------------------+
[ Critical 3  +1 ]  [ High 14  +1 ]  [ Medium 96 ]  [ Low 212  -4 ]         <- StatTiles, clickable

Photos   8,201 files · 212 GB   [=== severity bar ===]   4 issues
Videos   1,105 files · 3.2 TB   [=== severity bar ===]  31 issues
Music    3,201 files ·  96 GB   [=== severity bar ===]  90 issues

+-- Recommended next steps ----------------------------+  +-- Recent activity ---------------+
| 1. 3 files changed on their own. Restore from backup |  | 03:12 Scheduled scan finished    |
|    [Review]                                          |  | Yesterday  1 file repaired       |
| 2. 38 videos have no seek index.        [Fix all]    |  | Mon  Scan cancelled by user      |
| 3. 41 albums look converted from MP3.   [Review]     |  +----------------------------------+
| 4. 21 % of files not verified in 90 days [Verify now]|   Open issues over time (sparkline)
+------------------------------------------------------+
```

- Everything is clickable and lands on a pre-filtered Issues or Library view.
- Deltas compare with the previous completed run.
- Recommendations come from subplan 12, at most five, ordered by severity then by number of files.
- **Empty state** (no roots): one centred card, "Add a folder to start watching it", a primary *Add
  folder* button, and the whole page is a drop target for folders.
- **First scan running**: tiles show live counts with a subtle "scanning" label instead of deltas.

## Issues

```
[All 325] [Critical 3] [High 14] [Medium 96] [Low 212]   [Photos] [Videos] [Music]
Category v   Rule v   Folder v   Status: Open v           Group by: (Severity | Folder | Rule)
-----------------------------------------------------------------------------------------------
v Critical (3)
  (x) IMG_2041.jpg                                         D:\Photos\2019\Rome        4.2 MB
      File changed without being modified · unchanged since 14 Mar 2026          [Restore v]
v High (14)
  /!\ 07 - Track.flac                                      D:\Music\Artist\Album       31 MB
      Likely made from a lossy source · hard cutoff at 16.6 kHz                    [Details]
-----------------------------------------------------------------------------------------------
12 selected      [Fix...]  [Ignore]  [Rescan]  [Move to quarantine]  [Export]
```

- One row per finding, two lines, 44 px: severity shape icon, file name (`BodyStrong`), then rule
  title with wording by confidence and the evidence summary (`Text2`). Folder (middle-ellipsised) and
  size are right-aligned columns. On hover or focus the row shows its primary action and a *More*
  button; the same actions are in the context menu.
- Filter chips show live counts and combine with AND across groups, OR within a group. Filters,
  grouping and sort persist per session and are reflected in a one-line summary with *Clear filters*.
- Sort: severity (default, then confidence, then file name), name, folder, size, first seen.
- Multi-select shows the selection bar. Bulk *Fix…* opens the batch dialog from subplan 12, which
  only offers actions valid for every selected finding.
- While a run is active, new findings do not reshuffle the list under the pointer: a pill "14 new
  findings · Refresh" appears at the top; counts on chips update live.
- **Empty states**: no findings at all ("No problems found. 12,507 files checked, last scan today
  03:12"); filters exclude everything ("Nothing matches these filters" with *Clear filters*); nothing
  scanned yet ("Scan your library to see results" with *Scan*).

## Detail drawer

Opens from any file or finding (Enter, double-click, *Details*). Esc closes. Up/Down in the list
behind it moves to the next item without closing.

```
[thumb]  07 - Track.flac                                   [Open] [Show in folder] [...]
         D: > Music > Artist > Album
         FLAC · 44.1 kHz · 16-bit · stereo · 4:12 · 921 kbps · 31.2 MB
         Fingerprint unchanged since 2 Mar 2026 · verified 3 times · last 12 Oct 2026

Findings (2)
/!\ High · Likely      Lossless file made from a lossy source
    What this means    The audio has the hard frequency cutoff that MP3 encoders leave, but the
                       file is stored as FLAC. It was probably converted from an MP3.
    Why it matters     It takes the space of a lossless file without the quality of one.
    What you can do    Replace it from a genuine lossless source. There is no way to restore
                       what the MP3 encoder removed.
    Evidence           [ spectrogram, cutoff line at 16.6 kHz, Nyquist at 22.05 kHz ]
                       Cutoff 16.6 kHz · drop 67 dB · nothing but quantisation noise above
    [Ignore]  [Rescan]
(i) Low                DC offset ...

History                timeline of events for this file
Technical details      (collapsed) streams table · decoder messages · fingerprints · raw probe JSON [Copy]
```

- The header facts come from the index row; the technical section re-probes on demand.
- Evidence views by rule family: spectrogram and waveform (subplan 10); frame strip with scores and an
  original-versus-reconstructed toggle (subplan 11); decoder message summary with counts for
  corruption; a byte map showing zero-filled regions for `GEN-ZEROFILL` and `INT-ROT`.
- Thumbnails and evidence images are produced on demand on a background thread, cached on disk
  (LRU, 200 MB cap), and never block the drawer from opening.
- Actions are those the rule lists (subplan 12), the recommended one as the primary button.

## Library

- **Roots view.** One card per root: label and path, role (Library or Backup), online state, file
  count and size, severity bar, last scan, *Scan* split button, and a menu (Edit, Verify now, Open
  folder, Remove). *Add folder* button and drag-and-drop anywhere on the page.
- **Root editor dialog.** Path, label, role, media kinds, default depth, include and exclude
  patterns with a live count of matching files, storage class (detected, overridable), concurrent
  readers.
- **Browse view** (after choosing a root): folder tree on the left with roll-up severity badges;
  file list on the right, 36 px single-line rows: state icon, name, format summary, size, modified,
  last scanned, last verified. Filter by state (OK, Issues, Pending, Skipped, Missing, Error).
  Skipped files show their reason. Context menu: Details, Rescan, Verify now, Ignore folder.
- Removing a root asks for confirmation and states exactly what is deleted (index and history only,
  never media).

## Scans

- **Active run.** Phase, large progress bar segmented into done/remaining, files and bytes done,
  read rate, time left (or "estimating"), live new-finding counts by tier, list of in-flight items
  with their stage and progress, Pause/Resume and Stop. Pausing shows "Pausing, finishing 3 files".
- **Before a run starts** (when started from this page or the Scan menu with more than 10,000 files
  or 100 GB in scope): a confirmation sheet with scope, depth, file count, size and the estimated
  duration per depth, so the user can pick a cheaper depth.
- **Interrupted run banner**: "The last scan was interrupted" with *Resume* and *Dismiss*.
- **History.** Table of runs: started, trigger, depth, scope, duration, files, new, resolved,
  result. Selecting a run shows its summary, alerts and errors, and *Show findings from this run*.

## Schedule

Plans list with name, scope, depth, schedule in words ("Every day at 03:00"), budget, next run, last
result and an enabled toggle; plan editor dialog; the scrub-policy card. Fully specified in subplan 13.

## Tools

Cards, each opening a sub-view: **Quarantine** (items, size, restore, delete permanently),
**Operations** (journal with undo), **Reports** (export findings as HTML, CSV or JSON),
**FFmpeg** (path, version, capabilities, change, download), **Database** (location, size, back up,
compact, check, reset). Specified in subplans 04 and 12.

## Settings

A single scrolling page with a section list on the left; search box filters settings by text.

| Section | Contents |
|---------|----------|
| General | Theme, start with Windows, tray behaviour, default scan depth, confirmations |
| Scanning | Performance level, per-storage-class readers, file types per kind, global exclusions, cloud placeholders, size limit, timeouts, strict decoding, hardware decoding |
| Rules | Every rule from the catalog grouped by category: enable, severity cap, parameters with defaults and *Reset*. Changing a rule re-evaluates stored results immediately (no rescan) and shows how many findings changed |
| Integrity | Scrub period, budget, verify-on-analysis |
| Notifications | Subplan 13 |
| Updates | Subplan 14 |
| Advanced | FFmpeg location, database location, logs folder, diagnostics (run self-check, copy system report) |
| About | Version, links to the repository and release notes, third-party notices |

Settings apply immediately; there is no Save button. Destructive or expensive changes confirm first.

## Flows

**First run.** The dashboard empty state leads to an inline three-step guide, not a modal wizard:
(1) add folders and choose which are backups, (2) FFmpeg check with *Use existing* or *Download*,
(3) choose depth with the time estimate, then *Start*. The app is usable at any step.

**Review and fix a finding.** Issues → Enter opens the drawer → read the explanation and evidence →
primary action → confirmation dialog stating exactly what will happen to which files → progress in a
toast → result toast with *Undo* where available → the row disappears (resolved) or updates.

**Rot found by a scheduled run.** Toast "3 files changed on their own" → click → app opens Issues
filtered to that run and tier → drawer shows the healthy copy found in a backup root → *Restore*.

**Tuning a noisy rule.** Row menu → *Ignore this rule for this folder*, or *Adjust rule…* jumps to
Settings → Rules with the rule expanded.

## Keyboard

| Keys | Action |
|------|--------|
| `Ctrl+1` … `Ctrl+6`, `Ctrl+,` | Switch page |
| `Ctrl+F` or `/` | Focus search or the page filter |
| `F5` | Refresh the view; `Shift+F5` rescan selection |
| `Ctrl+N` | Add folder |
| `Ctrl+Shift+S` | Scan now (default depth) |
| `Up`/`Down`, `PgUp`/`PgDn`, `Home`/`End` | Move in lists |
| `Enter` | Open details |
| `Space` | Toggle selection |
| `Ctrl+A`, `Shift`+arrows | Select all, extend selection |
| `Ctrl+I` | Ignore selected findings |
| `Delete` | Move selected files to quarantine (confirms) |
| `Ctrl+Enter` | Run the primary action for the selection |
| `Ctrl+Z` | Undo the last operation (when undoable) |
| `Alt+Enter` | Show in folder |
| `Ctrl+C` | Copy path(s) |
| `Esc` | Close drawer, dialog or menu; clear selection |
| `F1` | Open the help page for the focused rule or page |

## Notifications in the app

The bell opens a flyout listing recent notifications (run results, alerts, update available, tool
results) with unread markers, a per-item action, *Mark all read* and *Clear*. Toasts (kit
`ToastHost`) confirm actions and offer *Undo*. System toasts are specified in subplan 13.

## Copy rules

- Sentence case. Plain words: "file", "folder", "damaged", "changed on its own". Avoid "artifact",
  "asset", "entity", "bit rot" without the gloss.
- Buttons are verbs: *Scan*, *Restore*, *Move to quarantine*, *Ignore*. Never *OK* / *Yes* for
  destructive confirmations; repeat the verb.
- Confirmations state the count, the action, where originals go and whether it can be undone:
  "Rebuild the index of 38 videos? New copies are written and checked first. The originals go to the
  Recycle Bin. You can undo this."
- Errors say what happened, why if known, and what to do next. No codes without words.
- Dates are relative when recent ("today 03:12", "yesterday") and absolute otherwise, in the user's
  locale. Durations: "1 h 12 min", "45 s".

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| UI-01 | Shell: header, global search, Scan split button, status strip bound to `EngineStatus`, nav badges | Status strip updates at up to 10 Hz without layout thrash; 0 % CPU when idle |
| UI-02 | Dashboard with all tiles, breakdowns, recommendations placeholder, activity, empty and first-scan states | Renders from the demo library in the harness; every element navigates correctly |
| UI-03 | Issues list: filters, grouping, sorting, multi-select, selection bar, live-update pill, empty states | 1,000,000 findings: filter change under 300 ms, smooth scroll; filters persist during the session |
| UI-04 | Detail drawer: header, findings with explanations, history, technical details, on-demand thumbnail | Opens in under 100 ms (content streams in); Up/Down browsing works with the drawer open |
| UI-05 | Library: roots view, root editor with live match count, browse view with tree and file list | Add, edit, remove root; offline root shown as offline, not as missing files |
| UI-06 | Scans: active run panel, pre-scan estimate sheet, interrupted banner, history and run detail | Pause, resume and stop work; the estimate matches the engine's numbers |
| UI-07 | Settings page with all sections available at this phase; rule editor wired to re-evaluation | Changing a threshold updates Issues counts without a rescan |
| UI-08 | Tools page skeleton with FFmpeg and Database sub-views (others arrive in subplan 12) | Back up, compact, check database from the UI |
| UI-09 | First-run guide | From a clean profile to a running scan with keyboard only |
| UI-10 | Notification flyout and in-app toasts | Unread count, actions, persistence across restart |
| UI-11 | Keyboard map and context menus on every list | Every action in the table works; menus show the shortcuts |
| UI-12 | Evidence views for M1 rules: decoder message summary, zero-region byte map, fingerprint history | Shown in the drawer for the corresponding findings |
| UI-13 | Screens committed to `docs/screens/` and embedded in the README | Three screenshots from the harness, dark theme |

## Exit criteria

- Every page renders correctly in the harness in dark, light and high contrast at 100 % and 150 %.
- A user can complete each flow above with the keyboard alone.
- No synchronous database or file access on the UI thread (verify with a debug assertion in the
  repository layer that throws when called on the UI thread).

## Pitfalls

- Lists that re-sort while the user is pointing at a row cause mis-clicks. Freeze order during
  interaction; offer a refresh.
- Do not disable the Scan button without saying why (no roots, FFmpeg missing, run active).
- A drawer that waits for its thumbnail feels slow; open first, fill later.
- Counts on chips must come from the same query predicate as the list, or they will disagree.
