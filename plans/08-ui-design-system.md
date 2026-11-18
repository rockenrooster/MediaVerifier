# 08 — UI design system

Phase 8. Depends on: phase 1. Can run in parallel with phases 2–7. Unblocks: UI screens.

## Who decides what

**The design is specified here and in [`docs/design/`](../docs/design/README.md). The implementer
builds it; the implementer does not design.** Every colour, size, position, icon, state and string
is given. Nobody is expected to supply artwork, fonts, mockups or wording: not the repository owner,
not the implementing agent.

Rules for the implementer:

1. Reproduce the reference mockups. A screen is correct when its render matches the reference PNG at
   100 % scale to within 1 px in position and size, and exactly in colour.
2. Never introduce a colour, font, size, radius, icon, illustration, animation or user-facing string
   that is not in this subplan, subplan 09, or the reference files.
3. If something needed is not specified, use the nearest specified pattern unchanged, and record it in
   the deviations log (`plans/README.md`) as an *unspecified design case*. Do not improvise a new look.
4. If a specification here is impossible in WinForms, record that too and choose the closest result.

**Terminology.** The app paints its own controls from these numbers instead of using the stock
Windows look. In this document that is called *custom-painted*. (WinForms literature calls the same
technique "owner-draw"; it does not mean the repository owner draws anything.)

**Self-contained.** The UI uses no icon font, no image assets for icons, no third-party UI library and
no web view. Icons are vector geometry listed in [`docs/design/icons.md`](../docs/design/icons.md).
Text uses Segoe UI and Consolas, which ship with every supported Windows version.

## Reference files

| File | Content |
|------|---------|
| [`docs/design/README.md`](../docs/design/README.md) | Index of all mockups with previews |
| `docs/design/*.svg` | Exact geometry of every screen and component. Source of truth for numbers |
| `docs/design/png/*.png` | Renders at 100 % scale, for comparison by eye |
| [`docs/design/tokens.md`](../docs/design/tokens.md) | Every colour and type token, with contrast ratios (all pass) |
| [`docs/design/icons.md`](../docs/design/icons.md) | Geometry of all 46 icons |

All measurements below are logical pixels at 96 DPI. Multiply by `DeviceDpi / 96` and round to whole
device pixels. Text in the mockups is positioned by its vertical centre.

## Principles

1. **Status first.** The state of the library is readable in one glance; the state of a file in one row.
2. **Calm surfaces, loud signals.** Neutral greys everywhere. Colour is reserved for severity, the
   primary action, selection and focus.
3. **Dense but readable.** 13 px body text, 36 and 44 px rows, a 4 px grid.
4. **Every state is visible.** Rest, hover, pressed, focused, selected and disabled are all distinct.
   Hover and focus always include a change with at least 3:1 contrast, never a faint tint alone.
5. **Never colour alone.** Severity is always a distinct shape plus a word plus a colour.
6. **Instant.** Hover, press, selection and page changes happen in the same frame.

## Colour tokens

Held in one `ThemeTokens` class with a dark and a light set. Nothing else contains a literal colour.

| Token | Dark | Light | Use |
|-------|------|-------|-----|
| `Bg` | `#1F1F1F` | `#F6F6F6` | Window and navigation rail background; fill of inset inputs |
| `Surface` | `#2A2A2A` | `#FFFFFF` | Cards, lists, drawer, status strip, inputs placed on `Bg` |
| `Raised` | `#333333` | `#FFFFFF` | Dialogs, menus, toasts, tooltips, floating bars |
| `Hover` | `#3A3A3A` | `#EBEBEB` | Hover fill; disabled fill of filled buttons; skeleton bars |
| `Pressed` | `#474747` | `#DCDCDC` | Pressed fill |
| `Selected` | `#243C4D` | `#D6E8F8` | Selected row, navigation item, chip, segment, option |
| `Stroke` | `#454545` | `#D0D0D0` | 1 px borders and dividers |
| `StrokeStrong` | `#8F8F8F` | `#6E6E6E` | Hover borders, hover bars, unchecked box borders, scrollbar thumb |
| `Text` | `#F5F5F5` | `#1A1A1A` | Primary text and icons |
| `Text2` | `#CECECE` | `#404040` | Secondary text and icons |
| `Text3` | `#B0B0B0` | `#595959` | Captions, placeholders, column headers |
| `TextDisabled` | `#7A7A7A` | `#9A9A9A` | Disabled text and icons |
| `Accent` | `#60CDFF` | `#005FB8` | Primary button, focus ring, selection bar, links, progress |
| `AccentHover` | `#8AD9FF` | `#0A70CF` | |
| `AccentPressed` | `#3FB4E8` | `#004A91` | |
| `OnAccent` | `#00131F` | `#FFFFFF` | Text and icons on accent fills |
| `DangerFill` | `#C93237` | `#C4252B` | Destructive button |
| `DangerHover` | `#D13C41` | `#D13438` | |
| `DangerPressed` | `#A8262B` | `#A61D22` | |
| `OnDanger` | `#FFFFFF` | `#FFFFFF` | Text on danger fills |
| `NeutralPill` | `#3A3A3A` | `#EBEBEB` | Background of neutral pills |
| `Scrim` | black at 50 % | black at 30 % | Dimming layer behind a modal dialog |
| `Shadow` | black at 50 % | black at 22 % | Shadow colour |

