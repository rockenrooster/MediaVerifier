# 09 — UI screens, behaviour and wording

Phase 9. Depends on: phases 3 (queries) and 8 (kit). Pages can be built against the demo data before
the engine is finished and wired to the real engine as phases 5–7 land.

## How to use this document

- The mockups in [`docs/design/`](../docs/design/README.md) are the layout. This document adds what a
  picture cannot show: what each element is bound to, how it behaves, every state, and the exact
  wording of every string.
- The same rule as subplan 08 applies: build what is specified; do not design. Strings are used
  exactly as written here, including punctuation and capitalisation.
- Coordinates are relative to the content area (subplan 08, *Window and shell*) at a 1280 × 800
  window unless stated. `x0` is its left edge, `y0` its top (72), `Wc` its width.

## Pages

| Page | Question it answers | Shortcut | Reference |
|------|--------------------|----------|-----------|
| Dashboard | Is my library healthy, and what should I do next? | `Ctrl+1` | `dashboard`, `dashboard-narrow`, `first-run` |
| Issues | What is wrong, and how bad is it? | `Ctrl+2` | `issues`, `issues-selection`, `issues-detail` |
| Library | What is being watched, and what state is each folder and file in? | `Ctrl+3` | `library`, `library-browse` |
| Scans | What is running, and what happened before? | `Ctrl+4` | `scans` |
| Schedule | When do scans run on their own? | `Ctrl+5` | `schedule`, `schedule-edit` |
| Tools | Quarantine, undo, reports, maintenance | `Ctrl+6` | `tools` |
| Settings | Everything configurable | `Ctrl+,` | `settings-rules` |

## Formats used in strings

| Kind | Format | Examples |
|------|--------|----------|
| Count | Current culture grouping | `12,507` |
| File size | Binary units, one decimal, no decimal for bytes and KB | `412 KB`, `31.2 MB`, `3.2 TB` |
| Percent | One decimal for health, none elsewhere | `98.7%`, `82%`, `21%` |
| Time of day | Current culture short time | `03:12` |
| Recent date | `Today 03:12`, `Yesterday 03:00`, weekday for the last 6 days (`Mon 21:40`), then `8 Nov 03:00`, with the year when not the current year or more than six months away (`14 Mar 2026`). In sentences the first word is lower case (`Scanned today 03:12`) | |
| Duration | `45 s`, `37 min`, `1 h 48 min`; estimates prefixed `About` | `About 1 h 12 min` |
| Rate | `142 MB/s` | |
| Separator | Space, middle dot, space (` · `) between facts on one line | `FLAC · 44.1 kHz · 16-bit` |
| Plurals | `1 problem` / `{n} problems`; `1 file` / `{n} files`. Zero uses the "No …" string where one is given | |
| Tier names | `Critical`, `High`, `Medium`, `Low`, `OK` | |

## Shell

| Element | Text | Behaviour |
|---------|------|-----------|
| Window title | `MediaVerifier {version}` | |
| Wordmark | `MediaVerifier` | |
| Navigation | `Dashboard`, `Issues`, `Library`, `Scans`, `Schedule`, `Tools`, `Settings`, `Update available` | Issues badge = open findings at Medium or above, capped at `999+`, coloured by the worst open tier. *Update available* opens Settings → Updates |
| Search box | Placeholder `Search files` | Searches file names. Enter opens Library → Browse filtered by the text; Esc clears |
| Bell | Tooltip `Notifications` | Badge = unread count (hidden at zero, `9+` above nine). Opens the notifications flyout (reference `dialogs`, H) |
| Scan split button | `Scan` | Main part scans all Library folders at the default depth. Menu: `Quick scan`, `Standard scan`, `Deep scan`, separator, `Verify fingerprints only`, `Scan a folder…`. Disabled with a tooltip when it cannot run: `Add a folder first`, `A scan is already running`, `A scheduled scan is running in the background`, `The media engine is missing or damaged` |
| Status strip, scanning | `Scanning {done} of {total} files` then ` · {rate} · about {time} left`; while estimating: ` · {rate} · estimating time left` | Click anywhere except the buttons to open Scans |
| Status strip, other phases | `Finding files… {n} so far`; `Pausing, finishing {n} files`; `Paused · {done} of {total} files`; `Finishing…` | |
| Status strip buttons | `Pause` / `Resume`, `Stop` | *Stop* asks: title `Stop this scan?`, text `Finished files keep their results. You can continue later.`, buttons `Stop scan` (primary), `Keep scanning` |

The status strip is hidden when no scan is running or paused.

## Dashboard

Reference `dashboard`. Layout (heights fixed, widths from the responsive rules in subplan 08):

| Region | Position | Height |
|--------|----------|--------|
| Health, Protection, Scans cards | `y0` | 136 |
| Four severity tiles | `y0 + 148` | 88 |
| By media type card | `y0 + 248` | 148 |
| Recommended next steps and Recent activity | `y0 + 408` | To the bottom of the content area, at least 264 |

