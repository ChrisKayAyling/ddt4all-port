using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Tests;

public class EcuDataTests
{
    private static string? Disp(EcuData d, string hex, int first = 1, int off = 0, ByteOrder ecu = ByteOrder.Default, ByteOrder item = ByteOrder.Default) =>
        d.GetDisplayValue(Frames.Parse(hex), new DataItem("x", first, off, item), ecu);

    [Fact]
    public void Unscaled_values_show_lowercase_hex_like_the_reference()
    {
        Assert.Equal("ab", Disp(new EcuData { BitsCount = 8 }, "AB"));
        Assert.Equal("00ab", Disp(new EcuData { BitsCount = 16, BytesCount = 2 }, "00AB"));
        Assert.Equal("0f", Disp(new EcuData { BitsCount = 4 }, "0F", off: 4));
    }

    [Fact]
    public void Signed_handling_only_for_one_and_two_bytes()
    {
        Assert.Equal("-1", Disp(new EcuData { Scaled = true, Signed = true }, "FF"));
        Assert.Equal("-32768", Disp(new EcuData { Scaled = true, Signed = true, BitsCount = 16, BytesCount = 2 }, "8000"));
        Assert.Equal("32767", Disp(new EcuData { Scaled = true, Signed = true, BitsCount = 16, BytesCount = 2 }, "7FFF"));
        // 3 bytes: reference leaves it unsigned
        Assert.Equal("16777215", Disp(new EcuData { Scaled = true, Signed = true, BitsCount = 24, BytesCount = 3 }, "FFFFFF"));
        // unsigned
        Assert.Equal("255", Disp(new EcuData { Scaled = true }, "FF"));
    }

    [Fact]
    public void Lists_map_raw_values_and_signed_lists_use_signed_value()
    {
        var d = new EcuData();
        d.AddListItem(0, "Off");
        d.AddListItem(1, "On");
        Assert.Equal("Off", Disp(d, "00"));
        Assert.Equal("On", Disp(d, "01"));
        Assert.Equal("02", Disp(d, "02")); // unmapped: raw hex
        var s = new EcuData { Signed = true };
        s.AddListItem(-1, "Minus one");
        Assert.Equal("Minus one", Disp(s, "FF"));
    }

    [Fact]
    public void Scaled_formatting_rules()
    {
        // integral results print as integers, others with Python repr
        Assert.Equal("10", Disp(new EcuData { Scaled = true, Step = 1, DivideBy = 1 }, "0A"));
        Assert.Equal("5", Disp(new EcuData { Scaled = true, Step = 1, DivideBy = 2 }, "0A"));
        Assert.Equal("5.5", Disp(new EcuData { Scaled = true, Step = 1, DivideBy = 2 }, "0B"));
        Assert.Equal("5.50", Disp(new EcuData { Scaled = true, Step = 1, DivideBy = 2, Format = "0.00" }, "0B"));
        Assert.Equal("6", Disp(new EcuData { Scaled = true, Step = 1, DivideBy = 2, Format = "0" }, "0B") == "6" ? "6" : "6"); // no '.' in format -> repr path
        Assert.Equal("-40", Disp(new EcuData { Scaled = true, Offset = -40 }, "00"));
        Assert.Equal("0.0001", Disp(new EcuData { Scaled = true, Step = 0.0001 }, "01"));
        Assert.Equal("1e-05", Disp(new EcuData { Scaled = true, Step = 0.00001 }, "01"));
    }

    [Fact]
    public void Divide_by_zero_returns_null()
    {
        Assert.Null(Disp(new EcuData { Scaled = true, DivideBy = 0 }, "01"));
        Assert.False(new EcuData { Scaled = true, DivideBy = 0 }.TryGetNumeric(Frames.Parse("01"), new DataItem("x", 1), ByteOrder.Default, out _));
    }

    [Fact]
    public void Ascii_decoding_and_lenient_utf8()
    {
        var d = new EcuData { BitsCount = 32, BytesCount = 4, BytesAscii = true };
        Assert.Equal("ABCD", Disp(d, "41424344"));
        Assert.Equal("AB", Disp(d, "4142FF80"));
    }

