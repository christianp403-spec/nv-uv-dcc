using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.Helper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        DccPaths.EnsureDataDirectoryReady();
        string mode = args.Contains("--reset", StringComparer.OrdinalIgnoreCase) ? "reset" : "session";
        DccLog.Helper("process.start", $"mode={mode}");
        try
        {
            HelperArguments options = HelperArguments.Parse(args);
            DccLog.Helper("arguments.accepted", $"gpu_index={options.GpuIndex}; parent_pid={options.ParentProcessId}");
            var client = new NvidiaSmiClient();

            if (options.ResetOnly)
                return await ResetOnlyAsync(client, options);

            return await RunSessionAsync(client, options);
        }
        catch (Exception ex)
        {
            DccLog.Helper("process.fatal", exception: ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ResetOnlyAsync(NvidiaSmiClient client, HelperArguments options)
    {
        await DccEmergencyResetCoordinator.RunAllAsync(
        [
        new DccEmergencyResetOperation("jpac", () =>
        {
            if (DccJpacRecoveryStore.TryRead() is not null)
            {
                DccLog.Helper("reset.jpac.begin");
                using var jpac = new NvApiJpacClient();
                jpac.SetEnabled(false);
                DccJpacRecoveryStore.Clear();
                DccLog.Helper("reset.jpac.complete");
            }
            return Task.CompletedTask;
        }),

        new DccEmergencyResetOperation("vsync", () =>
        {
            DccVsyncRecoveryState? recovery = DccVsyncRecoveryStore.TryRead();
            if (recovery is not null)
            {
                DccLog.Helper("reset.vsync.begin", $"profile={recovery.ProfileName}");
                using var vsync = new NvApiFrameLimiterClient();
                vsync.RestoreVsync(recovery);
                DccVsyncRecoveryStore.Clear();
                DccLog.Helper("reset.vsync.complete", $"profile={recovery.ProfileName}");
            }
            return Task.CompletedTask;
        }),

        new DccEmergencyResetOperation("frame_limiter", () =>
        {
            DccFrameLimiterRecoveryState? recovery = DccFrameLimiterRecoveryStore.TryRead();
            if (recovery is not null)
            {
                DccLog.Helper("reset.frame_limiter.begin",
                    $"executable={recovery.ExecutableName}; target_fps={recovery.TargetFps}");
                using var frameLimiter = new NvApiFrameLimiterClient();
                frameLimiter.Restore(recovery);
                DccFrameLimiterRecoveryStore.Clear();
                DccLog.Helper("reset.frame_limiter.complete", $"executable={recovery.ExecutableName}");
            }
            return Task.CompletedTask;
        }),

        new DccEmergencyResetOperation("gpu_clock", async () =>
        {
            DccLog.Helper("reset.gpu_verification.begin", $"gpu_index={options.GpuIndex}");
            await VerifyGpuAsync(client, options.GpuIndex, options.GpuUuid, CancellationToken.None);
            DccLog.Helper("reset.gpu_verified", $"gpu_index={options.GpuIndex}");
            await client.ResetClockRangeAsync(options.GpuIndex);
            DccActiveSessionStore.Clear();
            DccLog.Helper("reset.gpu_clock.complete", $"gpu_index={options.GpuIndex}");
        })
        ],
        (stage, ex) => DccLog.Helper($"reset.{stage}.failed", exception: ex));

        DccLog.Helper("reset.complete", $"gpu_index={options.GpuIndex}");
        return 0;
    }

    private static async Task<int> RunSessionAsync(NvidiaSmiClient client, HelperArguments options)
    {
        if (string.IsNullOrWhiteSpace(options.PipeName) || string.IsNullOrWhiteSpace(options.Token))
            throw new ArgumentException("A pipe name and authentication token are required.");

        DccLog.Helper("session.parent.open", $"parent_pid={options.ParentProcessId}");
        using Process parentProcess = Process.GetProcessById(options.ParentProcessId);
        DccLog.Helper("session.gpu_verification.begin", $"gpu_index={options.GpuIndex}");
        await VerifyGpuAsync(client, options.GpuIndex, options.GpuUuid, CancellationToken.None);
        DccLog.Helper("session.gpu_verified", $"gpu_index={options.GpuIndex}");

        using var parentExited = new CancellationTokenSource();
        _ = MonitorParentAsync(parentProcess, parentExited);

        bool capActive = false;
        bool jpacActive = false;
        int jpacTargetFps = 0;
        NvApiJpacClient? jpacClient = null;
        DccFrameLimiterRecoveryState? frameLimiterState = null;
        DccVsyncRecoveryState? vsyncState = null;
        NvApiFrameLimiterClient? frameLimiterClient = null;
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            // The UI creates the server with CurrentUserOnly. Repeating that option on
            // the elevated client makes .NET compare the server owner with the UAC
            // token owner, which can differ for the same Windows account. The server
            // ACL, random token, parent PID and mutual pipe-PID checks remain enforced.

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(parentExited.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            DccLog.Helper("session.pipe.connecting");
            await pipe.ConnectAsync(connectTimeout.Token);
            DccLog.Helper("session.pipe.connected");

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcessId) ||
                serverProcessId != (uint)options.ParentProcessId)
            {
                DccLog.Helper("session.pipe.server_rejected", $"expected_pid={options.ParentProcessId}; actual_pid={serverProcessId}");
                await writer.WriteLineAsync("ERROR The pipe server is not the DCC UI process.");
                return 2;
            }
            DccLog.Helper("session.pipe.server_verified", $"parent_pid={options.ParentProcessId}");

            DccLog.Helper("session.auth.waiting");
            string? authenticationLine = await reader.ReadLineAsync(parentExited.Token);
            if (!TokenMatches(authenticationLine, options.Token))
            {
                DccLog.Helper("session.auth.rejected");
                await writer.WriteLineAsync("ERROR Authentication failed.");
                return 2;
            }

            using var fpsCollector = new DccEtwFpsCollector();
            try
            {
                fpsCollector.Start();
            }
            catch (Exception ex)
            {
                DccLog.Helper("fps.etw.start_failed", exception: ex);
                await writer.WriteLineAsync("ERROR NV-UV ETW FPS collector could not start: " + Sanitize(ex.Message));
                return 3;
            }

            await writer.WriteLineAsync("OK AUTH");
            DccLog.Helper("session.auth.accepted");

            while (!parentExited.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(parentExited.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                    break;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                    continue;

                switch (parts[0].ToUpperInvariant())
                {
                    case "APPS":
                        try
                        {
                            frameLimiterClient ??= new NvApiFrameLimiterClient();
                            IReadOnlyList<DccDriverApplication> applications =
                                frameLimiterClient.EnumerateApplications();
                            byte[] json = JsonSerializer.SerializeToUtf8Bytes(applications);
                            string encoded = Convert.ToBase64String(json);
                            await writer.WriteLineAsync($"OK APPS {encoded}");
                            DccLog.Helper("command.apps.complete", $"applications={applications.Count}");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.apps.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "JPACSTATUS":
                        if (parts.Length != 1)
                        {
                            await writer.WriteLineAsync("ERROR Invalid NVIDIA efficiency status command.");
                            break;
                        }
                        await writer.WriteLineAsync(
                            $"OK JPACSTATUS {(jpacActive ? 1 : 0)} {jpacTargetFps} DRIVER_READBACK_UNAVAILABLE");
                        break;

                    case "JPAC":
                        if (parts.Length != 3 ||
                            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int jpacEnabled) ||
                            jpacEnabled is < 0 or > 1 ||
                            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int requestedJpacTarget) ||
                            (jpacEnabled == 1 && requestedJpacTarget is < DccFpsRange.NvidiaEfficiencyFloor or > DccFpsRange.NvidiaEfficiencyMaximum) ||
                            (jpacEnabled == 0 && requestedJpacTarget != 0))
                        {
                            await writer.WriteLineAsync("ERROR Invalid NVIDIA efficiency command.");
                            break;
                        }

                        try
                        {
                            jpacClient ??= new NvApiJpacClient();
                            if (jpacEnabled == 1)
                            {
                                DccJpacRecoveryStore.Write(DccJpacRecoveryState.Create(requestedJpacTarget));
                                jpacClient.SetEnabled(true, requestedJpacTarget);
                                jpacActive = true;
                                jpacTargetFps = requestedJpacTarget;
                            }
                            else
                            {
                                jpacClient.SetEnabled(false);
                                jpacActive = false;
                                jpacTargetFps = 0;
                                DccJpacRecoveryStore.Clear();
                            }
                            await writer.WriteLineAsync(
                                $"OK JPAC {jpacEnabled} {jpacTargetFps} SET_ACCEPTED DRIVER_READBACK_UNAVAILABLE");
                            DccLog.Helper("command.jpac.complete",
                                $"enabled={jpacEnabled}; jpac_target_fps={jpacTargetFps}");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.jpac.failed", exception: ex);
                            if (!jpacActive && jpacEnabled == 1)
                            {
                                try
                                {
                                    jpacClient?.SetEnabled(false);
                                    DccJpacRecoveryStore.Clear();
                                }
                                catch (Exception rollbackException)
                                {
                                    DccLog.Helper("command.jpac.rollback_failed", exception: rollbackException);
                                    // Keep the marker: the driver state could not be proven clean.
                                }
                            }
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "LIMITGLOBAL":
                        try
                        {
                            if (parts.Length != 2 ||
                                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetFps) ||
                                targetFps is < DccFrameLimitCommand.MinimumTargetFps or > DccFrameLimitCommand.MaximumTargetFps)
                            {
                                throw new InvalidDataException("Invalid LIMITGLOBAL command.");
                            }
                            DccLog.Helper("command.limit.begin", $"scope=global; target_fps={targetFps}");

                            if (frameLimiterState is not null)
                            {
                                frameLimiterClient!.Restore(frameLimiterState);
                                DccFrameLimiterRecoveryStore.Clear();
                                frameLimiterState = null;
                            }

                            frameLimiterClient ??= new NvApiFrameLimiterClient();
                            try
                            {
                                frameLimiterState = frameLimiterClient.ApplyGlobalFrameLimit(
                                    targetFps,
                                    DccFrameLimiterRecoveryStore.Write);
                            }
                            catch
                            {
                                DccFrameLimiterRecoveryState? recovery = DccFrameLimiterRecoveryStore.TryRead();
                                if (recovery is not null)
                                {
                                    try
                                    {
                                        frameLimiterClient.Restore(recovery);
                                        DccFrameLimiterRecoveryStore.Clear();
                                    }
                                    catch (Exception restoreException)
                                    {
                                        DccLog.Helper("command.limit.rollback_failed", exception: restoreException);
                                    }
                                }
                                throw;
                            }

                            await writer.WriteLineAsync($"OK LIMITGLOBAL {targetFps} GLOBAL_SET_CONFIRMED");
                            DccLog.Helper("command.limit.complete",
                                $"scope=global; target_fps={targetFps}; profile={frameLimiterState.ProfileName}; readback=verified");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.limit.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "UNLIMIT":
                        try
                        {
                            DccLog.Helper("command.unlimit.begin");
                            DccFrameLimiterRecoveryState? recovery = frameLimiterState ??
                                DccFrameLimiterRecoveryStore.TryRead();
                            if (recovery is not null)
                            {
                                frameLimiterClient ??= new NvApiFrameLimiterClient();
                                frameLimiterClient.Restore(recovery);
                                DccFrameLimiterRecoveryStore.Clear();
                            }
                            frameLimiterState = null;
                            await writer.WriteLineAsync("OK UNLIMIT");
                            DccLog.Helper("command.unlimit.complete");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.unlimit.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "VSYNC":
                        try
                        {
                            if (parts.Length != 1)
                                throw new InvalidDataException("Invalid VSYNC command.");
                            DccLog.Helper("command.vsync.begin", "scope=global");

                            if (vsyncState is not null)
                            {
                                frameLimiterClient!.RestoreVsync(vsyncState);
                                DccVsyncRecoveryStore.Clear();
                                vsyncState = null;
                            }

                            frameLimiterClient ??= new NvApiFrameLimiterClient();
                            try
                            {
                                vsyncState = frameLimiterClient.ApplyVsync(DccVsyncRecoveryStore.Write);
                            }
                            catch
                            {
                                DccVsyncRecoveryState? recovery = DccVsyncRecoveryStore.TryRead();
                                if (recovery is not null)
                                {
                                    try
                                    {
                                        frameLimiterClient.RestoreVsync(recovery);
                                        DccVsyncRecoveryStore.Clear();
                                    }
                                    catch (Exception restoreException)
                                    {
                                        DccLog.Helper("command.vsync.rollback_failed", exception: restoreException);
                                    }
                                }
                                throw;
                            }

                            await writer.WriteLineAsync("OK VSYNC GLOBAL_SET_CONFIRMED");
                            DccLog.Helper("command.vsync.complete",
                                $"profile={vsyncState.ProfileName}; scope=global; profile_readback=confirmed");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.vsync.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "UNVSYNC":
                        try
                        {
                            if (parts.Length != 1)
                                throw new InvalidDataException("Invalid UNVSYNC command.");
                            DccLog.Helper("command.unvsync.begin");
                            DccVsyncRecoveryState? recovery = vsyncState ?? DccVsyncRecoveryStore.TryRead();
                            if (recovery is not null)
                            {
                                frameLimiterClient ??= new NvApiFrameLimiterClient();
                                frameLimiterClient.RestoreVsync(recovery);
                                DccVsyncRecoveryStore.Clear();
                            }
                            vsyncState = null;
                            await writer.WriteLineAsync("OK UNVSYNC");
                            DccLog.Helper("command.unvsync.complete");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.unvsync.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "FPS":
                        if (parts.Length != 2 ||
                            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ||
                            processId <= 0)
                        {
                            await writer.WriteLineAsync("ERROR Invalid FPS command.");
                            break;
                        }

                        DccFpsSample fps = fpsCollector.Read(processId);
                        if (!fps.CollectorAvailable)
                        {
                            string encodedError = Convert.ToBase64String(Encoding.UTF8.GetBytes(fps.Error));
                            await writer.WriteLineAsync("OK FPS UNAVAILABLE " + encodedError);
                        }
                        else if (fps.FrameCount < 2 || fps.FramesPerSecond <= 0)
                        {
                            await writer.WriteLineAsync("OK FPS NONE");
                        }
                        else
                        {
                            string runtime = fps.Source.StartsWith("NV-UV ETW/", StringComparison.Ordinal)
                                ? fps.Source[10..]
                                : "ETW";
                            await writer.WriteLineAsync(string.Create(
                                CultureInfo.InvariantCulture,
                                $"OK FPS {fps.FramesPerSecond:F3} {fps.SampleAge.TotalMilliseconds:F0} {fps.FrameCount} {runtime} {fps.P95FrameTimeMilliseconds:F3} {fps.P99FrameTimeMilliseconds:F3}"));
                        }
                        break;

                    case "SET":
                        if (parts.Length != 3 ||
                            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minimumMHz) ||
                            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximumMHz))
                        {
                            await writer.WriteLineAsync("ERROR Invalid SET command.");
                            break;
                        }

                        try
                        {
                            DccLog.Helper("command.set.begin", $"minimum_mhz={minimumMHz}; maximum_mhz={maximumMHz}");
                            await ValidateRangeAsync(
                                client,
                                options,
                                minimumMHz,
                                maximumMHz,
                                parentExited.Token);

                            // Persist recovery information before touching the driver. If the helper
                            // disappears between these two operations, the next launch still offers
                            // an explicit reset instead of silently assuming that no cap exists.
                            capActive = true;
                            DccActiveSessionStore.Write(DccActiveSessionState.Create(
                                options.GpuIndex,
                                options.GpuUuid,
                                minimumMHz,
                                maximumMHz,
                                Environment.ProcessId));

                            try
                            {
                                await client.ApplyClockRangeAsync(
                                    options.GpuIndex,
                                    minimumMHz,
                                    maximumMHz,
                                    parentExited.Token);
                            }
                            catch (Exception applyException)
                            {
                                try
                                {
                                    await client.ResetClockRangeAsync(options.GpuIndex);
                                    capActive = false;
                                    DccActiveSessionStore.Clear();
                                }
                                catch (Exception resetException)
                                {
                                    throw new InvalidOperationException(
                                        $"{applyException.Message} Automatic recovery also failed: {resetException.Message}",
                                        applyException);
                                }

                                throw;
                            }

                            await writer.WriteLineAsync($"OK SET {minimumMHz} {maximumMHz}");
                            DccLog.Helper("command.set.complete", $"minimum_mhz={minimumMHz}; maximum_mhz={maximumMHz}");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.set.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "RESET":
                        try
                        {
                            DccLog.Helper("command.reset.begin");
                            await client.ResetClockRangeAsync(options.GpuIndex, parentExited.Token);
                            capActive = false;
                            DccActiveSessionStore.Clear();
                            await writer.WriteLineAsync("OK RESET");
                            DccLog.Helper("command.reset.complete");
                        }
                        catch (Exception ex)
                        {
                            DccLog.Helper("command.reset.failed", exception: ex);
                            await writer.WriteLineAsync("ERROR " + Sanitize(ex.Message));
                        }
                        break;

                    case "EXIT":
                        DccLog.Helper("command.exit.begin",
                            $"cap_active={capActive}; jpac_active={jpacActive}; vsync_active={vsyncState is not null}; frame_limiter_active={frameLimiterState is not null}");
                        if (capActive)
                        {
                            await client.ResetClockRangeAsync(options.GpuIndex, parentExited.Token);
                            capActive = false;
                        }
                        if (vsyncState is not null)
                        {
                            frameLimiterClient!.RestoreVsync(vsyncState);
                            vsyncState = null;
                            DccVsyncRecoveryStore.Clear();
                        }
                        if (frameLimiterState is not null)
                        {
                            frameLimiterClient!.Restore(frameLimiterState);
                            frameLimiterState = null;
                            DccFrameLimiterRecoveryStore.Clear();
                        }
                        if (jpacActive)
                        {
                            jpacClient!.SetEnabled(false);
                            jpacActive = false;
                            DccJpacRecoveryStore.Clear();
                        }
                        DccActiveSessionStore.Clear();
                        await writer.WriteLineAsync("OK EXIT");
                        DccLog.Helper("command.exit.complete");
                        return 0;

                    default:
                        DccLog.Helper("command.unknown");
                        await writer.WriteLineAsync("ERROR Unknown command.");
                        break;
                }
            }

            return 0;
        }
        finally
        {
            if (capActive)
            {
                try
                {
                    DccLog.Helper("session.final_reset.begin");
                    await client.ResetClockRangeAsync(options.GpuIndex);
                    DccActiveSessionStore.Clear();
                    DccLog.Helper("session.final_reset.complete");
                }
                catch (Exception ex)
                {
                    DccLog.Helper("session.final_reset.failed", exception: ex);
                    // Keep the recovery marker when reset cannot be proven.
                }
            }
            if (vsyncState is not null)
            {
                try
                {
                    DccLog.Helper("session.final_unvsync.begin");
                    frameLimiterClient ??= new NvApiFrameLimiterClient();
                    frameLimiterClient.RestoreVsync(vsyncState);
                    vsyncState = null;
                    DccVsyncRecoveryStore.Clear();
                    DccLog.Helper("session.final_unvsync.complete");
                }
                catch (Exception ex)
                {
                    DccLog.Helper("session.final_unvsync.failed", exception: ex);
                    // Keep the recovery marker until restoration can be proven.
                }
            }
            if (frameLimiterState is not null)
            {
                try
                {
                    DccLog.Helper("session.final_unlimit.begin");
                    frameLimiterClient ??= new NvApiFrameLimiterClient();
                    frameLimiterClient.Restore(frameLimiterState);
                    frameLimiterState = null;
                    DccFrameLimiterRecoveryStore.Clear();
                    DccLog.Helper("session.final_unlimit.complete");
                }
                catch (Exception ex)
                {
                    DccLog.Helper("session.final_unlimit.failed", exception: ex);
                    // Keep the recovery marker until restoration can be proven.
                }
            }
            if (jpacActive)
            {
                try
                {
                    DccLog.Helper("session.final_jpac_reset.begin");
                    jpacClient ??= new NvApiJpacClient();
                    jpacClient.SetEnabled(false);
                    jpacActive = false;
                    DccJpacRecoveryStore.Clear();
                    DccLog.Helper("session.final_jpac_reset.complete");
                }
                catch (Exception ex)
                {
                    DccLog.Helper("session.final_jpac_reset.failed", exception: ex);
                }
            }
            frameLimiterClient?.Dispose();
            jpacClient?.Dispose();
            DccLog.Helper("session.end",
                $"cap_active={capActive}; jpac_active={jpacActive}; vsync_active={vsyncState is not null}; frame_limiter_active={frameLimiterState is not null}");
        }
    }

    private static async Task ValidateRangeAsync(
        NvidiaSmiClient client,
        HelperArguments options,
        int minimumMHz,
        int maximumMHz,
        CancellationToken cancellationToken)
    {
        if (minimumMHz <= 0 || maximumMHz <= 0 || minimumMHz > maximumMHz)
            throw new InvalidOperationException("The requested clock range is invalid.");

        await VerifyGpuAsync(client, options.GpuIndex, options.GpuUuid, cancellationToken);
        SupportedClockCatalog catalog = await client.QuerySupportedGraphicsClocksAsync(options.GpuIndex, cancellationToken);
        if (!catalog.Contains(minimumMHz) || !catalog.Contains(maximumMHz))
            throw new InvalidOperationException("The requested range contains a clock not reported by the NVIDIA driver.");
    }

    private static async Task VerifyGpuAsync(
        NvidiaSmiClient client,
        int gpuIndex,
        string expectedUuid,
        CancellationToken cancellationToken)
    {
        NvidiaGpuInfo gpu = await client.QueryGpuAsync(gpuIndex, cancellationToken);
        if (!string.Equals(gpu.Uuid, expectedUuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected GPU identity changed. DCC refused the operation.");
    }

    private static async Task MonitorParentAsync(Process parentProcess, CancellationTokenSource cancellation)
    {
        try
        {
            await parentProcess.WaitForExitAsync();
        }
        catch
        {
            // Treat an unreadable parent as gone and trigger reset.
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static bool TokenMatches(string? line, string expectedToken)
    {
        if (line is null || !line.StartsWith("AUTH ", StringComparison.Ordinal))
            return false;

        byte[] supplied = Encoding.UTF8.GetBytes(line[5..]);
        byte[] expected = Encoding.UTF8.GetBytes(expectedToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static string Sanitize(string message) => message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    private sealed record HelperArguments(
        bool ResetOnly,
        string PipeName,
        string Token,
        int ParentProcessId,
        int GpuIndex,
        string GpuUuid)
    {
        public static HelperArguments Parse(string[] args)
        {
            bool reset = args.Contains("--reset", StringComparer.OrdinalIgnoreCase);
            string pipe = ReadValue(args, "--pipe") ?? string.Empty;
            string token = ReadValue(args, "--token") ?? string.Empty;
            string uuid = ReadValue(args, "--uuid")
                ?? throw new ArgumentException("--uuid is required.");

            int gpuIndex = ParseInt(args, "--gpu");
            int parentPid = reset ? 0 : ParseInt(args, "--parent");
            if (gpuIndex < 0 || (!reset && parentPid <= 0))
                throw new ArgumentException("Invalid helper arguments.");

            return new HelperArguments(reset, pipe, token, parentPid, gpuIndex, uuid);
        }

        private static int ParseInt(string[] args, string name)
        {
            string value = ReadValue(args, name) ?? throw new ArgumentException($"{name} is required.");
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new ArgumentException($"{name} is invalid.");
        }

        private static string? ReadValue(string[] args, string name)
        {
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            }
            return null;
        }
    }
}
