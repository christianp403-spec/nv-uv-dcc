namespace NvUv.Dcc.Core;

public static class DccPaths
{
    public static string LocalAppDataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string NvUvDataDirectory => Path.Combine(
        LocalAppDataDirectory,
        "NV-UV");

    public static string LegacyDataDirectory => Path.Combine(NvUvDataDirectory, "DCC");

    public static string DataDirectory => Path.Combine(LocalAppDataDirectory, "NVUV-DCC");

    public static string ActiveSessionFile => Path.Combine(DataDirectory, "active-session.json");
    public static string FrameLimiterRecoveryFile => Path.Combine(DataDirectory, "frame-limiter-recovery.json");
    public static string VsyncRecoveryFile => Path.Combine(DataDirectory, "vsync-recovery.json");
    public static string JpacRecoveryFile => Path.Combine(DataDirectory, "jpac-recovery.json");
    public static string SharedGameDatabaseFile => Path.Combine(DataDirectory, "GameDatabase.json");
    public static string NvUvGameOverridesFile => Path.Combine(NvUvDataDirectory, "nv-uv_game_overrides.json");
    public static string CustomGamesFile => Path.Combine(DataDirectory, "custom-games.json");
    public static string GameHistoryFile => Path.Combine(DataDirectory, "game-history.json");
    public static string GameUpdateMetaFile => Path.Combine(DataDirectory, "game-update-meta.json");
    public static string ProfileStoreFile => Path.Combine(DataDirectory, "profiles.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public static string PerformanceLogsDirectory => Path.Combine(DataDirectory, "performance-logs");
    public static string UiLogFile => Path.Combine(LogsDirectory, "ui.log");
    public static string HelperLogFile => Path.Combine(LogsDirectory, "helper.log");
    private static string LegacyMigrationMarkerFile => Path.Combine(DataDirectory, ".legacy-migration-v1.complete");

    public static void EnsureDataDirectoryReady()
    {
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(LegacyMigrationMarkerFile))
            return;

        CopyMissingTree(LegacyDataDirectory, DataDirectory);
        CopyMissingFile(
            Path.Combine(NvUvDataDirectory, "GameDatabase.json"),
            SharedGameDatabaseFile);

        string markerTemporary = LegacyMigrationMarkerFile + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(markerTemporary, "Legacy DCC data migration completed." + Environment.NewLine);
            try
            {
                File.Move(markerTemporary, LegacyMigrationMarkerFile, overwrite: false);
            }
            catch (IOException) when (File.Exists(LegacyMigrationMarkerFile))
            {
                // Another DCC process completed the same idempotent migration.
            }
        }
        finally
        {
            if (File.Exists(markerTemporary))
                File.Delete(markerTemporary);
        }
    }

    private static void CopyMissingTree(string sourceRoot, string destinationRoot)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            (string source, string destination) = pending.Pop();
            Directory.CreateDirectory(destination);

            foreach (string sourceFile in Directory.EnumerateFiles(source))
            {
                if ((File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0)
                    continue;
                CopyMissingFile(sourceFile, Path.Combine(destination, Path.GetFileName(sourceFile)));
            }

            foreach (string sourceDirectory in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
                    continue;
                pending.Push((
                    sourceDirectory,
                    Path.Combine(destination, Path.GetFileName(sourceDirectory))));
            }
        }
    }

    private static void CopyMissingFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
            return;

        string temporaryPath = destinationPath + ".migrating-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // The UI and helper may prepare the directory at the same time.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
