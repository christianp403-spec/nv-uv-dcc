using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NvUv.Dcc.Core;

public sealed record DccDetectedGame(
    DccGameDefinition Game,
    string Executable,
    int ProcessId,
    double? FramesPerSecond,
    bool FpsFresh,
    string DetectionSource,
    string ExecutablePath = "",
    bool IsForeground = false,
    int PresentationProcessId = 0,
    string PresentationExecutable = "",
    string GameKeyOverride = "",
    string GameIdentitySource = "executable")
{
    public int FpsProcessId => PresentationProcessId > 0
        ? PresentationProcessId
        : ProcessId;

    public string GameKey => DccGameCatalog.NormalizeExecutable(
        string.IsNullOrWhiteSpace(GameKeyOverride)
            ? Game.Executable
            : GameKeyOverride);
}

public sealed class DccGameDetector
{
    private readonly DccGameCatalog _catalog;

    public DccGameDetector(DccGameCatalog catalog)
        : this(catalog.Games)
    {
    }

    public DccGameDetector(IEnumerable<DccGameDefinition> games)
    {
        _catalog = new DccGameCatalog();
        _catalog.AddRuntimeGames(games);
    }

    public DccDetectedGame? DetectProcess()
        => DetectProcess(GetForegroundProcessId());

    public DccDetectedGame? DetectProcess(int foregroundProcessId)
    {
        (string? foregroundExecutable, string foregroundPath) = GetExecutable(foregroundProcessId);
        return DetectProcessCore(
            foregroundProcessId,
            foregroundExecutable,
            foregroundPath,
            processes: null);
    }

    public DccDetectedGame? DetectProcess(
        int foregroundProcessId,
        string? foregroundExecutable,
        string foregroundPath,
        IEnumerable<DccProcessTreeEntry> processes)
        => DetectProcessCore(foregroundProcessId, foregroundExecutable, foregroundPath, processes);

    private DccDetectedGame? DetectProcessCore(
        int foregroundProcessId,
        string? foregroundExecutable,
        string foregroundPath,
        IEnumerable<DccProcessTreeEntry>? processes)
    {
        foregroundExecutable = string.IsNullOrWhiteSpace(foregroundExecutable)
            ? null
            : DccGameCatalog.NormalizeExecutable(foregroundExecutable);
        IReadOnlyList<DccGameDefinition> foregroundCandidates =
            _catalog.FindCandidatesByExecutable(foregroundExecutable);
        if (foregroundCandidates.Count > 0)
        {
            DccGameDefinition foregroundGame = ResolveKnownExecutableCollision(
                foregroundCandidates,
                foregroundExecutable!,
                foregroundPath,
                out string gameKeyOverride,
                out string identitySource);
            return new DccDetectedGame(
                foregroundGame,
                foregroundExecutable!,
                foregroundProcessId,
                null,
                false,
                string.IsNullOrWhiteSpace(foregroundGame.NvidiaProfileName)
                    ? "Known game"
                    : "NVIDIA DRS",
                foregroundPath,
                IsForeground: true,
                GameKeyOverride: gameKeyOverride,
                GameIdentitySource: identitySource);
        }

        if (foregroundExecutable is null ||
            DccGameCatalog.IsUnsupportedExecutable(foregroundExecutable) ||
            !IsTrustedGameChildExecutable(foregroundExecutable))
        {
            return null;
        }

        DccGameDefinition? ancestorGame = FindNearestKnownAncestor(
            foregroundProcessId,
            processes ?? DccProcessTree.Capture());
        if (ancestorGame is not null)
        {
            DccGameDefinition childGame = ancestorGame with
            {
                Executable = foregroundExecutable,
                Aliases = []
            };
            return new DccDetectedGame(
                childGame,
                foregroundExecutable,
                foregroundProcessId,
                null,
                false,
                string.IsNullOrWhiteSpace(ancestorGame.NvidiaProfileName)
                    ? "Known game child process"
                    : "NVIDIA DRS child process",
                foregroundPath,
                IsForeground: true);
        }

        // DCC mutates global driver state. A running background process is not
        // sufficient authorization: launchers, overlays and desktop applications
        // frequently have NVIDIA profiles and render frames of their own.
        return null;
    }