| Element | Text | Bound to |
|---------|------|----------|
| Card titles | `HEALTH`, `PROTECTION`, `SCANS`, `BY MEDIA TYPE`, `RECOMMENDED NEXT STEPS`, `RECENT ACTIVITY` | |
| Health | `{pct}%` + `healthy`; `{ok} of {total} files have no problems`; `{n} files need attention` (hidden at zero) | Files with no open finding at Medium or above ÷ files scanned. Donut in `OkFill` with the OK shape inside |
| Protection | `{pct}%` + `verified`; `Re-read and unchanged in {days} days`; `{n} files are overdue` (hidden at zero) | Subplan 06. Donut in `Accent` with `shield-check` inside |
| Scans | `Last` + `{when} · {Scheduled or Manual} · {depth}`; `{n} new problems, {m} resolved` or `No new problems`; `Next` + `{when} · {schedule name}` or `No schedule` | Last completed run, next enabled schedule |
| Tiles | Tier name; open count; `+{n} since last scan`, `−{n} since last scan` or `No change` | Clicking opens Issues filtered to that tier |
| Media rows | `Photos`, `Videos`, `Music`; `{files} files · {size}`; severity bar; `{n} problems` or `No problems` | Clicking a row or a bar segment opens Issues filtered to that kind (and tier) |
| Recommendations | Up to four rows: tier shape, title, one line of detail, one compact button. The first row's button is primary | Subplan 12. Empty: `Nothing needs doing right now.` centred in `Text2` |
| Activity | Up to four events with their time; `Open problems, last 30 scans` and the sparkline | Run and operation history |

Recommendation wording (the engine in subplan 12 fills the numbers):

| Situation | Title | Detail | Button |
|-----------|-------|--------|--------|
| Rot with healthy copies | `{n} files changed on their own` | `A healthy copy exists in {root} for all {n}` or `… for {m} of {n}` | `Review` |
| Rot without copies | `{n} files changed on their own` | `No healthy copy was found` | `Review` |
| Missing seek index | `{n} videos have no seek index` | `Rebuilding is lossless and takes about {time}` | `Fix all` |
| Wrong extension | `{n} files have the wrong extension` | `Renaming is safe and can be undone` | `Fix all` |
| Fake lossless | `{n} albums look converted from MP3` | `They cannot be repaired, only replaced` | `Review` |
| Upscaled video | `{n} videos have less detail than their resolution` | `They cannot be repaired, only replaced` | `Review` |
| Verification overdue | `{pct}% of files not verified in {days} days` | `At {budget} per night a full pass takes {days} days` | `Verify now` |
| No backup folder | `No backup folder is set up` | `With one, damaged files can be restored automatically` | `Add folder` |
| No schedule | `Scans only run when you start them` | `A schedule keeps checking in the background` | `Create schedule` |
| Read errors | `Drive {letter}: returned read errors on {n} files` | `Copy what you can elsewhere, then check the drive` | `What to do` |
| Interrupted scan | `The last scan was interrupted` | `{done} of {total} files were finished` | `Continue` |

Activity wording: `Scheduled scan finished`, `Scan finished`, `Scan stopped by you`, `Scan could not run`, `{n} files restored from backup`, `{n} files repaired`, `Folder added: {name}`, `Folder removed: {name}`, `Updated to version {v}`.

**Before any folder exists** (reference `first-run`): a dashed drop area 360 px high containing the
48 px `folder-add` icon, headline `Add a folder to start watching it`, two lines `MediaVerifier checks photos, videos and music for damage, silent changes and fake quality.` and `It only reads your files. Nothing is changed unless you ask.`, a primary `Add folder` button holding the focus, and the caption
`or drop folders anywhere in this window`. Below it three cards 132 px high:

| Step | Title | Text |
|------|-------|------|
| 1 | `Add folders` | `Choose what to watch. Mark backup drives as Backup so damaged files can be restored from them.` |
| 2 | `Scan` | `A Standard scan decodes audio and photos fully and samples video. You see the estimated time first.` |
| 3 | `Stay protected` | `Create a schedule. Scans then run on their own and you hear about new problems only.` |

The current step's number circle uses `Selected` fill with an `Accent` border. The whole window
accepts dropped folders on every page.

**Media engine missing or damaged** (subplan 04): a Critical info bar above the top cards,
`The media engine is missing or damaged. Reinstall MediaVerifier.` with the action
`Open download page`. Everything else stays usable except scanning and repairs.

**Folders exist but nothing has been scanned**: the normal layout with dashes (`—`) for values that do
not exist yet, and one recommendation: `Your folders have not been scanned yet` / `A first Standard scan takes about {time}` / `Scan`.

## Issues

Reference `issues`, `issues-selection`, `issues-detail`. One row per open finding.

| Region | Position |
|--------|----------|
| Tier and kind chips | `y0`, 28 high, 8 px apart; a 1 px `Stroke` divider with 8 px on each side between the two groups |
| Drop-downs and grouping | `y0 + 40`, 28 high. Grouping control right-aligned with the caption `Group by` 8 px to its left |
| List card | `y0 + 80` to the bottom of the content area |

