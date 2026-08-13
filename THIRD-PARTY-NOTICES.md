# Third-party notices

## Microsoft .NET 10 Runtime and Windows Desktop Runtime

- Copyright Microsoft Corporation and contributors.
- License: MIT and the licenses identified by the .NET projects' notices.
- Runtime: https://github.com/dotnet/runtime
- WPF: https://github.com/dotnet/wpf

The portable package is self-contained and includes trimmed portions of the
.NET Runtime and Windows Presentation Foundation. These components remain
under their own licenses. Their source repositories contain the corresponding
license and third-party notice files.

DCC by NV-UV uses the following open-source component:

## Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5

- Copyright Microsoft Corporation and contributors.
- License: MIT.
- Project: https://github.com/microsoft/perfview
- Package: https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/3.2.5

The component is used by the elevated DCC helper to consume Windows Event
Tracing for Windows (ETW) graphics events. Its MIT license is available from
the project and package links above.

## NVIDIA NVAPI headers and interface definitions

- Copyright NVIDIA Corporation and affiliates.
- License: MIT.
- Project: https://github.com/NVIDIA/nvapi
- Documentation: https://docs.nvidia.com/nvapi/

DCC contains a small managed interoperability definition for the public NVAPI
DRS functions and the documented `Frame Rate Limiter` setting. NVIDIA's SDK
binaries and headers are not redistributed in the portable package; calls are
resolved at runtime from the NVIDIA display driver's `nvapi64.dll`.

The optional experimental NVIDIA driver method additionally resolves an
undocumented JPAC/Max-Perf-per-Watt interface at runtime. It is not presented
as an NVIDIA-supported API, and DCC disables the option cleanly when the
installed driver does not expose or accept that interface.
