using System.Text.Json;

namespace NvUv.Dcc.Core;

public enum DccControlMethod
{
    DccGovernor = 0,
    NvidiaEfficiency = 1,
    Baseline = 2
}

public enum DccActivationScope
{
    Persistent = 0,
    PerGame = 1
}

public static class DccUiScale
{
    private static readonly int[] Steps = [100, 110, 125, 150, 175, 200, 225, 250, 300];

    public static int Clamp(int percent) => Math.Clamp(percent, Steps[0], Steps[^1]);

    public static int Next(int currentPercent)
    {
        foreach (int step in Steps)
        {
            if (step > currentPercent)
                return step;
        }

        return Steps[0];
    }

    public static int Previous(int currentPercent)
    {
        for (int index = Steps.Length - 1; index >= 0; index--)
        {
            if (Steps[index] < currentPercent)
                return Steps[index];
        }

        return Steps[^1];
    }
}

public sealed record DccUserSettings(
    int SchemaVersion = 10,
    int TargetFps = 120,
    bool GovernorEnabled = false,
    string Language = "",
    int UiScalePercent = 100,
    DccControlMethod ControlMethod = DccControlMethod.DccGovernor,
    bool FrameLimiterEnabled = false,
    DccActivationScope ActivationScope = DccActivationScope.Persistent,
    bool PerformanceLoggingEnabled = false,
    bool StartMinimizedToTray = false,
    int NvidiaTargetFps = 120,
    int FrameLimitFps = 120,
    bool NvidiaVsyncEnabled = false,
    bool NvidiaTargetFpsManual = false)
{
    public DccUserSettings WithValidatedValues()
    {
        DccControlMethod method = ControlMethod is DccControlMethod.DccGovernor or DccControlMethod.NvidiaEfficiency
            ? ControlMethod
            : DccControlMethod.DccGovernor;
        int minimumTarget = method == DccControlMethod.NvidiaEfficiency
            ? DccFpsRange.NvidiaEfficiencyFloor
            : DccFpsRange.Minimum;
        int maximumTarget = method == DccControlMethod.NvidiaEfficiency
            ? DccFpsRange.NvidiaEfficiencyMaximum
            : DccFpsRange.Maximum;
        return this with
        {
            SchemaVersion = 10,
            TargetFps = Math.Clamp(TargetFps, minimumTarget, maximumTarget),
            NvidiaTargetFps = Math.Clamp(
                NvidiaTargetFps,
                DccFpsRange.NvidiaEfficiencyFloor,
                DccFpsRange.NvidiaEfficiencyMaximum),
            FrameLimitFps = Math.Clamp(
                FrameLimitFps,
                DccFpsRange.Minimum,
                DccFpsRange.Maximum),
            Language = NormalizeLanguage(Language),
            UiScalePercent = DccUiScale.Clamp(UiScalePercent),
            ControlMethod = method,
            ActivationScope = Enum.IsDefined(ActivationScope) ? ActivationScope : DccActivationScope.Persistent
        };
    }

    public DccUserSettings WithMigratedValues()
    {
        DccUserSettings migrated = this;
        if (SchemaVersion < 4)
        {
            migrated = migrated with
            {
                ControlMethod = DccControlMethod.DccGovernor,
                FrameLimiterEnabled = false,
                ActivationScope = DccActivationScope.Persistent
            };
        }
        if (SchemaVersion < 5)
            migrated = migrated with { PerformanceLoggingEnabled = false };
        if (SchemaVersion < 6)
        {
            migrated = migrated with
            {
                ControlMethod = DccControlMethod.DccGovernor,
                FrameLimiterEnabled = false,
                PerformanceLoggingEnabled = false
            };
        }
        if (SchemaVersion < 7)
            migrated = migrated with { StartMinimizedToTray = false };
        if (SchemaVersion < 8)
        {
            migrated = migrated with
            {
                NvidiaTargetFps = Math.Clamp(
                    TargetFps,
                    DccFpsRange.NvidiaEfficiencyFloor,
                    DccFpsRange.NvidiaEfficiencyMaximum),
                FrameLimitFps = Math.Clamp(
                    TargetFps,
                    DccFpsRange.Minimum,
                    DccFpsRange.Maximum)
            };
        }
        if (SchemaVersion < 9)
            migrated = migrated with { NvidiaVsyncEnabled = false };
        if (SchemaVersion < 10)
        {
            migrated = migrated with
            {
                // Legacy defaults become automatic. Preserve other lower,
                // plausibly user-selected targets as manual overrides.
                NvidiaTargetFpsManual = NvidiaTargetFps is not 120 and < DccFpsRange.NvidiaEfficiencyMaximum
            };
        }

        return migrated.WithValidatedValues();
    }

    private static string NormalizeLanguage(string? language)
    {
        string normalized = language?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is "de" or "en" or "ru" or "es")
            return normalized;

        string systemLanguage = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return systemLanguage is "de" or "en" or "ru" or "es" ? systemLanguage : "en";
    }
}

public static class DccUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DccUserSettings Load()
    {
        try
        {
            if (!File.Exists(DccPaths.SettingsFile))
                return new DccUserSettings().WithValidatedValues();
            return (JsonSerializer.Deserialize<DccUserSettings>(
                        File.ReadAllBytes(DccPaths.SettingsFile),
                        JsonOptions) ?? new DccUserSettings())
                .WithMigratedValues();
        }
        catch
        {
            return new DccUserSettings().WithValidatedValues();
        }
    }

    public static void Save(DccUserSettings settings)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporary = DccPaths.SettingsFile + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(settings.WithValidatedValues(), JsonOptions));
        File.Move(temporary, DccPaths.SettingsFile, overwrite: true);
    }
}