| Element | Text | Behaviour |
|---------|------|-----------|
| Tier chips | `All {n}`, `Critical {n}`, `High {n}`, `Medium {n}`, `Low {n}` | `All` is exclusive; the tier chips combine with OR. Counts respect the other filters |
| Kind chips | `Photos`, `Videos`, `Music` | Combine with OR; none selected means all |
| Drop-downs | `Category` (`All categories`, `Damage`, `Integrity`, `Authenticity`, `Quality`, `Structure`), `Rule` (the rules that have findings, with counts), `Folder` (the folder tree), `Status: {value}` (`Open`, `Ignored`, `Resolved`) | A drop-down with a choice other than its default shows the choice as its label and an `Accent` border |
| Grouping | `Severity`, `Folder`, `Rule` | Group headers show the group name and count and collapse on click |
| Row line 1 | File name | |
| Row line 2 | Summary: the rule's short title, with the confidence wording, then ` · ` and the evidence phrase | Subplan 02 wording rules; phrases come from the rule tables in subplans 06, 07, 10, 11 |
| Row right | Folder path, size | Hover and focus replace the size with `Details` and a `more` button (tooltip `More actions`) |
| Sorting | By tier, then confidence, then file name. The table header is not shown; sorting is changed from the row menu `Sort by` (`Severity`, `Name`, `Folder`, `Size`, `First seen`) | |
| Selection bar | `{n} selected`; `Fix…`, `Ignore`, `Scan again`, `Quarantine`; close | `Fix…` lists only actions valid for every selected finding; when there is none it is disabled with the tooltip `These problems need different fixes` |
| New results during a scan | A pill centred at the top of the list: `{n} new problems · Show` | The list never reorders under the pointer; chip counts update live |
| Filter summary | When any filter is set, the first row of the list is a 32 px summary row: `Showing {n} of {total}` in `Body` `Text2` and the link `Clear filters` | |

Empty states (centred in the list card, 48 px icon):

| Situation | Icon | Headline | Text | Button |
|-----------|------|----------|------|--------|
| No findings at all | `check` | `No problems found` | `{n} files checked. Last scan {when}.` | — |
| Filters exclude everything | `filter` | `Nothing matches these filters` | — | `Clear filters` |
| Nothing scanned yet | `scans` | `Scan your library to see results` | — | `Scan` |
| No folders | `folder-add` | `Add a folder to start watching it` | — | `Add folder` |

## Detail drawer

Reference `issues-detail`. Opens from any finding or file with Enter or `Details` (and a double-click
outside Issues; in Issues a double-click opens the file itself in its program). Esc
closes it. Up and Down in the list move to the neighbouring item without closing it.

| Region | Content |
|--------|---------|
| Header | Thumbnail 64 × 64 (cover art, photo or first key frame; while loading or when there is none, the media-kind icon at 24 px in `Text3` on `Bg`). File name in `Subtitle`. Breadcrumb in `Caption`, each segment a link to Library → Browse. Format line in `Caption` `Text2`. Close button, tooltip `Close (Esc)` |
| Format line | Audio: `{codec} · {rate} kHz · {bits}-bit · {channels} · {duration} · {size}` with channels as `mono`, `stereo`, `5.1`. Video: `{container} · {video codec} · {height}p · {fps} fps · {duration} · {size}`. Photo: `{format} · {width} × {height} · {size}` |
| Fingerprint line | 16 px `shield-check` in `OkText` + `Unchanged since {date} · verified {n} times, last {date}`. Never verified: `shield` in `Text3` + `Fingerprint recorded {date} · not verified since`. No fingerprint: `No fingerprint yet`. Changed: Critical shape + `Changed since {date}` |
| Actions | The recommended action as a primary button, `Show in folder` as a secondary button, and a `more` menu with the remaining actions (reference `dialogs`, C) |
| Findings | Caption `FINDINGS ({n})`. The first finding is expanded, the rest collapsed to 44 px rows (badge, title, chevron). Expanded: tier badge, confidence pill (`Likely`, `Possibly`, `Suspicious`; none for facts), title in `BodyStrong`, then labelled paragraphs `What this means`, `Why it matters`, `What you can do`, then `Evidence` with its view and one caption line of numbers, then subtle buttons `Ignore` and `Scan again` |
| History | Collapsed row `History`; expanded: the timeline of the file's events |
| Technical details | Collapsed row `Technical details`; expanded: streams table, decoder messages with counts, fingerprints in `Mono`, and a `Copy details` button |

Event wording for History: `First seen`, `Fingerprint recorded`, `Verified unchanged`, `Modified`,
`Changed without being modified`, `Tags changed`, `Moved from {folder}`, `Went missing`, `Came back`,
`Repaired: {action}`, `Restored from {root}`.

Evidence views: spectrogram and waveform for audio rules (subplan 10); frame strip and the
original/rebuilt comparison for upscale rules (subplan 11); a message table (`Message`, `Count`) for
decoder findings; a byte map (one 4 px column per 1/200 of the file, zero-filled regions in
`CritFill`, the rest in `Stroke`) for zero-fill and rot findings. Views load in the background and
never delay the drawer.

## Library

Reference `library` (Folders) and `library-browse` (Browse). A segmented control `Folders` | `Browse`
at (`x0`, `y0`) switches the view.

**Folders.** A secondary `Add folder` button with the `folder-add` icon, right-aligned at `y0`. Cards
from `y0 + 48`, 160 px high, 12 px apart.

