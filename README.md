# DCC – Dynamic Clock Capping

**An NV-UV project for NVIDIA GeForce GPUs.**

DCC automatically finds and remembers the lowest GPU clock needed to maintain
a chosen FPS target. It reduces power use without editing voltage or clock
curves.

## Features

- Adaptive DCC algorithm with persistent profiles per GPU, game, and FPS target
- Targets from **20–1000 FPS**
- Automatic game detection; browsers, launchers, and the desktop are excluded
- Immediate GPU release on Alt+Tab and profile reuse when returning to the game
- `Global` and one-session `Per game` modes
- Optional elevated autostart, minimized tray start, and **100–300%** UI scaling
- Optional NVIDIA frame limiter and performance logging, both off by default
- Recovery paths for clock caps, frame limits, and NVIDIA mode
- No RTSS, MSI Afterburner, cloud service, or AI model required

DCC is **not undervolting** and does not modify the GPU V/F curve. For best
efficiency, use one FPS cap matching the selected DCC target.

## NVIDIA Ultra Efficiency

An experimental alternative using NVIDIA's internal JPAC interface is included.
It is limited to **60–400 FPS** and has no reliable direct driver readback.

NVIDIA Project G-Assist can be installed separately through the NVIDIA App for
comparison.

## Download

[Download DCC 0.1.0-alpha.14](https://github.com/christianp403-spec/nv-uv-dcc/releases/tag/v0.1.0-alpha.14)

**Portable:** `DCC-0.1.0-alpha.14-win-x64-portable.zip`

**SHA-256:** `49842780DAFD3772602AA43E0C2733E7D98A01AE217A94B68FA58DF7F42566C8`

DCC is not code-signed, so Windows SmartScreen may warn. This remains alpha
software; test carefully.

[Report a bug or test result](https://github.com/christianp403-spec/nv-uv-dcc/issues/new/choose)
