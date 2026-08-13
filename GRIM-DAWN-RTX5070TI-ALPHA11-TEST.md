# Preliminary Grim Dawn efficiency test

This is an early proof-of-concept result, not a standardized benchmark.
The measurements were taken while playing Grim Dawn and are intended to show
whether Dynamic Clock Capping is worth broader community testing.

## Test system and target

- GPU: NVIDIA GeForce RTX 5070 Ti
- Resolution: 3840 × 2160 (4K)
- Target: 100 FPS
- Game: Grim Dawn
- Existing GPU setup: low-voltage NV-UV Eco/UV profile

## Observed result

| Configuration | Observed GPU clock | Observed board power | Result |
|---|---:|---:|---|
| Existing Eco/UV operation without DCC | about 2 GHz | 89–92 W | 100 FPS |
| DCC active | about 990 MHz | 69–75 W | 100 FPS |

During parts of the DCC run, the GPU fans did not need to start. The observed
power range was roughly 16–25% below the comparison range while preserving the
100-FPS target in the tested scenes.

## Interpretation

DCC is not another undervolting method. This particular proof-of-concept run
used an existing Eco/UV profile, but new alpha testers should establish their
first baseline at stock settings with MSI Afterburner fully closed. That avoids
two clock-control systems influencing the result. RTSS may remain running as
long as it is not applying a competing frame limit. In this test Grim Dawn still
met its FPS target at roughly half the previously observed GPU clock.

The RTX 5090 may show a larger absolute reduction because it has considerably
more unused performance in light games, but that is a hypothesis until users
measure it. More demanding scenes must also be tested because DCC's controller
needs to restore clock headroom quickly when the workload rises.

## Limitations

- single GPU and game;
- no fixed benchmark route or external power meter;
- values are observed ranges, not long-term averages;
- no claim yet about every scene, driver version, API, or NVIDIA GPU;
- the NVIDIA-driver efficiency method is experimental and may vary by driver.

For a useful comparison, report the GPU and driver, game, resolution, target
FPS, DCC method, FPS stability, board power before/after, and whether the game
had to be restarted before the NVIDIA frame limit became active.
