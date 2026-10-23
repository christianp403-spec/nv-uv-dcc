using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record DccGameHistoryEntry(
    string Name,
    string Executable,
    string NvidiaProfileName,
    DateTimeOffset FirstActivatedUtc,
    DateTimeOffset LastActivatedUtc,
    int ActivationCount,
    DccControlMethod LastMethod,
    int LastTargetFps,
    bool LastFrameLimiterEnabled = false,
    int LastFrameLimitFps = 0,
    string GameKey = "")
{
    public string EffectiveGameKey => DccGameCatalog.NormalizeExecutable(
        string.IsNullOrWhiteSpace(GameKey) ? Executable : GameKey);
}

public sealed class DccGameHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _storeFile;
    private readonly Dictionary<string, DccGameHistoryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private DccGameHistoryStore(string storeFile)
    {
        _storeFile = storeFile;
    }

    public IReadOnlyCollection<DccGameHistoryEntry> Entries => _entries.Values;

    public static DccGameHistoryStore Load(string? storeFile = null)
    {
        storeFile ??= DccPaths.GameHistoryFile;
        var store = new DccGameHistoryStore(storeFile);
        try
        {
            if (!File.Exists(storeFile))
                return store;
            HistoryFile? file = JsonSerializer.Deserialize<HistoryFile>(
                File.ReadAllBytes(storeFile),
                JsonOptions);
            if (file?.SchemaVersion is not (1 or 2))
                return store;
            foreach (DccGameHistoryEntry rawEntry in file.Games)
            {
                DccGameHistoryEntry entry = rawEntry.LastFrameLimiterEnabled &&
                                            rawEntry.LastFrameLimitFps < DccFpsRange.Minimum
                    ? rawEntry with
                    {
                        LastFrameLimitFps = rawEntry.LastTargetFps is >= DccFpsRange.Minimum and <= DccFpsRange.Maximum
                            ? rawEntry.LastTargetFps
                            : 120
                    }
                    : rawEntry;
                if (IsValid(entry))
                    store._entries[entry.EffectiveGameKey] = entry with
                    {
                        Executable = DccGameCatalog.NormalizeExecutable(entry.Executable),
                        GameKey = entry.EffectiveGameKey
                    };
            }
        }
        catch
        {
            // A damaged history must not authorize hardware control.
        }
        return store;
    }

    public void RememberActivation(
        string name,
        string executable,
        string nvidiaProfileName,
        DccControlMethod method,
        int targetFps,
        DateTimeOffset? activatedAtUtc = null,
        bool frameLimiterEnabled = false,
        int frameLimitFps = 120,
        string gameKey = "")
    {
        string normalized = DccGameCatalog.NormalizeExecutable(executable);
        string normalizedGameKey = DccGameCatalog.NormalizeExecutable(
            string.IsNullOrWhiteSpace(gameKey) ? normalized : gameKey);
        if (DccGameCatalog.IsUnsupportedExecutable(normalized))
            throw new InvalidDataException("The executable is not eligible for DCC game history.");
        DateTimeOffset now = activatedAtUtc ?? DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(normalizedGameKey, out DccGameHistoryEntry? existing))
        {
            _entries[normalizedGameKey] = existing with
            {
                Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim(),
                Executable = normalized,
                GameKey = normalizedGameKey,
                NvidiaProfileName = string.IsNullOrWhiteSpace(nvidiaProfileName)
                    ? existing.NvidiaProfileName
                    : nvidiaProfileName.Trim(),
                LastActivatedUtc = now,
                ActivationCount = checked(existing.ActivationCount + 1),
                LastMethod = method,
                LastTargetFps = NormalizeTarget(method, targetFps),
                LastFrameLimiterEnabled = frameLimiterEnabled,
                LastFrameLimitFps = frameLimiterEnabled ? NormalizeFrameLimit(frameLimitFps) : 0
            };
        }
        else
        {
            _entries[normalizedGameKey] = new DccGameHistoryEntry(
                string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(normalized) : name.Trim(),
                normalized,
                nvidiaProfileName?.Trim() ?? string.Empty,
                now,
                now,
                1,
                method,
                NormalizeTarget(method, targetFps),
                frameLimiterEnabled,
                frameLimiterEnabled ? NormalizeFrameLimit(frameLimitFps) : 0,
                normalizedGameKey);
        }
        Save();
    }

    public IReadOnlyList<DccGameDefinition> ToGameDefinitions() => _entries.Values
        .Select(entry => new DccGameDefinition(
            entry.Name,
            entry.Executable,
            [],
            Ambiguous: false,
            UserAdded: true,
            NvidiaProfileName: entry.NvidiaProfileName,
            StableGameKey: string.Equals(
                entry.EffectiveGameKey,
                DccGameCatalog.NormalizeExecutable(entry.Executable),
                StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : entry.EffectiveGameKey))
        .ToArray();

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_storeFile);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporary = _storeFile + ".tmp";
        var file = new HistoryFile(
            2,
            _entries.Values.OrderByDescending(entry => entry.LastActivatedUtc).ToList());
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
        File.Move(temporary, _storeFile, overwrite: true);
    }

    private static bool IsValid(DccGameHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Name) &&
        !string.IsNullOrWhiteSpace(entry.Executable) &&
        !DccGameCatalog.IsUnsupportedExecutable(entry.Executable) &&
        DccGameCatalog.NormalizeExecutable(entry.Executable).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        entry.ActivationCount > 0 &&
        (entry.LastTargetFps == DccLearnedProfile.AutomaticTargetFps ||
         entry.LastTargetFps is >= DccFpsRange.Minimum and <= DccFpsRange.Maximum) &&
        (!entry.LastFrameLimiterEnabled ||
         entry.LastFrameLimitFps is >= DccFpsRange.Minimum and <= DccFpsRange.Maximum);

    private static int NormalizeTarget(DccControlMethod method, int targetFps) =>
        method == DccControlMethod.DccGovernor
            ? DccLearnedProfile.AutomaticTargetFps
            : Math.Clamp(targetFps, DccFpsRange.Minimum, DccFpsRange.Maximum);

    private static int NormalizeFrameLimit(int frameLimitFps) =>
        Math.Clamp(frameLimitFps, DccFpsRange.Minimum, DccFpsRange.Maximum);

    private sealed record HistoryFile(int SchemaVersion, List<DccGameHistoryEntry> Games);
}

