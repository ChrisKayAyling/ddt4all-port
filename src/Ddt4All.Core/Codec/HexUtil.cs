using System.Globalization;

namespace Ddt4All.Core.Codec;

/// <summary>Hex string helpers for diagnostic frames.</summary>
public static class HexUtil
{
    /// <summary>Value of a hex digit, or -1.</summary>
    public static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;

    /// <summary>
    /// Parses hex text (whitespace ignored) into bytes. Fails on non-hex characters or an odd digit count.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> hex, Span<byte> dest, out int written)
    {
        written = 0;
        int hi = -1;
        foreach (char c in hex)
        {
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
            int v = HexValue(c);
            if (v < 0) return false;
            if (hi < 0) hi = v;
            else
            {
                if (written >= dest.Length) return false;
                dest[written++] = (byte)((hi << 4) | v);
                hi = -1;
            }
        }

        return hi < 0;
    }

    /// <summary>Parses hex text into a new array, or returns null if invalid.</summary>
    public static byte[]? Parse(ReadOnlySpan<char> hex)
    {
        var buf = new byte[hex.Length / 2 + 1];
        return TryParse(hex, buf, out int n) ? buf.AsSpan(0, n).ToArray() : null;
    }

    /// <summary>Formats bytes as upper-case hex, optionally space separated ("62 F1 90").</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes, bool spaced = false)
    {
        if (!spaced) return Convert.ToHexString(bytes);
        if (bytes.IsEmpty) return string.Empty;
        return string.Create(bytes.Length * 3 - 1, bytes.ToArray(), static (span, b) =>
        {
            const string digits = "0123456789ABCDEF";
            for (int i = 0, p = 0; i < b.Length; i++)
            {
                if (i > 0) span[p++] = ' ';
                span[p++] = digits[b[i] >> 4];
                span[p++] = digits[b[i] & 15];
            }
        });
    }

    /// <summary>Parses a hex number ("1F", "7e0"), as <c>int("0x"+s, 16)</c> does. Fails for empty/invalid text.</summary>
    public static bool TryParseHexInt(ReadOnlySpan<char> s, out long value)
    {
        s = s.Trim();
        if (s.Length == 0) { value = 0; return false; }
        return long.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
