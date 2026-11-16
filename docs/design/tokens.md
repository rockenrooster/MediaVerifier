# Design tokens

Generated from the design source. These values are the specification; the implementation holds them in one ThemeTokens class and nothing else contains a literal colour. Contrast ratios follow WCAG 2.1.

## Colour

| Token | Dark | Light |
| --- | --- | --- |
| Bg | `#1F1F1F` | `#F6F6F6` |
| Surface | `#2A2A2A` | `#FFFFFF` |
| Raised | `#333333` | `#FFFFFF` |
| Hover | `#3A3A3A` | `#EBEBEB` |
| Pressed | `#474747` | `#DCDCDC` |
| Selected | `#243C4D` | `#D6E8F8` |
| Stroke | `#454545` | `#D0D0D0` |
| StrokeStrong | `#8F8F8F` | `#6E6E6E` |
| Text | `#F5F5F5` | `#1A1A1A` |
| Text2 | `#CECECE` | `#404040` |
| Text3 | `#B0B0B0` | `#595959` |
| TextDisabled | `#7A7A7A` | `#9A9A9A` |
| Accent | `#60CDFF` | `#005FB8` |
| AccentHover | `#8AD9FF` | `#0A70CF` |
| AccentPressed | `#3FB4E8` | `#004A91` |
| OnAccent | `#00131F` | `#FFFFFF` |
| DangerFill | `#C93237` | `#C4252B` |
| DangerHover | `#D13C41` | `#D13438` |
| DangerPressed | `#A8262B` | `#A61D22` |
| OnDanger | `#FFFFFF` | `#FFFFFF` |
| CritText | `#FF8585` | `#B91C22` |
| CritFill | `#E5484D` | `#D13438` |
| CritPill | `#4C2F30` | `#F9E7E7` |
| HighText | `#FFA657` | `#A54300` |
| HighFill | `#F76B15` | `#D45A00` |
| HighPill | `#4F3626` | `#FAEBE0` |
| MedText | `#FFD23F` | `#735400` |
| MedFill | `#FFB224` | `#A37A00` |
| MedPill | `#504229` | `#F6F1E0` |
| LowText | `#85BDFF` | `#1A56B0` |
| LowFill | `#3E8BFF` | `#2B6FD6` |
| LowPill | `#2E3B50` | `#E6EEFA` |
| OkText | `#5AD694` | `#146C3E` |
| OkFill | `#30A46C` | `#1F8A52` |
| OkPill | `#2B4036` | `#E4F1EA` |
| NeutralPill | `#3A3A3A` | `#EBEBEB` |
| Scrim | `rgba(0,0,0,0.50)` | `rgba(0,0,0,0.30)` |
| Shadow | `rgba(0,0,0,0.50)` | `rgba(0,0,0,0.22)` |

## Type

| Style | Size (px) | Weight | Family |
| --- | --- | --- | --- |
| caption | 11 | 400 | Segoe UI |
| captionStrong | 11 | 600 | Segoe UI |
| body | 13 | 400 | Segoe UI |
| bodyStrong | 13 | 600 | Segoe UI |
| subtitle | 16 | 600 | Segoe UI |
| title | 20 | 600 | Segoe UI |
| metric | 28 | 600 | Segoe UI |
| mono | 12 | 400 | Consolas |

## Contrast

### Dark

