using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NvUv.Dcc.Core;

public sealed record DccDiagnosticSnapshot(
    string AppVersion,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string GpuName,
    int? GpuIndex,
    string GpuUuid,
    string DriverVersion,
    string PState,
    string PciBusId,
    int? MinimumClockMHz,
    int? MaximumClockMHz,
    int? ClockBinCount,
    bool RecoveryMarkerPresent,
    bool DccSessionActive,
    int TargetFps,
    string Language,
    int UiScalePercent,
    bool AutostartEnabled,
    int ProfileCount,
    int GameCount,
    bool FpsCollectorAvailable,
    string FpsCollectorSource,
    int FpsCollectorFrameCount,
    string FpsCollectorError,
    string ControlMethod = "DccGovernor",
    bool FrameLimiterEnabled = false,
    string ActivationScope = "Persistent",
    bool PerformanceLoggingEnabled = false,
    int FrameLimitFps = 0,
    bool NvidiaVsyncEnabled = false);

public sealed record DccDiagnosticLogSource(string ArchiveName, string FilePath);

public static class DccDiagnosticReport
{
    public static void Export(
        string destinationPath,
        DccDiagnosticSnapshot snapshot,
        IEnumerable<DccDiagnosticLogSource>? logSources = null)
    {
        string fullDestination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(archive, "diagnostics.txt", BuildSummary(snapshot));
                WriteEntry(archive, "privacy.txt",
                    "This archive is generated locally. It contains DCC environment metadata and sanitized rolling logs.\n" +
                    "It excludes authentication tokens, complete GPU UUIDs, full user paths, learned profiles, game EXE lists, and telemetry history.\n");

                foreach (DccDiagnosticLogSource source in logSources ?? DefaultLogSources())
                {
                    string content = DccLog.ReadSanitized(source.FilePath);
                    if (!string.IsNullOrWhiteSpace(content))
                        WriteEntry(archive, "logs/" + SanitizeArchiveName(source.ArchiveName), content + Environment.NewLine);
                }
            }

            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static string GpuFingerprint(string? gpuUuid)
    {
        if (string.IsNullOrWhiteSpace(gpuUuid))
            return "unavailable";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(gpuUuid.Trim().ToUpperInvariant()));
        return Convert.ToHexString(hash)[..12];
    }

    private static string BuildSummary(DccDiagnosticSnapshot snapshot)
    {
        var lines = new[]
        {
            Pair("Generated UTC", DateTimeOffset.UtcNow.ToString("O")),
            Pair("DCC version", snapshot.AppVersion),
            Pair("OS", snapshot.OsDescription),
            Pair("OS architecture", snapshot.OsArchitecture),
            Pair("Process architecture", snapshot.ProcessArchitecture),
            string.Empty,
            Pair("GPU", snapshot.GpuName),
            Pair("GPU index", snapshot.GpuIndex?.ToString() ?? "unavailable"),
            Pair("GPU fingerprint", GpuFingerprint(snapshot.GpuUuid)),
            Pair("Driver", snapshot.DriverVersion),
            Pair("P-state", snapshot.PState),
            Pair("PCI bus", snapshot.PciBusId),
            Pair("Supported clock minimum MHz", snapshot.MinimumClockMHz?.ToString() ?? "unavailable"),
            Pair("Supported clock maximum MHz", snapshot.MaximumClockMHz?.ToString() ?? "unavailable"),
            Pair("Supported clock bins", snapshot.ClockBinCount?.ToString() ?? "unavailable"),
            string.Empty,
            Pair("Recovery marker present", snapshot.RecoveryMarkerPresent.ToString()),
            Pair("DCC session active", snapshot.DccSessionActive.ToString()),
            Pair("Control method", snapshot.ControlMethod),
            Pair(snapshot.ControlMethod == "NvidiaEfficiency" ? "NVIDIA efficiency target FPS" : "DCC control",
                snapshot.ControlMethod == "NvidiaEfficiency"
                    ? snapshot.TargetFps.ToString()
                    : "automatic GPU utilization"),
            Pair("NVIDIA efficiency target range",
                snapshot.ControlMethod == "NvidiaEfficiency"
                    ? $"{DccFpsRange.NvidiaEfficiencyFloor}–{DccFpsRange.NvidiaEfficiencyMaximum} FPS"
                    : "not active"),
            Pair("Optional NVIDIA frame limiter", snapshot.FrameLimiterEnabled.ToString()),
            Pair("Optional frame limit FPS",
                snapshot.FrameLimiterEnabled ? snapshot.FrameLimitFps.ToString() : "not active"),
            Pair("Optional global NVIDIA VSync", snapshot.NvidiaVsyncEnabled.ToString()),
            Pair("Optional performance logging", snapshot.PerformanceLoggingEnabled.ToString()),
            Pair("Activation scope", snapshot.ActivationScope),
            Pair("Language", snapshot.Language),
            Pair("UI scale percent", snapshot.UiScalePercent.ToString()),
            Pair("Autostart enabled", snapshot.AutostartEnabled.ToString()),
            Pair("Learned profile count", snapshot.ProfileCount.ToString()),
            Pair("Game catalog count", snapshot.GameCount.ToString()),
            Pair("NV-UV FPS collector available", snapshot.FpsCollectorAvailable.ToString()),
            Pair("NV-UV FPS collector source", snapshot.FpsCollectorSource),
            Pair("NV-UV FPS collector frame count", snapshot.FpsCollectorFrameCount.ToString()),
            Pair("NV-UV FPS collector error", snapshot.FpsCollectorError)
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Pair(string key, string? value) => $"{key}: {DccLog.Sanitize(value)}";

    private static IEnumerable<DccDiagnosticLogSource> DefaultLogSources()
    {
        yield return new("ui.log", DccPaths.UiLogFile);
        yield return new("ui.previous.log", DccLog.PreviousFile(DccPaths.UiLogFile));
        yield return new("helper.log", DccPaths.HelperLogFile);
        yield return new("helper.previous.log", DccLog.PreviousFile(DccPaths.HelperLogFile));
    }

    private static string SanitizeArchiveName(string name)
    {
        string fileName = Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(fileName) ? "diagnostic.log" : fileName;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
