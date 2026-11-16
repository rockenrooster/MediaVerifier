# 08 — UI design system

Phase 8. Depends on: phase 1. Can run in parallel with phases 2–7. Unblocks: UI screens.

## Goal

A small, token-driven, owner-drawn UI kit for WinForms that looks at home on Windows 11, stays fast
with a million rows, is fully usable by keyboard, and makes every interactive state obvious. Pages in
subplan 09 are assembled only from this kit.

## Principles

1. **Status first.** The user should learn the state of the library in one glance, and the state of a
   file in one row.
2. **Calm by default, loud when it matters.** Neutral surfaces; colour is reserved for severity,
   the primary action and focus.
3. **Dense but readable.** 13 px body text, 36 px rows, 4 px grid. No decorative chrome.
4. **Every state is visible.** Rest, hover, pressed, focused, selected and disabled are distinct, and
   hover and focus use a 3:1 or better contrast change, never a faint tint alone.
5. **Never colour alone.** Severity is always icon + label + colour.
6. **Instant.** Hover and press respond in the same frame. No animation gates an action.

## Tokens

All colours, sizes and fonts come from `ThemeTokens`. No control contains a literal colour.

### Colour

| Token | Dark | Light | Use |
|-------|------|-------|-----|
| `Bg` | `#1F1F1F` | `#F6F6F6` | Window background |
| `Surface` | `#2A2A2A` | `#FFFFFF` | Cards, lists, inputs |
| `Raised` | `#333333` | `#FFFFFF` | Menus, dialogs, drawers (with stroke) |
| `Hover` | `#3A3A3A` | `#EBEBEB` | Hover fill |
| `Pressed` | `#474747` | `#DCDCDC` | Pressed fill |
| `Selected` | `#243C4D` | `#D6E8F8` | Selected row or nav item |
| `Stroke` | `#454545` | `#D0D0D0` | Hairlines, card borders |
| `StrokeStrong` | `#8F8F8F` | `#6E6E6E` | Hover borders, input borders, separators that must be seen |
| `Text` | `#F5F5F5` | `#1A1A1A` | Primary text |
| `Text2` | `#CECECE` | `#404040` | Secondary text |
| `Text3` | `#B0B0B0` | `#595959` | Captions, placeholders |
| `TextDisabled` | `#7A7A7A` | `#9A9A9A` | Disabled text |
| `Accent` | `#60CDFF` | `#005FB8` | Primary button, focus ring, selection indicator, links |
| `AccentHover` | `#8AD9FF` | `#0A70CF` | |
| `AccentPressed` | `#3FB4E8` | `#004A91` | |
| `OnAccent` | `#00131F` | `#FFFFFF` | Text on accent fills |

Severity, three tokens each: `Text` for labels and icons on neutral surfaces, `Fill` for chart
segments and bars, `PillBg` for the opaque badge background.

| Severity | Dark text / fill / pill | Light text / fill / pill | Icon shape |
|----------|-------------------------|--------------------------|------------|
| Critical | `#FF8585` / `#E5484D` / `#4C2F30` | `#B91C22` / `#D13438` / `#F9E7E7` | Octagon with × |
| High | `#FFA657` / `#F76B15` / `#4F3626` | `#A54300` / `#D45A00` / `#FAEBE0` | Triangle with ! |
| Medium | `#FFD23F` / `#FFB224` / `#504229` | `#735400` / `#A37A00` / `#F6F1E0` | Diamond with ! |
| Low | `#85BDFF` / `#3E8BFF` / `#2E3B50` | `#1A56B0` / `#2B6FD6` / `#E6EEFA` | Circle with i |
| OK | `#5AD694` / `#30A46C` / `#2B4036` | `#146C3E` / `#1F8A52` / `#E4F1EA` | Circle with ✓ |

These values were checked for WCAG contrast: body text pairs are 4.5:1 or better on `Bg`, `Surface`,
`Raised`, `Hover` and `Selected`; severity text on its pill is 5:1 or better; fills and
`StrokeStrong` are 3:1 or better against `Bg` and `Surface`. Task UK-02 turns that check into a unit
test so the palette cannot regress. If a value is changed, the test decides.

### Type

Font family: `Segoe UI Variable Text` when installed (Windows 11), else `Segoe UI`.