Severity has three tokens per tier: `Text` (labels and shape icons on neutral surfaces), `Fill`
(bars, chart segments, accent stripes), `Pill` (opaque badge and info-bar background).

| Tier | Dark text / fill / pill | Light text / fill / pill |
|------|-------------------------|--------------------------|
| Critical (`Crit`) | `#FF8585` / `#E5484D` / `#4C2F30` | `#B91C22` / `#D13438` / `#F9E7E7` |
| High | `#FFA657` / `#F76B15` / `#4F3626` | `#A54300` / `#D45A00` / `#FAEBE0` |
| Medium (`Med`) | `#FFD23F` / `#FFB224` / `#504229` | `#735400` / `#A37A00` / `#F6F1E0` |
| Low | `#85BDFF` / `#3E8BFF` / `#2E3B50` | `#1A56B0` / `#2B6FD6` / `#E6EEFA` |
| OK | `#5AD694` / `#30A46C` / `#2B4036` | `#146C3E` / `#1F8A52` / `#E4F1EA` |

All 71 text, pill, button and indicator pairs per theme meet WCAG 2.1 (4.5:1 for text, 3:1 for
indicators); the table is in `tokens.md`. Task UK-02 turns that table into a unit test.

## Type

| Token | Font | Size | Weight | Line height | Use |
|-------|------|------|--------|-------------|-----|
| `Caption` | Segoe UI | 11 px (8.25 pt) | 400 | 16 | Secondary facts, timestamps, helper text |
| `CaptionStrong` | Segoe UI | 11 px | 600 | 16 | Card titles (upper case), column headers, badge text |
| `Body` | Segoe UI | 13 px (9.75 pt) | 400 | 20 | Default |
| `BodyStrong` | Segoe UI | 13 px | 600 | 20 | File names, selected items, primary-button labels |
| `Subtitle` | Segoe UI | 16 px (12 pt) | 600 | 24 | Dialog and card headings |
| `Title` | Segoe UI | 20 px (15 pt) | 600 | 28 | Page title |
| `Metric` | Segoe UI | 28 px (21 pt) | 600 | 36 | Large numbers |
| `Mono` | Consolas | 12 px (9 pt) | 400 | 16 | Hashes, raw paths and logs in technical details |

No other sizes or weights exist. Card titles are written in upper case in the source strings.
Numbers use the current culture's digit grouping; file sizes use binary units with one decimal
(`31.2 MB`). Text that does not fit is cut with an ellipsis (`…`) and shows the full value in a tooltip.

## Space, shape, elevation

- Grid 4 px. Spacing values: 4, 8, 12, 16, 20, 24, 32.
- Radius: 3 (check box, segment), 4 (buttons, inputs, list items, info bars, tooltips), 8 (cards,
  dialogs, menus, toasts), half the height (pills, chips, switches, progress bars).
- Borders 1 px, drawn inside the element's bounds.
- **Focus ring**: 2 px `Accent`, 1 px away from the control: the ring's outer rectangle is the control
  inflated by 3 px, radius = control radius + 3. Shown for keyboard focus only.
- **Shadow** (dialogs, menus, toasts, tooltips, floating bars): the element's rounded rectangle,
  offset 6 px down, Gaussian blur with a standard deviation of 10 px, colour `Shadow`. Render it once
  per size into a cached bitmap.
- **Scrim**: a `Scrim` rectangle covering the whole window behind a modal dialog.

## Interaction states

