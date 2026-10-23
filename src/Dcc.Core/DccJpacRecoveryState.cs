using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccJpacRecoveryState(
    int SchemaVersion,
    DateTimeOffset AppliedAtUtc,
    int TargetFps)
{
    public const int CurrentSchemaVersion = 1;

    public static DccJpacRecoveryState Create(int targetFps) =>
        new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            Math.Clamp(
                targetFps,
                DccFpsRange.NvidiaEfficiencyFloor,
                DccFpsRange.NvidiaEfficiencyMaximum));
}

public static class DccJpacRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DccJpacRecoveryState? TryRead()
    {
        try
        {
            if (!File.Exists(DccPaths.JpacRecoveryFile))
                return null;
            DccJpacRecoveryState? state = JsonSerializer.Deserialize<DccJpacRecoveryState>(
                File.ReadAllText(DccPaths.JpacRecoveryFile), JsonOptions);
            return state?.SchemaVersion == DccJpacRecoveryState.CurrentSchemaVersion ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(DccJpacRecoveryState state)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporaryPath = DccPaths.JpacRecoveryFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, DccPaths.JpacRecoveryFile, overwrite: true);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(DccPaths.JpacRecoveryFile))
                File.Delete(DccPaths.JpacRecoveryFile);
        }
        catch
        {
            // A stale recovery marker is safer than assuming JPAC was disabled.
        }
    }
}
