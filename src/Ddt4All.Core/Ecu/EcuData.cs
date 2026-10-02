using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Ddt4All.Core.Codec;

namespace Ddt4All.Core.Ecu;

/// <summary>
/// Definition of one named value (width, scaling, list mapping, ASCII...) plus the codec to read it from /
/// write it into a frame. All decode/encode methods are bit-exact with the Python <c>EcuData</c>
/// (<c>getHexValue</c>, <c>getDisplayValue</c>, <c>getIntValue</c>, <c>setValue</c>), except where noted.
/// </summary>
public sealed class EcuData : INamed
{
    /// <summary>Data name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Free text description (XML only; not part of the JSON schema).</summary>
    public string Description { get; set; } = "";
    /// <summary>Comment (HTML in XML sources; tags stripped on JSON dump).</summary>
    public string Comment { get; set; } = "";
    /// <summary>Width in bits (default 8).</summary>
    public int BitsCount { get; set; } = 8;
    /// <summary>Width in bytes (default 1).</summary>
    public int BytesCount { get; set; } = 1;
    /// <summary>Value is shown through <c>(raw*Step+Offset)/DivideBy</c>.</summary>
    public bool Scaled { get; set; }
    /// <summary>Raw value is a signed integer (honoured for 1 and 2 byte values, like the reference).</summary>
    public bool Signed { get; set; }
    /// <summary>Declared with a Bytes element.</summary>
    public bool Byte { get; set; }
    /// <summary>Declared with a Binary element.</summary>
    public bool Binary { get; set; }
    /// <summary>Value is ASCII text.</summary>
    public bool BytesAscii { get; set; }
    /// <summary>Scale step.</summary>
    public double Step { get; set; } = 1.0;
    /// <summary>Scale offset.</summary>
    public double Offset { get; set; }
    /// <summary>Scale divisor.</summary>
    public double DivideBy { get; set; } = 1.0;
    /// <summary>Display format; the number of decimals is taken from the text after the first '.' ("0.00").</summary>
    public string Format { get; set; } = "";
    /// <summary>Engineering unit.</summary>
    public string Unit { get; set; } = "";
    /// <summary>Raw value to text map (enumerations).</summary>
    public Dictionary<long, string> Lists { get; } = new();
    /// <summary>Text to raw value map (inverse of <see cref="Lists"/>).</summary>
    public Dictionary<string, long> Items { get; } = new();

    /// <summary>True if this data has an enumeration list.</summary>
    public bool HasList => Lists.Count > 0;

    /// <summary>Number of bytes in the raw value.</summary>
    public int RawByteLength => BitCodec.ByteLength(BitsCount);

