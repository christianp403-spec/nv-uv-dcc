using System.Text.Json;
using System.Text.Json.Serialization;

namespace NvUv.Dcc.Core;

public sealed record DccGameDefinition(
    string Name,
    string Executable,
    IReadOnlyList<string> Aliases,
    bool Ambiguous,
    bool UserAdded = false,
    string NvidiaProfileName = "",
    string StableGameKey = "",
    IReadOnlyList<string>? InstallPathHints = null)
{
    public IReadOnlyList<string> EffectiveInstallPathHints => InstallPathHints ?? [];

    public IEnumerable<string> AllExecutables()
    {
        yield return Executable;
        foreach (string alias in Aliases)
            yield return alias;
    }
}

public sealed class DccGameCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private static readonly HashSet<string> UnsupportedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "bssndrpt64.exe", "bssndrpt.exe", "crashreportclient.exe",
        "crashreportclienteditor.exe", "unitycrashhandler64.exe",
        "unitycrashhandler32.exe", "werfault.exe", "crashpad_handler.exe",
        "applicationframehost.exe", "gamingservices.exe", "gamingservicesnet.exe",
        "gameinputsvc.exe", "gameinputredistservice.exe", "xboxpcapp.exe",
        "xboxapp.exe", "gamingapp_services.exe", "gamebar.exe",
        "gamebarftserver.exe", "gamebarpresencewriter.exe", "xboxgamebarwidgets.exe",

        // Windows shell and desktop applications can own a DRS profile and render
        // through DXGI. Neither signal makes them safe DCC control targets.
        "explorer.exe", "searchhost.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "textinputhost.exe", "dwm.exe", "lockapp.exe",
        "systemsettings.exe", "taskmgr.exe", "notepad.exe",

        // Launchers and storefront helpers are deliberately excluded. DCC waits
        // for the actual foreground game executable instead.
        "steam.exe", "steamservice.exe", "steamwebhelper.exe", "gldriverquery64.exe",
        "vulkandriverquery64.exe", "eadesktop.exe", "eacefsubprocess.exe",
        "ealocalhostsvc.exe", "eabackgroundservice.exe", "link2ea.exe", "origin.exe",
        "originclient.exe", "originclientservice.exe", "originthinsetupinternal.exe",
        "originwebhelperservice.exe", "igoproxy.exe", "igoproxy64.exe",
        "battle.net.exe", "epicgameslauncher.exe", "epicwebhelper.exe",
        "unrealcefsubprocess.exe", "eosoverlayrenderer-win64-shipping.exe",
        "galaxyclient.exe", "galaxyclient helper.exe", "galaxycommunication.exe",
        "galaxyclientservice.exe", "galaxyupdater.exe",
        "gog galaxy notifications renderer.exe", "ubisoftconnect.exe",
        "ubisoftconnectwebcore.exe", "ubisoftgamelauncher.exe",
        "ubisoftgamelauncher64.exe", "uplay.exe", "upc.exe", "uplaywebcore.exe",
        "gamingapp.exe", "microsoft.xboxapp_8wekyb3d8bbwe.exe", "xboxidp.exe",
        "microsoft.xboxidentityprovider_12.30.5001.0_x64__8wekyb3d8bbwe.exe",
        "rockstar-games-launcher.exe", "rockstarservice.exe", "socialclubhelper.exe",
        "riotclientservices.exe", "riotclientux.exe", "riotclientuxrender.exe",
        "amazon games.exe", "amazon games ui.exe", "amazon games services.exe",
        "playnite.desktopapp.exe", "playnite.fullscreenapp.exe", "heroic.exe",
        "itch.exe", "wgc.exe", "wgc_api.exe", "paradox launcher.exe",
        "hoyoplay.exe", "hyp.exe", "2klauncher.exe", "bethesdanetlauncher.exe",

        // Browsers, overlays and GPU monitoring tools also produce fresh present
        // events, but must never receive a game profile or hardware-control state.
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "vivaldi.exe",
        "opera.exe", "opera_gx.exe", "discord.exe", "slack.exe", "teams.exe",
        "nvidia app.exe", "nvcplui.exe", "nvcontainer.exe", "nvidia overlay.exe", "nvsphelper64.exe",
        "msiafterburner.exe", "rtss.exe", "rtsshooksloader.exe",
        "rtsshooksloader64.exe", "hwinfo64.exe", "icue.exe", "lghub.exe",
        "signalrgb.exe", "fancontrol.exe"
    };

    private readonly Dictionary<string, List<DccGameDefinition>> _lookup =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DccGameDefinition> _games = [];

    public IReadOnlyList<DccGameDefinition> Games => _games;
    public string? Version { get; private set; }
    public string? Generated { get; private set; }
    public string SourcePath { get; private set; } = string.Empty;

    public static DccGameCatalog Load(string? bundledPath = null)
    {
        var catalog = new DccGameCatalog();
        string defaultBundledPath = Path.Combine(AppContext.BaseDirectory, "Data", "GameDatabase.json");
        string? selectedPath = File.Exists(DccPaths.SharedGameDatabaseFile)
            ? DccPaths.SharedGameDatabaseFile
            : File.Exists(bundledPath ?? defaultBundledPath)
                ? bundledPath ?? defaultBundledPath
                : null;

        if (selectedPath is not null)
            catalog.LoadSharedDatabase(selectedPath);
        else
            catalog.LoadEmbeddedDatabase();

        catalog.ApplyBuiltInIdentityHints();
        catalog.LoadNvUvUserGamesReadOnly();
        catalog.LoadDccCustomGames();
        catalog.RebuildLookup();
        return catalog;
    }

    public DccGameDefinition? FindByExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        return FindCandidatesByExecutable(executable).FirstOrDefault();
    }

    public IReadOnlyList<DccGameDefinition> FindCandidatesByExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return [];

        return _lookup.TryGetValue(
            NormalizeExecutable(executable),
            out List<DccGameDefinition>? games)
            ? games.ToArray()
            : [];
    }

    public DccGameDefinition AddCustomGame(string name, string executable)
    {
        string normalized = NormalizeExecutable(executable);
        if (IsUnsupportedExecutable(normalized))
            throw new InvalidOperationException("Diese EXE ist ein gemeinsamer Windows-, Launcher- oder Crash-Helper und kein eindeutiges Spiel.");
        if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Es muss eine EXE-Datei ausgewählt werden.");
        if (FindByExecutable(normalized) is not null)
            throw new InvalidOperationException("Diese EXE ist bereits in der gemeinsamen Spieleliste enthalten.");

        var custom = LoadCustomStore();
        custom.Games.RemoveAll(game =>
            string.Equals(NormalizeExecutable(game.Executable), normalized, StringComparison.OrdinalIgnoreCase));
        custom.Games.Add(new CustomGameRecord
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(normalized) : name.Trim(),
            Executable = normalized
        });
        SaveCustomStore(custom);

        var definition = new DccGameDefinition(
            custom.Games[^1].Name,
            normalized,
            [],
            IsAmbiguous(normalized, explicitAmbiguous: false),
            UserAdded: true);
        _games.Add(definition);
        RebuildLookup();
        return definition;
    }

    public void AddRuntimeGame(DccGameDefinition game)
    {
        AddRuntimeGames([game]);
    }

    public void AddRuntimeGames(IEnumerable<DccGameDefinition> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        foreach (DccGameDefinition game in games)
        {
            if (game is null)
                continue;
            string executable = NormalizeExecutable(game.Executable);
            if (IsUnsupportedExecutable(executable) ||
                !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] aliases = game.Aliases
                .Select(NormalizeExecutable)
                .Where(alias => !IsUnsupportedExecutable(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] installPathHints = game.EffectiveInstallPathHints
                .Where(static hint => !string.IsNullOrWhiteSpace(hint))
                .Select(static hint => hint.Trim())
                .Where(static hint => hint.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DccGameDefinition normalized = game with
            {
                Executable = executable,
                Aliases = aliases,
                StableGameKey = NormalizeStableGameKey(game.StableGameKey),
                InstallPathHints = installPathHints
            };
            AddOrMergeDefinition(normalized);
        }
        RebuildLookup();
    }

    public static string NormalizeExecutable(string executable)
    {
        string fileName = Path.GetFileName(executable.Trim()).ToLowerInvariant();
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".exe";
    }

    public static string CreateStableGameKey(string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return string.Empty;
        string token = new(
            stableId.Trim().ToLowerInvariant()
                .Select(static character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray());
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);
        token = token.Trim('-');
        return token.Length == 0
            ? string.Empty
            : NormalizeExecutable("dcc-game-" + token);
    }

    public static string NormalizeStableGameKey(string? stableGameKey)
    {
        if (string.IsNullOrWhiteSpace(stableGameKey))
            return string.Empty;
        string normalized = NormalizeExecutable(stableGameKey);
        return normalized.StartsWith("dcc-", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : CreateStableGameKey(Path.GetFileNameWithoutExtension(normalized));
    }

    public static bool IsUnsupportedExecutable(string executable)
    {
        string normalized = NormalizeExecutable(executable);
        return UnsupportedExecutables.Contains(normalized) ||
               normalized.StartsWith("crashsender", StringComparison.OrdinalIgnoreCase);
    }

    private void LoadSharedDatabase(string path)
    {
        byte[] json = File.ReadAllBytes(path);
        DatabaseRoot? root = JsonSerializer.Deserialize<DatabaseRoot>(json, JsonOptions);
        LoadDatabase(root, path);
    }

    private void LoadEmbeddedDatabase()
    {
        using Stream? stream = typeof(DccGameCatalog).Assembly.GetManifestResourceStream(
            "NvUv.Dcc.Core.Data.GameDatabase.json");
        if (stream is null)
            return;
        DatabaseRoot? root = JsonSerializer.Deserialize<DatabaseRoot>(stream, JsonOptions);
        LoadDatabase(root, "embedded://NvUv.Dcc.Core.Data.GameDatabase.json");
    }

    private void LoadDatabase(DatabaseRoot? root, string sourcePath)
    {
        if (root?.Games is null || root.Games.Count == 0)
            throw new InvalidDataException("Die NV-UV-Spieledatenbank enthält keine Spiele.");

        Version = root.Version;
        Generated = root.Generated;
        SourcePath = sourcePath;
        foreach (DatabaseGame game in root.Games)
        {
            if (string.IsNullOrWhiteSpace(game.Name) || string.IsNullOrWhiteSpace(game.Process))
                continue;

            string executable = NormalizeExecutable(game.Process);
            if (IsUnsupportedExecutable(executable))
                continue;

            string[] aliases = (game.Aliases ?? [])
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(NormalizeExecutable)
                .Where(alias => !IsUnsupportedExecutable(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _games.Add(new DccGameDefinition(
                game.Name.Trim(),
                executable,
                aliases,
                IsAmbiguous(executable, game.Ambiguous),
                StableGameKey: CreateStableGameKey(game.Id),
                InstallPathHints: game.InstallPathHints));
        }
    }

    private void ApplyBuiltInIdentityHints()
    {
        for (int index = 0; index < _games.Count; index++)
        {
            DccGameDefinition game = _games[index];
            if (!string.Equals(
                    NormalizeExecutable(game.Executable),
                    "kingdomcome.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                !game.Name.Contains("Deliverance II", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _games[index] = game with
            {
                StableGameKey = CreateStableGameKey("kingdom-come-deliverance-ii"),
                InstallPathHints = game.EffectiveInstallPathHints
                    .Concat(["KingdomComeDeliverance2"])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }

    private void LoadNvUvUserGamesReadOnly()
    {
        if (!File.Exists(DccPaths.NvUvGameOverridesFile))
            return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(DccPaths.NvUvGameOverridesFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (JsonProperty item in document.RootElement.EnumerateObject())
            {
                JsonElement value = item.Value;
                if (!TryGetBoolean(value, "UserAdded"))
                    continue;

                string executable = NormalizeExecutable(item.Name);
                if (IsUnsupportedExecutable(executable))
                    continue;
                string name = TryGetString(value, "UserAddedName") ?? Path.GetFileNameWithoutExtension(executable);
                AddIfMissing(new DccGameDefinition(
                    name,
                    executable,
                    [],
                    IsAmbiguous(executable, explicitAmbiguous: false),
                    UserAdded: true));
            }
        }
        catch
        {
            // NV-UV owns this file. A partial or newer schema must never stop DCC.
        }
    }

    private void LoadDccCustomGames()
    {
        foreach (CustomGameRecord custom in LoadCustomStore().Games)
        {
            if (string.IsNullOrWhiteSpace(custom.Executable))
                continue;
            string executable = NormalizeExecutable(custom.Executable);
            if (IsUnsupportedExecutable(executable))
                continue;
            AddIfMissing(new DccGameDefinition(
                string.IsNullOrWhiteSpace(custom.Name) ? Path.GetFileNameWithoutExtension(executable) : custom.Name,
                executable,
                [],
                IsAmbiguous(executable, explicitAmbiguous: false),
                UserAdded: true));
        }
    }

    private void AddIfMissing(DccGameDefinition game)
    {
        if (_games.Any(existing => existing.AllExecutables().Any(executable =>
                string.Equals(NormalizeExecutable(executable), game.Executable, StringComparison.OrdinalIgnoreCase))))
            return;
        _games.Add(game);
    }

    private void RebuildLookup()
    {
        _lookup.Clear();
        foreach (DccGameDefinition game in _games)
        {
            foreach (string executable in game.AllExecutables())
            {
                string normalized = NormalizeExecutable(executable);
                if (!IsUnsupportedExecutable(normalized))
                {
                    if (!_lookup.TryGetValue(normalized, out List<DccGameDefinition>? matches))
                    {
                        matches = [];
                        _lookup.Add(normalized, matches);
                    }
                    if (!matches.Contains(game))
                        matches.Add(game);
                }
            }
        }
    }

    private void AddOrMergeDefinition(DccGameDefinition game)
    {
        int existingIndex = _games.FindIndex(existing =>
            string.Equals(
                NormalizeExecutable(existing.Executable),
                NormalizeExecutable(game.Executable),
                StringComparison.OrdinalIgnoreCase) &&
            ((!string.IsNullOrWhiteSpace(existing.StableGameKey) &&
              !string.IsNullOrWhiteSpace(game.StableGameKey) &&
              string.Equals(existing.StableGameKey, game.StableGameKey, StringComparison.OrdinalIgnoreCase)) ||
             string.Equals(existing.Name, game.Name, StringComparison.OrdinalIgnoreCase)));
        if (existingIndex < 0)
        {
            _games.Add(game);
            return;
        }

        DccGameDefinition existing = _games[existingIndex];
        _games[existingIndex] = existing with
        {
            Aliases = existing.Aliases
                .Concat(game.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Ambiguous = existing.Ambiguous || game.Ambiguous,
            UserAdded = existing.UserAdded || game.UserAdded,
            NvidiaProfileName = string.IsNullOrWhiteSpace(existing.NvidiaProfileName)
                ? game.NvidiaProfileName
                : existing.NvidiaProfileName,
            StableGameKey = string.IsNullOrWhiteSpace(existing.StableGameKey)
                ? game.StableGameKey
                : existing.StableGameKey,
            InstallPathHints = existing.EffectiveInstallPathHints
                .Concat(game.EffectiveInstallPathHints)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static bool IsAmbiguous(string executable, bool explicitAmbiguous)
    {
        string bareName = Path.GetFileNameWithoutExtension(executable);
        return explicitAmbiguous || bareName.Length <= 3;
    }

    private static bool TryGetBoolean(JsonElement value, string propertyName) =>
        TryGetProperty(value, propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.True;

    private static string? TryGetString(JsonElement value, string propertyName) =>
        TryGetProperty(value, propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement property)
    {
        foreach (JsonProperty candidate in value.EnumerateObject())
        {
            if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }
        property = default;
        return false;
    }

    private static CustomGameStore LoadCustomStore()
    {
        try
        {
            if (!File.Exists(DccPaths.CustomGamesFile))
                return new CustomGameStore();
            return JsonSerializer.Deserialize<CustomGameStore>(
                       File.ReadAllBytes(DccPaths.CustomGamesFile),
                       JsonOptions) ?? new CustomGameStore();
        }
        catch
        {
            return new CustomGameStore();
        }
    }

    private static void SaveCustomStore(CustomGameStore store)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporary = DccPaths.CustomGamesFile + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(store, JsonOptions));
        File.Move(temporary, DccPaths.CustomGamesFile, overwrite: true);
    }

    private sealed class DatabaseRoot
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("generated")]
        public string? Generated { get; set; }

        [JsonPropertyName("games")]
        public List<DatabaseGame> Games { get; set; } = [];
    }

    private sealed class DatabaseGame
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("process")]
        public string Process { get; set; } = string.Empty;

        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }

        [JsonPropertyName("ambiguous")]
        public bool Ambiguous { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("installPathHints")]
        public List<string>? InstallPathHints { get; set; }
    }

    private sealed class CustomGameStore
    {
        public int SchemaVersion { get; set; } = 1;
        public List<CustomGameRecord> Games { get; set; } = [];
    }

    private sealed class CustomGameRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Executable { get; set; } = string.Empty;
    }
}
