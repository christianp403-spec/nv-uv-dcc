using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccFrameLimiterRecoveryState(
    int SchemaVersion,
    DateTimeOffset AppliedAtUtc,
    string ExecutablePath,
    string ExecutableName,
    string ProfileName,
    bool ProfileCreatedByDcc,
    bool PreviousOverridePresent,
    uint PreviousValue,
    int TargetFps)
{
    public const int CurrentSchemaVersion = 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGlobal => string.IsNullOrWhiteSpace(ExecutableName);
}

public static class DccFrameLimiterRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DccFrameLimiterRecoveryState? TryRead()
    {
        try
        {
            if (!File.Exists(DccPaths.FrameLimiterRecoveryFile))
                return null;

            DccFrameLimiterRecoveryState? state = JsonSerializer.Deserialize<DccFrameLimiterRecoveryState>(
                File.ReadAllText(DccPaths.FrameLimiterRecoveryFile),
                JsonOptions);
            return state?.SchemaVersion == DccFrameLimiterRecoveryState.CurrentSchemaVersion
                ? state
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(DccFrameLimiterRecoveryState state)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporaryPath = DccPaths.FrameLimiterRecoveryFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, DccPaths.FrameLimiterRecoveryFile, overwrite: true);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(DccPaths.FrameLimiterRecoveryFile))
                File.Delete(DccPaths.FrameLimiterRecoveryFile);
        }
        catch
        {
            // A stale marker is safer than claiming that the NVIDIA profile was restored.
        }
    }
}