| State | Neutral controls (secondary and subtle buttons, inputs, chips, cards that act) | Rows and navigation items | Filled buttons (primary, danger) |
|-------|-----------|-----------|-----------|
| Rest | Token fill, `Stroke` border (subtle buttons: nothing) | No fill | Fill colour |
| Hover | Fill `Hover`, border `StrokeStrong` | Fill `Hover` and a 3 px `StrokeStrong` bar at the leading edge | Hover fill and a 1 px inner outline in the `On…` colour |
| Pressed | Fill `Pressed`, border `Accent`, content 1 px lower | — | Pressed fill, same outline, content 1 px lower |
| Selected / on | Fill `Selected`, border `Accent` | Fill `Selected`, 3 px `Accent` bar, label `BodyStrong` | — |
| Focused | Focus ring | Focus ring around the row or item | Focus ring |
| Disabled | Fill `Bg`, border `Stroke`, text `TextDisabled` | Text `TextDisabled` | Fill `Hover`, text `TextDisabled` |
| Busy | Disabled, with a 16 px progress ring in place of its icon | — | Same |

The cursor is the arrow everywhere except on text inputs (I-beam) and inline links (hand).

## Motion

| Element | Motion |
|---------|--------|
| Progress ring | A 259° arc (72 % of the circle), 2 px, rotating clockwise once per second, linear |
| Indeterminate bar | A segment 40 % of the track wide travelling left to right in 1.5 s, ease-in-out, repeating |
| Detail drawer | Slides in from the right over 120 ms, ease-out cubic; closes over 100 ms |
| Toast | Fades in over 150 ms, stays 5 s (paused while hovered), fades out over 150 ms |
| Everything else | No animation |

When Windows' *Animation effects* setting is off, the drawer and toasts appear and disappear at once.

## Icons

All icons are drawn from the primitives in `icons.md` on a 16 × 16 grid: 1.5 px stroke, round caps
and joins, no fill unless stated. They take the colour of the adjacent text.

| Drawn size | Stroke | Used for |
|------------|--------|----------|
| 12 px | 1.5 | Breadcrumb separators, sort indicators, tree chevrons |
| 16 px | 1.5 | Default: buttons, rows, navigation, menus |
| 20 px | 1.75 | Inside the dashboard rings; tool-card tiles |
| 24 px | 1.75 | Drawer thumbnail placeholder |
| 32 px | 2.2 | Small empty states |
| 48 px | 2.5 | Page-level empty states |

Icon assignment (names from `icons.md`): Dashboard `dashboard`, Issues `issues`, Library `library`,
Scans `scans`, Schedule `schedule`, Tools `tools`, Settings `settings`, Update `update`; media kinds
`photo`, `video`, `music`; actions as shown in the mockups.

### Severity shapes

Filled shapes with a glyph knocked out in the colour of the surface behind them. Coordinates on a
16 × 16 grid; scale for 12, 14, 16, 20 and 24 px. Shape colour: the tier's `Text` token.

| Tier | Shape | Glyph (knock-out colour, 1.7 px round stroke) |
|------|-------|-------|
| Critical | Octagon `(5,1.5) (11,1.5) (14.5,5) (14.5,11) (11,14.5) (5,14.5) (1.5,11) (1.5,5)`, plus a 1 px same-colour stroke | Cross: `(5.75,5.75)–(10.25,10.25)` and `(10.25,5.75)–(5.75,10.25)` |
| High | Triangle `(8,2.25) (14.5,13.5) (1.5,13.5)`, plus a 1.5 px same-colour round-join stroke | Line `(8,6.25)–(8,9.5)`; dot centre `(8,11.6)` r 0.95 |
| Medium | Diamond `(8,1.5) (14.5,8) (8,14.5) (1.5,8)`, plus a 1.2 px same-colour stroke | Line `(8,4.9)–(8,8.6)`; dot centre `(8,10.9)` r 0.95 |
| Low | Circle centre `(8,8)` r 6.75 | Dot centre `(8,5)` r 0.95; line `(8,7.4)–(8,11.2)` |
| OK | Circle centre `(8,8)` r 6.75 | Check `(4.9,8.2) (7.2,10.4) (11.1,5.9)` |

### File-state icons

1.5 px stroke in `Text3`, 16 px.

