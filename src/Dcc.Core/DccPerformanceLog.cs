using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccPerformanceSessionMetadata(
    string AppVersion,
    string GameName,
    string Executable,
    int ProcessId,
    string NvidiaProfileName,
    string GpuName,
    string GpuFingerprint,
    string DriverVersion,
    DccControlMethod ControlMethod,
    int TargetFps,
    bool FrameLimiterEnabled,
    int FrameLimitFps,
    DccActivationScope ActivationScope,
    DateTimeOffset StartedAtUtc,
    bool NvidiaVsyncEnabled = false,
    int GameClientWidth = 0,
    int GameClientHeight = 0,
    int DisplayWidth = 0,
    int DisplayHeight = 0,
    string GameKey = "");

public sealed record DccPerformanceSample(
    DateTimeOffset TimestampUtc,
    double FramesPerSecond,
    double P95FrameTimeMilliseconds,
    int FrameCount,
    double FpsSampleAgeMilliseconds,
    double PowerWatts,
    double GpuUtilizationPercent,
    int GraphicsClockMHz,
    int MemoryClockMHz,
    int? AppliedClockCapMHz,
    string PState,
    string ControlVerification,
    double P99FrameTimeMilliseconds = 0,
    double? CoreVoltageMillivolts = null,
    int ClockChangeSensitivityScore = 0,
    bool ClockChangeSensitive = false,
    string ClockChangeAssessment = "none");

public sealed record DccPerformanceSummary(
    int SchemaVersion,
    DccPerformanceSessionMetadata Session,
    DateTimeOffset EndedAtUtc,
    double DurationSeconds,
    int SampleCount,
    double AverageFps,
    double MinimumFps,
    double FpsFifthPercentile,
    double AverageP95FrameTimeMilliseconds,
    double MaximumP95FrameTimeMilliseconds,
    double AveragePowerWatts,
    double MaximumPowerWatts,
    double EnergyWattHours,
    double AverageGpuUtilizationPercent,
    double AverageGraphicsClockMHz,
    int MinimumGraphicsClockMHz,
    int MaximumGraphicsClockMHz,
    string LastControlVerification,
    double? AverageCoreVoltageMillivolts = null,
    double? MinimumCoreVoltageMillivolts = null,
    double? MaximumCoreVoltageMillivolts = null);

public sealed class DccPerformanceRecorder : IDisposable
{
    public const int CurrentSchemaVersion = 5;
    public const int MaximumRunsPerGame = 30;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DccPerformanceSessionMetadata _metadata;
    private readonly string _gameDirectory;
    private readonly string _summaryPath;
    private readonly StreamWriter _writer;
    private readonly List<double> _fpsSamples = [];
    private readonly List<double> _p95Samples = [];
    private DateTimeOffset? _firstSampleAtUtc;
    private DateTimeOffset? _lastSampleAtUtc;
    private double _lastPowerWatts;
    private double _powerSum;
    private double _maximumPower;
    private double _utilizationSum;
    private double _graphicsClockSum;
    private int _minimumGraphicsClock = int.MaxValue;
    private int _maximumGraphicsClock;
    private double _coreVoltageSum;
    private double _minimumCoreVoltage = double.MaxValue;
    private double _maximumCoreVoltage;
    private int _coreVoltageSampleCount;
    private double _energyWattHours;
    private int _sampleCount;
    private string _lastControlVerification = "unavailable";
    private bool _disposed;

    private DccPerformanceRecorder(
        DccPerformanceSessionMetadata metadata,
        string rootDirectory)
    {
        _metadata = metadata;
        string gameToken = SafeToken(Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(metadata.GameKey)
                ? metadata.Executable
                : metadata.GameKey));
        _gameDirectory = Path.Combine(rootDirectory, gameToken);
        Directory.CreateDirectory(_gameDirectory);

