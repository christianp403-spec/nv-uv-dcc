using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.App;

internal sealed class ElevatedClockCapSession : IAsyncDisposable
{
    private readonly Process _helperProcess;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private bool _disposed;

    private ElevatedClockCapSession(
        Process helperProcess,
        NamedPipeServerStream pipe,
        StreamReader reader,
        StreamWriter writer)
    {
        _helperProcess = helperProcess;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    public static async Task<ElevatedClockCapSession> StartAsync(
        NvidiaGpuInfo gpu,
        CancellationToken cancellationToken = default)
    {
        DccLog.Ui("activation.session.begin", $"gpu_index={gpu.Index}");
        string helperPath = Path.Combine(AppContext.BaseDirectory, "Dcc.Helper.exe");
        if (!File.Exists(helperPath))
        {
            DccLog.Ui("activation.helper.missing");
            throw new FileNotFoundException("The elevated DCC helper is missing.", helperPath);
        }

        string pipeName = "nvuv-dcc-" + Guid.NewGuid().ToString("N");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string stage = "creating the secure channel";
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        bool parentElevated = AutoStartService.IsCurrentProcessElevated();
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = !parentElevated,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = parentElevated
        };
        if (!parentElevated)
            startInfo.Verb = "runas";
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--gpu");
        startInfo.ArgumentList.Add(gpu.Index.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--uuid");
        startInfo.ArgumentList.Add(gpu.Uuid);

        Process helperProcess;
        try
        {
            DccLog.Ui(
                parentElevated ? "activation.helper.direct_start" : "activation.uac.request",
                $"parent_elevated={parentElevated}");
            helperProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated DCC helper could not be started.");
            DccLog.Ui("activation.helper.started", $"helper_pid={helperProcess.Id}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            pipe.Dispose();
            DccLog.Ui("activation.uac.cancelled");
            throw new OperationCanceledException("The administrator request was cancelled.", ex);
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            DccLog.Ui("activation.helper.start_failed", exception: ex);
            throw;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            stage = "waiting for the helper connection";
            DccLog.Ui("activation.pipe.waiting");
            await pipe.WaitForConnectionAsync(timeout.Token);
            DccLog.Ui("activation.pipe.connected");

            stage = "verifying the helper process";
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientProcessId) ||
                clientProcessId != (uint)helperProcess.Id)
            {
                throw new UnauthorizedAccessException("The pipe client is not the elevated DCC helper process.");
            }
            DccLog.Ui("activation.pipe.client_verified", $"helper_pid={helperProcess.Id}");

            var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            stage = "sending authentication";
            DccLog.Ui("activation.auth.sending");
            await writer.WriteLineAsync("AUTH " + token);
            stage = "reading the authentication reply";
            string response = await ReadResponseAsync(reader, timeout.Token);
            EnsureSuccess(response);
            DccLog.Ui("activation.auth.accepted");

            return new ElevatedClockCapSession(helperProcess, pipe, reader, writer);
        }
        catch (Exception ex)
        {
            Exception failure = ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? new TimeoutException($"The elevated DCC helper timed out while {stage}.", ex)
                : ex;
            string processState = DescribeProcessState(helperProcess);
            DccLog.Ui("activation.session.failed", $"stage={stage}; {processState}", failure);
            pipe.Dispose();
            TryTerminate(helperProcess);
            helperProcess.Dispose();
            if (ReferenceEquals(failure, ex))
                throw;
            throw failure;
        }
    }