| Token | Size | Weight | Use |
|-------|------|--------|-----|
| `Caption` | 8.25 pt (11 px) | Regular | Column headers, timestamps, badges |
| `Body` | 9.75 pt (13 px) | Regular | Default |
| `BodyStrong` | 9.75 pt | Semibold | File names, emphasised values |
| `Subtitle` | 12 pt (16 px) | Semibold | Card and section titles |
| `Title` | 15 pt (20 px) | Semibold | Page titles |
| `Metric` | 21 pt (28 px) | Semibold | Dashboard numbers |
| `Mono` | 9 pt, `Cascadia Mono` else `Consolas` | Regular | Hashes, paths in technical details, logs |

Numbers in tables use tabular alignment (right-aligned, same font) and thousands separators from the
current culture. File sizes use binary units with one decimal (`1.4 GB`).

### Space, shape, size

- Grid: 4 px. Spacing tokens: 4, 8, 12, 16, 24, 32.
- Radius: 4 px for controls, 8 px for cards and dialogs, fully round for pills.
- Stroke: 1 px. Focus ring: 2 px `Accent`, 1 px gap from the control.
- Heights: row 36 px (compact 28 px), nav item 40 px, button and input 32 px, chip 28 px, badge 20 px.
- Minimum hit target 28 × 28 px.
- Page padding 24 px; card padding 16 px; gap between cards 12 px.
- Everything is specified in logical pixels at 96 DPI and scaled by `DeviceDpi / 96`.

### Interaction states

Apply to every interactive control. A control that cannot show all of these is not finished.

| State | Treatment |
|-------|-----------|
| Rest | Token fill, `Stroke` border where bordered |
| Hover | Fill `Hover` **and** border `StrokeStrong` (or, for borderless items such as rows and nav entries, fill `Hover` plus a 3 px `StrokeStrong` bar at the leading edge). Cursor is a hand only on links |
| Pressed | Fill `Pressed`, border `Accent`, content shifted down 1 px |
| Focused (keyboard) | 2 px `Accent` ring. Shown only for keyboard focus, never suppressed |
| Selected | Fill `Selected`, 3 px `Accent` bar at the leading edge, `BodyStrong` text |
| Selected + hover | `Selected` fill, `StrokeStrong` border |
| Disabled | `TextDisabled`, no hover or press response, no hand cursor |
| Busy | Control disabled with an inline 16 px progress ring replacing its icon |

Primary buttons use `Accent` / `AccentHover` / `AccentPressed` fills with `OnAccent` text; pressed
also shifts content 1 px. Danger buttons use the Critical fill with white or `OnAccent`-equivalent
text that passes 4.5:1 (test it).

### Motion

Motion is optional polish and limited to: indeterminate progress, the detail drawer sliding in
(120 ms), toast fade (150 ms). Disabled entirely when Windows' *Animation effects* setting is off
(`SPI_GETCLIENTAREAANIMATION`). Hover, press, selection and page switches are immediate.

### Iconography

Icons are glyphs from `Segoe Fluent Icons` (Windows 11) with `Segoe MDL2 Assets` as the fallback
(Windows 10; the code points are shared). Drawn with `TextRenderer` at 16 px in the current text or
severity colour. One static class `Glyphs` holds the code points. Starting set (verify each renders
in both fonts before use; replace any that does not):

Home `E80F`, Search `E721`, Filter `E71C`, Refresh `E72C`, Settings `E713`, Folder `E8B7`,
Picture `E8B9`, Video `E714`, Audio `E8D6`, Library `E8F1`, Calendar `E787`, History `E81C`,
Repair `E90F`, Shield `E83D`, Warning `E7BA`, Error `E783`, Info `E946`, CheckMark `E73E`,
Play `E768`, Pause `E769`, Stop `E71A`, Cancel `E711`, Add `E710`, Delete `E74D`, Edit `E70F`,
Copy `E8C8`, Download `E896`, More `E712`, ChevronRight `E76C`, ChevronDown `E70D`, OpenFile `E8E5`.

Severity icons are drawn as vector shapes (octagon, triangle, diamond, circle), not font glyphs, so
their shape differs even at 12 px.

## Theme service

- `ThemeService` resolves `System` / `Light` / `Dark`, listens for the system setting changing
  (`SystemEvents.UserPreferenceChanged`), and raises `ThemeChanged`.
