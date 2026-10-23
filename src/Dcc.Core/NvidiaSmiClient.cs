using System.Diagnostics;
using System.Globalization;

namespace NvUv.Dcc.Core;

public sealed class NvidiaSmiClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);
    private readonly TimeSpan _timeout;

    public NvidiaSmiClient(string? executablePath = null, TimeSpan? timeout = null)
    {
        ExecutablePath = executablePath ?? LocateExecutable();
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public string ExecutablePath { get; }

    public async Task<IReadOnlyList<NvidiaGpuInfo>> QueryGpusAsync(
        CancellationToken cancellationToken = default,
        bool includeCoreVoltage = false)
    {
        string output = await RunAsync(
            [
                "--query-gpu=index,uuid,name,driver_version,pstate,clocks.current.graphics,clocks.current.memory,power.draw,utilization.gpu,display_active,pci.bus_id",
                "--format=csv,noheader,nounits"
            ],
            cancellationToken);

        return ParseGpuCsv(output, includeCoreVoltage);
    }

    public static IReadOnlyList<NvidiaGpuInfo> ParseGpuCsv(
        string output,
        bool includeCoreVoltage = false)
    {
        var result = new List<NvidiaGpuInfo>();
        foreach (string rawLine in SplitLines(output))
        {
            string[] fields = rawLine.Split(',').Select(field => field.Trim()).ToArray();
            if (fields.Length < 11 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                continue;

            int graphicsClockMHz = ParseInt(fields[5]);
            bool utilizationValid = double.TryParse(
                fields[8],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedUtilization) &&
                double.IsFinite(parsedUtilization) &&
                parsedUtilization is >= 0 and <= 100;
            double utilizationPercent = utilizationValid ? parsedUtilization : 0;
            bool telemetryAvailable = graphicsClockMHz > 0 &&
                                      utilizationValid;

            result.Add(new NvidiaGpuInfo(
                index,
                fields[1],
                fields[2],
                fields[3],
                fields[4],
                graphicsClockMHz,
                ParseInt(fields[6]),
                ParseDouble(fields[7]),
                utilizationPercent,
                IsEnabled(fields[9]),
                fields[10],
                includeCoreVoltage
                    ? NvApiCoreVoltageReader.Shared.TryReadCoreVoltageMillivolts(fields[10])
                    : null,
                telemetryAvailable));
        }

        if (result.Count == 0)
            throw new NvidiaSmiException("The NVIDIA driver returned no usable GPU records.");

        return result;
    }

    public async Task<NvidiaGpuInfo> QueryGpuAsync(
        int gpuIndex,
        CancellationToken cancellationToken = default,
        bool includeCoreVoltage = false)
    {
        IReadOnlyList<NvidiaGpuInfo> gpus = await QueryGpusAsync(cancellationToken, includeCoreVoltage);
        return gpus.FirstOrDefault(gpu => gpu.Index == gpuIndex)
            ?? throw new NvidiaSmiException($"GPU index {gpuIndex} is no longer present.");
    }

    public async Task<SupportedClockCatalog> QuerySupportedGraphicsClocksAsync(
        int gpuIndex,
        CancellationToken cancellationToken = default)
    {
        string output = await RunAsync(
            [
                "-i",
                gpuIndex.ToString(CultureInfo.InvariantCulture),
                "--query-supported-clocks=memory,graphics",
                "--format=csv,noheader,nounits"
            ],
            cancellationToken);

        return ParseSupportedClockCsv(output);
    }

    public async Task ApplyClockRangeAsync(
        int gpuIndex,
        int minimumMHz,
        int maximumMHz,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(
            [
                "-i",
                gpuIndex.ToString(CultureInfo.InvariantCulture),
                $"--lock-gpu-clocks={minimumMHz.ToString(CultureInfo.InvariantCulture)},{maximumMHz.ToString(CultureInfo.InvariantCulture)}"
            ],
            cancellationToken);
    }

    public async Task ResetClockRangeAsync(int gpuIndex, CancellationToken cancellationToken = default)
    {
        await RunAsync(
            [
                "-i",
                gpuIndex.ToString(CultureInfo.InvariantCulture),
                "--reset-gpu-clocks"
            ],
            cancellationToken);
    }

    public static SupportedClockCatalog ParseSupportedClockCsv(string output)
    {
        var graphicsBins = new List<int>();
        foreach (string line in SplitLines(output))
        {
            string[] fields = line.Split(',').Select(field => field.Trim()).ToArray();
            if (fields.Length < 2)
                continue;

            if (int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int graphicsMHz) &&
                graphicsMHz > 0)
            {
                graphicsBins.Add(graphicsMHz);
            }
        }

        if (graphicsBins.Count == 0)
            throw new NvidiaSmiException("The NVIDIA driver returned no supported graphics clock bins.");

        return new SupportedClockCatalog(graphicsBins);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };

        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                throw new NvidiaSmiException("nvidia-smi could not be started.");
        }
        catch (Exception ex) when (ex is not NvidiaSmiException)
        {
            throw new NvidiaSmiException($"nvidia-smi could not be started: {ex.Message}");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex)
        {
            await TerminateAndWaitAsync(process, stdoutTask, stderrTask);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "The nvidia-smi operation was cancelled after its process was terminated.",
                    ex,
                    cancellationToken);
            }
            throw new NvidiaSmiException("nvidia-smi did not finish within the safety timeout.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            throw new NvidiaSmiException(
                string.IsNullOrWhiteSpace(details)
                    ? $"nvidia-smi failed with exit code {process.ExitCode}."
                    : details,
                process.ExitCode,
                details);
        }

        return stdout;
    }

    private static string LocateExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
        };

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));

        string? match = candidates.FirstOrDefault(File.Exists);
        return match is not null
            ? Path.GetFullPath(match)
            : throw new NvidiaSmiException(
                "nvidia-smi.exe was not found in a trusted NVIDIA system location.");
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

    private static bool IsEnabled(string value) =>
        value.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase);

    private static async Task TerminateAndWaitAsync(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // A concurrent process exit is harmless; the wait below is authoritative.
        }

        await process.WaitForExitAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            // Stream teardown after forced termination does not change process exit.
        }
    }
}