#### Text (required 4.5)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| Text | Bg | 15.12 | 4.5 | Pass |
| Text | Surface | 13.17 | 4.5 | Pass |
| Text | Raised | 11.59 | 4.5 | Pass |
| Text | Hover | 10.43 | 4.5 | Pass |
| Text | Selected | 10.53 | 4.5 | Pass |
| Text2 | Bg | 10.47 | 4.5 | Pass |
| Text2 | Surface | 9.12 | 4.5 | Pass |
| Text2 | Raised | 8.03 | 4.5 | Pass |
| Text2 | Hover | 7.23 | 4.5 | Pass |
| Text2 | Selected | 7.30 | 4.5 | Pass |
| Text3 | Bg | 7.60 | 4.5 | Pass |
| Text3 | Surface | 6.62 | 4.5 | Pass |
| Text3 | Raised | 5.83 | 4.5 | Pass |
| Text3 | Hover | 5.24 | 4.5 | Pass |
| Text3 | Selected | 5.30 | 4.5 | Pass |
| Accent | Bg | 9.16 | 4.5 | Pass |
| Accent | Surface | 7.98 | 4.5 | Pass |
| Accent | Raised | 7.02 | 4.5 | Pass |
| Accent | Hover | 6.32 | 4.5 | Pass |
| Accent | Selected | 6.38 | 4.5 | Pass |
| CritText | Bg | 7.02 | 4.5 | Pass |
| CritText | Surface | 6.11 | 4.5 | Pass |
| CritText | Raised | 5.38 | 4.5 | Pass |
| CritText | Hover | 4.85 | 4.5 | Pass |
| CritText | Selected | 4.89 | 4.5 | Pass |
| HighText | Bg | 8.51 | 4.5 | Pass |
| HighText | Surface | 7.41 | 4.5 | Pass |
| HighText | Raised | 6.52 | 4.5 | Pass |
| HighText | Hover | 5.87 | 4.5 | Pass |
| HighText | Selected | 5.93 | 4.5 | Pass |
| MedText | Bg | 11.41 | 4.5 | Pass |
| MedText | Surface | 9.94 | 4.5 | Pass |
| MedText | Raised | 8.75 | 4.5 | Pass |
| MedText | Hover | 7.88 | 4.5 | Pass |
| MedText | Selected | 7.95 | 4.5 | Pass |
| LowText | Bg | 8.41 | 4.5 | Pass |
| LowText | Surface | 7.33 | 4.5 | Pass |
| LowText | Raised | 6.45 | 4.5 | Pass |
| LowText | Hover | 5.81 | 4.5 | Pass |
| LowText | Selected | 5.86 | 4.5 | Pass |
| OkText | Bg | 9.01 | 4.5 | Pass |
| OkText | Surface | 7.85 | 4.5 | Pass |
| OkText | Raised | 6.91 | 4.5 | Pass |
| OkText | Hover | 6.22 | 4.5 | Pass |
| OkText | Selected | 6.28 | 4.5 | Pass |

#### Pills (required 4.5)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| CritText | CritPill | 5.09 | 4.5 | Pass |
| HighText | HighPill | 5.74 | 4.5 | Pass |
| MedText | MedPill | 6.76 | 4.5 | Pass |
| LowText | LowPill | 5.77 | 4.5 | Pass |
| OkText | OkPill | 6.08 | 4.5 | Pass |
| Text2 | NeutralPill | 7.23 | 4.5 | Pass |

#### Buttons (required 4.5)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| OnAccent | Accent | 10.49 | 4.5 | Pass |
| OnAccent | AccentHover | 12.08 | 4.5 | Pass |
| OnAccent | AccentPressed | 8.00 | 4.5 | Pass |
| OnDanger | DangerFill | 5.26 | 4.5 | Pass |
| OnDanger | DangerHover | 4.74 | 4.5 | Pass |
| OnDanger | DangerPressed | 7.05 | 4.5 | Pass |

#### Indicators and fills (required 3.0)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| StrokeStrong | Bg | 5.10 | 3.0 | Pass |
| StrokeStrong | Surface | 4.44 | 3.0 | Pass |
| Accent | Bg | 9.16 | 3.0 | Pass |
| Accent | Surface | 7.98 | 3.0 | Pass |
| CritFill | Bg | 4.21 | 3.0 | Pass |
| CritFill | Surface | 3.67 | 3.0 | Pass |
| HighFill | Bg | 5.55 | 3.0 | Pass |
| HighFill | Surface | 4.83 | 3.0 | Pass |
| MedFill | Bg | 9.14 | 3.0 | Pass |
| MedFill | Surface | 7.96 | 3.0 | Pass |
| LowFill | Bg | 4.98 | 3.0 | Pass |
| LowFill | Surface | 4.33 | 3.0 | Pass |
| OkFill | Bg | 5.22 | 3.0 | Pass |
| OkFill | Surface | 4.55 | 3.0 | Pass |

### Light