        string controlTarget = metadata.TargetFps == DccLearnedProfile.AutomaticTargetFps
            ? "auto"
            : metadata.TargetFps.ToString(CultureInfo.InvariantCulture) + "fps";
        string stem = string.Create(
            CultureInfo.InvariantCulture,
            $"{metadata.StartedAtUtc:yyyyMMdd-HHmmss-fff}-{metadata.ControlMethod}-{controlTarget}-{Guid.NewGuid():N}");
        stem = stem[..Math.Min(stem.Length, 96)];
        CsvPath = Path.Combine(_gameDirectory, stem + ".csv");
        _summaryPath = Path.Combine(_gameDirectory, stem + ".summary.json");

        var stream = new FileStream(
            CsvPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        WriteHeader();
    }

    public string CsvPath { get; }
    public DccPerformanceSessionMetadata Metadata => _metadata;

    public static DccPerformanceRecorder Start(
        DccPerformanceSessionMetadata metadata,
        string? rootDirectory = null) =>
        new(metadata, Path.GetFullPath(rootDirectory ?? DccPaths.PerformanceLogsDirectory));

    public void Append(DccPerformanceSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DateTimeOffset timestamp = sample.TimestampUtc.ToUniversalTime();
        _firstSampleAtUtc ??= timestamp;
        double elapsedSeconds = Math.Max(0, (timestamp - _metadata.StartedAtUtc.ToUniversalTime()).TotalSeconds);
        double averageFrameTime = sample.FramesPerSecond > 0 ? 1000d / sample.FramesPerSecond : 0;
        double? coreVoltage = NormalizeCoreVoltage(sample.CoreVoltageMillivolts);

        _writer.WriteLine(string.Join(',', new[]
        {
            Csv(timestamp.ToString("O", CultureInfo.InvariantCulture)),
            Number(elapsedSeconds),
            Number(sample.FramesPerSecond),
            Number(averageFrameTime),
            Number(sample.P95FrameTimeMilliseconds),
            Number(sample.P99FrameTimeMilliseconds),
            Number(sample.P99FrameTimeMilliseconds > 0 ? 1000d / sample.P99FrameTimeMilliseconds : 0),
            sample.FrameCount.ToString(CultureInfo.InvariantCulture),
            Number(sample.FpsSampleAgeMilliseconds),
            Number(sample.PowerWatts),
            Number(sample.GpuUtilizationPercent),
            sample.GraphicsClockMHz.ToString(CultureInfo.InvariantCulture),
            sample.MemoryClockMHz.ToString(CultureInfo.InvariantCulture),
            sample.AppliedClockCapMHz?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Csv(sample.PState),
            Csv(sample.ControlVerification),
            coreVoltage.HasValue ? Number(coreVoltage.Value) : string.Empty,
            sample.ClockChangeSensitivityScore.ToString(CultureInfo.InvariantCulture),
            sample.ClockChangeSensitive.ToString(CultureInfo.InvariantCulture),
            Csv(sample.ClockChangeAssessment)
        }));

        _sampleCount++;
        if (sample.FramesPerSecond > 0)
            _fpsSamples.Add(sample.FramesPerSecond);
        if (sample.P95FrameTimeMilliseconds > 0)
            _p95Samples.Add(sample.P95FrameTimeMilliseconds);
        if (sample.PowerWatts >= 0)
        {
            _powerSum += sample.PowerWatts;
            _maximumPower = Math.Max(_maximumPower, sample.PowerWatts);
        }
        _utilizationSum += Math.Max(0, sample.GpuUtilizationPercent);
        _graphicsClockSum += Math.Max(0, sample.GraphicsClockMHz);
        _minimumGraphicsClock = Math.Min(_minimumGraphicsClock, Math.Max(0, sample.GraphicsClockMHz));
        _maximumGraphicsClock = Math.Max(_maximumGraphicsClock, Math.Max(0, sample.GraphicsClockMHz));
        if (coreVoltage is double voltage)
        {
            _coreVoltageSum += voltage;
            _minimumCoreVoltage = Math.Min(_minimumCoreVoltage, voltage);
            _maximumCoreVoltage = Math.Max(_maximumCoreVoltage, voltage);
            _coreVoltageSampleCount++;
        }
        _lastControlVerification = string.IsNullOrWhiteSpace(sample.ControlVerification)
            ? "unavailable"
            : sample.ControlVerification;

        if (_lastSampleAtUtc is DateTimeOffset previous)
        {
            double hours = (timestamp - previous).TotalHours;
            if (hours is > 0 and <= (5d / 3600d))
                _energyWattHours += ((_lastPowerWatts + Math.Max(0, sample.PowerWatts)) / 2d) * hours;
        }
        _lastSampleAtUtc = timestamp;
        _lastPowerWatts = Math.Max(0, sample.PowerWatts);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DateTimeOffset endedAt = _lastSampleAtUtc ?? DateTimeOffset.UtcNow;
        _writer.Dispose();

        try
        {
            DccPerformanceSummary summary = BuildSummary(endedAt);
            string temporary = _summaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, _summaryPath, overwrite: true);
            PruneOldRuns(_gameDirectory);
        }
        catch
        {
            // Performance logging must never affect GPU control or recovery.
        }
    }

    private void WriteHeader()
    {
        _writer.WriteLine($"# DCC performance log schema={CurrentSchemaVersion}");
        _writer.WriteLine("# game=" + HeaderValue(_metadata.GameName));
        _writer.WriteLine("# executable=" + HeaderValue(_metadata.Executable));
        _writer.WriteLine("# game_key=" + HeaderValue(
            string.IsNullOrWhiteSpace(_metadata.GameKey)
                ? DccGameCatalog.NormalizeExecutable(_metadata.Executable)
                : DccGameCatalog.NormalizeExecutable(_metadata.GameKey)));
        _writer.WriteLine("# process_id=" + _metadata.ProcessId.ToString(CultureInfo.InvariantCulture));
        _writer.WriteLine("# nvidia_profile=" + HeaderValue(_metadata.NvidiaProfileName));
        _writer.WriteLine("# gpu=" + HeaderValue(_metadata.GpuName));
        _writer.WriteLine("# gpu_fingerprint=" + HeaderValue(_metadata.GpuFingerprint));
        _writer.WriteLine("# driver=" + HeaderValue(_metadata.DriverVersion));
        _writer.WriteLine("# method=" + _metadata.ControlMethod);
        _writer.WriteLine("# control_target_fps=" +
            (_metadata.TargetFps == DccLearnedProfile.AutomaticTargetFps
                ? "automatic"
                : _metadata.TargetFps.ToString(CultureInfo.InvariantCulture)));
        _writer.WriteLine("# frame_limiter=" + _metadata.FrameLimiterEnabled.ToString(CultureInfo.InvariantCulture));
        _writer.WriteLine("# frame_limit_fps=" + _metadata.FrameLimitFps.ToString(CultureInfo.InvariantCulture));
        _writer.WriteLine("# nvidia_vsync=" + _metadata.NvidiaVsyncEnabled.ToString(CultureInfo.InvariantCulture));
        _writer.WriteLine("# activation_scope=" + _metadata.ActivationScope);
        _writer.WriteLine("# game_client_resolution=" + Resolution(
            _metadata.GameClientWidth,
            _metadata.GameClientHeight));
        _writer.WriteLine("# display_resolution=" + Resolution(
            _metadata.DisplayWidth,
            _metadata.DisplayHeight));
        _writer.WriteLine("# started_utc=" + _metadata.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        _writer.WriteLine("timestamp_utc,elapsed_s,fps,average_frame_time_ms,p95_frame_time_ms,p99_frame_time_ms,one_percent_low_fps,frame_count,fps_sample_age_ms,power_w,gpu_util_percent,graphics_clock_mhz,memory_clock_mhz,applied_clock_cap_mhz,pstate,control_verification,gpu_core_voltage_mv,clock_change_sensitivity_score,clock_change_sensitive,clock_change_assessment");
    }

    private DccPerformanceSummary BuildSummary(DateTimeOffset endedAt)
    {
        double duration = _firstSampleAtUtc is null
            ? 0
            : Math.Max(0, (endedAt - _firstSampleAtUtc.Value).TotalSeconds);
        return new DccPerformanceSummary(
            SchemaVersion: CurrentSchemaVersion,
            Session: _metadata,
            EndedAtUtc: endedAt,
            DurationSeconds: duration,
            SampleCount: _sampleCount,
            AverageFps: Average(_fpsSamples),
            MinimumFps: _fpsSamples.Count == 0 ? 0 : _fpsSamples.Min(),
            FpsFifthPercentile: Percentile(_fpsSamples, 0.05),
            AverageP95FrameTimeMilliseconds: Average(_p95Samples),
            MaximumP95FrameTimeMilliseconds: _p95Samples.Count == 0 ? 0 : _p95Samples.Max(),
            AveragePowerWatts: _sampleCount == 0 ? 0 : _powerSum / _sampleCount,
            MaximumPowerWatts: _maximumPower,
            EnergyWattHours: _energyWattHours,
            AverageGpuUtilizationPercent: _sampleCount == 0 ? 0 : _utilizationSum / _sampleCount,
            AverageGraphicsClockMHz: _sampleCount == 0 ? 0 : _graphicsClockSum / _sampleCount,
            MinimumGraphicsClockMHz: _minimumGraphicsClock == int.MaxValue ? 0 : _minimumGraphicsClock,
            MaximumGraphicsClockMHz: _maximumGraphicsClock,
            LastControlVerification: _lastControlVerification,
            AverageCoreVoltageMillivolts: _coreVoltageSampleCount == 0
                ? null
                : _coreVoltageSum / _coreVoltageSampleCount,
            MinimumCoreVoltageMillivolts: _coreVoltageSampleCount == 0 ? null : _minimumCoreVoltage,
            MaximumCoreVoltageMillivolts: _coreVoltageSampleCount == 0 ? null : _maximumCoreVoltage);
    }

    private static void PruneOldRuns(string gameDirectory)
    {
        FileInfo[] runs = new DirectoryInfo(gameDirectory)
            .EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (FileInfo oldRun in runs.Skip(MaximumRunsPerGame))
        {
            try
            {
                string summary = Path.ChangeExtension(oldRun.FullName, ".summary.json");
                oldRun.Delete();
                if (File.Exists(summary))
                    File.Delete(summary);
            }
            catch
            {
                // A retained or externally opened run is skipped and may be pruned later.
            }
        }
    }

    private static double Average(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? 0 : values.Average();

    private static double? NormalizeCoreVoltage(double? value) =>
        value is double voltage && double.IsFinite(voltage) && voltage > 0
            ? voltage
            : null;

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        double[] ordered = values.Order().ToArray();
        double position = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return ordered[lower];
        double fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private static string SafeToken(string value)
    {
        string token = new(value
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray());
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);
        token = token.Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "game" : token[..Math.Min(token.Length, 48)];
    }

    private static string HeaderValue(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Resolution(int width, int height) =>
        width > 0 && height > 0
            ? $"{width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}"
            : "unknown";

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ');
        return clean.IndexOfAny([',', '"']) < 0 ? clean : '"' + clean.Replace("\"", "\"\"") + '"';
    }
}

