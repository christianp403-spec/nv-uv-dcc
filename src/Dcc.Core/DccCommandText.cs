using System.Text;

namespace NvUv.Dcc.Core;

internal static class DccCommandText
{
    private const string EmptyValueToken = "~";
    private const int MaximumTextBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Encode(string value)
    {
        if (value.Length == 0)
            return EmptyValueToken;

        byte[] bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumTextBytes)
            throw new InvalidDataException("The command contains an oversized text field.");
        return Convert.ToBase64String(bytes);
    }

    public static string Decode(string token)
    {
        if (token == EmptyValueToken)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidDataException("The command contains an empty transport token.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(token);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The command contains invalid Base64 data.", ex);
        }

        if (bytes.Length > MaximumTextBytes)
            throw new InvalidDataException("The command contains an oversized text field.");
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The command contains invalid UTF-8 data.", ex);
        }
    }
}
