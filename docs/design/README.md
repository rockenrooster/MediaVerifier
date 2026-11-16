# Design reference

These mockups are the visual specification for MediaVerifier. They were generated from the design tokens and reviewed as rendered images. The numbers in them are binding: every position, size, colour and string is what the app must produce. [Subplan 08](../../plans/08-ui-design-system.md) explains the rules behind them and [subplan 09](../../plans/09-ui-screens.md) explains behaviour.

- **SVG files** are the source of truth. Open one in a text editor to read exact coordinates, sizes and colours. Text is positioned by its vertical centre (`dominant-baseline="central"`).
- **PNG files** in `png/` are renders made on Windows with Segoe UI at 100 % scale. Use them to compare by eye; SVG text can reflow on machines without Segoe UI.
- [tokens.md](tokens.md) lists every colour and type token with contrast ratios. [icons.md](icons.md) lists the geometry of every icon.

## Screens

| Screen | Dark | Light | Size | Shows |
|--------|------|-------|------|-------|
| Dashboard | [svg](dashboard-dark.svg) · [png](png/dashboard-dark.png) | [svg](dashboard-light.svg) · [png](png/dashboard-light.png) | 1280 × 800 | Health, protection, counts per severity, media breakdown, recommendations, activity; a scan running in the status strip |
| Dashboard at minimum window size | [svg](dashboard-narrow-dark.svg) · [png](png/dashboard-narrow-dark.png) | — | 960 × 600 | 960 × 600 window: collapsed navigation rail, wrapped captions, page scrolling |
| Dashboard before any folder is added | [svg](first-run-dark.svg) · [png](png/first-run-dark.png) | — | 1280 × 800 | Empty state, drop target, three-step guide |
| Issues | [svg](issues-dark.svg) · [png](png/issues-dark.png) | — | 1280 × 800 | Filter chips, drop-downs, grouping, grouped two-line list with one hovered row |
| Issues with a multiple selection | [svg](issues-selection-dark.svg) · [png](png/issues-selection-dark.png) | — | 1280 × 800 | Selected rows and the floating selection bar |
| Issues with the detail drawer | [svg](issues-detail-dark.svg) · [png](png/issues-detail-dark.png) | [svg](issues-detail-light.svg) · [png](png/issues-detail-light.png) | 1280 × 800 | Narrow filter layout, selected row, drawer with a finding, explanation and evidence |
| Library, folders | [svg](library-dark.svg) · [png](png/library-dark.png) | — | 1280 × 800 | Folder cards, an offline backup folder, drop area |
| Library, browse | [svg](library-browse-dark.svg) · [png](png/library-browse-dark.png) | — | 1280 × 800 | Folder tree with roll-up severity, file table with every file state |
| Scans | [svg](scans-dark.svg) · [png](png/scans-dark.png) | — | 1280 × 800 | Running scan panel, files in progress, history table |
| Schedule | [svg](schedule-dark.svg) · [png](png/schedule-dark.png) | — | 1280 × 800 | Schedules list and verification settings |
| Edit schedule dialog | [svg](schedule-edit-dark.svg) · [png](png/schedule-edit-dark.png) | — | 1280 × 800 | Modal dialog over the dimmed Schedule page |
| Tools | [svg](tools-dark.svg) · [png](png/tools-dark.png) | — | 1280 × 800 | Quarantine, operations, reports, media engine and database cards |
| Settings, rules | [svg](settings-rules-dark.svg) · [png](png/settings-rules-dark.png) | — | 1280 × 800 | Section list, rule rows, one rule expanded with a changed value |

## Reference sheets

| Sheet | Dark | Light | Size | Shows |
|-------|------|-------|------|-------|
| Components and states | [svg](components-dark.svg) · [png](png/components-dark.png) | [svg](components-light.svg) · [png](png/components-light.png) | 1280 × 1434 | Every control in rest, hover, pressed, disabled and focused states |
| Dialogs, menus and transient surfaces | [svg](dialogs-dark.svg) · [png](png/dialogs-dark.png) | [svg](dialogs-light.svg) · [png](png/dialogs-light.png) | 1280 × 1396 | Confirmations, context menu, toast, tooltip, scan estimate, add folder, notifications, drop-down list |
| Icons and app icon | [svg](icons-dark.svg) · [png](png/icons-dark.png) | [svg](icons-light.svg) · [png](png/icons-light.png) | 1280 × 852 | All icons at 16 and 32 px and on the accent colour; the app icon at six sizes |

## App icon

| File | Use |
|------|-----|
| [app-icon.svg](app-icon.svg) | Sizes 32 px and larger: gradient shield with a play triangle |
| [app-icon-small.svg](app-icon-small.svg) | Sizes 24 px and smaller: flat colour, larger triangle |

## Previews

### Dashboard

![Dashboard](png/dashboard-dark.png)

### Dashboard at minimum window size

![Dashboard at minimum window size](png/dashboard-narrow-dark.png)

### Dashboard before any folder is added

![Dashboard before any folder is added](png/first-run-dark.png)

### Issues

![Issues](png/issues-dark.png)

### Issues with a multiple selection

![Issues with a multiple selection](png/issues-selection-dark.png)

### Issues with the detail drawer

![Issues with the detail drawer](png/issues-detail-dark.png)

### Library, folders

![Library, folders](png/library-dark.png)

### Library, browse

![Library, browse](png/library-browse-dark.png)

### Scans

![Scans](png/scans-dark.png)

### Schedule

![Schedule](png/schedule-dark.png)

### Edit schedule dialog

![Edit schedule dialog](png/schedule-edit-dark.png)

### Tools

![Tools](png/tools-dark.png)

### Settings, rules

![Settings, rules](png/settings-rules-dark.png)

### Components and states

![Components and states](png/components-dark.png)

### Dialogs, menus and transient surfaces

![Dialogs, menus and transient surfaces](png/dialogs-dark.png)

### Icons and app icon

![Icons and app icon](png/icons-dark.png)
