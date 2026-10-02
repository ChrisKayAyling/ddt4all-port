using System.Buffers.Binary;

namespace Ddt4All.Core.Codec;

/// <summary>
/// Allocation-free bit-field reader/writer over raw diagnostic frames. It reproduces the exact
/// bit layout of the Python reference (<c>EcuData.getHexValue</c> / <c>EcuData.setValue</c>),
/// including its little-endian quirks.
/// <para>
/// Conventions (identical to the Python reference): <c>firstByte</c> is 1-based and indexes the frame
/// including the service id byte; <c>bitOffset</c> counts from the MSB of the first byte for big-endian
/// fields; for little-endian fields the field ends <c>bitOffset</c> bits before the end of the first byte
/// (so only 0..7 is valid). Values are returned right-aligned in <c>ceil(bitCount/8)</c> big-endian bytes.
/// </para>
/// <para>
/// Little-endian read is intentionally bug-compatible: for multi-byte fields the Python
/// reference re-reads bytes (e.g. a 16 bit LE field at byte 2 of "62 12 34" yields 0x1212).
/// Do not "fix" this here; behaviour is pinned by the Python-derived test vectors.
/// </para>
/// </summary>
public static class BitCodec
{
    /// <summary>Number of bytes needed to hold <paramref name="bitCount"/> bits.</summary>
    public static int ByteLength(int bitCount) => (bitCount + 7) >> 3;

    private const int MaxBits = 1 << 20;

    private static bool TryGeometry(int frameLen, int firstByte, int bitOffset, int bitCount, bool little,
        out int sb, out int reqLen)
    {
        sb = firstByte - 1;
        reqLen = 0;
        if (sb < 0 || bitOffset < 0 || bitCount <= 0 || bitCount > MaxBits || bitOffset > MaxBits)
            return false;
        if (little && bitOffset > 7)
            return false;
        reqLen = (bitCount + bitOffset + 7) >> 3;
        int dataLen = (bitCount + 7) >> 3;
        return (long)sb + dataLen <= frameLen;
    }

    /// <summary>Bit segments [a,b) (bit indices from the MSB of byte <c>sb</c>) that are concatenated to form the value.</summary>
    private static int GetReadSegments(bool little, int bitOffset, int bitCount, int reqLen, Span<int> seg)
    {
        if (!little)
        {
            seg[0] = bitOffset;
            seg[1] = bitOffset + bitCount;
            return 1;
        }

        int n = 0;
        int lastBit = 8 - bitOffset;
        int firstBit = Math.Max(0, lastBit - bitCount);
        seg[0] = firstBit;
        seg[1] = lastBit;
        n = 1;
        int remaining = bitCount - (lastBit - firstBit);
        if (remaining > 8)
        {
            int o1 = 8;
            int o2 = o1 + (reqLen - 2) * 8;
            seg[2] = o1;
            seg[3] = o2;
            n = 2;
            remaining -= o2 - o1;
        }

        if (remaining > 0)
        {
            int o1 = (reqLen - 1) * 8;
            int o2 = o1 - remaining;
            seg[n * 2] = o2;
            seg[n * 2 + 1] = o1;
            n++;
        }

        return n;
    }

    private static ulong ReadBits(ReadOnlySpan<byte> f, int bitStart, int count)
    {
        ulong r = 0;
        int pos = bitStart;
        int remaining = count;
        while (remaining > 0)
        {
            int idx = pos >> 3;
            int inBit = pos & 7;
            int take = Math.Min(8 - inBit, remaining);
            uint b = f[idx];
            r = (r << take) | ((b >> (8 - inBit - take)) & ((1u << take) - 1));
            pos += take;
            remaining -= take;
        }

        return r;
    }

    /// <summary>
    /// Reads a field of up to 64 bits. Returns false when the frame is too short or the geometry is invalid
    /// (mirrors the cases where the Python <c>getHexValue</c> returns <c>None</c>).
    /// </summary>
    public static bool TryReadUInt64(ReadOnlySpan<byte> frame, int firstByte, int bitOffset, int bitCount,
        bool littleEndian, out ulong value)
    {
        value = 0;
        if (bitCount > 64 || !TryGeometry(frame.Length, firstByte, bitOffset, bitCount, littleEndian, out int sb, out int reqLen))
            return false;

        var f = frame.Slice(sb);
        int avail = Math.Min(reqLen, f.Length) * 8;

        // Fast path: big-endian, fully available.
        if (!littleEndian)
        {
            int end = Math.Min(bitOffset + bitCount, avail);
            int n = end - bitOffset;
            if (n <= 0) return false;
            value = ReadBits(f, bitOffset, n);
            return true;
        }

        Span<int> seg = stackalloc int[6];
        int segs = GetReadSegments(true, bitOffset, bitCount, reqLen, seg);
        ulong acc = 0;
        int total = 0;
        for (int i = 0; i < segs; i++)
        {
            int a = seg[i * 2];
            int b = Math.Min(seg[i * 2 + 1], avail);
            int n = b - a;
            if (a < 0 || n <= 0) continue;
            acc = (n >= 64 ? 0 : acc << n) | ReadBits(f, a, n);
            total += n;
        }

        if (total == 0) return false;
        value = acc;
        return true;
    }

