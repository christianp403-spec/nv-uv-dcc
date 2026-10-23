using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.App;

internal static class AutoStartService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DCC by NV-UV";
    private const string ConfigureArgument = "--configure-autostart";
    private const int UacCancelledError = 1223;
    private const string InstallDirectoryName = "NVUV-DCC";
    private const string MainExecutableName = "DCC-by-NV-UV.exe";
    private const string HelperExecutableName = "Dcc.Helper.exe";

    public static bool IsEnabled()
    {
        try
        {
            string executable = InstalledExecutablePath();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            return !string.IsNullOrWhiteSpace(sid) &&
                   InstalledFilesMatchCurrentVersion() &&
                   TryReadTaskXml(DccAutoStartTaskDefinition.TaskNameForUser(sid), out string taskXml) &&
                   DccAutoStartTaskDefinition.Matches(taskXml, executable, sid, identity.Name);
        }
        catch
        {
            return false;
        }
    }

    public static async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        string executable = CurrentExecutablePath();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ConfigureArgument);
        startInfo.ArgumentList.Add(enabled ? "enable" : "disable");
        startInfo.ArgumentList.Add(sid);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated DCC autostart setup could not be started.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacCancelledError)
        {
            throw new OperationCanceledException("Administrator approval was cancelled.", ex);
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                if (process.ExitCode == 3)
                {
                    throw new InvalidOperationException(
                        "Secure autostart requires the signed-in Windows account itself to be an administrator. " +
                        "Approving UAC with a different administrator account is not supported.");
                }
                throw new InvalidOperationException(
                    $"Windows autostart setup failed (code {process.ExitCode}). See the DCC UI log for details.");
            }
        }
    }

    public static bool IsConfigurationCommand(IReadOnlyList<string> arguments) =>
        arguments.Count == 3 &&
        string.Equals(arguments[0], ConfigureArgument, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(arguments[1], "enable", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(arguments[1], "disable", StringComparison.OrdinalIgnoreCase));

    public static int RunConfigurationCommand(IReadOnlyList<string> arguments)
    {
        try
        {
            if (!IsConfigurationCommand(arguments))
                return 2;
            if (!IsCurrentProcessElevated())
                throw new UnauthorizedAccessException("The DCC autostart setup requires administrator rights.");

            _ = DccAutoStartTaskDefinition.TaskNameForUser(arguments[2]);
            string expectedSid = arguments[2].Trim();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string elevatedSid = identity.User?.Value
                ?? throw new InvalidOperationException("The elevated Windows user SID is unavailable.");
            if (!string.Equals(expectedSid, elevatedSid, StringComparison.OrdinalIgnoreCase))
                return 3;

            bool enabled = string.Equals(arguments[1], "enable", StringComparison.OrdinalIgnoreCase);
            if (enabled)
                RegisterTask();
            else
                DeleteTask();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                DccPaths.EnsureDataDirectoryReady();
                DccLog.Ui("autostart.configure.failed", exception: ex);
            }
            catch
            {
                // The process exit code still reports failure if logging is unavailable.
            }
            return 1;
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterTask()
    {
        InstallProtectedAutostartCopy();
        string executable = InstalledExecutablePath();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        string taskName = DccAutoStartTaskDefinition.TaskNameForUser(sid);
        string taskXml = DccAutoStartTaskDefinition.BuildXml(executable, sid);
        string temporaryXml = Path.Combine(
            InstalledDirectoryPath(),
            ".autostart-task-" + Guid.NewGuid().ToString("N") + ".xml");

        try
        {
            File.WriteAllText(temporaryXml, taskXml, Encoding.Unicode);
            ApplyProtectedFileAcl(temporaryXml);
            RunSchtasks("/Create", "/TN", taskName,
                "/XML", temporaryXml, "/F");

            if (!TryReadTaskXml(taskName, out string registeredXml))
            {
                TryDeleteTask(taskName);
                throw new InvalidOperationException("Windows did not return the registered DCC autostart task.");
            }

            IReadOnlyList<string> validationFailures =
                DccAutoStartTaskDefinition.ValidationFailures(
                    registeredXml,
                    executable,
                    sid,
                    identity.Name);
            if (validationFailures.Count != 0)
            {
                DccLog.Ui(
                    "autostart.validation.failed",
                    "fields=" + string.Join(',', validationFailures));
                TryDeleteTask(taskName);
                throw new InvalidOperationException("Windows registered an unexpected DCC autostart task.");
            }

            TryRemoveLegacyRunValue();
            DccLog.Ui("autostart.enabled", $"task={taskName}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryXml);
            }
            catch
            {
                // A stale task-definition file contains no secrets or credentials.
            }
        }
    }

    private static void DeleteTask()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        string taskName = DccAutoStartTaskDefinition.TaskNameForUser(sid);
        if (TryReadTaskXml(taskName, out _))
            RunSchtasks("/Delete", "/TN", taskName, "/F");
        RemoveLegacyRunValue();
        DccLog.Ui("autostart.disabled", $"task={taskName}");
    }

    private static bool TryReadTaskXml(string taskName, out string taskXml)
    {
        ProcessResult result = RunSchtasksCore(
            ["/Query", "/TN", taskName, "/XML"]);
        taskXml = result.StandardOutput;
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(taskXml);
    }

    private static void TryDeleteTask(string taskName)
    {
        try
        {
            RunSchtasks("/Delete", "/TN", taskName, "/F");
        }
        catch (Exception ex)
        {
            DccLog.Ui("autostart.rollback_failed", $"task={taskName}", ex);
        }
    }

    private static void RunSchtasks(params string[] arguments)
    {
        ProcessResult result = RunSchtasksCore(arguments);
        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Windows Task Scheduler returned code {result.ExitCode}."
                    : $"Windows Task Scheduler returned code {result.ExitCode}: {detail}");
        }
    }

    private static ProcessResult RunSchtasksCore(IReadOnlyList<string> arguments)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string schtasks = Path.Combine(systemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = schtasks,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // schtasks emits redirected XML as UTF-8 even though the XML
            // declaration describes the Task Scheduler document as UTF-16.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Windows Task Scheduler did not respond within 30 seconds.");
        }
        Task.WaitAll(outputTask, errorTask);
        return new ProcessResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static void TryRemoveLegacyRunValue()
    {
        try
        {
            RemoveLegacyRunValue();
        }
        catch (Exception ex)
        {
            DccLog.Ui("autostart.legacy_cleanup_failed", exception: ex);
        }
    }

    private static void RemoveLegacyRunValue()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("The legacy Windows startup key could not be opened.");
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string CurrentExecutablePath()
    {
        return Environment.ProcessPath
            ?? throw new InvalidOperationException("The DCC executable path is unavailable.");
    }

    private static void InstallProtectedAutostartCopy()
    {
        string sourceExecutable = CurrentExecutablePath();
        string sourceDirectory = Path.GetDirectoryName(sourceExecutable)
            ?? throw new InvalidOperationException("The DCC source directory is unavailable.");
        string sourceHelper = Path.Combine(sourceDirectory, HelperExecutableName);
        if (!File.Exists(sourceHelper))
            throw new FileNotFoundException("The DCC helper is missing from the portable folder.", sourceHelper);

        string appRuntimeConfig = Path.ChangeExtension(sourceExecutable, ".runtimeconfig.json");
        string helperRuntimeConfig = Path.Combine(sourceDirectory, "Dcc.Helper.runtimeconfig.json");
        if (File.Exists(appRuntimeConfig) || File.Exists(helperRuntimeConfig))
        {
            throw new InvalidOperationException(
                "Secure Windows autostart can only be enabled from the self-contained portable package.");
        }

        string installDirectory = InstalledDirectoryPath();
        Directory.CreateDirectory(installDirectory);
        ApplyProtectedDirectoryAcl(installDirectory);

        CopyAndProtect(sourceExecutable, InstalledExecutablePath());
        CopyAndProtect(sourceHelper, Path.Combine(installDirectory, HelperExecutableName));
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "DOTNET-RUNTIME-LICENSE.txt");
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt");
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "LICENSE.txt");
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "MEDIA-EXCEPTION.txt");
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "NOTICE.txt");
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "THIRD-PARTY-NOTICES.txt");
        CopyOptionalAndProtect(sourceDirectory, installDirectory, "WINDOWS-DESKTOP-RUNTIME-LICENSE.txt");

        if (!InstalledFilesMatchCurrentVersion())
            throw new InvalidOperationException("The protected DCC autostart copy failed version verification.");
    }

    private static void CopyOptionalAndProtect(
        string sourceDirectory,
        string destinationDirectory,
        string fileName)
    {
        string source = Path.Combine(sourceDirectory, fileName);
        if (File.Exists(source))
            CopyAndProtect(source, Path.Combine(destinationDirectory, fileName));
    }

    private static void CopyAndProtect(string sourcePath, string destinationPath)
    {
        if (!string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
        ApplyProtectedFileAcl(destinationPath);
    }

    private static bool InstalledFilesMatchCurrentVersion()
    {
        try
        {
            string sourceExecutable = CurrentExecutablePath();
            string sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
            string installedExecutable = InstalledExecutablePath();
            string installedHelper = Path.Combine(InstalledDirectoryPath(), HelperExecutableName);
            string sourceHelper = Path.Combine(sourceDirectory, HelperExecutableName);
            return File.Exists(installedExecutable) &&
                   File.Exists(installedHelper) &&
                   File.Exists(sourceHelper) &&
                   ProductVersionsEqual(sourceExecutable, installedExecutable) &&
                   ProductVersionsEqual(sourceHelper, installedHelper);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProductVersionsEqual(string first, string second)
    {
        string? firstVersion = FileVersionInfo.GetVersionInfo(first).ProductVersion;
        string? secondVersion = FileVersionInfo.GetVersionInfo(second).ProductVersion;
        return !string.IsNullOrWhiteSpace(firstVersion) &&
               string.Equals(firstVersion, secondVersion, StringComparison.Ordinal);
    }

    private static string InstalledExecutablePath() =>
        Path.Combine(InstalledDirectoryPath(), MainExecutableName);

    private static string InstalledDirectoryPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidOperationException("The Windows Program Files directory is unavailable.");
        return Path.Combine(programFiles, InstallDirectoryName);
    }

    private static void ApplyProtectedDirectoryAcl(string directoryPath)
    {
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, domainSid: null);
        SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, domainSid: null);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directoryPath).SetAccessControl(security);
    }

    private static void ApplyProtectedFileAcl(string filePath)
    {
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, domainSid: null);
        SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, domainSid: null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