| Element | Text |
|---------|------|
| Card heading | Folder name in `Subtitle`; pill `Library` or `Backup`; path in `Body` `Text2` |
| Buttons | Compact `Scan` (Library folders that are connected); `more` menu: `Edit…`, `Verify now`, `Open in Explorer`, separator, `Remove…` |
| Stats | `{files} files · {size}`; right: `{n} problems` or `No problems`; severity bar |
| Footer | `Scanned {when} · {storage}` with storage as `SSD`, `Hard drive`, `Network`, `Removable`; never scanned: `Not scanned yet · {storage}` |
| Not connected | Medium info bar `Not connected. Its files are left untouched.`; stats in `Text3`; footer `Last seen {date} · {storage}` |
| Backup folder | No Scan button and no severity bar; stats line `{files} files · {size}` and footer `Fingerprints recorded {when} · {storage}` |
| Drop area | `Drop folders here, or press Ctrl+N to add one` |

*Remove…* asks: title `Stop watching {name}?`, text `Its index, fingerprints and history are deleted from MediaVerifier. Your files are not touched.`, buttons `Stop watching` (danger), `Cancel` (focused).

**Add or edit a folder** (reference `dialogs`, G; title `Add folder` or `Edit folder`):

| Field | Control | Text and rules |
|-------|---------|----------------|
| `Folder` | Path box with `Browse…` | Must exist (Edit: a folder whose path is left unchanged may be disconnected; it is not checked). Errors shown under the field in `CritText`: `This folder does not exist`, `This folder is already being watched`, `This folder is inside {name}, which is already being watched`, `This folder contains {name}, which is already being watched`. A drive's root is stored with its separator (`E:\`) |
| `Name` | Text box | Defaults to the folder's own name |
| `Use as` | Option rows | `Library` — `Scan everything here and report problems`; `Backup` — `Only record fingerprints, so damaged files can be restored from here` |
| `Contains` | Check boxes | `Photos`, `Videos`, `Music`; at least one |
| `Skip` | Text box | Helper `Folder or file patterns, separated by semicolons` |
| Count | Low info bar | While counting: `Counting files…`; then `{n} media files found · {size}`; none: `No media files found here` (Medium). Counts the kinds ticked under `Contains` and leaves out the `Skip` patterns; counted again when either changes |
| Buttons | | `Add folder` or `Save` (primary), `Cancel` |

After adding a Library folder, a toast offers the scan: `{name} added` / `{n} files are waiting to be scanned` / button `Scan now`. When `Add folder` was pressed before the count finished, the toast has no second line.

**Browse.** Breadcrumb from `x0 + 180`; state chips right-aligned: `All {n}`, `OK {n}`,
`Problems {n}` (exclusive). Tree card 280 px wide from `y0 + 48`; table card to its right, 12 px gap.
When the page is narrower than 652 px (smallest window, or the drawer open) the tree gives up width, down to
180 px.

| Column | Offset from the table's left edge | Content |
|--------|-----------------------------------|---------|
| (icon) | 16 | Severity shape of the worst open finding, the OK shape, or a file-state icon |
| `Name` | 44 | File name; the column takes the width the others leave (at least 80) |
| `Problem` | 170 wide | The worst open finding's title, hedged as in Issues (`Likely DC offset`), with `{title} and {n} more` when the file has more; empty without findings. Sorts by severity |
| `Format` | 130 wide | Audio `{codec} {rate} kHz {bits}-bit`; video `{codec} {height}p`; photo `{format} {width} × {height}`; pending `Not scanned yet`; skipped `Skipped: {reason}` in `Text3` |
| `Size` | 64 wide, right-aligned | |
| `Scanned` | 96 wide | Recent date or `—` |
| `Verified` | 100 wide | Recent date or `—` |

Narrow tables. When `Name` would get less than 160 px, columns hide in this order until it has 160:
`Verified`, `Scanned`, `Format`, `Size`, `Problem`. The same rule keeps every table's last useful column
in view: Scans history keeps `Result` at least 160 px by hiding `Started by`, `Depth`, `Duration`,
`Resolved`, `New`, `Folders`, `Files` in that order; Quarantine keeps `Removed in` at least 120 px by
hiding `Size`, then `Moved`.

Skip reasons: `online-only cloud file`, `still being written`, `in use by another program`, `no permission to read`, `larger than the size limit`, `format not supported`.

Row menu: `Details`, `Scan again`, `Verify now`, separator, `Show in folder`, `Copy path`. Tree menu:
`Scan this folder`, `Verify this folder`, `Ignore a rule here…`, `Show in folder`.

## Scans

Reference `scans`.

| Element | Text |
|---------|------|
| Run card heading | `Scanning {scope}` with scope as the folder names joined (`Music and Photos`), `all folders`, or `{n} files`; paused: `Paused · {scope}`; enumerating: `Finding files in {scope}` |
| Sub-line | `{depth} depth · started {time} · started by you` or `… · started by schedule "{name}"` |
| Buttons | `Pause` / `Resume`, `Stop` |
| Progress | Bar and `{pct}%` |
| Stats | `Files` `{done} of {total}`; `Data read` `{done} of {total}`; `Speed` `{rate}`; `Time left` `About {time}` or `Estimating…` |
| Found so far | The four tier shapes, each with its count for this run |
| Working on | Caption `WORKING ON`; up to eight rows: media icon, file name, stage (`Reading`, `Decoding`, `Decoding and analysing`, `Sampling frames`, `Decoding every frame`), a 160 × 4 progress bar |
| No scan running | The card shows the `scans` icon at 32 px, headline `No scan is running`, text `Last scan {when}.`, primary `Scan` |
| History | Caption `HISTORY`. Columns: `Started`, `Started by` (`You`, `Schedule`), `Depth`, `Folders`, `Duration`, `Files`, `New`, `Resolved`, `Result` |
| Result pills | `Completed` (OK colours), `Stopped` (Medium), `Time limit reached` (Low), `Interrupted` (Medium), `Could not run` (Critical), `Skipped` (neutral) |