    /// <summary>Adds an enumeration entry.</summary>
    public void AddListItem(long value, string text)
    {
        Lists[value] = text;
        Items[text] = value;
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Reads the raw field as an unsigned integer (<see cref="BitsCount"/> must be at most 64).
    /// Mirrors <c>getIntValue</c>. Returns false if the frame is too short (Python: <c>None</c>).
    /// </summary>
    public bool TryGetRaw(ReadOnlySpan<byte> frame, DataItem item, ByteOrder ecuOrder, out ulong raw) =>
        BitCodec.TryReadUInt64(frame, item.FirstByte, item.BitOffset, BitsCount, item.IsLittleEndian(ecuOrder), out raw);

    /// <summary>Reads the raw field into <paramref name="dest"/> (right-aligned big-endian, <see cref="RawByteLength"/> bytes). Returns bytes written or -1.</summary>
    public int TryGetRawBytes(ReadOnlySpan<byte> frame, DataItem item, ByteOrder ecuOrder, Span<byte> dest) =>
        BitCodec.TryReadRaw(frame, item.FirstByte, item.BitOffset, BitsCount, item.IsLittleEndian(ecuOrder), dest);

    /// <summary>
    /// Mirrors <c>getHexValue</c>: the raw value as a lower-case hex string, zero padded to the byte length,
    /// or null when the frame is too short.
    /// </summary>
    public string? GetHexValue(ReadOnlySpan<byte> frame, DataItem item, ByteOrder ecuOrder)
    {
        int len = RawByteLength;
        if (len <= 0) return null;
        Span<byte> buf = len <= 256 ? stackalloc byte[len] : new byte[len];
        if (TryGetRawBytes(frame, item, ecuOrder, buf) < 0) return null;
        return Convert.ToHexStringLower(buf);
    }

    /// <summary>Applies the Python signed handling (only 1 and 2 byte values are sign-converted).</summary>
    public long ToSignedValue(ulong raw)
    {
        long v = (long)raw;
        if (!Signed) return v;
        if (BytesCount == 1) return -(v & 0x80) | (v & 0x7f);
        if (BytesCount == 2) return -(v & 0x8000) | (v & 0x7fff);
        return v;
    }

    /// <summary>
    /// Numeric value for graphs/logging without allocating: the scaled value for scaled data, otherwise the
    /// (sign-converted) integer. Returns false for ASCII data, wide fields (&gt;64 bits), short frames or divide-by-zero.
    /// </summary>
    public bool TryGetNumeric(ReadOnlySpan<byte> frame, DataItem item, ByteOrder ecuOrder, out double value)
    {
        value = 0;
        if (BytesAscii || BitsCount > 64 || !TryGetRaw(frame, item, ecuOrder, out ulong raw)) return false;
        double dv = ScaleInput(raw);
        if (!Scaled) { value = dv; return true; }
        if (DivideBy == 0) return false;
        value = (dv * Step + Offset) / DivideBy;
        return true;
    }

    private double ScaleInput(ulong raw) =>
        Signed && (BytesCount == 1 || BytesCount == 2) ? (double)ToSignedValue(raw) : (double)raw;

    /// <summary>
    /// Mirrors <c>getDisplayValue</c>: ASCII text, list text, lower-case hex (unscaled) or the formatted scaled value.
    /// Returns null when the frame is too short or the divisor is zero.
    /// </summary>
    public string? GetDisplayValue(ReadOnlySpan<byte> frame, DataItem item, ByteOrder ecuOrder)
    {
        int len = RawByteLength;
        if (len <= 0) return null;
        Span<byte> buf = len <= 256 ? stackalloc byte[len] : new byte[len];
        if (TryGetRawBytes(frame, item, ecuOrder, buf) < 0) return null;
        buf = buf.Slice(0, len);

        if (BytesAscii) return Utf8Lenient.GetString(buf);

        bool fits = TryToUInt64(buf, out ulong raw);

        if (!Scaled)
        {
            if (fits && Lists.Count > 0)
            {
                bool signedConv = Signed && (BytesCount == 1 || BytesCount == 2);
                if ((signedConv || raw <= long.MaxValue) && Lists.TryGetValue(ToSignedValue(raw), out var text)) return text;
            }

            return Convert.ToHexStringLower(buf);
        }

        if (DivideBy == 0) return null;
        double dv = fits ? ScaleInput(raw) : BigNumber(buf);
        return FormatNumber((dv * Step + Offset) / DivideBy);
    }

    private static double BigNumber(ReadOnlySpan<byte> be) => (double)new BigInteger(be, isUnsigned: true, isBigEndian: true);

    private static bool TryToUInt64(ReadOnlySpan<byte> be, out ulong v)
    {
        v = 0;
        int i = 0;
        while (i < be.Length && be[i] == 0) i++;
        if (be.Length - i > 8) return false;
        for (; i < be.Length; i++) v = (v << 8) | be[i];
        return true;
    }

    private string? FormatNumber(double res)
    {
        if (double.IsNaN(res) || double.IsInfinity(res)) return null;
        int dot = Format.IndexOf('.');
        if (Format.Length > 0 && dot >= 0)
        {
            int end = Format.IndexOf('.', dot + 1);
            int acc = (end < 0 ? Format.Length : end) - dot - 1;
            return PyNumber.FormatFixed(res, acc);
        }

        if (res == Math.Truncate(res)) return PyNumber.IntegerString(res);
        return PyNumber.Repr(res);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Writes a value given as text, like <c>setValue</c>: ASCII data take text, scaled data take a decimal number
    /// (display units), everything else takes a hex string. On failure the frame is untouched and false is returned
    /// with a message (Python silently returned None).
    /// <para>Deviations from Python (which would crash or emit garbage): negative results are stored in two's complement
    /// for signed fields and rejected for unsigned ones, values that do not fit the field width are rejected,
    /// non-Latin-1 ASCII characters are written as '?'.</para>
    /// </summary>
    public bool TrySetValue(Span<byte> frame, DataItem item, ByteOrder ecuOrder, ReadOnlySpan<char> value, out string? error)
    {
        error = null;
        bool little = item.IsLittleEndian(ecuOrder);
        int len = RawByteLength;
        if (BytesAscii)
        {
            Span<byte> tmp = len <= 256 ? stackalloc byte[len] : new byte[len];
            int chars = Math.Max(BytesCount, 0);
            if (chars > len) chars = len;
            tmp.Clear();
            // pad with spaces / truncate to BytesCount characters; left aligned in the field
            int n = Math.Max(BytesCount, 0);
            Span<byte> text = n <= 256 ? stackalloc byte[n] : new byte[n];
            for (int i = 0; i < n; i++)
            {
                char c = i < value.Length ? value[i] : ' ';
                text[i] = c <= 0xFF ? (byte)c : (byte)'?';
            }
            if (n > len) { error = "ASCII length exceeds field width"; return false; }
            text.CopyTo(tmp.Slice(len - n));
            if (!BitCodec.TryWriteRaw(frame, item.FirstByte, item.BitOffset, BitsCount, little, tmp))
            { error = "Value does not fit the frame or the field"; return false; }
            return true;
        }

        if (Scaled)
        {
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            { error = $"'{value}' is not a number"; return false; }
            return TrySetNumeric(frame, item, ecuOrder, d, out error);
        }

        // hex
        var hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { error = "Hex value must not have a 0x prefix"; return false; }
        if (hex.IsEmpty) { error = "Empty value"; return false; }
        int nbytes = (hex.Length + 1) / 2;
        Span<byte> raw = nbytes <= 256 ? stackalloc byte[nbytes] : new byte[nbytes];
        raw.Clear();
        // right-align digits
        int bi = nbytes - 1;
        int shift = 0;
        for (int i = hex.Length - 1; i >= 0; i--)
        {
            int hv = HexUtil.HexValue(hex[i]);
            if (hv < 0) { error = $"'{value}' is not a valid hex value"; return false; }
            raw[bi] |= (byte)(hv << shift);
            if (shift == 0) shift = 4; else { shift = 0; bi--; }
        }

        if (!BitCodec.TryWriteRaw(frame, item.FirstByte, item.BitOffset, BitsCount, little, raw))
        { error = "Value does not fit the field or the frame is too short"; return false; }
        return true;
    }

    /// <summary>Writes a scaled value given in display units: <c>int((v*DivideBy - Offset)/Step)</c> (truncating, as the reference).</summary>
    public bool TrySetNumeric(Span<byte> frame, DataItem item, ByteOrder ecuOrder, double displayValue, out string? error)
    {
        error = null;
        if (BitsCount > 64) { error = "Scaled values wider than 64 bits are not supported"; return false; }
        if (Step == 0) { error = "Step is zero"; return false; }
        double q = (displayValue * DivideBy - Offset) / Step;
        if (double.IsNaN(q) || double.IsInfinity(q) || Math.Abs(q) >= 9.2e18)
        { error = "Value out of range"; return false; }
        long iv = (long)q;
        return TrySetInteger(frame, item, ecuOrder, iv, out error);
    }

    /// <summary>Writes an integer raw value (negative numbers in two's complement for signed fields).</summary>
    public bool TrySetInteger(Span<byte> frame, DataItem item, ByteOrder ecuOrder, long raw, out string? error)
    {
        error = null;
        int bits = BitsCount;
        if (bits > 64) { error = "Fields wider than 64 bits are not supported here"; return false; }
        ulong u;
        if (raw < 0)
        {
            if (!Signed) { error = "Negative value for an unsigned field"; return false; }
            if (bits < 64 && raw < -(1L << (bits - 1))) { error = "Value out of range"; return false; }
            u = bits >= 64 ? (ulong)raw : (ulong)raw & ((1UL << bits) - 1);
        }
        else u = (ulong)raw;

        if (!BitCodec.TryWriteUInt64(frame, item.FirstByte, item.BitOffset, bits, item.IsLittleEndian(ecuOrder), u))
        { error = "Value does not fit the field or the frame is too short"; return false; }
        return true;
    }

    /// <summary>Writes the raw field from a big-endian integer.</summary>
    public bool TrySetRaw(Span<byte> frame, DataItem item, ByteOrder ecuOrder, ReadOnlySpan<byte> valueBigEndian) =>
        BitCodec.TryWriteRaw(frame, item.FirstByte, item.BitOffset, BitsCount, item.IsLittleEndian(ecuOrder), valueBigEndian);
}

/// <summary>UTF-8 decoding that silently drops invalid bytes, like Python's <c>decode('utf-8', errors='ignore')</c>.</summary>
internal static class Utf8Lenient
{
    public static string GetString(ReadOnlySpan<byte> b)
    {
        bool ascii = true;
        foreach (var x in b) if (x >= 0x80) { ascii = false; break; }
        if (ascii) return Encoding.ASCII.GetString(b);

        Span<char> chars = b.Length <= 512 ? stackalloc char[b.Length] : new char[b.Length];
        int o = 0;
        for (int i = 0; i < b.Length;)
        {
            int c = b[i];
            if (c < 0x80) { chars[o++] = (char)c; i++; continue; }
            int need; int min2 = 0x80, max2 = 0xBF; int cp;
            if (c >= 0xC2 && c <= 0xDF) { need = 1; cp = c & 0x1F; }
            else if (c >= 0xE0 && c <= 0xEF)
            {
                need = 2; cp = c & 0x0F;
                if (c == 0xE0) min2 = 0xA0;
                if (c == 0xED) max2 = 0x9F;
            }
            else if (c >= 0xF0 && c <= 0xF4)
            {
                need = 3; cp = c & 0x07;
                if (c == 0xF0) min2 = 0x90;
                if (c == 0xF4) max2 = 0x8F;
            }
            else { i++; continue; }

            bool ok = i + need < b.Length;
            if (ok)
            {
                int c2 = b[i + 1];
                if (c2 < min2 || c2 > max2) ok = false;
                else
                {
                    cp = (cp << 6) | (c2 & 0x3F);
                    for (int k = 2; k <= need; k++)
                    {
                        int ck = b[i + k];
                        if (ck < 0x80 || ck > 0xBF) { ok = false; break; }
                        cp = (cp << 6) | (ck & 0x3F);
                    }
                }
            }

            if (!ok) { i++; continue; }
            i += need + 1;
            if (cp < 0x10000) chars[o++] = (char)cp;
            else { cp -= 0x10000; chars[o++] = (char)(0xD800 + (cp >> 10)); chars[o++] = (char)(0xDC00 + (cp & 0x3FF)); }
        }

        return new string(chars.Slice(0, o));
    }
}