| State | Geometry |
|-------|----------|
| Pending | Circle r 5.75 with dash pattern 2.2 on, 2.3 off |
| Skipped | Circle r 5.75; line `(5.25,8)–(10.75,8)` |
| Missing | Circle r 5.75; question mark: path `M6.4 6.5 C6.4 5.5 7.1 4.9 8 4.9 C8.9 4.9 9.6 5.5 9.6 6.4 C9.6 7.5 8 7.6 8 8.9`; dot centre `(8,11)` r 0.5 |
| Error | Rounded square `x 2.5, y 2.5, 11 × 11`, radius 2; line `(8,5.25)–(8,8.75)`; dot centre `(8,10.9)` r 0.5 |

### App icon

A shield holding a play triangle, on a 256 × 256 grid.

- Shield: `M128 18 L226 54 V124 C226 182 185 224 128 242 C71 224 30 182 30 124 V54 Z`
- Standard version (32 px and larger): shield filled with a vertical gradient from `#6ED3FF` at the
  top to `#1F6FE0` at the bottom; triangle `(104,86) (180,128) (104,170)` in white with a 14-unit
  white round-join stroke.
- Small version (24 px and smaller): shield filled `#2B8CEB`; triangle `(98,80) (186,128) (98,176)`,
  same stroke.
- `MediaVerifier.ico` holds 16, 20, 24 (small version) and 32, 40, 48, 64, 128, 256 (standard
  version), each rasterised from the geometry at that size with anti-aliasing. Generate it with a
  script kept in `tools/`; do not hand-edit pixels.
- The same shapes at 24 px mark the top of the navigation rail.

## Window and shell

Reference: `dashboard-dark`, `dashboard-narrow-dark`.

| Part | Geometry |
|------|----------|
| Window | Default client size 1280 × 800, centred on first run. Minimum 960 × 600. Background `Bg`. Standard Windows title bar with the theme's dark or light caption |
| Navigation rail | Left edge, full height, 200 px wide at window widths of 1100 px or more, 56 px below that. `Bg` fill, 1 px `Stroke` line on its right edge |
| App mark | App icon 24 px at (18, 16); wordmark `MediaVerifier` in `BodyStrong` centred on y 28 at x 50. Collapsed: icon at (16, 16), no wordmark |
| Navigation items | First at y 64, pitch 44. Each 40 px high, 8 px from the rail's left edge, 184 px wide (collapsed 40). Radius 4. Icon 16 px at +14 (collapsed +12), label `Body` at +42 |
| Bottom items | *Settings* 8 px above the bottom edge; *Update available* 44 px above it, only when an update exists, with its icon in `Accent` |
| Issues badge | Pill 20 px high, radius 10, 10 px from the item's right edge, count in `CaptionStrong`, coloured with the worst open tier's `Pill` and `Text`. Collapsed: a dot r 4.5 in the tier's `Fill` with a 1.5 px `Bg` outline at the icon's top right |
| Header | Height 56. Page title in `Title` at rail + 24, centred on y 28. From the right edge inward: 24 px margin, Scan split button, 8, bell icon button, 8, search box 280 px. All 32 px high at y 12 |
| Content area | From rail + 24 to window width − 24 (to 16 px left of the detail drawer while it is open); top at y 72; bottom 24 px above the window bottom, or 16 px above the status strip when it is visible |
| Status strip | Height 36, from the rail to the right edge, `Surface` fill, 1 px `Stroke` top line. A 2 px `Accent` line along its top edge shows progress. Ring r 8 at rail + 32; text from rail + 48; *Pause* and *Stop* compact subtle buttons 16 px from the right. Below 1100 px width the text shortens to files and time left |
| Scrolling | When a page is taller than the content area, the content scrolls vertically; rail, header and status strip do not move |

## Components

Each row gives the full anatomy. Colours follow the state table above unless stated.

### Buttons

| Property | Value |
|----------|-------|
| Height | 32; compact 28 (inside rows, cards, bars) |
| Radius | 4 |
| Padding | 12 left and right |
| Content | Optional 16 px icon, 8 px gap, label, optional 4 px gap and 16 px `chevron-down`; centred as a group |
| Width | Content + padding, rounded to a whole pixel; minimum 64 with a label, 32 without |
| Label | `BodyStrong` for primary and danger, `Body` for secondary and subtle |
| Primary | Fill `Accent` / `AccentHover` / `AccentPressed`; text and icon `OnAccent` |
| Danger | Fill `DangerFill` / `DangerHover` / `DangerPressed`; text `OnDanger` |
| Secondary | Fill `Surface`, border `Stroke`; text `Text` |
| Subtle | No fill or border at rest; text `Text` |
| Split button | Primary colours. Left part: 12, icon 16, 8, label, 12. A 1 px `OnAccent` divider at 35 % opacity, inset 6 px top and bottom. Right part 29 px with a centred `chevron-down`. Each part has its own hover and pressed state |
| Icon button | 32 × 32 (compact 28); icon 16 centred; no fill at rest; tooltip mandatory |
| Icon-button badge | Circle r 7 centred 8 px in from the right and 9 px down from the top, fill `Accent`, 2 px `Bg` outline, count in `CaptionStrong` `OnAccent` |

