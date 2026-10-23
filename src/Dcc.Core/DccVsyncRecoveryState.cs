using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccVsyncRecoveryState(
    int SchemaVersion,
    DateTimeOffset AppliedAtUtc,
    string ProfileName,
    bool PreviousOverridePresent,
    uint PreviousValue,
    uint ForcedValue)
{
    public const int CurrentSchemaVersion = 2;
}

public static class DccVsyncRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DccVsyncRecoveryState? TryRead()
    {
        try
        {
            if (!File.Exists(DccPaths.VsyncRecoveryFile))
                return null;

            DccVsyncRecoveryState? state = JsonSerializer.Deserialize<DccVsyncRecoveryState>(
                File.ReadAllText(DccPaths.VsyncRecoveryFile),
                JsonOptions);
            return state?.SchemaVersion == DccVsyncRecoveryState.CurrentSchemaVersion
                ? state
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(DccVsyncRecoveryState state)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporaryPath = DccPaths.VsyncRecoveryFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, DccPaths.VsyncRecoveryFile, overwrite: true);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(DccPaths.VsyncRecoveryFile))
                File.Delete(DccPaths.VsyncRecoveryFile);
        }
        catch
        {
            // A stale marker is safer than claiming that the NVIDIA profile was restored.
        }
    }
}
