# DCC – Dynamic Clock Capping

**An NV-UV project for NVIDIA GeForce GPUs.**

DCC automatically finds and remembers the lowest GPU clock needed to maintain a chosen FPS target. It is designed to reduce power consumption without manually editing voltage or clock curves.

## Features

- Adaptive DCC algorithm with persistent profiles per GPU, game and FPS target
- Targets from 20–1000 FPS
- Automatic game detection; browsers, launchers and the desktop are excluded
- Immediate GPU release on Alt+Tab and profile reuse when returning to the game
- Optional NVIDIA frame limiter and performance logging, both off by default
- Recovery paths for clock caps, frame limits and NVIDIA mode
- No RTSS, MSI Afterburner, cloud service or AI model required

DCC is **not undervolting** and does not modify the GPU V/F curve.

For best efficiency, use one FPS cap matching the selected DCC target. The cap can come from the game, NVIDIA driver or DCC's optional limiter.

## NVIDIA Ultra Efficiency

An experimental alternative using NVIDIA's internal JPAC interface is included. It is limited to **60–400 FPS** and does not provide reliable direct driver readback.

NVIDIA Project G-Assist can be installed separately through the NVIDIA App for comparison.

## Download

Download the latest Community Preview from:

https://github.com/christianp403-spec/nv-uv-dcc/releases/latest

DCC is alpha software. Test carefully and report problems through GitHub Issues.

[Report a bug or test result](https://github.com/christianp403-spec/nv-uv-dcc/issues/new/choose)