One primary button per view or dialog at most. In a confirmation that destroys data, the safe button
holds the initial focus.

### Badges, pills, chips

| Component | Anatomy |
|-----------|---------|
| Severity badge | Height 20, radius 10. 6 px, 12 px shape icon, 4 px, tier word in `CaptionStrong`, 8 px. Fill tier `Pill`, text tier `Text` |
| Pill | Height 20, radius 10, 8 px padding, `CaptionStrong`. Neutral: `Text2` on `NeutralPill`. Status pills use a tier's `Text` on its `Pill` |
| Chip | Height 28, radius 14, 12 px padding. Optional 14 px severity shape or 16 px icon then 6 px. Label `Body` (`BodyStrong` when on). Optional count: 6 px gap, `Body` in `Text3`. Off: `Surface` + `Stroke`. Hover: `Hover` + `StrokeStrong`. On: `Selected` + `Accent` border |

### Inputs

| Component | Anatomy |
|-----------|---------|
| Text box | Height 32, radius 4, text inset 10. Fill `Surface` on `Bg`; fill `Bg` when it sits on `Surface` or `Raised`. Border `Stroke`; hover `StrokeStrong`; focused 2 px `Accent` border and a 1 px `Text` caret. Placeholder in `Text3` |
| Search box | Text box with a 16 px `search` icon at +10 and text at +34. While it has text, a 16 px `close` icon 10 px from the right clears it. Filters after 200 ms without typing |
| Number box | Text box, right-aligned digits, Up/Down change by one step, rejects non-digits except a leading minus sign when the range has negative values (the dBFS rule limits). Leaving the box brings the value within its limits, so the box always shows the value in use |
| Path box | Text box followed by an 8 px gap and a secondary *Browse…* button |
| Drop-down | Height 32 (compact 28), padding 12, label `Body`, `chevron-down` 16 px with its left edge 26 px from the right. Colours as a secondary button |
| Drop-down list | Opens 4 px below. `Raised`, `Stroke`, radius 8, shadow, 4 px padding. Items 32 high, radius 4, label at +12. Current item: `Selected`, `Accent` bar, `BodyStrong`, 16 px `check` 14 px from the right. Hover: `Hover` + `StrokeStrong` bar |
| Segmented control | Height 32 (compact 28). Container `Surface` (or `Bg` when inset), `Stroke`, radius 4. Segment width = label + 28. Selected segment: a `Selected` rectangle inset 2 px, radius 3; a 2 px `Accent` underline inset 10 px from the segment's sides, 3 px above the bottom edge; label `BodyStrong` raised 1 px. Others: `Body` in `Text2`. 1 px `Stroke` separators, inset 8 px, between two unselected neighbours |
| Switch | 40 × 20, radius 10. Off: `Surface`, `StrokeStrong` border, knob r 6 at x + 10 in `Text2`. On: `Accent`, knob at x + 30 in `OnAccent`. Hover: knob r 7; off fill `Hover`, on fill `AccentHover` |
| Check box | 18 × 18, radius 3. Off: `Surface` + `StrokeStrong`. On: `Accent` with a 2 px `OnAccent` check `(4.5,9.5) (7.75,12.5) (13.5,5.75)`. Hover: off fill `Hover`, on fill `AccentHover`. Label `Body`, 10 px to the right, clickable |
| Radio button | 18 px. Off: circle r 8.5, `Surface` + `StrokeStrong`. On: `Accent` disc r 9 with an `OnAccent` dot r 4 |
| Option row | For choices that need a description. Height 52–56, radius 4. Off: `Bg` + `Stroke`. On: `Selected` + `Accent` border. Radio at +14; title `BodyStrong` at +46; description `Caption` `Text2` below; optional value `Body`, right-aligned, 14 px from the right |

### Containers and surfaces