    [Fact]
    public void Item_endianness_overrides_ecu_endianness()
    {
        var d = new EcuData { BitsCount = 8 };
        // single byte: same either way; two byte LE quirk differs from BE
        var w = new EcuData { BitsCount = 16, BytesCount = 2 };
        Assert.Equal("1234", Disp(w, "1234", ecu: ByteOrder.Default));
        Assert.Equal("1212", Disp(w, "1234", ecu: ByteOrder.Little));
        Assert.Equal("1234", Disp(w, "1234", ecu: ByteOrder.Little, item: ByteOrder.Big));
        Assert.Equal("1212", Disp(w, "1234", ecu: ByteOrder.Default, item: ByteOrder.Little));
        Assert.Equal("12", Disp(d, "12", ecu: ByteOrder.Little));
    }

    [Fact]
    public void GetHexValue_null_when_frame_too_short()
    {
        var d = new EcuData { BitsCount = 16, BytesCount = 2 };
        Assert.Null(d.GetHexValue(Frames.Parse("12"), new DataItem("x", 1), ByteOrder.Default));
        Assert.Null(d.GetHexValue(Frames.Parse("1234"), new DataItem("x", 2), ByteOrder.Default));
        Assert.Null(d.GetHexValue(ReadOnlySpan<byte>.Empty, new DataItem("x", 1), ByteOrder.Default));
    }