- **High contrast:** when `SystemInformation.HighContrast` is true, use a third token set mapped to
  `SystemColors` (`Window`, `WindowText`, `Highlight`, `HighlightText`, `GrayText`, `ControlText`)
  and draw 1 px borders on everything.
- Resources (`Brush`, `Pen`, `Font`, `GraphicsPath`) are cached per theme and DPI in
  `ThemeResources` and disposed on change. Paint handlers allocate nothing.
- Title bar follows the theme through `DwmSetWindowAttribute` (attribute 20).
- Native controls that remain (text box internals, tooltips, system menus) are darkened with
  `Application.SetColorMode` where the installed SDK supports it without experimental warnings;
  otherwise `SetWindowTheme(handle, "DarkMode_Explorer", null)`. Confine any suppression of an
  experimental diagnostic to that single call site.

## Rendering rules

- Base class `KitControl : Control` sets `OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint |
  ResizeRedraw`. No flicker on resize.
- Text: `TextRenderer.DrawText` with `NoPadding | NoPrefix | EndEllipsis` (ClearType). Never
  `Graphics.DrawString`.
- Shapes: GDI+ with `SmoothingMode.AntiAlias` and `PixelOffsetMode.Half`; rounded rectangles from
  cached paths.
- Invalidate the smallest rectangle (the row or the control), never the whole page.
- Truncated text always has a tooltip with the full value.
- Per-monitor DPI: handle `DpiChanged`, rebuild cached resources, re-layout. Test at 100 %, 125 %,
  150 %, 200 % and when dragging between monitors.
- Right-to-left is out of scope for v1, but do not hard-code left/right in layout helpers; use
  leading/trailing.

## Accessibility

- Every control sets `AccessibleName`, `AccessibleRole` and, where relevant, `AccessibleDescription`.
- `VirtualList` exposes its visible rows as accessible children with name (file name, severity, rule
  title), role `ListItem`, and selection state.
- Tab order follows visual order. Arrow keys move within a composite control; Tab leaves it.
- Minimum contrast as stated under tokens. Focus is always visible.
- Screen-reader text for severity: "Critical", "High", "Medium", "Low"; never colour names.

## Component catalogue

Each component lives in `UI/Kit`, is driven only by tokens, supports all interaction states, keyboard
operation and accessibility, and is rendered by the screen harness.

| Component | Notes |
|-----------|-------|
| `NavRail` | 200 px expanded, 48 px collapsed (below 1000 px window width or by toggle). Items: icon + label, optional count badge. Bottom-aligned items for Settings and the update indicator |
| `Button` | Variants: Primary, Secondary, Subtle (no border at rest), Danger. Optional leading icon. Default and cancel behaviour for dialogs. Sizes 32 and 28 px |
| `IconButton` | 32 × 32, tooltip mandatory |
| `SplitButton` | Primary action plus menu (used for *Scan* with depth choice, *Fix* with alternatives) |
| `ToggleSwitch`, `CheckBox`, `RadioButton` | Label clickable; Space toggles |
| `SegmentedControl` | 2–5 mutually exclusive options (depth, theme, grouping) |
| `Chip` | Toggle filter with optional count and severity colouring; `Delete` clears when focused |
| `SeverityBadge` | Pill: shape icon + label on `PillBg`. Compact variant is the icon alone with a tooltip |
| `TextBox`, `SearchBox`, `NumberBox`, `PathBox` | Borderless native edit hosted in a kit frame so borders and states are consistent. `SearchBox` has a clear button and debounces 200 ms. `PathBox` has browse and validates existence |
| `DropDown` | Kit-drawn button plus popup list; type-ahead |
| `ProgressBar`, `ProgressRing` | Determinate and indeterminate; bar can be segmented by severity |
| `Card`, `SectionHeader`, `Divider` | Layout primitives |
| `InfoBar` | Inline message with severity icon, text, optional action and dismiss |
| `ToastHost` | In-app transient messages, bottom-right, stack of three, 5 s, pause on hover, with *Undo* where applicable |
| `Dialog` | Modal, kit-drawn content, Esc cancels, Enter confirms the default, focus trapped |
| `Drawer` | Right-side detail panel, resizable (320–640 px), remembers width, Esc closes |
| `Tooltip`, `ContextMenu` | Themed. Context menus mirror the row's action buttons and show shortcuts |
| `VirtualList` | Port and extend FastTaskMgr's `VirtualTable`: virtual row source, fixed row height, optional columns with sort indicators and resizing, group headers with counts and collapse, multi-select (Ctrl, Shift, Ctrl+A), keyboard navigation, type-ahead, hover row actions, skeleton rows while data loads, thin auto-hiding scrollbar. No per-row allocations during scroll |
| `TreeList` | Folder tree with roll-up badges, lazy children |
| `Breadcrumb` | Path segments, each clickable, overflow menu when narrow |
| `EmptyState` | Icon, headline, one sentence, primary action |
| `StatTile` | Label, big number, delta, optional sparkline; clickable |
| `SeverityBar` | Horizontal stacked bar of counts by tier with a legend; segments clickable |
| `Donut` | Healthy-percentage ring |
| `Sparkline` | Trend of open findings per run |
| `SpectrogramView` | Renders a precomputed magnitude image with a frequency axis, a marker line at the detected cutoff and the Nyquist limit (subplan 10) |
| `WaveformView` | Min/max envelope with clipped regions marked (subplan 10) |
| `FrameStrip` | Sampled video frames with per-frame scores (subplan 11) |
| `Timeline` | Events for a file or run, newest first |