    /// <summary>
    /// Reads a field of any width into <paramref name="dest"/> (right-aligned big-endian, exactly
    /// <see cref="ByteLength"/> bytes). Returns the number of bytes written or -1 on failure.
    /// </summary>
    public static int TryReadRaw(ReadOnlySpan<byte> frame, int firstByte, int bitOffset, int bitCount,
        bool littleEndian, Span<byte> dest)
    {
        int dataLen = (bitCount + 7) >> 3;
        if (bitCount <= 64)
        {
            if (dest.Length < dataLen || !TryReadUInt64(frame, firstByte, bitOffset, bitCount, littleEndian, out ulong v))
                return -1;
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(tmp, v);
            tmp.Slice(8 - dataLen).CopyTo(dest);
            return dataLen;
        }

        if (dest.Length < dataLen || !TryGeometry(frame.Length, firstByte, bitOffset, bitCount, littleEndian, out int sb, out int reqLen))
            return -1;

        var f = frame.Slice(sb);
        int avail = Math.Min(reqLen, f.Length) * 8;
        Span<int> seg = stackalloc int[6];
        int segs = GetReadSegments(littleEndian, bitOffset, bitCount, reqLen, seg);

        int totalBits = 0;
        for (int i = 0; i < segs; i++)
        {
            int n = Math.Min(seg[i * 2 + 1], avail) - seg[i * 2];
            if (seg[i * 2] >= 0 && n > 0) totalBits += n;
        }

        if (totalBits == 0 || totalBits > dataLen * 8) return -1;
        var d = dest.Slice(0, dataLen);
        d.Clear();
        int p = dataLen * 8 - totalBits;
        for (int i = 0; i < segs; i++)
        {
            int a = seg[i * 2];
            int n = Math.Min(seg[i * 2 + 1], avail) - a;
            if (a < 0 || n <= 0) continue;
            CopyBits(f, a, d, p, n);
            p += n;
        }

        return dataLen;
    }

    /// <summary>Copies <paramref name="n"/> bits (MSB-first bit addressing) from src to dst.</summary>
    internal static void CopyBits(ReadOnlySpan<byte> src, int srcBit, Span<byte> dst, int dstBit, int n)
    {
        while (n > 0)
        {
            int si = srcBit & 7, di = dstBit & 7;
            if (si == 0 && di == 0 && n >= 8)
            {
                int bytes = n >> 3;
                src.Slice(srcBit >> 3, bytes).CopyTo(dst.Slice(dstBit >> 3));
                int bits = bytes * 8;
                srcBit += bits; dstBit += bits; n -= bits;
                continue;
            }

            int take = Math.Min(n, Math.Min(8 - si, 8 - di));
            int val = (src[srcBit >> 3] >> (8 - si - take)) & ((1 << take) - 1);
            int shift = 8 - di - take;
            int mask = ((1 << take) - 1) << shift;
            int idx = dstBit >> 3;
            dst[idx] = (byte)((dst[idx] & ~mask) | (val << shift));
            srcBit += take; dstBit += take; n -= take;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> (must fit in <paramref name="bitCount"/> bits) into the frame, leaving all
    /// other bits untouched. Returns false (frame untouched) if the value does not fit or the frame is too short.
    /// </summary>
    public static bool TryWriteUInt64(Span<byte> frame, int firstByte, int bitOffset, int bitCount,
        bool littleEndian, ulong value)
    {
        if (bitCount > 64) return false;
        if (bitCount < 64 && (value >> bitCount) != 0) return false;
        Span<byte> be = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(be, value);
        return TryWriteRaw(frame, firstByte, bitOffset, bitCount, littleEndian, be);
    }

    /// <summary>
    /// Writes a big-endian integer (any length, leading zero bytes allowed) into the field. The integer must fit
    /// in <paramref name="bitCount"/> bits. Returns false (frame untouched) otherwise.
    /// </summary>
    public static bool TryWriteRaw(Span<byte> frame, int firstByte, int bitOffset, int bitCount,
        bool littleEndian, ReadOnlySpan<byte> valueBigEndian)
    {
        if (!TryGeometry(frame.Length, firstByte, bitOffset, bitCount, littleEndian, out int sb, out int reqLen))
            return false;
        if (sb + reqLen > frame.Length) return false;

        int dataLen = (bitCount + 7) >> 3;
        // Normalise value to exactly dataLen bytes and check overflow.
        Span<byte> v = dataLen <= 256 ? stackalloc byte[dataLen] : new byte[dataLen];
        v.Clear();
        if (valueBigEndian.Length > dataLen)
        {
            var dropped = valueBigEndian.Slice(0, valueBigEndian.Length - dataLen);
            foreach (var b in dropped) if (b != 0) return false;
            valueBigEndian.Slice(valueBigEndian.Length - dataLen).CopyTo(v);
        }
        else
        {
            valueBigEndian.CopyTo(v.Slice(dataLen - valueBigEndian.Length));
        }

        int pad = dataLen * 8 - bitCount;
        if (pad > 0 && (v[0] >> (8 - pad)) != 0) return false;

        var f = frame.Slice(sb, reqLen);
        int vb = pad; // bit cursor inside v

        if (!littleEndian)
        {
            CopyBits(v, vb, f, bitOffset, bitCount);
            return true;
        }

        // Little-endian (mirrors the 3-step Python algorithm).
        int lastBit = 8 - bitOffset;
        int firstBit = Math.Max(0, lastBit - bitCount);
        int count = lastBit - firstBit;
        int remaining = bitCount - count;
        // Pre-validate extents so we never write partially.
        int neededBytes = 1 + (remaining >> 3) + ((remaining & 7) != 0 ? 1 : 0);
        if (neededBytes > reqLen) return false;

        CopyBits(v, vb, f, firstBit, count);
        vb += count;
        int cur = 1;
        while (remaining >= 8)
        {
            CopyBits(v, vb, f, cur * 8, 8);
            vb += 8; remaining -= 8; cur++;
        }

        if (remaining > 0)
        {
            CopyBits(v, vb, f, cur * 8 + (8 - remaining), remaining);
        }

        return true;
    }
}