Selecting a history row opens the run in the drawer: heading `Scan of {scope}`, the same numbers,
alerts as info bars, errors as a list, and the button `Show problems found in this scan`.

**Before a large scan** (more than 10,000 files or 100 GB in scope; reference `dialogs`, F): title
`Scan {n} files?`, line `{folders} · {size}`, three option rows with their estimates, note `Estimates come from earlier scans on these drives. You can pause or stop at any time, and a stopped scan continues where it left off.`, buttons `Start scan`, `Cancel`. Option descriptions: `Quick` — `Checks structure and fingerprints new files`; `Standard` — `Decodes audio and photos fully, samples video`;
`Deep` — `Also decodes every video frame`. With no history yet the estimate reads `Not known yet`.

**Interrupted scan**: a Medium info bar above the run card: `The last scan was interrupted.` with the
action `Continue`.

## Schedule

Reference `schedule`, `schedule-edit`. Behaviour in subplan 13.

| Element | Text |
|---------|------|
| Intro | `Scans run on their own, even when MediaVerifier is closed.` |
| Button | `New schedule` |
| Row | Switch; name; sentence `{when} · {depth} · {folders} · {limit}` with limit as `up to {n} h` or `no time limit`; `Next run` + recent date or `Off`; `Last result` + result pill or `Never run`; compact `Run now`, `Edit`; `more` menu: `Duplicate`, `Delete…` |
| When phrases | `Every day at {time}`; `Every {weekday} at {time}` (several: `Every Mon and Thu at {time}`); `Day {n} of every month at {time}`; `Last day of every month at {time}`; `Every {n} days at {time}` |
| Empty | `schedule` icon, `No schedules yet`, `A schedule keeps your library checked without you thinking about it.`, primary `New schedule`, and two secondary buttons `Nightly verification`, `Weekly check` that create those presets |
| Verification card | Title `VERIFICATION`; donut with the protected percentage; `Every file should be re-read and compared with its fingerprint regularly.`; `That is how silent damage is found while a healthy copy still exists.`; `Re-read every file at least every` [number] `days`; `Spend up to` [number] `minutes on this in each scheduled scan`; Low info bar `At {budget} per night a full pass takes {n} days.`; caption `{n} files are overdue. Raise the limit or verify now.` with `verify now` as a link |

**Edit schedule dialog** (title `New schedule` or `Edit schedule`):

| Field | Control | Options and text |
|-------|---------|------------------|
| `Name` | Text box | Required |
| `Folders` | Drop-down with check boxes | `All folders`, or a selection shown as `Music, Photos` |
| `Depth` | Segmented | `Quick`, `Standard`, `Deep`; helper under it: Quick `Checks structure and fingerprints new files.`; Standard `Decodes audio and photos fully, and samples video.`; Deep `Also decodes every video frame. Slow.` |
| `Repeat` | Segmented | `Daily`, `Weekly`, `Monthly`, `Every … days` |
| `On` | Depends on `Repeat` | Weekly: day chips `Mon` … `Sun`. Monthly: drop-down `Day 1` … `Day 28`, `Last day`. Every … days: number box with `days`. Daily: row hidden |
| `At` | Time box | 24-hour or 12-hour per the culture |
| `Time limit` | Drop-down | `No limit`, `30 minutes`, `1 hour`, `2 hours`, `4 hours`, `8 hours` |
| `Conditions` | Check boxes | `Only when plugged in` (on), `Wait if a game or full-screen app is running` (on), `Wake the computer to run` (off) |
| Summary | Box with the `clock` icon | `{when phrase}, {depth} scan of {folders}.` and `Next run: {weekday} {day} {month}, {time}` |
| Buttons | | `Save` (primary), `Cancel` |

While the form is not valid, Save is disabled and the summary box shows why in place of the summary: the Critical shape and the message in `CritText`, `Give the schedule a name`, `Choose at least one day`, `Choose at least one folder` or `Enter a time such as 03:00 or 21:30` (a time is read as `H:mm` or `HH:mm`; the day of the month goes up to 28). If Windows refuses to register the task: Critical info bar above the buttons `Windows would not accept this schedule: {system message}` and the schedule is saved switched off.

## Tools

Reference `tools`. Five cards; each card's button opens a sub-view that replaces the page content
and has a `chevron-left` icon button with the tooltip `Back to Tools` before the page title.