| Component | Anatomy |
|-----------|---------|
| Card | `Surface`, 1 px `Stroke`, radius 8, 16 px padding. Title in `CaptionStrong` `Text3`, upper case, at +16, centred 22 px below the top. A card that acts as a button gets `StrokeStrong` on hover (stat tiles also get `Hover` fill) |
| Info bar | Height 40 (36 or 32 in tight places), radius 4, fill tier `Pill`, a 3 px tier `Fill` bar on the left edge, 16 px shape icon at +14, text `Body` `Text` at +40, optional action in `BodyStrong` `Accent` with a 1 px underline, 16 px from the right |
| Dialog | `Raised`, `Stroke`, radius 8, shadow, over the scrim, centred in the window. Padding 24. Title `Subtitle` centred 34 px below the top; body from 60 px. Buttons at the bottom right, 24 px from the edges, 8 px apart, primary on the right. Widths: 400 simple message, 480 confirmation, 560 form. Esc cancels, Enter triggers the focused button, Tab stays inside |
| Menu | `Raised`, `Stroke`, radius 8, shadow, 4 px padding, 280 px wide. Items 32 high, radius 4: icon at +16, label at +44, shortcut in `Caption` `Text3` 16 px from the right. Separator: 9 px high with a 1 px `Stroke` line inset 12 |
| Tooltip | Height 26, radius 4, `Raised`, `Stroke`, shadow, 8 px padding, `Caption` `Text`. Appears after 500 ms, 8 px below the pointer |
| Toast | 360 × 64, radius 8, `Raised`, `Stroke`, shadow, 16 px from the window's bottom-right corner (above the status strip), stacked upward with 8 px gaps, at most three. A 3 × 48 bar in the level's `Fill` on the left; 16 px shape icon at (16, 14); title `BodyStrong` at +44, centred on y 22; text `Caption` `Text2` centred on y 43; optional compact button and a compact `close` icon button at the right |
| Drawer | 440 px wide (drag its left edge between 360 and 640; the width is remembered), from below the header to above the status strip. `Surface`, 1 px `Stroke` on its left and top edges, 20 px padding, scrolls vertically |
| Selection bar | 560 × 48, radius 8, `Raised`, `StrokeStrong` border, shadow, centred horizontally in the list, 12 px above its bottom edge. Count in `BodyStrong` at +16; buttons from +104, 8 px apart; `close` icon button at the right. The list gains 64 px of bottom scroll padding while it shows |
| Empty state | Centred column: icon 48 px (or 32) in `Text3`; headline; one or two sentences in `Body` `Text2`; optional primary button; optional `Caption` `Text3` line |
| Skeleton | Placeholder while rows load: `Hover`-coloured bars, radius 4, 10 px high (first line) and 8 px high at 60 % opacity (second line), with a 16 px circle where the icon will be |
| Scrollbar | Thumb 6 px wide, radius 3, `StrokeStrong` at 60 % opacity, 3 px from the container's right edge and 4 px from its top and bottom. On hover 10 px wide at full opacity. No track, no arrows. Minimum thumb length 32 |
| Breadcrumb | Segments in `Body` `Text2`, last one `Text`, separated by 16 px `chevron-right` in `Text3` with 4 px on each side. Each segment is a link |

### Lists and tables

| Component | Anatomy |
|-----------|---------|
| Two-line row (Issues) | Height 44. Severity shape 16 px at (+16, +14). Line 1 `BodyStrong` at +44, centred on y 13. Line 2 `Body` `Text2`, centred on y 31. Size in `Body` `Text2`, right-aligned 16 px from the right. Folder in `Body` `Text3`, right-aligned 100 px from the right, at most 264 px wide. A 1 px `Stroke` divider at 60 % opacity along the bottom from +44. Hover: the size is replaced by a compact *Details* button and a compact `more` icon button; the folder moves 132 px left |
| Group header | Height 32, `Bg` at 55 % opacity over the list. `chevron-down` (or `chevron-right` when collapsed) at +14, severity shape at +38, label `BodyStrong` at +62, count `Body` `Text3` 8 px after it |
| One-line row (tables, tree) | Height 36 (tree 32). State or severity icon at (+16, +10). Text `Body` centred. Hover: `Hover` and a 3 × 24 `StrokeStrong` bar. Divider as above |
| Table header | Height 32, `Bg` at 55 % opacity, `CaptionStrong` `Text3`, 1 px `Stroke` below. The sorted column shows a 12 px `sort-up` or `sort-down` 4 px after its label. Click to sort, click again to reverse |
| Navigation item | See *Window and shell* |

