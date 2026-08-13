# DCC by NV-UV 0.1.0-alpha.11

Experimental binary-only Community Preview for NVIDIA GeForce GPUs.

## Included

- compact 280 × 276 CPU-rendered Mini View;
- DCC, Windows autostart, method, and target FPS in one window;
- automatic primary NVIDIA GPU selection;
- game matching against locally installed NVIDIA driver profiles;
- own ETW FPS collector without overlay injection or RTSS;
- target range from 30 to 400 FPS;
- learning DCC algorithm and optional NVIDIA Ultra Efficiency method using the
  driver's internal JPAC/Max-Perf-per-Watt control;
- per-game NVIDIA frame limit with restoration of its previous value;
- activation history, learned profiles, diagnostics, tray, and emergency reset;
- self-contained portable Windows x64 executables.

## Known alpha limitations

- Hardware and driver support is determined at runtime.
- The NVIDIA-driver method uses an undocumented interface.
- For the first clean test, reset UV/OC and fully close MSI Afterburner or other
  GPU clock-control tools. RTSS may remain running without a competing limiter.
- DCC is primarily useful for light, older, capped, or CPU-limited games with
  unused GPU headroom; a fully GPU-bound game offers little room to save power.
- A running game may need to be restarted after changing its driver frame limit.
- The first community preview has only been hardware-tested on an RTX 5070 Ti.
- The included Grim Dawn numbers are observed ranges, not a standardized
  benchmark.

The source code will be added to the public repository next week under MPL-2.0,
and DCC is planned as an integrated feature in the next NV-UV release.
