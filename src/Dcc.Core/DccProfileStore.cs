using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccLearnedProfile(
    string GpuUuid,
    string GpuName,
    string DriverVersion,
    string GameName,
    string Executable,
    int TargetFps,
    int LearnedClockMHz,
    double LastVerifiedFps,
    int LearningWindows,
    DateTimeOffset UpdatedAtUtc,
    int? LowLoadClockMHz = null,
    int? ResumeClockMHz = null,
    int? ResumeConfidence = null,
    int? ClockChangeSensitivityScore = null)
{
    public const int AutomaticTargetFps = 0;
    public const int MaximumResumeConfidence = 3;

    public string Key => CreateKey(GpuUuid, Executable, TargetFps);

    public int EffectiveResumeConfidence => ResumeConfidence ??
        (ResumeClockMHz.HasValue && LearningWindows >= 10 ? 1 : 0);

    public int EffectiveClockChangeSensitivityScore => Math.Clamp(
        ClockChangeSensitivityScore ?? 0,
        0,
        DccAutomaticClockGovernor.MaximumClockChangeSensitivityScore);

    public static string CreateKey(string gpuUuid, string executable, int targetFps) =>
        $"{gpuUuid.Trim().ToLowerInvariant()}|{DccGameCatalog.NormalizeExecutable(executable)}|{targetFps}";
}

public sealed record DccProfileImportResult(int Added, int Updated, int Skipped, string BackupPath);
public sealed record DccProfileResetResult(int Removed, string BackupPath);