    private static DccGameDefinition ResolveKnownExecutableCollision(
        IReadOnlyList<DccGameDefinition> candidates,
        string executable,
        string executablePath,
        out string gameKeyOverride,
        out string identitySource)
    {
        gameKeyOverride = string.Empty;
        identitySource = "executable";
        DccGameDefinition game = candidates[0];
        // Petroglyph ships both 8-Bit Hordes and Command & Conquer Remastered
        // as ClientG.exe/ClientLauncherG.exe. NVIDIA DRS therefore identifies
        // the Remastered executable as the first shared profile it knows.
        // Keep the real DRS profile for transparency, but use the unambiguous
        // install-directory token to label the game and its learned profile.
        string installToken = NormalizeInstallToken(executablePath);
        if (executable is "clientg.exe" or "clientlauncherg.exe")
        {
            if (installToken.Contains("cncremastered", StringComparison.Ordinal))
            {
                gameKeyOverride = "dcc-cncremastered-clientg.exe";
                identitySource = "catalog-path";
                return game with
                {
                    Name = "Command & Conquer Remastered Collection",
                    Executable = executable,
                    Aliases = []
                };
            }

            if (installToken.Contains("8bithordes", StringComparison.Ordinal))
            {
                gameKeyOverride = "dcc-8bithordes-clientg.exe";
                identitySource = "catalog-path";
            }
            return game;
        }

        var pathMatches = candidates
            .Select(candidate => new
            {
                Game = candidate,
                HintLength = candidate.EffectiveInstallPathHints
                    .Select(NormalizeInstallToken)
                    .Where(static hint => hint.Length >= 4)
                    .Where(installToken.Contains)
                    .Select(static hint => hint.Length)
                    .DefaultIfEmpty(0)
                    .Max()
            })
            .Where(match => match.HintLength > 0)
            .OrderByDescending(match => match.HintLength)
            .ToArray();
        if (pathMatches.Length > 0 &&
            (pathMatches.Length == 1 || pathMatches[0].HintLength > pathMatches[1].HintLength ||
             string.Equals(
                 pathMatches[0].Game.StableGameKey,
                 pathMatches[1].Game.StableGameKey,
                 StringComparison.OrdinalIgnoreCase)))
        {
            game = pathMatches[0].Game;
            gameKeyOverride = string.IsNullOrWhiteSpace(game.StableGameKey)
                ? CreatePrivatePathGameKey(executablePath)
                : game.StableGameKey;
            identitySource = "catalog-path";
            return game;
        }

        DccGameDefinition[] defaultCandidates = candidates
            .Where(candidate => candidate.EffectiveInstallPathHints.Count == 0)
            .ToArray();
        if (defaultCandidates.Length == 1)
        {
            game = defaultCandidates[0];
            if (!string.IsNullOrWhiteSpace(game.StableGameKey))
            {
                gameKeyOverride = game.StableGameKey;
                identitySource = "catalog-id";
            }
            return game;
        }

        if (candidates.Count == 1)
        {
            game = candidates[0];
            if (!string.IsNullOrWhiteSpace(game.StableGameKey))
            {
                gameKeyOverride = game.StableGameKey;
                identitySource = "catalog-id";
            }
            return game;
        }

        game = defaultCandidates.FirstOrDefault() ?? candidates[0];
        gameKeyOverride = CreatePrivatePathGameKey(executablePath);
        identitySource = string.IsNullOrWhiteSpace(gameKeyOverride)
            ? "ambiguous-executable"
            : "private-path-hash";

        return game;
    }

    private static string NormalizeInstallToken(string value) => new(
        (value ?? string.Empty)
            .Where(static character => char.IsLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character))
            .ToArray());

    private static string CreatePrivatePathGameKey(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return string.Empty;
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(executablePath)
                .Replace('\\', '/')
                .Trim()
                .ToLowerInvariant();
        }
        catch
        {
            normalizedPath = executablePath.Replace('\\', '/').Trim().ToLowerInvariant();
        }
        if (normalizedPath.Length == 0)
            return string.Empty;
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..12].ToLowerInvariant();
        return $"dcc-path-{fingerprint}.exe";
    }

    public static bool IsTrustedGameChildExecutable(string executable)
    {
        string normalized = DccGameCatalog.NormalizeExecutable(executable);
        return normalized.EndsWith("-win64-shipping.exe", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-win32-shipping.exe", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-wingdk-shipping.exe", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-uwp64-shipping.exe", StringComparison.OrdinalIgnoreCase);
    }

    private DccGameDefinition? FindNearestKnownAncestor(
        int foregroundProcessId,
        IEnumerable<DccProcessTreeEntry> processes)
    {
        Dictionary<int, DccProcessTreeEntry> processById = processes
            .Where(entry => entry.ProcessId > 0)
            .GroupBy(entry => entry.ProcessId)
            .Select(group => group.First())
            .ToDictionary(entry => entry.ProcessId);
        if (!processById.TryGetValue(foregroundProcessId, out DccProcessTreeEntry? foreground))
            return null;

        int parentProcessId = foreground.ParentProcessId;
        var visited = new HashSet<int> { foregroundProcessId };
        for (int depth = 0; depth < 32 && parentProcessId > 0; depth++)
        {
            if (!visited.Add(parentProcessId) ||
                !processById.TryGetValue(parentProcessId, out DccProcessTreeEntry? parent))
            {
                return null;
            }

            DccGameDefinition? game = _catalog.FindByExecutable(parent.Executable);
            if (game is not null)
                return game;
            parentProcessId = parent.ParentProcessId;
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static int GetForegroundProcessId()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return 0;
            GetWindowThreadProcessId(window, out uint processId);
            return checked((int)processId);
        }
        catch
        {
            return 0;
        }
    }

    private static (string? Executable, string Path) GetExecutable(int processId)
    {
        if (processId <= 0)
            return (null, string.Empty);
        try
        {
            using Process process = Process.GetProcessById(processId);
            string executable = DccGameCatalog.NormalizeExecutable(process.ProcessName);
            string path;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                path = string.Empty;
            }
            return (executable, path);
        }
        catch
        {
            return (null, string.Empty);
        }
    }
}


