using Ddt4All.Core.Codec;

namespace Ddt4All.Core.Tests;

public class BitCodecTests
{
    private static byte[] B(string hex) => Frames.Parse(hex);

    [Theory]
    [InlineData("62 12 34 56", 2, 0, 16, false, 0x1234UL)]
    [InlineData("62 12 34 56", 2, 0, 8, false, 0x12UL)]
    [InlineData("62 F0", 2, 4, 4, false, 0x0UL)]
    [InlineData("62 0F", 2, 4, 4, false, 0xFUL)]
    [InlineData("62 A5", 2, 0, 1, false, 1UL)]
    [InlineData("62 A5", 2, 1, 1, false, 0UL)]
    [InlineData("62 A5", 2, 2, 3, false, 4UL)]   // bits 2..4 of 10100101 = 100
    [InlineData("62 12 34 56", 1, 4, 12, false, 0x212UL)]
    [InlineData("FF FF FF FF FF FF FF FF FF", 1, 0, 64, false, ulong.MaxValue)]
    [InlineData("FF FF FF FF FF FF FF FF FF", 1, 3, 64, false, ulong.MaxValue)]
    [InlineData("01 02 03 04 05 06 07 08 09", 1, 0, 64, false, 0x0102030405060708UL)]
    public void Reads_big_endian_fields(string frame, int first, int off, int bits, bool le, ulong expected)
    {
        Assert.True(BitCodec.TryReadUInt64(B(frame), first, off, bits, le, out ulong v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Little_endian_read_is_bug_compatible_with_reference()
    {
        // Python: getHexValue("62 12 34 56", first=1, bits=16, 'Little') == "6262", first=2 -> "1212"
        Assert.True(BitCodec.TryReadUInt64(B("62 12 34 56"), 1, 0, 16, true, out ulong a));
        Assert.Equal(0x6262UL, a);
        Assert.True(BitCodec.TryReadUInt64(B("62 12 34 56"), 2, 0, 16, true, out ulong b));
        Assert.Equal(0x1212UL, b);
        // single byte little endian is just the byte
        Assert.True(BitCodec.TryReadUInt64(B("62 12 34 56"), 3, 0, 8, true, out ulong c));
        Assert.Equal(0x34UL, c);
    }

    [Theory]
    [InlineData("62", 2)]   // first byte beyond frame
    [InlineData("62 12", 0)] // firstByte 0 (python negative index)
    public void Short_frames_fail(string frame, int first)
    {
        Assert.False(BitCodec.TryReadUInt64(B(frame), first, 0, 8, false, out _));
    }

    [Fact]
    public void Invalid_geometry_is_rejected()
    {
        var f = B("00 01 02 03");
        Assert.False(BitCodec.TryReadUInt64(f, 1, 0, 0, false, out _));
        Assert.False(BitCodec.TryReadUInt64(f, 1, -1, 8, false, out _));
        Assert.False(BitCodec.TryReadUInt64(f, 1, 8, 8, true, out _));   // LE offset > 7
        Assert.False(BitCodec.TryReadUInt64(f, 1, 0, 65, false, out _));
    }

    [Fact]
    public void Wide_fields_read_as_raw_bytes()
    {
        var frame = new byte[30];
        for (int i = 0; i < frame.Length; i++) frame[i] = (byte)i;
        var dest = new byte[17];
        Assert.Equal(17, BitCodec.TryReadRaw(frame, 2, 0, 136, false, dest));
        Assert.Equal(Enumerable.Range(1, 17).Select(i => (byte)i), dest);
        // unaligned
        Assert.Equal(17, BitCodec.TryReadRaw(frame, 2, 4, 136, false, dest));
        Assert.Equal(0x10, dest[0]); // low nibble of byte1 (0x01) -> 0x1 then high nibble of 0x02 -> 0x0 => 0x10
    }

    [Fact]
    public void Raw_read_rejects_small_destination_and_short_frame()
    {
        Assert.Equal(-1, BitCodec.TryReadRaw(new byte[30], 2, 0, 136, false, new byte[16]));
        Assert.Equal(-1, BitCodec.TryReadRaw(new byte[10], 2, 0, 136, false, new byte[17]));
    }

    [Theory]
    [InlineData(0xABCDUL, 1, 0, 16, false, "ABCD00")]
    [InlineData(0xABCDUL, 2, 0, 16, false, "00ABCD")]
    [InlineData(0xFUL, 1, 4, 4, false, "0F0000")]
    [InlineData(0x5UL, 1, 1, 3, false, "500000")]
    [InlineData(1UL, 3, 7, 1, false, "000001")]
    public void Writes_big_endian_fields_preserving_neighbours(ulong value, int first, int off, int bits, bool le, string expected)
    {
        var f = new byte[3];
        Assert.True(BitCodec.TryWriteUInt64(f, first, off, bits, le, value));
        Assert.Equal(expected, Convert.ToHexString(f));
    }

    [Fact]
    public void Write_keeps_untouched_bits()
    {
        var f = B("FF FF FF");
        Assert.True(BitCodec.TryWriteUInt64(f, 1, 4, 8, false, 0));
        Assert.Equal("F00FFF", Convert.ToHexString(f));
    }

    [Fact]
    public void Write_rejects_overflow_and_short_frames_without_touching_frame()
    {
        var f = B("11 22 33");
        Assert.False(BitCodec.TryWriteUInt64(f, 1, 0, 4, false, 0x10));
        Assert.False(BitCodec.TryWriteUInt64(f, 3, 0, 16, false, 1));
        Assert.False(BitCodec.TryWriteUInt64(f, 1, 0, 65, false, 1));
        Assert.Equal("112233", Convert.ToHexString(f));
    }

    [Fact]
    public void Little_endian_write_puts_high_bits_first()
    {
        var f = new byte[4];
        Assert.True(BitCodec.TryWriteUInt64(f, 1, 0, 16, true, 0x1234));
        Assert.Equal("12340000", Convert.ToHexString(f));
    }

    [Fact]
    public void Raw_write_accepts_any_length_but_not_overflowing_values()
    {
        var f = new byte[4];
        Assert.True(BitCodec.TryWriteRaw(f, 1, 0, 12, false, B("000ABC")));
        Assert.Equal("ABC00000", Convert.ToHexString(f).PadRight(8, '0').Substring(0, 8));
        Assert.False(BitCodec.TryWriteRaw(f, 1, 0, 12, false, B("1ABC")));
        Assert.False(BitCodec.TryWriteRaw(f, 1, 0, 12, false, B("01000ABC")));
    }

    [Fact]
    public void Roundtrip_all_widths_and_offsets_big_endian()
    {
        var rnd = new Random(5);
        for (int bits = 1; bits <= 64; bits++)
            for (int off = 0; off < 8; off++)
            {
                ulong v = (ulong)rnd.NextInt64() ^ ((ulong)rnd.NextInt64() << 1);
                if (bits < 64) v &= (1UL << bits) - 1;
                var f = new byte[12];
                rnd.NextBytes(f);
                var copy = (byte[])f.Clone();
                Assert.True(BitCodec.TryWriteUInt64(f, 2, off, bits, false, v));
                Assert.True(BitCodec.TryReadUInt64(f, 2, off, bits, false, out ulong back));
                Assert.Equal(v, back);
                // bytes outside the touched range are unchanged
                int touched = (bits + off + 7) / 8;
                for (int i = 0; i < f.Length; i++)
                    if (i < 1 || i >= 1 + touched) Assert.Equal(copy[i], f[i]);
            }
    }

    [Fact]
    public void Wide_roundtrip_is_exact()
    {
        var rnd = new Random(9);
        foreach (int bits in new[] { 65, 72, 100, 128, 136, 200 })
            for (int off = 0; off < 8; off++)
            {
                int len = (bits + 7) / 8;
                var v = new byte[len];
                rnd.NextBytes(v);
                int pad = len * 8 - bits;
                if (pad > 0) v[0] &= (byte)(0xFF >> pad);
                var f = new byte[len + 4];
                rnd.NextBytes(f);
                Assert.True(BitCodec.TryWriteRaw(f, 2, off, bits, false, v));
                var back = new byte[len];
                Assert.Equal(len, BitCodec.TryReadRaw(f, 2, off, bits, false, back));
                Assert.Equal(v, back);
            }
    }
}
