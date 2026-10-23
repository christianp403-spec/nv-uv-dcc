using System.Security.Principal;
using System.Xml.Linq;

namespace NvUv.Dcc.Core;

public static class DccAutoStartTaskDefinition
{
    private const string TaskNamePrefix = @"\NVUV-DCC Ready Start ";
    public const string ScheduledStartArgument = "--scheduled-start";

    private static readonly XNamespace TaskNamespace =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string BuildXml(string executablePath, string userSid)
    {
        string executable = NormalizeExecutablePath(executablePath);
        string sid = RequireValue(userSid, nameof(userSid));
        string taskName = TaskNameForUser(sid);
        string workingDirectory = Path.GetDirectoryName(executable)
            ?? throw new ArgumentException("The DCC executable has no working directory.", nameof(executablePath));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(TaskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Description",
                        "Starts the protected DCC copy for the current user with the privileges required by its NVIDIA helper."),
                    new XElement(TaskNamespace + "URI", taskName)),
                new XElement(TaskNamespace + "Triggers",
                    new XElement(TaskNamespace + "LogonTrigger",
                        new XElement(TaskNamespace + "Enabled", "true"),
                        new XElement(TaskNamespace + "UserId", sid))),
                new XElement(TaskNamespace + "Principals",
                    new XElement(TaskNamespace + "Principal",
                        new XAttribute("id", "DccUser"),
                        new XElement(TaskNamespace + "UserId", sid),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "HighestAvailable"))),
                new XElement(TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNamespace + "AllowHardTerminate", "true"),
                    new XElement(TaskNamespace + "StartWhenAvailable", "true"),
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNamespace + "IdleSettings",
                        new XElement(TaskNamespace + "StopOnIdleEnd", "false"),
                        new XElement(TaskNamespace + "RestartOnIdle", "false")),
                    new XElement(TaskNamespace + "AllowStartOnDemand", "true"),
                    new XElement(TaskNamespace + "Enabled", "true"),
                    new XElement(TaskNamespace + "Hidden", "false"),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNamespace + "WakeToRun", "false"),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNamespace + "Priority", "7")),
                new XElement(TaskNamespace + "Actions",
                    new XAttribute("Context", "DccUser"),
                    new XElement(TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", executable),
                        new XElement(TaskNamespace + "Arguments", ScheduledStartArgument),
                        new XElement(TaskNamespace + "WorkingDirectory", workingDirectory)))));

        return document.Declaration + Environment.NewLine +
               document.ToString(SaveOptions.DisableFormatting);
    }

    public static bool Matches(
        string taskXml,
        string executablePath,
        string userSid,
        string? accountName = null) =>
        ValidationFailures(taskXml, executablePath, userSid, accountName).Count == 0;

    public static IReadOnlyList<string> ValidationFailures(
        string taskXml,
        string executablePath,
        string userSid,
        string? accountName = null)
    {
        try
        {
            string executable = NormalizeExecutablePath(executablePath);
            string workingDirectory = Path.GetDirectoryName(executable)!;
            string sid = RequireValue(userSid, nameof(userSid));
            string taskName = TaskNameForUser(sid);
            XDocument document = XDocument.Parse(taskXml, LoadOptions.None);
            XElement? root = document.Root;
            if (root is null || root.Name.LocalName != "Task")
                return ["task-root"];

            XElement[] actions = Descendants(root, "Exec").ToArray();
            XElement[] triggers = Descendants(root, "LogonTrigger").ToArray();
            XElement[] principals = Descendants(root, "Principal").ToArray();
            XElement[] settings = Descendants(root, "Settings").ToArray();
            var failures = new List<string>();
            if (actions.Length != 1 || triggers.Length != 1 ||
                principals.Length != 1 || settings.Length != 1)
            {
                if (actions.Length != 1)
                    failures.Add("exec-count");
                if (triggers.Length != 1)
                    failures.Add("logon-trigger-count");
                if (principals.Length != 1)
                    failures.Add("principal-count");
                if (settings.Length != 1)
                    failures.Add("settings-count");
                return failures;
            }

            string? command = ChildValue(actions[0], "Command");
            string? arguments = ChildValue(actions[0], "Arguments");
            string? taskWorkingDirectory = ChildValue(actions[0], "WorkingDirectory");
            string? principalUser = ChildValue(principals[0], "UserId");
            string? triggerUser = ChildValue(triggers[0], "UserId");

            if (!PathsEqual(command, executable))
                failures.Add("command");
            if (!string.Equals(arguments?.Trim(), ScheduledStartArgument, StringComparison.Ordinal))
                failures.Add("arguments");
            if (!PathsEqual(taskWorkingDirectory, workingDirectory))
                failures.Add("working-directory");
            if (!UserMatches(principalUser, sid, accountName))
                failures.Add("principal-user");
            if (!UserMatches(triggerUser, sid, accountName))
                failures.Add("trigger-user");
            if (!string.Equals(
                    ChildValue(principals[0], "LogonType"),
                    "InteractiveToken",
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("logon-type");
            }
            if (!string.Equals(
                    ChildValue(principals[0], "RunLevel"),
                    "HighestAvailable",
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("run-level");
            }
            if (!TrueOrMissing(ChildValue(triggers[0], "Enabled")))
                failures.Add("trigger-enabled");
            if (!TrueOrMissing(ChildValue(settings[0], "Enabled")))
                failures.Add("task-enabled");

            string? uri = FirstDescendantValue(root, "URI");
            if (!string.IsNullOrWhiteSpace(uri) &&
                !string.Equals(uri, taskName, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("uri");
            }
            return failures;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException)
        {
            return ["malformed-xml"];
        }
    }

    private static IEnumerable<XElement> Descendants(XContainer root, string localName) =>
        root.Descendants().Where(element => element.Name.LocalName == localName);

    private static string? FirstDescendantValue(XContainer root, string localName) =>
        Descendants(root, localName).Select(element => element.Value.Trim()).FirstOrDefault();

    private static string? ChildValue(XContainer parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();

    private static bool TrueOrMissing(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool UserMatches(string? actual, string sid, string? accountName)
    {
        string? value = actual?.Trim();
        if (string.Equals(value, sid, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(accountName) &&
             string.Equals(value, accountName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var account = new NTAccount(value);
            var resolvedSid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return string.Equals(resolvedSid.Value, sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        catch (SystemException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return false;
        return string.Equals(
            Path.GetFullPath(first.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        string value = RequireValue(executablePath, nameof(executablePath));
        string fullPath = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The DCC autostart target must be an executable.", nameof(executablePath));
        return fullPath;
    }

    private static string RequireValue(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A value is required.", parameterName);

    public static string TaskNameForUser(string userSid)
    {
        string sid = RequireValue(userSid, nameof(userSid));
        if (!sid.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ||
            sid.Any(character => character != '-' && !char.IsAsciiDigit(character) &&
                                 character is not ('S' or 's')))
        {
            throw new ArgumentException("The Windows user SID is invalid.", nameof(userSid));
        }
        return TaskNamePrefix + sid.ToUpperInvariant();
    }
}
