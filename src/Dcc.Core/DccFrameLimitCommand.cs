using System.Globalization;
using System.Text;

namespace NvUv.Dcc.Core;

public sealed record DccFrameLimitCommand(
    int TargetFps,
    string ExecutablePath,
    string ExecutableName,
    string FriendlyName)
{
    public const int MinimumTargetFps = DccFpsRange.Minimum;
    public const int MaximumTargetFps = DccFpsRange.Maximum;

    public static DccFrameLimitCommand Create(
        string? executablePath,
        string? executableName,
        string? friendlyName,
        int targetFps)
    {
        if (targetFps is < MinimumTargetFps or > MaximumTargetFps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetFps),
                $"The frame-limit target must be between {MinimumTargetFps} and {MaximumTargetFps} FPS.");
        }

        string path = executablePath?.Trim() ?? string.Empty;
        string nameCandidate = string.IsNullOrWhiteSpace(executableName)
            ? Path.GetFileName(path)
            : executableName.Trim();
        if (string.IsNullOrWhiteSpace(nameCandidate))
            throw new InvalidDataException("The frame-limit request does not identify a game executable.");

        string name = DccGameCatalog.NormalizeExecutable(nameCandidate);
        string displayName = string.IsNullOrWhiteSpace(friendlyName)
            ? Path.GetFileNameWithoutExtension(name)
            : friendlyName.Trim();

        return new DccFrameLimitCommand(targetFps, path, name, displayName);
    }

    public string Serialize() =>
        $"LIMIT {TargetFps.ToString(CultureInfo.InvariantCulture)} " +
        $"{DccCommandText.Encode(ExecutablePath)} {DccCommandText.Encode(ExecutableName)} " +
        DccCommandText.Encode(FriendlyName);

    public static DccFrameLimitCommand Parse(IReadOnlyList<string> parts)
    {
        if (parts.Count != 5 || !string.Equals(parts[0], "LIMIT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid LIMIT command: expected five fields.");
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetFps))
            throw new InvalidDataException("Invalid LIMIT command: the FPS target is not an integer.");

        try
        {
            return Create(
                DccCommandText.Decode(parts[2]),
                DccCommandText.Decode(parts[3]),
                DccCommandText.Decode(parts[4]),
                targetFps);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException or
                                   InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Invalid LIMIT command: the game data is incomplete or malformed.", ex);
        }
    }

}
