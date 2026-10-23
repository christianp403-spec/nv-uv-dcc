# Third-party notices

## Microsoft .NET 10 Runtime and Windows Desktop Runtime

- Copyright the .NET Foundation, Microsoft Corporation, and contributors, as
  identified by the applicable distribution notices.
- License and distribution terms: the terms and third-party notices supplied
  with the exact .NET runtime distribution used for the build.
- Runtime: https://github.com/dotnet/runtime
- WPF: https://github.com/dotnet/wpf

The portable package is self-contained and includes trimmed portions of the
.NET Runtime and Windows Presentation Foundation. These components remain
under their own terms. A binary portable distribution must therefore include,
without modification, the applicable .NET runtime license and third-party
notice files from the exact runtime used to create that distribution. The
project links above and this summary do not replace those files.

DCC – Dynamic Clock Capping uses the following open-source components:

## Microsoft diagnostics and support packages

- Copyright Microsoft Corporation and contributors.
- License: MIT.
- Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5:
  https://github.com/microsoft/perfview
- Microsoft.Diagnostics.NETCore.Client 0.2.510501:
  https://www.nuget.org/packages/Microsoft.Diagnostics.NETCore.Client/0.2.510501
- Microsoft.Extensions.DependencyInjection.Abstractions 6.0.0:
  https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/6.0.0
- Microsoft.Extensions.DependencyInjection 6.0.0:
  https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/6.0.0
- Microsoft.Extensions.Logging.Abstractions 6.0.0:
  https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/6.0.0
- Microsoft.Extensions.Logging 6.0.0:
  https://www.nuget.org/packages/Microsoft.Extensions.Logging/6.0.0
- Microsoft.Extensions.Options 6.0.0:
  https://www.nuget.org/packages/Microsoft.Extensions.Options/6.0.0
- Microsoft.Extensions.Primitives 6.0.0:
  https://www.nuget.org/packages/Microsoft.Extensions.Primitives/6.0.0
- System.Reflection.TypeExtensions 4.7.0:
  https://www.nuget.org/packages/System.Reflection.TypeExtensions/4.7.0

TraceEvent and its support packages are used by the elevated DCC helper to
consume Windows Event Tracing for Windows (ETW) graphics events.

## NVIDIA NVAPI headers and interface definitions

- Copyright NVIDIA Corporation and affiliates.
- License: MIT.
- Project: https://github.com/NVIDIA/nvapi
- Documentation: https://docs.nvidia.com/nvapi/

DCC contains a small managed interoperability definition for the public NVAPI
DRS functions and the documented `Frame Rate Limiter` setting. NVIDIA's SDK
binaries and headers are not redistributed in the portable package; calls are
resolved at runtime from the NVIDIA display driver's `nvapi64.dll`.

The optional experimental NVIDIA efficiency method additionally resolves a
driver-internal interface at runtime. It is not presented as an
NVIDIA-supported API, and DCC disables the option cleanly when the installed
driver does not expose or accept that interface.

## MIT License text

The following license text applies to the MIT-licensed components identified
above. Their copyright notices remain those stated above and in their upstream
package metadata.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