## Visual QA harness

`MediaVerifier.exe --render-screens <dir>`:

1. Builds an in-memory demo library with the synthetic generator from task S-10 (fixed seed).
2. Constructs each page and each kit component gallery page off-screen.
3. Renders PNGs at 1280 × 800 for every combination of theme (dark, light, high contrast) and scale
   (100 %, 150 %) with `Control.DrawToBitmap`.
4. Writes an `index.html` contact sheet.

The implementing agent must run this after every UI task and look at the images: check alignment to
the 4 px grid, clipped or overlapping text, state visibility and consistency with the tokens. Commit
three representative screenshots to `docs/screens/` for the README at the end of phase 9.

## Tasks

| ID | Task | Acceptance |
|----|------|------------|
| UK-01 | `ThemeTokens` (dark, light, high contrast), `ThemeService`, `ThemeResources` cache | Switching theme at runtime repaints everything; no GDI handle growth after 200 switches (assert via `GetGuiResources`) |
| UK-02 | Contrast unit test over the token pairs listed above | Fails if any text pair is below 4.5:1 or any indicator pair below 3:1 |
| UK-03 | `KitControl` base, DPI helper, glyph and shape-icon helpers | Gallery renders crisp at all four scales |
| UK-04 | Buttons, toggles, segmented control, chips, badges | Gallery shows every variant in every state; keyboard operable |
| UK-05 | Inputs: text, search, number, path, drop-down | Tab order, validation states, clear button, type-ahead |
| UK-06 | `VirtualList` with groups, multi-select, columns, hover actions, skeletons, scrollbar | 1,000,000-row source scrolls without dropped frames; zero allocations per scroll step (measure with an allocation counter in a test) |
| UK-07 | `TreeList`, `Breadcrumb` | Lazy load, keyboard expand/collapse |
| UK-08 | `Card`, `InfoBar`, `ToastHost`, `Dialog`, `Drawer`, `Tooltip`, `ContextMenu`, `EmptyState` | Esc/Enter behaviour, focus trap, focus returns to the invoker on close |
| UK-09 | Data visuals: `StatTile`, `SeverityBar`, `Donut`, `Sparkline`, `Timeline` | Render from sample data; clickable segments raise events |
| UK-10 | Accessibility objects for all kit controls | Inspect with Accessibility Insights or a UI Automation test: names and roles present; list rows exposed |
| UK-11 | `--render-screens` harness and component gallery | Produces the contact sheet in under 10 seconds |
| UK-12 | High-contrast theme pass | Every control legible and bordered in the Windows contrast themes |

## Exit criteria

- The gallery shows every component in every state in dark, light and high contrast.
- Contrast test and GDI-leak test pass.
- No page code draws directly; pages compose kit controls.

## Pitfalls

- `Control.DrawToBitmap` does not capture some native child windows faithfully; another reason to keep
  controls owner-drawn.
- GDI objects leak silently. Always go through `ThemeResources`; watch the GDI handle count while
  developing.
- `TextRenderer` measures and draws with GDI; do not mix with `Graphics.MeasureString`.
- Mouse-leave is not delivered when the pointer exits the window quickly; reset hover state on
  `MouseLeave` of the parent and on deactivate.
- A focus ring that appears on mouse click is noise; track whether focus came from the keyboard.