#### Text (required 4.5)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| Text | Bg | 16.10 | 4.5 | Pass |
| Text | Surface | 17.40 | 4.5 | Pass |
| Text | Raised | 17.40 | 4.5 | Pass |
| Text | Hover | 14.60 | 4.5 | Pass |
| Text | Selected | 13.89 | 4.5 | Pass |
| Text2 | Bg | 9.59 | 4.5 | Pass |
| Text2 | Surface | 10.37 | 4.5 | Pass |
| Text2 | Raised | 10.37 | 4.5 | Pass |
| Text2 | Hover | 8.70 | 4.5 | Pass |
| Text2 | Selected | 8.27 | 4.5 | Pass |
| Text3 | Bg | 6.48 | 4.5 | Pass |
| Text3 | Surface | 7.00 | 4.5 | Pass |
| Text3 | Raised | 7.00 | 4.5 | Pass |
| Text3 | Hover | 5.88 | 4.5 | Pass |
| Text3 | Selected | 5.59 | 4.5 | Pass |
| Accent | Bg | 5.84 | 4.5 | Pass |
| Accent | Surface | 6.31 | 4.5 | Pass |
| Accent | Raised | 6.31 | 4.5 | Pass |
| Accent | Hover | 5.29 | 4.5 | Pass |
| Accent | Selected | 5.03 | 4.5 | Pass |
| CritText | Bg | 5.98 | 4.5 | Pass |
| CritText | Surface | 6.46 | 4.5 | Pass |
| CritText | Raised | 6.46 | 4.5 | Pass |
| CritText | Hover | 5.42 | 4.5 | Pass |
| CritText | Selected | 5.15 | 4.5 | Pass |
| HighText | Bg | 5.71 | 4.5 | Pass |
| HighText | Surface | 6.17 | 4.5 | Pass |
| HighText | Raised | 6.17 | 4.5 | Pass |
| HighText | Hover | 5.18 | 4.5 | Pass |
| HighText | Selected | 4.92 | 4.5 | Pass |
| MedText | Bg | 6.48 | 4.5 | Pass |
| MedText | Surface | 7.01 | 4.5 | Pass |
| MedText | Raised | 7.01 | 4.5 | Pass |
| MedText | Hover | 5.88 | 4.5 | Pass |
| MedText | Selected | 5.59 | 4.5 | Pass |
| LowText | Bg | 6.47 | 4.5 | Pass |
| LowText | Surface | 7.00 | 4.5 | Pass |
| LowText | Raised | 7.00 | 4.5 | Pass |
| LowText | Hover | 5.87 | 4.5 | Pass |
| LowText | Selected | 5.58 | 4.5 | Pass |
| OkText | Bg | 5.99 | 4.5 | Pass |
| OkText | Surface | 6.47 | 4.5 | Pass |
| OkText | Raised | 6.47 | 4.5 | Pass |
| OkText | Hover | 5.43 | 4.5 | Pass |
| OkText | Selected | 5.17 | 4.5 | Pass |

#### Pills (required 4.5)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| CritText | CritPill | 5.42 | 4.5 | Pass |
| HighText | HighPill | 5.30 | 4.5 | Pass |
| MedText | MedPill | 6.20 | 4.5 | Pass |
| LowText | LowPill | 5.99 | 4.5 | Pass |
| OkText | OkPill | 5.57 | 4.5 | Pass |
| Text2 | NeutralPill | 8.70 | 4.5 | Pass |

#### Buttons (required 4.5)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| OnAccent | Accent | 6.31 | 4.5 | Pass |
| OnAccent | AccentHover | 4.96 | 4.5 | Pass |
| OnAccent | AccentPressed | 8.79 | 4.5 | Pass |
| OnDanger | DangerFill | 5.76 | 4.5 | Pass |
| OnDanger | DangerHover | 4.93 | 4.5 | Pass |
| OnDanger | DangerPressed | 7.45 | 4.5 | Pass |

#### Indicators and fills (required 3.0)

| Foreground | Background | Ratio | Required | Result |
| --- | --- | --- | --- | --- |
| StrokeStrong | Bg | 4.72 | 3.0 | Pass |
| StrokeStrong | Surface | 5.10 | 3.0 | Pass |
| Accent | Bg | 5.84 | 3.0 | Pass |
| Accent | Surface | 6.31 | 3.0 | Pass |
| CritFill | Bg | 4.56 | 3.0 | Pass |
| CritFill | Surface | 4.93 | 3.0 | Pass |
| HighFill | Bg | 3.69 | 3.0 | Pass |
| HighFill | Surface | 3.99 | 3.0 | Pass |
| MedFill | Bg | 3.64 | 3.0 | Pass |
| MedFill | Surface | 3.93 | 3.0 | Pass |
| LowFill | Bg | 4.47 | 3.0 | Pass |
| LowFill | Surface | 4.83 | 3.0 | Pass |
| OkFill | Bg | 4.04 | 3.0 | Pass |
| OkFill | Surface | 4.36 | 3.0 | Pass |

## Failures

None.