Lists are virtual: only visible rows are drawn, the row height is fixed, and scrolling allocates
nothing. Multi-select with Ctrl and Shift; Ctrl+A selects all.

### Data graphics

| Component | Anatomy |
|-----------|---------|
| Progress bar | Height 8 (4 in rows), radius half the height, track `Stroke`, fill `Accent`. A non-zero value is never drawn narrower than the height |
| Progress ring | Radius 8 (10 beside a `Subtitle`), 2 px `Accent`, see *Motion* |
| Severity bar | Height 8. One segment per tier that has a count, in the order Critical, High, Medium, Low, OK; widths proportional to counts with a 4 px minimum; 2 px gaps; segment radius 2; tier `Fill` colours. Each segment is clickable and has a tooltip `<tier>: <count>` |
| Donut | Outer radius 40, thickness 10 (compact: 22 and 6). Track `Stroke`. Value arc starts at 12 o'clock, runs clockwise, round ends. `OkFill` for health, `Accent` for protection |
| Sparkline | 1.5 px `Accent` line with round joins; the area beneath filled `Accent` at 14 % opacity; a dot r 2.5 on the last point. No axes |
| Timeline | A 1 px `Stroke` vertical line; each event a circle r 4, `Surface` fill, 1.5 px `StrokeStrong` outline; text `Body`; time `Caption` `Text3` right-aligned |
| Spectrogram | Frame radius 4 with a `Stroke` border. Frequency runs bottom to top, time left to right. Intensity colours, low to high: `#000004`, `#3B0F70`, `#8C2981`, `#DE4968`, `#FE9F6D`, `#FCFDBF` at 0, 25, 50, 75, 90 and 100 % of the range from −120 dB to 0 dB, linearly interpolated. Marker: a 1 px `Accent` dashed line (4 on, 3 off) at the detected cutoff, with a label chip 18 px high, radius 3, `Accent` fill, `CaptionStrong` `OnAccent`, right-aligned 6 px from the edge and 3 px above the line. Axis captions in `Caption` `Text3` beneath |
| Waveform | Same frame. Envelope (minimum to maximum per pixel column) filled `Text3`; centre line `Stroke`; clipped runs marked by 2 px `CritFill` ticks, 6 px tall, at the top edge; dropouts by `HighFill` ticks at the bottom edge |
| Frame strip | Thumbnails 96 × 54, radius 4, 8 px apart, each with a `Caption` score beneath; frames that could not be used are drawn at 40 % opacity |

## Responsive rules

| Condition | Change |
|-----------|--------|
| Window width below 1100 | Rail collapses to 56 px; status strip text shortens |
| Dashboard | Top row: three equal cards, 12 px gaps. Tiles: four equal, 12 px gaps. Activity card is always 340 px wide; recommendations take the rest. In a top-row card narrower than 320 px the first caption wraps to two lines and the second is hidden; the Scans card drops the middle term of each line. The media bar starts at 35 % of the card width (at least 300 px in) and ends 152 px from the right |
| Issues list narrower than 760 | Media-kind chips and the grouping control become drop-downs in the second row (`Kind`, `Group: <value>`); the status drop-down shows only its value |
| Issues list narrower than 720 | The folder column is hidden |
| Library folders | Two columns when the content area is 1000 px wide or more, otherwise one |
| Tools | Three columns at 1000 px or more, otherwise two |
| Tables | Fixed-width columns keep their width; the first text column absorbs the difference |

## Theme service and high contrast

- `ThemeService` resolves the *System / Light / Dark* setting, follows the Windows setting when it
  changes, and raises `ThemeChanged`. Brushes, pens, fonts and paths are cached per theme and DPI in
  `ThemeResources` and disposed on change; painting allocates nothing.
- The title bar follows the theme (`DwmSetWindowAttribute`, attribute 20).
- **High contrast.** When Windows contrast themes are active, a third token set maps to system
  colours: `Bg`, `Surface`, `Raised` → Window; `Text`, `Text2`, `Text3`, `Stroke`, `StrokeStrong` →
  WindowText; `TextDisabled` → GrayText; `Accent`, `Selected` → Highlight; `OnAccent` and text on
  selected items → HighlightText; severity text and fills → WindowText; pills → Window with a 1 px
  WindowText border. Hover shows a 2 px Highlight border instead of a fill. Every container gets a
  1 px border.