public static class DccPerformanceArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool HasLogs(string? sourceDirectory = null)
    {
        string source = Path.GetFullPath(sourceDirectory ?? DccPaths.PerformanceLogsDirectory);
        return Directory.Exists(source) && Directory.EnumerateFiles(source, "*.csv", SearchOption.AllDirectories).Any();
    }

    public static void Export(string destinationPath, string? sourceDirectory = null)
    {
        string source = Path.GetFullPath(sourceDirectory ?? DccPaths.PerformanceLogsDirectory);
        if (!Directory.Exists(source))
            throw new InvalidOperationException("No DCC performance logs are available yet.");

        FileInfo[] runFiles = new DirectoryInfo(source)
            .EnumerateFiles("*.csv", SearchOption.AllDirectories)
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runFiles.Length == 0)
            throw new InvalidOperationException("No DCC performance logs are available yet.");

        string destination = Path.GetFullPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteText(archive, "README.txt",
                    "DCC records one telemetry sample per second while it actively controls a confirmed foreground game.\n" +
                    "comparison-summary.csv contains one row per completed run. The runs folder contains the full time series.\n" +
                    "FPS fifth percentile is based on one-second samples and is not a frame-level 1% low metric.\n" +
                    "GPU core voltage fields are blank when no read-only voltage provider is available.\n");

                List<DccPerformanceSummary> summaries = [];
                foreach (FileInfo run in runFiles)
                {
                    string relative = Path.GetRelativePath(source, run.FullName).Replace('\\', '/');
                    CopyFile(archive, run.FullName, "runs/" + relative);
                    string summaryPath = Path.ChangeExtension(run.FullName, ".summary.json");
                    if (!File.Exists(summaryPath))
                        continue;
                    string summaryRelative = Path.GetRelativePath(source, summaryPath).Replace('\\', '/');
                    CopyFile(archive, summaryPath, "runs/" + summaryRelative);
                    try
                    {
                        DccPerformanceSummary? summary = JsonSerializer.Deserialize<DccPerformanceSummary>(
                            File.ReadAllText(summaryPath, Encoding.UTF8),
                            JsonOptions);
                        if (summary is not null)
                            summaries.Add(summary);
                    }
                    catch
                    {
                        // A malformed summary does not prevent export of the raw run.
                    }
                }
                WriteText(archive, "comparison-summary.csv", BuildComparisonCsv(summaries));
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string BuildComparisonCsv(IEnumerable<DccPerformanceSummary> summaries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("game,executable,method,control_target_fps,frame_limiter,frame_limit_fps,nvidia_vsync,started_utc,duration_s,samples,average_fps,minimum_fps,fps_fifth_percentile,average_p95_frame_time_ms,maximum_p95_frame_time_ms,average_power_w,maximum_power_w,energy_wh,average_gpu_util_percent,average_graphics_clock_mhz,minimum_graphics_clock_mhz,maximum_graphics_clock_mhz,control_verification,average_gpu_core_voltage_mv,minimum_gpu_core_voltage_mv,maximum_gpu_core_voltage_mv");
        foreach (DccPerformanceSummary item in summaries.OrderBy(summary => summary.Session.StartedAtUtc))
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(item.Session.GameName),
                Csv(item.Session.Executable),
                item.Session.ControlMethod.ToString(),
                item.Session.TargetFps == DccLearnedProfile.AutomaticTargetFps
                    ? "automatic"
                    : item.Session.TargetFps.ToString(CultureInfo.InvariantCulture),
                item.Session.FrameLimiterEnabled.ToString(CultureInfo.InvariantCulture),
                item.Session.FrameLimitFps.ToString(CultureInfo.InvariantCulture),
                item.Session.NvidiaVsyncEnabled.ToString(CultureInfo.InvariantCulture),
                Csv(item.Session.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                Number(item.DurationSeconds),
                item.SampleCount.ToString(CultureInfo.InvariantCulture),
                Number(item.AverageFps),
                Number(item.MinimumFps),
                Number(item.FpsFifthPercentile),
                Number(item.AverageP95FrameTimeMilliseconds),
                Number(item.MaximumP95FrameTimeMilliseconds),
                Number(item.AveragePowerWatts),
                Number(item.MaximumPowerWatts),
                Number(item.EnergyWattHours),
                Number(item.AverageGpuUtilizationPercent),
                Number(item.AverageGraphicsClockMHz),
                item.MinimumGraphicsClockMHz.ToString(CultureInfo.InvariantCulture),
                item.MaximumGraphicsClockMHz.ToString(CultureInfo.InvariantCulture),
                Csv(item.LastControlVerification),
                NullableNumber(item.AverageCoreVoltageMillivolts),
                NullableNumber(item.MinimumCoreVoltageMillivolts),
                NullableNumber(item.MaximumCoreVoltageMillivolts)
            }));
        }
        return builder.ToString();
    }

    private static void CopyFile(ZipArchive archive, string sourcePath, string entryName)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        input.CopyTo(output);
    }

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string NullableNumber(double? value) =>
        value is double number && double.IsFinite(number) && number > 0
            ? Number(number)
            : string.Empty;

    private static string Csv(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ');
        return clean.IndexOfAny([',', '"']) < 0 ? clean : '"' + clean.Replace("\"", "\"\"") + '"';
    }
}