    public Task SetAsync(int minimumMHz, int maximumMHz, CancellationToken cancellationToken = default) =>
        SendAsync($"SET {minimumMHz} {maximumMHz}", cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        SendAsync("RESET", cancellationToken);

    public Task ApplyGlobalFrameLimitAsync(
        int targetFps,
        CancellationToken cancellationToken = default)
    {
        if (targetFps is < DccFrameLimitCommand.MinimumTargetFps or > DccFrameLimitCommand.MaximumTargetFps)
            throw new ArgumentOutOfRangeException(nameof(targetFps));
        return SendAsync(
            $"LIMITGLOBAL {targetFps.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    public Task RemoveFrameLimitAsync(CancellationToken cancellationToken = default) =>
        SendAsync("UNLIMIT", cancellationToken);

    public Task ApplyVsyncAsync(CancellationToken cancellationToken = default) =>
        SendAsync("VSYNC", cancellationToken);

    public Task RemoveVsyncAsync(CancellationToken cancellationToken = default) =>
        SendAsync("UNVSYNC", cancellationToken);

    public async Task<DccJpacVerification> SetNvidiaEfficiencyAsync(
        bool enabled,
        int targetFps,
        CancellationToken cancellationToken = default)
    {
        int requestedTarget = enabled
            ? Math.Clamp(targetFps, DccFpsRange.NvidiaEfficiencyFloor, DccFpsRange.NvidiaEfficiencyMaximum)
            : 0;
        string response = await SendForResponseAsync(
            $"JPAC {(enabled ? 1 : 0)} {requestedTarget}",
            cancellationToken,
            logCommand: true);
        EnsureSuccess(response);
        string[] setParts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool accepted = setParts.Length >= 6 &&
                        string.Equals(setParts[1], "JPAC", StringComparison.Ordinal) &&
                        int.TryParse(setParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int setValue) &&
                        setValue == (enabled ? 1 : 0) &&
                        int.TryParse(setParts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int setTarget) &&
                        setTarget == requestedTarget &&
                        string.Equals(setParts[4], "SET_ACCEPTED", StringComparison.Ordinal);
        if (!accepted)
            throw new InvalidDataException("The DCC helper returned an invalid NVIDIA efficiency response.");

        string statusResponse = await SendForResponseAsync("JPACSTATUS", cancellationToken, logCommand: true);
        EnsureSuccess(statusResponse);
        string[] statusParts = statusResponse.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int stateValue = -1;
        int stateTarget = -1;
        bool stateParsed = statusParts.Length >= 5 &&
                           string.Equals(statusParts[1], "JPACSTATUS", StringComparison.Ordinal) &&
                           int.TryParse(statusParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out stateValue) &&
                           stateValue is 0 or 1 &&
                           int.TryParse(statusParts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out stateTarget);
        if (!stateParsed)
            throw new InvalidDataException("The DCC helper returned an invalid NVIDIA efficiency status response.");

        bool stateMatches = stateValue == (enabled ? 1 : 0);
        bool targetMatches = stateTarget == requestedTarget;
        bool driverReadbackAvailable = !string.Equals(
            statusParts[4],
            "DRIVER_READBACK_UNAVAILABLE",
            StringComparison.Ordinal);
        bool? driverEnabled = driverReadbackAvailable ? stateValue == 1 : null;
        var verification = new DccJpacVerification(
            enabled,
            requestedTarget,
            DriverCommandAccepted: true,
            HelperStateMatches: stateMatches,
            HelperTargetMatches: targetMatches,
            DriverReadbackAvailable: driverReadbackAvailable,
            DriverEnabled: driverEnabled);
        DccLog.Ui("jpac.verification", verification.LogValue);
        return verification;
    }

    public async Task<IReadOnlyList<DccDriverApplication>> QueryDriverApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        string response = await SendForResponseAsync("APPS", cancellationToken, logCommand: true);
        EnsureSuccess(response);
        string[] parts = response.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !string.Equals(parts[1], "APPS", StringComparison.Ordinal))
            throw new InvalidDataException("The DCC helper returned an invalid NVIDIA profile response.");

        byte[] json;
        try
        {
            json = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The NVIDIA profile response was not valid Base64.", ex);
        }
        if (json.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The NVIDIA profile response exceeded the DCC size limit.");

        DccDriverApplication[] applications =
            JsonSerializer.Deserialize<DccDriverApplication[]>(json) ?? [];
        if (applications.Length > 50_000)
            throw new InvalidDataException("The NVIDIA profile response contained too many applications.");
        return applications
            .Where(application => !string.IsNullOrWhiteSpace(application.Executable))
            .DistinctBy(application => DccGameCatalog.NormalizeExecutable(application.Executable), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<DccFpsSample> QueryFpsAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            return DccFpsSample.NoFrames(processId, "NV-UV ETW");

        string response = await SendForResponseAsync(
            $"FPS {processId}",
            cancellationToken,
            logCommand: false);
        EnsureSuccess(response);
        string[] parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 && string.Equals(parts[2], "NONE", StringComparison.Ordinal))
            return DccFpsSample.NoFrames(processId, "NV-UV ETW");
        if (parts.Length >= 4 && string.Equals(parts[2], "UNAVAILABLE", StringComparison.Ordinal))
        {
            string error;
            try
            {
                error = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            }
            catch
            {
                error = "The NV-UV ETW collector reported an unreadable error.";
            }
            return DccFpsSample.Unavailable(processId, "NV-UV ETW", error);
        }
        if (parts.Length != 8 ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double ageMs) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frameCount) ||
            !double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double p95FrameTimeMs) ||
            !double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double p99FrameTimeMs))
        {
            throw new InvalidDataException("The NV-UV ETW collector returned an invalid FPS response.");
        }

        return new DccFpsSample(
            true,
            processId,
            fps,
            TimeSpan.FromMilliseconds(Math.Max(0, ageMs)),
            frameCount,
            "NV-UV ETW/" + parts[5],
            P95FrameTimeMilliseconds: Math.Max(0, p95FrameTimeMs),
            P99FrameTimeMilliseconds: Math.Max(0, p99FrameTimeMs));
    }

    public async Task ExitAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        await SendAsync("EXIT", cancellationToken);
    }

    public static async Task RunEmergencyResetAsync(
        NvidiaGpuInfo gpu,
        CancellationToken cancellationToken = default)
    {
        string helperPath = Path.Combine(AppContext.BaseDirectory, "Dcc.Helper.exe");
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("The elevated DCC helper is missing.", helperPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--reset");
        startInfo.ArgumentList.Add("--gpu");
        startInfo.ArgumentList.Add(gpu.Index.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--uuid");
        startInfo.ArgumentList.Add(gpu.Uuid);

        Process process;
        try
        {
            DccLog.Ui("reset.uac.request", $"gpu_index={gpu.Index}");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated reset helper could not be started.");
            DccLog.Ui("reset.helper.started", $"helper_pid={process.Id}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            DccLog.Ui("reset.uac.cancelled");
            throw new OperationCanceledException("The administrator request was cancelled.", ex);
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                DccLog.Ui("reset.helper.failed", $"exit_code={process.ExitCode}");
                throw new InvalidOperationException($"The reset helper failed with exit code {process.ExitCode}.");
            }
            DccLog.Ui("reset.helper.complete");
        }
    }

    private async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        string response = await SendForResponseAsync(command, cancellationToken, logCommand: true);
        EnsureSuccess(response);
    }

    private async Task<string> SendForResponseAsync(
        string command,
        CancellationToken cancellationToken,
        bool logCommand)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            string commandName = command.Split(' ', 2)[0].ToUpperInvariant();
            string detail = commandName == "SET" ? command : commandName;
            if (logCommand)
                DccLog.Ui("helper.command.send", detail);
            await _writer.WriteLineAsync(command);
            string response = await ReadResponseAsync(_reader, cancellationToken);
            if (logCommand)
            {
                if (response.StartsWith("OK", StringComparison.Ordinal))
                    DccLog.Ui("helper.command.accepted", detail);
                else
                    DccLog.Ui("helper.command.rejected", $"command={commandName}; response={DccLog.Sanitize(response)}");
            }
            return response;
        }
        catch (Exception ex)
        {
            DccLog.Ui("helper.command.failed", DescribeProcessState(_helperProcess), ex);
            throw;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static async Task<string> ReadResponseAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? response = await reader.ReadLineAsync(cancellationToken);
        return response ?? throw new IOException("The elevated DCC helper disconnected unexpectedly.");
    }

    private static void EnsureSuccess(string response)
    {
        if (!response.StartsWith("OK", StringComparison.Ordinal))
            throw new InvalidOperationException(response.StartsWith("ERROR ", StringComparison.Ordinal)
                ? response[6..]
                : response);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            if (_pipe.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await ExitAsync(timeout.Token);
                }
                catch
                {
                    // Closing the pipe still causes the helper's finally block to reset.
                }
            }
        }
        finally
        {
            _disposed = true;
            _writer.Dispose();
            _reader.Dispose();
            _pipe.Dispose();
            _commandGate.Dispose();

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _helperProcess.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                TryTerminate(_helperProcess);
            }
            _helperProcess.Dispose();
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string DescribeProcessState(Process process)
    {
        try
        {
            return process.HasExited
                ? $"helper_exited=true; exit_code={process.ExitCode}"
                : "helper_exited=false";
        }
        catch (Exception ex)
        {
            return "helper_state_unavailable=" + DccLog.Sanitize(ex.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
