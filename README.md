# DCC - Dynamic Clock Capping

A standalone Windows utility for NVIDIA GeForce GPUs, developed by Christian
as part of NV-UV. DCC reduces unnecessary GPU clock headroom while a supported
game is running. It does not edit voltage or the voltage/frequency curve.

This repository contains the **0.1.0-alpha.18-hotfix.8** standalone source
under the [GNU GPLv3](LICENSE), version 3 only. It is experimental alpha software.

## Features

- Automatic per-game clock control with learned profiles per game and GPU.
- A separate **Games & Profiles** window with game history, profile import
  and export, and individual learned-profile reset.
- Game detection using NVIDIA driver profiles, with the NV-UV game catalog
  as a fallback. NV-UV and Play do not need to be installed.
- Global or single-game-session activation, tray operation, and optional
  Windows logon startup.
- Optional NVIDIA frame-limit and VSync control with restoration of the
  previous driver values.
- A separate experimental NVIDIA efficiency mode, subject to driver support.
  Its efficiency target is not an FPS limiter.
- German, English, Russian, and Spanish interfaces, local diagnostics,
  optional performance logs, and emergency recovery.

## Build and test

Requires Windows x64 and the .NET 10 SDK 10.0.300 or a compatible servicing
release. Running DCC requires a supported NVIDIA GPU and driver. Privileged
control and ETW capture request administrator approval.

```powershell
dotnet restore Dcc.slnx --locked-mode
dotnet build Dcc.slnx -c Release --no-restore
dotnet run --project tests/Dcc.SelfTest/Dcc.SelfTest.csproj -c Release --no-build
```

The default self-tests use simulated inputs and do not query or change GPU
state. They do not establish compatibility with every GPU, driver, or game.

Run `src/Dcc.App/bin/Release/net10.0-windows/DCC-by-NV-UV.exe` and keep its
output folder together. This framework-dependent build requires the .NET 10
Desktop Runtime. PowerShell 7 scripts are also provided:

- `scripts/Publish-Portable.ps1`: create a self-contained portable build.
- `scripts/Publish-SourceSnapshot.ps1`: create a source archive with a SHA-256
  manifest from the explicit file list.

## Use and local data

Start DCC, select the method and activation scope, and enable it before
starting a game. For the first test, use stock GPU settings. Avoid simultaneous
control of the same clock cap or driver settings by another utility.

Settings, learned profiles, history, and logs are stored under
`%LocalAppData%/NVUV-DCC`. The public NV-UV game catalog can be updated from
GitHub. Local game profiles and performance data are not uploaded.

If a clock cap remains active after an interruption, use **Emergency reset**.
Further recovery instructions are in [SECURITY.md](SECURITY.md).

## Existing downloads

[Existing Community Preview downloads](https://github.com/christianp403-spec/nv-uv-dcc/releases)
remain available. The latest existing binary is **0.1.0-alpha.18-hotfix.6**;
it is not a build of the Hotfix 8 source published here. Those older packages
retain their included [Community Preview terms](COMMUNITY-PREVIEW-TERMS.txt).

## License

DCC is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License as published by the Free Software Foundation,
version 3 only (`GPL-3.0-only`). See [LICENSE](LICENSE).

DCC is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License for more details.

When distributing DCC or modified versions, the corresponding source must be
made available as required by the GPL. Commercial use and sale are permitted.
Previously distributed copies retain their applicable license terms.

[Third-party notices](THIRD-PARTY-NOTICES.md) and [attribution](NOTICE.txt)
remain applicable. DCC is not endorsed, certified, or supported by NVIDIA.

[Report a bug](https://github.com/christianp403-spec/nv-uv-dcc/issues/new/choose).