public sealed class DccProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, DccLearnedProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storeFile;
    private readonly string _backupsDirectory;

    private DccProfileStore(string storeFile, string backupsDirectory)
    {
        _storeFile = storeFile;
        _backupsDirectory = backupsDirectory;
    }

    public IReadOnlyCollection<DccLearnedProfile> Profiles => _profiles.Values;

    public static DccProfileStore Load(string? storeFile = null, string? backupsDirectory = null)
    {
        storeFile ??= DccPaths.ProfileStoreFile;
        backupsDirectory ??= DccPaths.BackupsDirectory;
        var store = new DccProfileStore(storeFile, backupsDirectory);
        try
        {
            if (!File.Exists(storeFile))
                return store;
            ProfileFile? file = JsonSerializer.Deserialize<ProfileFile>(
                File.ReadAllBytes(storeFile),
                JsonOptions);
            if (file?.SchemaVersion != 1)
                return store;
            foreach (DccLearnedProfile profile in file.Profiles.Where(IsValid))
                store._profiles[profile.Key] = profile;
        }
        catch
        {
            // A bad user file never authorizes a cap. Import/backup can recover it.
        }
        return store;
    }

    public DccLearnedProfile? Find(string gpuUuid, string executable, int targetFps)
    {
        _profiles.TryGetValue(DccLearnedProfile.CreateKey(gpuUuid, executable, targetFps), out DccLearnedProfile? profile);
        return profile;
    }

    public void Upsert(DccLearnedProfile profile)
    {
        profile = NormalizeForPersistence(profile);
        if (!IsValid(profile))
        {
            throw new InvalidDataException(
                $"The learned DCC profile is invalid: clock={profile.LearnedClockMHz}; " +
                $"low_load={profile.LowLoadClockMHz}; resume={profile.ResumeClockMHz}; " +
                $"fps={profile.LastVerifiedFps}; windows={profile.LearningWindows}.");
        }
        _profiles[profile.Key] = profile;
        Save();
    }

    public DccProfileResetResult RemoveForGame(string gpuUuid, string gameKey)
    {
        if (string.IsNullOrWhiteSpace(gpuUuid))
            throw new ArgumentException("A GPU identity is required.", nameof(gpuUuid));
        if (string.IsNullOrWhiteSpace(gameKey))
            throw new ArgumentException("A game identity is required.", nameof(gameKey));

        string normalizedGpu = gpuUuid.Trim();
        string normalizedGame = DccGameCatalog.NormalizeExecutable(gameKey);
        DccLearnedProfile[] removed = _profiles.Values
            .Where(profile =>
                string.Equals(profile.GpuUuid.Trim(), normalizedGpu, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    DccGameCatalog.NormalizeExecutable(profile.Executable),
                    normalizedGame,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (removed.Length == 0)
            return new DccProfileResetResult(0, string.Empty);

        string backup = CreateBackup();
        foreach (DccLearnedProfile profile in removed)
            _profiles.Remove(profile.Key);

        try
        {
            Save();
        }
        catch
        {
            foreach (DccLearnedProfile profile in removed)
                _profiles[profile.Key] = profile;
            throw;
        }

        return new DccProfileResetResult(removed.Length, backup);
    }

    public void UpdateResumeConfidence(
        string gpuUuid,
        string executable,
        int targetFps,
        int resumeConfidence)
    {
        string key = DccLearnedProfile.CreateKey(gpuUuid, executable, targetFps);
        if (!_profiles.TryGetValue(key, out DccLearnedProfile? profile))
            return;

        int confidence = Math.Clamp(
            resumeConfidence,
            0,
            DccLearnedProfile.MaximumResumeConfidence);
        if (profile.ResumeConfidence == confidence)
            return;

        _profiles[key] = profile with
        {
            ResumeConfidence = confidence,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        Save();
    }

    public string CreateBackup()
    {
        Directory.CreateDirectory(_backupsDirectory);
        string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        string path = Path.Combine(_backupsDirectory, $"dcc-profiles-{timestamp}.json");
        WriteExport(path, _profiles.Values);
        return path;
    }

    public void Export(string destinationPath) => WriteExport(destinationPath, _profiles.Values);

    public DccProfileImportResult Import(string sourcePath)
    {
        byte[] bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Die Profildatei ist zu groß.");

        ProfileExport? export = JsonSerializer.Deserialize<ProfileExport>(bytes, JsonOptions);
        if (export?.SchemaVersion != 1 || export.Profiles is null)
            throw new InvalidDataException("Unbekanntes DCC-Profilformat.");
        if (export.Profiles.Count > 10_000)
            throw new InvalidDataException("Die Profildatei enthält zu viele Einträge.");

        string backup = CreateBackup();
        int added = 0;
        int updated = 0;
        int skipped = 0;
        foreach (DccLearnedProfile profile in export.Profiles)
        {
            if (!IsValid(profile))
            {
                skipped++;
                continue;
            }

            if (_profiles.TryGetValue(profile.Key, out DccLearnedProfile? existing))
            {
                if (profile.UpdatedAtUtc <= existing.UpdatedAtUtc)
                {
                    skipped++;
                    continue;
                }
                updated++;
            }
            else
            {
                added++;
            }
            _profiles[profile.Key] = profile;
        }

        Save();
        return new DccProfileImportResult(added, updated, skipped, backup);
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(_storeFile));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporary = _storeFile + ".tmp";
        var file = new ProfileFile(1, _profiles.Values.OrderBy(profile => profile.GameName).ToList());
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
        File.Move(temporary, _storeFile, overwrite: true);
    }

    private static void WriteExport(string path, IEnumerable<DccLearnedProfile> profiles)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        var export = new ProfileExport(1, DateTimeOffset.UtcNow, profiles.ToList());
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static DccLearnedProfile NormalizeForPersistence(DccLearnedProfile profile)
    {
        int? lowLoadClock = profile.LowLoadClockMHz;
        if (lowLoadClock is < 100 or > 10_000 ||
            (lowLoadClock.HasValue && lowLoadClock.Value >= profile.LearnedClockMHz))
        {
            lowLoadClock = null;
        }

        int? resumeClock = profile.ResumeClockMHz;
        if (resumeClock is < 100 or > 10_000)
            resumeClock = null;
        else if (resumeClock.HasValue &&
                 profile.LearnedClockMHz is >= 100 and <= 10_000 &&
                 resumeClock.Value > profile.LearnedClockMHz)
        {
            resumeClock = profile.LearnedClockMHz;
        }

        int? resumeConfidence = profile.ResumeConfidence.HasValue
            ? Math.Clamp(profile.ResumeConfidence.Value, 0, DccLearnedProfile.MaximumResumeConfidence)
            : null;
        int? clockChangeSensitivity = profile.ClockChangeSensitivityScore.HasValue
            ? Math.Clamp(
                profile.ClockChangeSensitivityScore.Value,
                0,
                DccAutomaticClockGovernor.MaximumClockChangeSensitivityScore)
            : null;
        double verifiedFps = double.IsFinite(profile.LastVerifiedFps)
            ? Math.Clamp(profile.LastVerifiedFps, 0, 10_000)
            : 0;

        return profile with
        {
            GpuUuid = profile.GpuUuid.Trim(),
            GpuName = profile.GpuName.Trim(),
            DriverVersion = profile.DriverVersion.Trim(),
            GameName = profile.GameName.Trim(),
            Executable = string.IsNullOrWhiteSpace(profile.Executable)
                ? profile.Executable
                : DccGameCatalog.NormalizeExecutable(profile.Executable),
            LastVerifiedFps = verifiedFps,
            LearningWindows = Math.Clamp(profile.LearningWindows, 0, 1_000_000),
            LowLoadClockMHz = lowLoadClock,
            ResumeClockMHz = resumeClock,
            ResumeConfidence = resumeConfidence,
            ClockChangeSensitivityScore = clockChangeSensitivity
        };
    }

    private static bool IsValid(DccLearnedProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.GpuUuid) &&
        !string.IsNullOrWhiteSpace(profile.GameName) &&
        !string.IsNullOrWhiteSpace(profile.Executable) &&
        DccGameCatalog.NormalizeExecutable(profile.Executable).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        (profile.TargetFps == DccLearnedProfile.AutomaticTargetFps ||
         profile.TargetFps is >= DccFpsRange.Minimum and <= DccFpsRange.Maximum) &&
        profile.LearnedClockMHz is >= 100 and <= 10_000 &&
        (!profile.LowLoadClockMHz.HasValue ||
         (profile.LowLoadClockMHz.Value is >= 100 and <= 10_000 &&
          profile.LowLoadClockMHz.Value < profile.LearnedClockMHz)) &&
        (!profile.ResumeClockMHz.HasValue ||
         profile.ResumeClockMHz.Value is >= 100 and <= 10_000) &&
        (!profile.ResumeConfidence.HasValue ||
         profile.ResumeConfidence.Value is >= 0 and <= DccLearnedProfile.MaximumResumeConfidence) &&
        (!profile.ClockChangeSensitivityScore.HasValue ||
         profile.ClockChangeSensitivityScore.Value is >= 0 and <=
             DccAutomaticClockGovernor.MaximumClockChangeSensitivityScore) &&
        profile.LastVerifiedFps is >= 0 and <= 10_000 &&
        profile.LearningWindows is >= 0 and <= 1_000_000;

    private sealed record ProfileFile(int SchemaVersion, List<DccLearnedProfile> Profiles);
    private sealed record ProfileExport(int SchemaVersion, DateTimeOffset ExportedAtUtc, List<DccLearnedProfile> Profiles);
}
