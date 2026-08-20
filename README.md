# DCC – Dynamic Clock Capping

**An NV-UV project for NVIDIA GeForce GPUs.**

DCC automatically finds and remembers an efficient GPU clock for each game.
It learns primarily from GPU utilization and protects FPS, frame time and
one-percent lows without editing voltage or clock curves.

## Features

- Automatic DCC algorithm with persistent profiles per GPU and game
- Performance-guarded learning without a manual DCC FPS target
- Automatic game detection including Unreal `Shipping.exe` and RTX Remix;
  browsers, launchers, overlays, and the desktop are excluded
- Immediate GPU release on Alt+Tab and profile reuse when returning to the game
- `Global` and one-session `Per game` modes
- Optional global NVIDIA VSync with readback and automatic restore
- Optional elevated autostart, minimized tray start, and **100–300%** UI scaling
- Visible **DE / EN / RU / ES** language selector under Games & Profiles
- Optional NVIDIA frame limiter and performance logging, both off by default
- A–B–A power/energy guard that stops below-valley clock reductions
- Optional read-only GPU core-voltage values in performance logs when supported
- Primary presentation-stream selection for multi-swapchain games
- Recovery paths for clock caps, frame limits, and NVIDIA mode
- No RTSS, MSI Afterburner, cloud service, or AI model required

DCC is **not undervolting** and does not modify the GPU V/F curve. A single FPS
cap can create additional efficiency headroom, but the automatic DCC algorithm
detects the active limit itself. The separate DCC frame limiter is optional.

For fixed Frame Generation with G-SYNC, NVIDIA driver VSync can be useful. DCC
can apply it globally while active when enabled before the game starts.
NVIDIA currently recommends neither VSync nor a frame limiter for Dynamic MFG.
In practice, VSync may still work—for example in Cyberpunk 2077—so give it a
try.

## NVIDIA Ultra Efficiency

An experimental alternative using NVIDIA's internal JPAC interface is included.
Its selectable target range is **60–1,000 FPS**. This is a JPAC efficiency
target, not a frame-rate limit.

## Download

[Download DCC 0.1.0-alpha.16](https://github.com/christianp403-spec/nv-uv-dcc/releases/tag/v0.1.0-alpha.16)

**Portable:** `DCC-0.1.0-alpha.16-win-x64-portable.zip`

**SHA-256:** `D85B32FD81FC36695C53BA81967F042CE46D5ED46F31EE827FA61A62AB265A21`

DCC is not code-signed, so Windows SmartScreen may warn. This remains alpha
software; test carefully.

[Report a bug or test result](https://github.com/christianp403-spec/nv-uv-dcc/issues/new/choose)