| Card | Description | Facts | Button |
|------|-------------|-------|--------|
| `Quarantine` | `Originals kept after a repair, in case you want them back.` | `Items`, `Size`, `Oldest is removed in` | `Open quarantine` |
| `Operations` | `Everything MediaVerifier changed, newest first. Most can be undone.` | `Last`, `When`, `Can be undone` | `Open journal` |
| `Reports` | `Save findings as a web page, a spreadsheet file, or JSON.` | `Open problems`, `Last report` | `Create report` |
| `Media engine` | `FFmpeg is built in. It decodes files to check them and performs repairs.` | `Version`, `Licence` | `View licences` |
| `Database` | `The index of your library, its fingerprints and history.` | `Size`, `Location`, `Last backup` | `Manage` |

Sub-views (built from the table, list and dialog patterns in subplan 08; no new visuals):

| Sub-view | Content |
|----------|---------|
| Quarantine | Title `Quarantine`. Table: `File`, `Was in`, `Size`, `Moved`, `Removed in`. Selection bar actions `Restore`, `Delete for good`. Header buttons `Open folder`, `Delete all…`. Empty: `quarantine` icon, `Quarantine is empty`, `Originals appear here after a repair.` Confirmation as reference `dialogs`, B |
| Operations | Title `Operations`. Table: `When`, `What`, `Files`, `Result`, and a compact `Undo` button on rows that allow it. Result pills `Done`, `Failed`, `Undone`, `Cancelled`. Selecting a row lists its files and messages in the drawer. Empty: `undo` icon, `Nothing has been changed yet` |
| Create report | Dialog 480 wide, title `Create report`. `What` drop-down (`Open problems (current filters)`, `All open problems`, `Last scan`); `Format` option rows `Web page` — `One file you can open in a browser or print`, `Spreadsheet (CSV)` — `Opens in Excel`, `JSON` — `For other programs`; `Save to` path box. Buttons `Create`, `Cancel`. Result toast `Report saved` with the button `Open` |
| Media engine | Title `Media engine`. Facts: `FFmpeg version` (`9.0.2`), `Build` (`mediaverifier-1`), `Licence` (`LGPL 2.1 or later`), `Location`, `Integrity` (`Verified`, `Changed`, `Missing`). When the integrity check reports Changed, a Medium info bar `The media engine files were changed. Results may differ from a standard installation.` A scrolling `Mono` text of the licence, the notice `MediaVerifier runs FFmpeg as a separate program. Its exact source is published with each release.`, and buttons `Open licences folder`, `Where to get the source` (opens the bundle's release page) |
| Database | Title `Database`. Facts: `Location`, `Size`, `Files indexed`, `Last backup`. Buttons with one caption line each: `Back up now` — `Saves a copy next to the database`; `Compact` — `Reclaims unused space`; `Check` — `Looks for damage in the database itself`; `Move…` — `Use a different folder`; `Reset…` (danger) — `Forget everything and start again`. Each action reports in a toast: `Backup saved` with the file name and `Open`; `Database compacted` with the new size; `No damage found in the database`, or Critical `The database is damaged` with the first problem SQLite reports; a failure is a Critical `{action} failed` with the reason. Reset confirmation: title `Reset the library?`, text `Folders, fingerprints, findings and history are deleted. Your files are not touched. This cannot be undone.`, buttons `Reset` (danger), `Cancel` (focused) |

## Settings

Reference `settings-rules`. A section list 168 px wide at `x0`; the section's content from
`x0 + 192`. Each section starts with its name in `Subtitle` and one line of `Body` `Text2`. Settings
apply at once; there is no Save button. A dot r 3 in `Accent` marks *Updates* when an update is
available.

Rows inside a section are 56 px high (label in `Body`, optional helper in `Caption` `Text3` beneath,
control right-aligned), separated by 1 px `Stroke` lines, inside one card.

| Section | Intro line | Rows: label — control — default — helper |
|---------|------------|-------------------------------------------|
| `General` | `How MediaVerifier looks and starts.` | `Theme` — segmented `System`, `Light`, `Dark` — System. `Start with Windows` — switch — off — `Starts hidden in the notification area`. `Keep running in the notification area when closed` — switch — off. `Default scan depth` — segmented — Standard. `Ask before large scans` — switch — on — `Shows the estimated time first` |
| `Scanning` | `How much of the computer scans may use, and what they look at.` | `Speed` — segmented `Low`, `Balanced`, `High` — Balanced — `Share of processor cores: 25 %, 50 %, 100 %`. `Readers per hard drive` — number — 1. `Readers per SSD` — number — 4. `Readers per network folder` — number — 2. `File types` — button `Edit…` opening a dialog with three text boxes (`Photos`, `Videos`, `Music`) and `Restore defaults`. `Always skip` — button `Edit…` (folder-name patterns). `Scan online-only cloud files` — switch — off — `Downloads them first. Can use a lot of data`. `Skip files larger than` — drop-down `No limit`, `10 GB`, `50 GB`, `100 GB` — No limit. `Stop a file that makes no progress for` — number + `seconds` — 60. `Check photos for upscaling` — switch — on. `Strict decoding in Deep scans` — switch — off — `Reports files that break the format rules even if they play` |
| `Rules` | `Turn checks on or off, cap how serious they can be, and tune their limits. Changes apply at once, with no rescan.` | See below |
| `Integrity` | `How often files are re-read to catch silent changes.` | `Re-read every file at least every` — number + `days` — 90. `Time for this in each scheduled scan` — number + `minutes` — 120. `Also verify whenever a file is analysed` — switch — on. `Keep originals after a repair in` — drop-down `Quarantine folder`, `Recycle Bin`, `Another folder…` — Quarantine folder. `Empty quarantine after` — number + `days` — 30 |
| `Notifications` | `When MediaVerifier tells you something outside its window.` | `Show Windows notifications` — switch — on. `Notify about` — drop-down `Critical only`, `High and above`, `Medium and above`, `Everything` — Medium and above. `Quiet hours` — switch — off; when on, two time boxes `From`, `To`. Button `Send a test notification` |
| `Updates` | `MediaVerifier updates itself from signed releases on GitHub.` | The update card (subplan 14): `Version {v}`; status line; buttons `Check now`, `Download and install`; link `Release notes`. `Check for updates when MediaVerifier starts` — switch — on |
| `Advanced` | `For troubleshooting.` | `Database location` — path, button `Open folder` (shows the file in Explorer; `Change…` returns with Tools → Database `Move…` in phase 16). `Logs` — button `Open logs folder`. `Diagnostics` — buttons `Run self-check`, `Copy system report` |
| `About` | — | App icon 48 px, `MediaVerifier`, `Version {v}`, links `Website`, `Release notes`, `Licences` |

**Rules section.** Filter box (placeholder `Filter rules`) and category chips `All`, `Damage`,
`Authenticity`, `Quality`, `Structure` (exclusive). One 64 px row per rule: switch, title in
`BodyStrong`, meta line `{rule id} · {kind} · {depth} depth` in `Caption` `Text3`, a drop-down
`Up to Critical` / `Up to High` / `Up to Medium` / `Up to Low` (only tiers the rule can reach), and a
chevron that expands the rule's parameters. Each parameter row: label, number box, unit; when the
value differs from the default, the caption `Default is {n}` and a subtle `Reset` button. The label
column is as wide as the rule's longest label (at least 120 px), as far as the box, unit, caption and
`Reset` leave room; the filter box narrows (down to 160 px) so the chips stay in view. After any
change a Low info bar reports the effect: `{n} findings changed tier with this value.`, `{n} findings appeared.`, `{n} findings no longer apply.`, or `No findings changed.` A rule whose switch is off
shows its title in `Text2`.

Status lines for the update card: `Update status has not been checked.`, `Checking…`,
`MediaVerifier is up to date.`, `Version {v} is available.`, `Downloading {pct}%`, `Update found, but it could not be verified. Nothing was installed.`, `Could not check for updates. {reason}`.

## Dialogs and transient surfaces

Reference `dialogs` (letters refer to that sheet).

| Dialog | Title | Body | Buttons |
|--------|-------|------|---------|
| A. Confirm a repair | `{Action verb} {n} {things}?` for example `Rebuild the index of 38 videos?` | One paragraph from the action (subplan 12); four fact lines with icons: what and total size; `About {time}`; `Originals go to quarantine for {n} days` (or `Originals go to the Recycle Bin`); `You can undo this` (or `This cannot be undone here`); optional Medium info bar `{n} changed {things} will be skipped.` with `Show`; while a scan runs, the check box `Pause the running scan while this happens` (on) | Action verb (primary, focused), `Cancel` |
| B. Permanent deletion | `Delete {n} files for good?` | `These are the originals kept in quarantine, {size}. This cannot be undone.` | `Delete for good` (danger), `Keep them` (focused) |
| Accept a changed file | `Accept this file as it is now?` | `MediaVerifier will treat the current content as correct and stop reporting the change.` Check box `I changed this file myself` (must be ticked to enable the button) | `Accept`, `Cancel` |
| Ignore a rule in a folder | `Ignore "{rule title}" in {folder}?` | `Existing and future findings of this kind in this folder and its subfolders are hidden. To show them again, set Status to Ignored and choose Stop ignoring.` | `Ignore`, `Cancel` |
| Install an update during a scan | `Install the update now?` | `A scan is running. It will be interrupted and can continue after the update.` | `Install`, `Not now` |
| Unhandled error (once per session) | `Something went wrong` | `MediaVerifier will keep running. Details were saved to the log.` | `Open log`, `Close` |

| Surface | Wording |
|---------|---------|
| C. Row menu (Issues) | `Details` (Enter), the recommended action (Ctrl+Enter), `Scan again` (Shift+F5), separator, `Ignore` (Ctrl+I), `Ignore this rule for this folder`, separator, `Open file`, `Show in folder` (Alt+Enter), `Copy path` (Ctrl+C), separator, `Move to quarantine` (Del). With `Status: Ignored`, `Ignore` reads `Stop ignoring` (menu, Ctrl+I and the selection bar) and shows the findings again; a finding hidden by a folder ignore lifts that ignore. Ignore shows the success toast `Ignored for {n} problems` (a folder ignore: `Ignored` / `{rule title} in {folder}`) with `Undo`. The drawer's `more` menu: `Open file`, `Scan again`, `Copy path`, separator, `Move to quarantine` |
| D. Toasts | Success: `{Result} for {n} {things}` with detail `{m} skipped · originals are in quarantine` and `Undo`. Failure (Critical bar): `{Action} failed for {n} {things}` with `Show`. Information: single line |
| E. Tooltips | Full text of anything cut short; names for icon buttons |
| H. Notifications flyout | Title `Notifications`; link `Mark all read`; items with tier shape, title, one line, time, and an `Accent` dot r 3 when unread; footer subtle button `Clear all`. Empty: `bell` icon 32 px, `No notifications` |
| I. Drop-down list | As drawn |

Action verbs and results (subplan 12 actions): `Restore` / `Restored`; `Rebuild index` / `Index rebuilt`; `Rename` / `Renamed`; `Convert to 16-bit` / `Converted to 16-bit`; `Rewrite` / `Rewritten`;
`Save a playable copy` / `Playable copy saved`; `Extract preview` / `Preview extracted`; `Move to quarantine` / `Moved to quarantine`; `Ignore` / `Ignored`.

## Keyboard

| Keys | Action |
|------|--------|
| `Ctrl+1` … `Ctrl+6`, `Ctrl+,` | Switch page |
| `Ctrl+F` or `/` | Focus the search box, or the page's filter box when it has one |
| `F5` | Refresh the view. `Shift+F5`: scan the selection again |
| `Ctrl+N` | Add folder |
| `Ctrl+Shift+S` | Scan now at the default depth |
| Arrows, `PgUp`, `PgDn`, `Home`, `End` | Move in lists |
| `Enter` | Open details |
| `Space` | Toggle selection of the focused row |
| `Ctrl+A`, `Shift`+arrows, `Ctrl`+click | Select all, extend, toggle |
| `Ctrl+I` | Ignore the selected findings |
| `Delete` | Move the selected files to quarantine (asks first) |
| `Ctrl+Enter` | Run the recommended action for the selection |
| `Ctrl+Z` | Undo the last operation that allows it |
| `Alt+Enter` | Show in folder |
| `Ctrl+C` | Copy the selected paths |
| `Esc` | Close the menu, dialog or drawer; then clear the selection |
| `F1` | Open the help page for the focused rule, or for the page |
| `Tab`, `Shift+Tab` | Move between controls in reading order |

## Demo data for the harness

`--render-screens` shows exactly the data in the reference mockups so renders can be compared. Take
every name, number and string from the SVG files: the library totals (12,507 files; 3 Critical,
14 High, 96 Medium, 212 Low), the four folders, the thirteen Issues rows, the ten files of
`Music\Night Drive\Overpass`, the six history rows, the three schedules, and the running scan
(12,404 of 58,210 files, 21 %). Dates are relative to a fixed "now" of Monday 16 November 2026, 14:20.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| UI-01 | Demo data set matching the references | Harness renders show the same names and numbers as the mockups |
| UI-02 | Shell bindings: navigation badge, search, bell, Scan split button with its menu and disabled reasons, status strip in every phase | Matches `dashboard`; strings as tabled; 0 % CPU when idle |
| UI-03 | Dashboard, including the first-run and not-yet-scanned states | Matches `dashboard`, `dashboard-narrow`, `first-run`; every element navigates as specified |
| UI-04 | Issues: chips, drop-downs, grouping, list, hover actions, selection bar, live-result pill, empty states, narrow layout | Matches `issues`, `issues-selection`; 1,000,000 findings filter in under 300 ms |
| UI-05 | Detail drawer with header, findings, history, technical details, on-demand thumbnail | Matches `issues-detail`; opens in under 100 ms; Up/Down browsing works while open |
| UI-06 | Library: folders view, add/edit dialog with validation and live count, browse view | Matches `library`, `library-browse`, `dialogs` G; a disconnected folder is shown as not connected, never as missing files |
| UI-07 | Scans: run card in every phase, large-scan dialog, interrupted bar, history, run details | Matches `scans`, `dialogs` F |
| UI-08 | Schedule page and edit dialog (logic in subplan 13) | Matches `schedule`, `schedule-edit`; summary sentence equals the schedule calculator's output |
| UI-09 | Tools page and its five sub-views | Matches `tools`; sub-views use only kit patterns |
| UI-10 | Settings: all sections and rows as tabled; rule editor with instant re-evaluation | Matches `settings-rules`; changing a limit updates Issues without a rescan |
| UI-11 | Dialogs A, B and the others tabled; menus; toasts with Undo; tooltips; notifications flyout | Matches `dialogs`; focus rules from subplan 08 hold |
| UI-12 | Keyboard map on every page | Every row of the table works; menus show the shortcuts |
| UI-13 | String table: every string in this document in one resource class, none inline | A test fails on any user-facing literal outside the string table |
| UI-14 | Evidence views for M1 findings: message table, byte map, fingerprint line | Shown in the drawer for the matching findings |

## Exit criteria

- Every page matches its reference in dark and light at 100 % and 150 %, to within 1 px.
- Each flow (first run, review and fix, restore after a scheduled run, tune a rule) can be completed
  with the keyboard alone.
- No synchronous database or file access on the UI thread (a debug assertion in the repository layer
  throws when called from it).
- The deviations log lists every unspecified design case met.

## Pitfalls

- A list that re-sorts under the pointer causes mis-clicks. Freeze the order while the user interacts
  and offer the `Show` pill.
- Never disable a button without a tooltip that says why.
- Counts on chips must come from the same query as the list, or they will disagree.
- Do not wait for a thumbnail or an evidence image before opening the drawer.
