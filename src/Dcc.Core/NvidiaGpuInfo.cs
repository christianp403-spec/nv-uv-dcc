namespace NvUv.Dcc.Core;

public sealed record NvidiaGpuInfo(
    int Index,
    string Uuid,
    string Name,
    string DriverVersion,
    string PState,
    int GraphicsClockMHz,
    int MemoryClockMHz,
    double PowerWatts,
    double UtilizationPercent,
    bool DisplayActive,
    string PciBusId,
    double? CoreVoltageMillivolts = null,
    bool TelemetryAvailable = true)
{
    public string DisplayName => $"GPU {Index}: {Name}";
}
