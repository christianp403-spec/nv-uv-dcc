using System.Text;
using System.Text.RegularExpressions;

namespace NvUv.Dcc.Core;

public static partial class DccLog
{
    private const long MaximumLogBytes = 1024 * 1024;
    private const int MaximumDetailLength = 12_000;
    private static readonly object UiGate = new();
    private static readonly object HelperGate = new();

    public static void Ui(string eventName, string? detail = null, Exception? exception = null) =>
        Write(DccPaths.UiLogFile, UiGate, "UI", eventName, detail, exception);

    public static void Helper(string eventName, string? detail = null, Exception? exception = null) =>
        Write(DccPaths.HelperLogFile, HelperGate, "HELPER", eventName, detail, exception);

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string result = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        foreach ((string path, string replacement) in SensitivePaths()
                     .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                     .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(item => item.Path.Length))
        {
            result = Regex.Replace(
                result,
                Regex.Escape(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        result = AuthenticationTokenRegex().Replace(result, "<REDACTED_SECRET>");
        result = PipeNameRegex().Replace(result, "<REDACTED_PIPE>");
        result = RedactWindowsPaths(result);
        return result.Length <= MaximumDetailLength ? result : result[..MaximumDetailLength] + " …";
    }

    public static string ReadSanitized(string path)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return string.Join(Environment.NewLine,
                reader.ReadToEnd()
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(Sanitize));
        }
        catch (Exception ex)
        {
            return $"Log could not be read: {Sanitize(ex.Message)}";
        }
    }

    public static string PreviousFile(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(path) + ".previous" + Path.GetExtension(path));

    private static void Write(
        string path,
        object gate,
        string component,
        string eventName,
        string? detail,
        Exception? exception)
    {
        try
        {
            var fields = new List<string>
            {
                DateTimeOffset.UtcNow.ToString("O"),
                $"pid={Environment.ProcessId}",
                $"component={component}",
                $"event={Sanitize(eventName)}"
            };
            if (!string.IsNullOrWhiteSpace(detail))
                fields.Add("detail=" + Sanitize(detail));
            if (exception is not null)
            {
                string exceptionText = $"{exception.GetType().Name}: {exception.Message}";
                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                    exceptionText += " | " + exception.StackTrace;
                fields.Add("exception=" + Sanitize(exceptionText));
            }

            string line = string.Join("\t", fields) + Environment.NewLine;
            lock (gate)
            {
                Directory.CreateDirectory(DccPaths.LogsDirectory);
                RotateIfNeeded(path, Encoding.UTF8.GetByteCount(line));
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
            }
        }
        catch
        {
            // Diagnostics must never affect DCC operation or recovery.
        }
    }

    private static void RotateIfNeeded(string path, int incomingBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaximumLogBytes)
            return;

        string previous = PreviousFile(path);
        if (File.Exists(previous))
            File.Delete(previous);
        File.Move(path, previous);
    }

    private static IEnumerable<(string Path, string Replacement)> SensitivePaths()
    {
        yield return (DccPaths.DataDirectory, "<DCC_DATA>");
        yield return (AppContext.BaseDirectory, "<APPDIR>");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<USERPROFILE>");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<LOCALAPPDATA>");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "<APPDATA>");
        yield return (Path.GetTempPath(), "<TEMP>");
    }

    private static string RedactWindowsPaths(string value)
    {
        var output = new StringBuilder(value.Length);
        int copiedThrough = 0;
        for (int index = 0; index < value.Length - 2; index++)
        {
            bool drivePath = char.IsLetter(value[index]) && value[index + 1] == ':' &&
                             value[index + 2] is '\\' or '/';
            bool uncPath = value[index] == '\\' && value[index + 1] == '\\';
            if (!drivePath && !uncPath)
                continue;

            output.Append(value, copiedThrough, index - copiedThrough);
            output.Append("<PATH>");
            int end = FindPathEnd(value, index + (drivePath ? 3 : 2));
            copiedThrough = end;
            index = Math.Max(index, end - 1);
        }

        output.Append(value, copiedThrough, value.Length - copiedThrough);
        return output.ToString();
    }

    private static int FindPathEnd(string value, int start)
    {
        int end = value.Length;
        foreach (string delimiter in new[] { " | ", " at ", "; ", "'", "\"" })
        {
            int candidate = value.IndexOf(delimiter, start, StringComparison.OrdinalIgnoreCase);
            if (candidate >= 0)
                end = Math.Min(end, candidate);
        }
        return end;
    }

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticationTokenRegex();

    [GeneratedRegex(@"nvuv-dcc-[0-9a-f]{32}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipeNameRegex();

}