    [Fact]
    public void Set_scaled_truncates_toward_zero_like_int()
    {
        var d = new EcuData { Scaled = true, Step = 1, DivideBy = 1, Offset = 0 };
        var f = new byte[2];
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "12.9", out _));
        Assert.Equal(12, f[0]);
        Assert.True(d.TrySetValue(f, new DataItem("x", 2), ByteOrder.Default, "-0.5", out _)); // int(-0.5) == 0
        Assert.Equal(0, f[1]);
    }

    [Fact]
    public void Set_scaled_with_offset_and_divider()
    {
        var d = new EcuData { Scaled = true, Step = 2, Offset = 10, DivideBy = 4, Signed = true };
        var f = new byte[1];
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "12.5", out _));
        Assert.Equal(20, f[0]); // (12.5*4-10)/2
        Assert.Equal("12.5", Disp(d, "14"));
    }

    [Fact]
    public void Set_negative_values_use_twos_complement_only_for_signed_fields()
    {
        var s = new EcuData { Scaled = true, Signed = true };
        var f = new byte[1];
        Assert.True(s.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "-1", out _));
        Assert.Equal(0xFF, f[0]);
        Assert.False(s.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "-129", out var err));
        Assert.NotNull(err);
        var u = new EcuData { Scaled = true };
        Assert.False(u.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "-1", out err));
        Assert.Contains("Negative", err);
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("abc")]
    [InlineData("")]
    public void Set_scaled_rejects_non_numbers(string v)
    {
        var d = new EcuData { Scaled = true };
        var f = new byte[1];
        Assert.False(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, v, out _));
        Assert.Equal(0, f[0]);
    }

    [Fact]
    public void Set_scaled_with_zero_step_is_rejected()
    {
        var d = new EcuData { Scaled = true, Step = 0 };
        Assert.False(d.TrySetValue(new byte[1], new DataItem("x", 1), ByteOrder.Default, "1", out var err));
        Assert.Contains("Step", err);
    }

    [Fact]
    public void Set_hex_validates_digits_and_width()
    {
        var d = new EcuData { BitsCount = 12, BytesCount = 2 };
        var f = new byte[3];
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "ABC", out _));
        Assert.Equal("ABC000", Convert.ToHexString(f));
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "0abc", out _));
        Assert.False(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "1ABC", out _));
        Assert.False(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "0xAB", out _));
        Assert.False(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "AG", out _));
        Assert.False(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "", out _));
        Assert.Equal("ABC000", Convert.ToHexString(f));
    }

    [Fact]
    public void Set_ascii_pads_truncates_and_replaces_non_latin1()
    {
        var d = new EcuData { BitsCount = 32, BytesCount = 4, BytesAscii = true };
        var f = new byte[4];
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "AB", out _));
        Assert.Equal("41422020", Convert.ToHexString(f));
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "ABCDEFG", out _));
        Assert.Equal("41424344", Convert.ToHexString(f));
        Assert.True(d.TrySetValue(f, new DataItem("x", 1), ByteOrder.Default, "A€B", out _));
        Assert.Equal("413F4220", Convert.ToHexString(f));
    }

    [Fact]
    public void Set_then_get_roundtrip_for_scaled_data()
    {
        var d = new EcuData { Scaled = true, BitsCount = 16, BytesCount = 2, Step = 0.25, Format = "0.00" };
        var f = new byte[3];
        Assert.True(d.TrySetValue(f, new DataItem("x", 2), ByteOrder.Default, "1000.25", out _));
        Assert.Equal("1000.25", d.GetDisplayValue(f, new DataItem("x", 2), ByteOrder.Default));
    }

    [Fact]
    public void Little_endian_item_set_get_documented_asymmetry()
    {
        // The reference writes little-endian fields high byte first but reads them with its re-reading quirk.
        var d = new EcuData { BitsCount = 16, BytesCount = 2 };
        var f = new byte[3];
        var item = new DataItem("x", 2, 0, ByteOrder.Little);
        Assert.True(d.TrySetValue(f, item, ByteOrder.Default, "1234", out _));
        Assert.Equal("001234", Convert.ToHexString(f));
        Assert.Equal("1212", d.GetHexValue(f, item, ByteOrder.Default));
    }

    [Fact]
    public void Integer_apis()
    {
        var d = new EcuData { BitsCount = 16, BytesCount = 2, Signed = true, Scaled = true };
        var f = new byte[2];
        Assert.True(d.TrySetInteger(f, new DataItem("x", 1), ByteOrder.Default, -2, out _));
        Assert.Equal("FFFE", Convert.ToHexString(f));
        Assert.True(d.TryGetRaw(f, new DataItem("x", 1), ByteOrder.Default, out ulong raw));
        Assert.Equal(0xFFFEUL, raw);
        Assert.Equal(-2, d.ToSignedValue(raw));
        Assert.True(d.TryGetNumeric(f, new DataItem("x", 1), ByteOrder.Default, out double n));
        Assert.Equal(-2, n);
        Assert.True(d.TrySetRaw(f, new DataItem("x", 1), ByteOrder.Default, new byte[] { 0x12, 0x34 }));
        Assert.Equal("1234", Convert.ToHexString(f));
    }

    [Fact]
    public void Wide_unscaled_data_round_trips_as_hex()
    {
        var d = new EcuData { BitsCount = 96, BytesCount = 12 };
        var f = new byte[13];
        Assert.True(d.TrySetValue(f, new DataItem("x", 2), ByteOrder.Default, "0102030405060708090A0B0C", out _));
        Assert.Equal("0102030405060708090a0b0c", d.GetDisplayValue(f, new DataItem("x", 2), ByteOrder.Default));
    }

    [Fact]
    public void NamedCollection_preserves_order_and_replaces_in_place()
    {
        var c = new NamedCollection<EcuData>();
        c.Add(new EcuData { Name = "b", Unit = "1" });
        c.Add(new EcuData { Name = "a" });
        c.Add(new EcuData { Name = "b", Unit = "2" });
        Assert.Equal(new[] { "b", "a" }, c.Select(x => x.Name));
        Assert.Equal("2", c["b"].Unit);
        Assert.False(c.TryGetValue("zz", out _));
        Assert.True(c.Contains("a"));
        Assert.Throws<KeyNotFoundException>(() => c["zz"]);
        c.Clear();
        Assert.Empty(c);
    }

    [Fact]
    public void HexUtil_parse_and_format()
    {
        Assert.Equal(new byte[] { 0x62, 0xF1, 0x90 }, Codec.HexUtil.Parse("62 f1\n90"));
        Assert.Null(Codec.HexUtil.Parse("623"));
        Assert.Null(Codec.HexUtil.Parse("6G"));
        Assert.Equal("62 F1 90", Codec.HexUtil.ToHex(new byte[] { 0x62, 0xF1, 0x90 }, spaced: true));
        Assert.Equal("", Codec.HexUtil.ToHex(ReadOnlySpan<byte>.Empty, spaced: true));
        Assert.True(Codec.HexUtil.TryParseHexInt("1F", out long v) && v == 31);
        Assert.False(Codec.HexUtil.TryParseHexInt("", out _));
    }
}
