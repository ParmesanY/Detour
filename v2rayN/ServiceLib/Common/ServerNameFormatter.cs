namespace ServiceLib.Common;

/// <summary>
/// Splits a server name like "🇫🇮 [VL] Финляндия" into a country code ("FI") and the rest.
/// Windows has no flag emoji, so the flag would otherwise show as two loose letters.
/// </summary>
public static class ServerNameFormatter
{
    private const int RegionalIndicatorA = 0x1F1E6;

    public static (string? CountryCode, string Name) Split(string? remarks)
    {
        if (remarks.IsNullOrEmpty())
        {
            return (null, string.Empty);
        }

        var text = remarks!.TrimStart();
        if (TryReadFlag(text, 0, out var code, out var length))
        {
            return (code, text[length..].TrimStart());
        }

        // Flag somewhere later ("Сервер 🇩🇪"): take it out of the name as well.
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (TryReadFlag(text, i, out code, out length))
            {
                return (code, (text[..i] + text[(i + length)..]).Replace("  ", " ").Trim());
            }
        }
        return (null, text);
    }

    /// <summary>
    /// Country the traffic exits in: the last flag of the name ("🇷🇺🇸🇪 …" is a Russian relay to Sweden → "SE"),
    /// or null when the name has no flag.
    /// </summary>
    public static string? ExitCountry(string? remarks)
    {
        if (remarks.IsNullOrEmpty())
        {
            return null;
        }
        string? last = null;
        for (var i = 0; i < remarks!.Length - 3; i++)
        {
            if (TryReadFlag(remarks, i, out var code, out var length))
            {
                last = code;
                i += length - 1;
            }
        }
        return last;
    }

    private static bool TryReadFlag(string text, int index, out string? code, out int length)
    {
        code = null;
        length = 0;
        if (index + 3 >= text.Length || !char.IsSurrogatePair(text, index) || !char.IsSurrogatePair(text, index + 2))
        {
            return false;
        }
        var first = char.ConvertToUtf32(text, index) - RegionalIndicatorA;
        var second = char.ConvertToUtf32(text, index + 2) - RegionalIndicatorA;
        if (first is < 0 or > 25 || second is < 0 or > 25)
        {
            return false;
        }
        code = $"{(char)('A' + first)}{(char)('A' + second)}";
        length = 4;
        return true;
    }
}
