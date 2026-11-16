# MediaVerifier

MediaVerifier is a planned Windows app that checks photo, video and music libraries for corruption,
bit rot and fake quality, ranks what it finds, and helps fix it.

**Status: planning.** This repository currently contains the implementation plans only. There is no
source code and nothing to download yet.

## What it will do

- **Find damage.** Truncated files, broken containers, decoder errors, checksum mismatches and
  unreadable sectors, in photos, video and audio.
- **Find silent change (bit rot).** Every file gets a fingerprint when first seen and is re-read on
  a schedule. A file whose bytes changed without being modified is flagged, and a healthy copy in a
  backup folder can be restored.
- **Find fakes and quality problems.** Lossless audio made from MP3, upsampled or padded "hi-res"
  audio, video and photos enlarged from a lower resolution, bitrates that do not match the quality,
  starved encodes, clipping and other audio distortion.
- **Rank everything** as Critical, High, Medium or Low, with a confidence level and a plain-language
  explanation of what it means and what to do.
- **Recommend and repair.** Built-in tools that never modify a file in place: restore from a healthy
  copy, rebuild a container or index, fix a wrong extension, salvage a playable copy, and more, with
  a journal and undo.
- **Run on a schedule** through Windows Task Scheduler with no background process, and notify only
  when something new needs attention.
- **Stay small and fast.** One small executable, a clean owner-drawn interface, full keyboard
  support, and a signed built-in updater.

It will be honest about limits. Decoding cannot catch every kind of damage (a single flipped byte in
a JPEG usually decodes without complaint), which is why fingerprints matter; and no method reliably
detects AI upscaling.

## Plans

Start at [plans/README.md](plans/README.md). The master plan is
[plans/00-master-plan.md](plans/00-master-plan.md).

| Area | Plan |
|------|------|
| Scope, decisions, architecture, phases | [00](plans/00-master-plan.md) |
| App shell and foundation | [01](plans/01-foundation.md) |
| Severity tiers and the full rule catalog | [02](plans/02-findings-model.md) |
| Storage | [03](plans/03-storage.md) |
| FFmpeg integration | [04](plans/04-ffmpeg-toolchain.md) |
| Scan engine | [05](plans/05-scan-engine.md) |
| Integrity and bit rot | [06](plans/06-integrity-and-rot.md) |
| Corruption detectors | [07](plans/07-corruption-detectors.md) |
| UI design system and screens | [08](plans/08-ui-design-system.md), [09](plans/09-ui-screens.md) |
| Audio quality and authenticity | [10](plans/10-audio-quality-detectors.md) |
| Video and photo quality | [11](plans/11-video-photo-quality-detectors.md) |
| Recommendations and repair tools | [12](plans/12-remediation-and-recommendations.md) |
| Scheduling and notifications | [13](plans/13-scheduling-and-notifications.md) |
| Updater and release pipeline | [14](plans/14-updater-and-release.md) |
| Testing, fixtures, calibration | [15](plans/15-testing-fixtures-calibration.md) |
| Hardening, release criteria, backlog | [16](plans/16-hardening-and-backlog.md) |
| Verified FFmpeg behaviour, audio prototype | [A](plans/appendix-a-ffmpeg-verified-behavior.md), [B](plans/appendix-b-audio-prototype.md) |

The detection methods in the plans were checked with throwaway experiments before being written
down; the commands and their real output are in the appendices.

## Technology

C# on .NET 10 with WinForms for Windows 10 and 11 (x64). Media is decoded by external FFmpeg
processes, never in-process, so a corrupt file cannot crash the app. Storage is SQLite. The updater
follows the signed-manifest flow in [docs/updater.md](docs/updater.md).

## For implementers

Agents and developers: read [AGENTS.md](AGENTS.md) first.

## Privacy

No telemetry. Planned network use is limited to update checks against GitHub Releases, an optional
FFmpeg download that the user starts, and optional user-configured webhooks.

## License

No license file is currently included.
