using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccActiveSessionState(
    int SchemaVersion,
    DateTimeOffset AppliedAtUtc,
    int GpuIndex,
    string GpuUuid,
    int MinimumClockMHz,
    int MaximumClockMHz,
    int HelperProcessId)
{
    public const int CurrentSchemaVersion = 1;

    public static DccActiveSessionState Create(
        int gpuIndex,
        string gpuUuid,
        int minimumClockMHz,
        int maximumClockMHz,
        int helperProcessId) =>
        new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            gpuIndex,
            gpuUuid,
            minimumClockMHz,
            maximumClockMHz,
            helperProcessId);
}

public static class DccActiveSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DccActiveSessionState? TryRead()
    {
        try
        {
            if (!File.Exists(DccPaths.ActiveSessionFile))
                return null;

            return JsonSerializer.Deserialize<DccActiveSessionState>(
                File.ReadAllText(DccPaths.ActiveSessionFile),
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(DccActiveSessionState state)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporaryPath = DccPaths.ActiveSessionFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, DccPaths.ActiveSessionFile, overwrite: true);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(DccPaths.ActiveSessionFile))
                File.Delete(DccPaths.ActiveSessionFile);
        }
        catch
        {
            // A stale marker is preferable to claiming recovery succeeded when it did not.
        }
    }
}
