# DCC by NV-UV 0.1.0-alpha.13

The adaptive **DCC algorithm** has been refined and is the default method
again. **NVIDIA Ultra Efficiency** remains available as an experimental
alternative.

## Highlights

- DCC supports **20–1000 FPS** and stores dynamic profiles by GPU, game, and
  target FPS. They are reused immediately; menus, loading screens, and CPU
  limits no longer invalidate them.
- Faster control keeps roughly five percent reserve and restores the last stable
  clock after a failed probe. Alt+Tab releases control and reapplies the game
  profile on return.
- For the most efficient control, use exactly one FPS cap matching the DCC
  target—from the game, driver, or optional DCC limiter. The limiter remains
  off by default.
- Steam, GOG, Epic, Xbox, and similar launchers are not controlled as games;
  RTSS and MSI Afterburner are not required.
- Performance logging is optional and off. Baseline, DCC, and NVIDIA runs can
  be compared locally without automatic uploads. Data uses
  `%LocalAppData%\NVUV-DCC`, and all control methods have recovery paths.

## Early RTX 5090 tests at 120 FPS

### Manor Lords

| Method | Board power | FPS | P95 frame time |
|---|---:|---:|---:|
| Stock | about 407 W | about 120 | – |
| NVIDIA Ultra Efficiency | about 367 W | 119.998 | 8.584 ms |
| DCC, final 30 s | about 323 W | 119.981 | 8.608 ms |

### Resident Evil 4

| Method | Board power | Graphics clock | FPS | P95 frame time |
|---|---:|---:|---:|---:|
| Stock, short measurement point | 408.7 W | 2730 MHz | 120.007 | 8.912 ms |
| NVIDIA, final 30 s | about 352 W | about 2471 MHz | 120.001 | 8.786 ms |
| DCC, final 30 s | about 306 W | about 1918 MHz | 119.985 | 8.898 ms |

At practically identical FPS, DCC used about **12% less power than NVIDIA in
Manor Lords** and **13% less in Resident Evil 4**. Compared with stock, the
reductions were about 21% and 25%, respectively.

These are early, non-standardized tests on one RTX 5090 with driver 610.88. The
RE4 stock figure is only a short measurement point; initial Manor Lords learning
briefly dipped to roughly 117 FPS. Other games and systems can differ.

## NVIDIA method

NVIDIA Ultra Efficiency runs directly through `nvapi64.dll`, without G-Assist,
AI, or a cloud service, and now receives the selected target correctly. It
remains experimental and limited to **60–400 FPS** because reliable direct
driver readback of the active JPAC state is not available. Anyone who would
also like to try NVIDIA's official Project G-Assist can install it from the
Discover section of NVIDIA App: [Project G-Assist](https://www.nvidia.com/en-us/software/nvidia-app/g-assist/).

## Verification

- Build: zero warnings, zero errors; all 26 self-tests passed
- VirusTotal: [main executable 0/70](https://www.virustotal.com/gui/file/0761e28ad4b11a7f9666af2eb081bfb810f611ede96c50dd19c18d6176fad4ce),
  [helper 0/71](https://www.virustotal.com/gui/file/62f26c18f0c45e95e315f369ff8038e6e6b495973ed22f9a9d842d0d95060103)

DCC is not code-signed, so Windows SmartScreen may warn.

**Portable:** `DCC-by-NV-UV-0.1.0-alpha.13-community-preview-win-x64-portable.zip`

**SHA-256:** `1EE8C9BE53A4FAB8FFCEB735729A4B54B549DA69C662CF5B3B10B37273F93A15`