## Rendering rules

- A `KitControl` base class sets optimised double buffering, all painting in the paint handler, and
  redraw on resize. No flicker.
- Text is drawn with `TextRenderer` (ClearType) using no padding, no prefix characters and end
  ellipsis. Do not mix in `Graphics.DrawString` or its measurements.
- Shapes use anti-aliasing with half-pixel offset; rounded rectangles come from cached paths.
- Invalidate the smallest rectangle that changed.
- Per-monitor DPI: on a DPI change rebuild cached resources and lay out again. Verified at 100, 125,
  150 and 200 %.
- Hover state is cleared on mouse leave of the parent and on window deactivation.

## Accessibility

- Every control exposes an accessible name and role; lists expose their visible rows with name (file
  name, tier, rule title), role and selection state.
- Tab order follows reading order. Arrow keys move inside a composite control; Tab leaves it.
- Focus is always visible. Nothing is conveyed by colour alone. Tier names are read as "Critical",
  "High", "Medium", "Low".

## Visual QA harness

`MediaVerifier.exe --render-screens <dir>` builds the same demo data the mockups show (subplan 09
lists it), constructs every screen and reference sheet off-screen, and writes PNGs at 100 % and 150 %
in dark, light and high contrast, plus an HTML page that places each render beside its reference from
`docs/design/png/`. After every UI task the implementer opens that page and fixes any difference in
position, size, colour or text. A screen passes when it matches its reference to within 1 px.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| UK-01 | `ThemeTokens` (dark, light, high contrast) exactly as tabled, `ThemeService`, `ThemeResources` | Values equal `tokens.md`; switching theme repaints everything; no GDI handle growth after 200 switches |
| UK-02 | Contrast unit test over the pairs in `tokens.md` | Fails if any text pair is below 4.5:1 or any indicator pair below 3:1 |
| UK-03 | Icon renderer for the primitives in `icons.md`, severity shapes, state icons; app icon generator script and `MediaVerifier.ico` | The rendered icon sheet matches `icons-dark.png` and `icons-light.png` |
| UK-04 | `KitControl`, DPI helper, shadow cache, focus-ring helper | Sheet renders crisp at 100, 125, 150, 200 % |
| UK-05 | Buttons, split button, icon button, badges, pills, chips | Matches the `components` reference in every state |
| UK-06 | Inputs: text, search, number, path, drop-down and list, segmented, switch, check, radio, option row | Matches reference; keyboard operable; tab order correct |
| UK-07 | Virtual list: two-line rows, group headers, one-line rows, table header with sorting, multi-select, hover actions, skeletons, scrollbar | Matches reference; 1,000,000 rows scroll without dropped frames and without allocations per scroll step |
| UK-08 | Tree list and breadcrumb | Lazy children; keyboard expand and collapse |
| UK-09 | Card, info bar, dialog with scrim, menu, tooltip, toast host, drawer, selection bar, empty state | Matches the `dialogs` reference; Esc, Enter and focus rules hold; focus returns to the invoker on close |
| UK-10 | Data graphics: progress bar and ring, severity bar, donut, sparkline, timeline | Matches reference; clickable segments raise events |
| UK-11 | Shell: window, rail (both widths), header, status strip, content scrolling, responsive rules | Matches `dashboard` and `dashboard-narrow` references at their window sizes |
| UK-12 | Accessibility objects for all kit controls | Names and roles present; list rows exposed to UI Automation |
| UK-13 | `--render-screens` harness with side-by-side comparison page | Produces the page in under 10 seconds |
| UK-14 | High-contrast token set | Every control legible and bordered in the Windows contrast themes |

## Exit criteria

- The rendered `components`, `dialogs` and `icons` sheets match their references in dark and light.
- Contrast and GDI-leak tests pass.
- Pages contain no drawing code of their own; they compose kit controls.
- The deviations log lists every unspecified design case met, or none.

## Pitfalls

- `Control.DrawToBitmap` does not capture some native child windows faithfully; keep controls
  custom-painted so the harness sees what the user sees.
- GDI objects leak silently. Always go through `ThemeResources`.
- Segoe UI renders slightly wider in GDI than in a browser; where a label must fit a fixed width, the
  ellipsis rule applies. Do not shrink the font.
- A focus ring that appears on mouse click is noise; track whether focus came from the keyboard.
