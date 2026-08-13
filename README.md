# DCC by NV-UV

**Dynamic Clock Capping for NVIDIA GeForce GPUs.**

This repository currently hosts the experimental **0.1.0-alpha.11 binary-only
Community Preview**. The source code and normal repository development are
planned for the following week under MPL-2.0. DCC is also planned as an
integrated feature in the next NV-UV release.

![DCC by NV-UV alpha.11 Mini View](assets/dcc-alpha11.png)

## What it does

DCC lets the user choose target FPS instead of a GPU clock. It applies a
per-game NVIDIA frame limit and its built-in controller learns the lowest
driver-supported graphics-clock bin that still sustains that target. If a
heavier scene needs more performance, DCC restores clock headroom quickly and
later re-tests lower bins cautiously.

- automatic primary NVIDIA GPU selection;
- game matching from profiles installed with the NVIDIA driver;
- own Windows ETW FPS collector without overlay or DLL injection;
- targets from 30 to 400 FPS;
- learning `DCC algorithm` and optional `NVIDIA Ultra Efficiency` method;
- restoration of DCC-owned clock and frame-limiter changes;
- activation history, learned profiles, diagnostics, tray, and emergency reset;
- no RTSS, MSI Afterburner, MAHM, cloud service, or AI model required.

DCC is not undervolting and does not edit the V/F curve.

## Two clearly different methods

- **DCC algorithm** is NV-UV's own adaptive controller. It learns the lowest
  sufficient clock, restores performance quickly for heavier scenes, and later
  re-tests lower clocks cautiously.
- **NVIDIA Ultra Efficiency** calls NVIDIA's internal, undocumented
  JPAC/Max-Perf-per-Watt algorithm while DCC supplies the target FPS and per-game
  frame limit. “Ultra Efficiency” is DCC's descriptive UI name, not an official
  NVIDIA product name. Driver support is not guaranteed.

## Best use cases and clean testing

DCC is most useful when a game has substantial unused GPU performance at the
chosen FPS target: older or lightweight games, indie titles, ARPGs, strategy
games, emulators, capped esports titles, and CPU-limited games. A fully
GPU-bound game that barely reaches its target has little headroom to remove.

For the first test, reset UV/OC to stock and fully close **MSI Afterburner** or
any other utility that can change clocks, voltage, V/F curves, or power limits.
**RTSS may remain running**; DCC does not use its FPS hook. Avoid a competing
RTSS frame limit so only one limiter controls the comparison. Once a stock run
works, experienced users may separately re-test a known-stable UV profile.

## Download the alpha

Use the latest GitHub **Pre-release** and read its included README, Community
Preview terms, safety notes, and SHA-256 before testing:

https://github.com/christianp403-spec/nv-uv-dcc/releases/tag/v0.1.0-alpha.11

This first archive is a binary Community Preview, not an open-source release.
Permission and warranty terms are in `COMMUNITY-PREVIEW-TERMS.txt` within the
download and this repository.

- [Community Preview terms](COMMUNITY-PREVIEW-TERMS.txt)
- [Alpha.11 preview notes](PREVIEW-NOTES-0.1.0-alpha.11.md)
- [Detailed Grim Dawn test notes](GRIM-DAWN-RTX5070TI-ALPHA11-TEST.md)

## Preliminary Grim Dawn result

On an RTX 5070 Ti at 2560 × 1440 and a 120-FPS target, the existing Eco/UV
configuration was observed around 89–92 W at roughly 2 GHz. With DCC active,
the tested scenes held 120 FPS around 990 MHz and 69–75 W. This is an early
single-system proof of concept, not a standardized benchmark.

An RTX 5090 may have a larger absolute saving in light games, but community
measurements are needed before making that claim.

## Help test it

Useful issue reports include:

- GPU and NVIDIA driver version;
- game, resolution, target FPS, and selected method;
- FPS/frametime stability in both light and demanding scenes;
- board power before and after DCC;
- learned clock;
- whether the game needed a restart for its frame limit;
- privacy-filtered diagnostic ZIP when something fails.

I'll be on vacation for the first four days, so I may not reply right away.
Feel free to experiment while I'm away — just please test carefully and expect
alpha behavior.

[Share a test result or report a bug](https://github.com/christianp403-spec/nv-uv-dcc/issues/new/choose)
