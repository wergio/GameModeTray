using System.Globalization;

namespace GameModeTray;

/// <summary>Hex byte strings: "AA EC 09", "AA:EC:09" or "AAEC09"; "??" is a wildcard in patterns.</summary>
static class Hex
{
    public const int Any = -1;

    public static byte[] Parse(string? text) =>
        ParsePattern(text, allowWildcards: false).Select(b => (byte)b).ToArray();

    public static int[] ParsePattern(string? text, bool allowWildcards = true)
    {
        var s = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-' or ',')).ToArray());
        if (s.Length % 2 != 0) throw new FormatException($"odd number of hex digits in \"{text}\"");

        var result = new int[s.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            var pair = s.Substring(2 * i, 2);
            if (pair == "??" && allowWildcards) result[i] = Any;
            else if (byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) result[i] = b;
            else throw new FormatException($"\"{pair}\" is not a hex byte in \"{text}\"");
        }
        return result;
    }

    public static string Format(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    public static string FormatPattern(IEnumerable<int> pattern) => string.Join(" ", pattern.Select(b => b == Any ? "??" : b.ToString("X2")));

    /// <summary>Position of the last occurrence of pattern in data, or -1.</summary>
    public static int LastIndexOf(IReadOnlyList<byte> data, int[] pattern)
    {
        if (pattern.Length == 0) return -1;
        for (int i = data.Count - pattern.Length; i >= 0; i--)
        {
            int j = 0;
            while (j < pattern.Length && (pattern[j] == Any || pattern[j] == data[i + j])) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}
