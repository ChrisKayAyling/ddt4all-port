using System.Text.Json;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Tests;

/// <summary>
/// Bit-exact equivalence with the Python reference. TestData/codec_vectors.json was produced by running the
/// reference EcuData.getHexValue / getIntValue / getDisplayValue / setValue (tools/gen_vectors.py) on thousands of
/// random definitions, frames and byte orders.
/// </summary>
public class CodecVectorTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(TestPaths.Bytes("codec_vectors.json")));

    internal static EcuData ToData(JsonElement d)
    {
        var data = new EcuData { Name = "x" };
        foreach (var p in d.EnumerateObject())
        {
            switch (p.Name)
            {
                case "bitscount": data.BitsCount = p.Value.GetInt32(); break;
                case "bytescount": data.BytesCount = p.Value.GetInt32(); break;
                case "scaled": data.Scaled = p.Value.GetBoolean(); break;
                case "signed": data.Signed = p.Value.GetBoolean(); break;
                case "bytesascii": data.BytesAscii = p.Value.GetBoolean(); break;
                case "step": data.Step = p.Value.GetDouble(); break;
                case "offset": data.Offset = p.Value.GetDouble(); break;
                case "divideby": data.DivideBy = p.Value.GetDouble(); break;
                case "format": data.Format = p.Value.GetString()!; break;
                case "unit": data.Unit = p.Value.GetString()!; break;
                case "lists":
                    foreach (var l in p.Value.EnumerateObject()) data.AddListItem(long.Parse(l.Name), l.Value.GetString()!);
                    break;
            }
        }

        return data;
    }

    internal static DataItem ToItem(JsonElement i)
    {
        var di = new DataItem { Name = "x", FirstByte = i.GetProperty("firstbyte").GetInt32(), BitOffset = i.GetProperty("bitoffset").GetInt32() };
        if (i.TryGetProperty("endian", out var e)) di.Endian = e.GetString() == "Little" ? ByteOrder.Little : ByteOrder.Big;
        return di;
    }

    internal static ByteOrder Order(string s) => s == "Little" ? ByteOrder.Little : s == "Big" ? ByteOrder.Big : ByteOrder.Default;

    [Fact]
    public void Get_matches_python_hex_int_and_display()
    {
        int compared = 0, refExceptions = 0;
        foreach (var c in Doc.Value.RootElement.GetProperty("get").EnumerateArray())
        {
            var data = ToData(c.GetProperty("data"));
            var item = ToItem(c.GetProperty("item"));
            var order = Order(c.GetProperty("ecu_endian").GetString()!);
            var frame = Frames.Parse(c.GetProperty("frame").GetString()!);
            string ctx = c.ToString();

            var hex = c.GetProperty("hex");
            if (hex.ValueKind == JsonValueKind.String && hex.GetString() == "EXC")
            {
                refExceptions++;
                // the reference crashes (ValueError); we must not throw and must report "no value"
                Assert.Null(data.GetHexValue(frame, item, order));
                continue;
            }

            string? gotHex = data.GetHexValue(frame, item, order);
            if (hex.ValueKind == JsonValueKind.Null) Assert.True(gotHex == null, ctx);
            else Assert.True(hex.GetString() == gotHex, $"hex: expected {hex.GetString()} got {gotHex}: {ctx}");

            var iv = c.GetProperty("int");
            if (iv.ValueKind == JsonValueKind.Number && data.BitsCount <= 64)
            {
                Assert.True(data.TryGetRaw(frame, item, order, out ulong raw), ctx);
                Assert.True(iv.GetUInt64() == raw, $"int: expected {iv} got {raw}: {ctx}");
            }

            var disp = c.GetProperty("display");
            string? gotDisp = data.GetDisplayValue(frame, item, order);
            if (disp.ValueKind == JsonValueKind.Null) Assert.True(gotDisp == null, ctx);
            else if (disp.ValueKind == JsonValueKind.String && disp.GetString() == "EXC") { /* reference crashed */ }
            else Assert.True(disp.GetString() == gotDisp, $"display: expected '{disp.GetString()}' got '{gotDisp}': {ctx}");
            compared++;
        }

        Assert.True(compared > 5000);
        Assert.True(refExceptions < 50);
    }

    [Fact]
    public void Set_matches_python_for_every_input_the_reference_accepts()
    {
        int compared = 0, rejectedByBoth = 0, skipped = 0;
        foreach (var c in Doc.Value.RootElement.GetProperty("set").EnumerateArray())
        {
            var data = ToData(c.GetProperty("data"));
            var item = ToItem(c.GetProperty("item"));
            var order = Order(c.GetProperty("ecu_endian").GetString()!);
            var frame = Frames.Parse(c.GetProperty("frame").GetString()!);
            string value = c.GetProperty("value").GetString()!;
            string ctx = c.ToString();
            var res = c.GetProperty("result");

            if (res.ValueKind == JsonValueKind.String && res.GetString() == "EXC") { skipped++; continue; }
            if (c.GetProperty("overflow").GetBoolean())
            {
                // Reference stores the TOP bits of an over-wide value (garbage); we refuse and leave the frame alone.
                var untouched = (byte[])frame.Clone();
                Assert.False(data.TrySetValue(untouched, item, order, value, out _), ctx);
                Assert.Equal(frame, untouched);
                skipped++;
                continue;
            }
            // reference writes garbage for ASCII wider than the field; we reject (documented deviation)
            if (data.BytesAscii && data.BytesCount * 8 > data.BitsCount) { skipped++; continue; }

            var work = (byte[])frame.Clone();
            bool ok = data.TrySetValue(work, item, order, value, out var error);
            if (res.ValueKind == JsonValueKind.Null)
            {
                Assert.False(ok, "reference rejects, we accepted: " + ctx);
                Assert.Equal(frame, work);
                rejectedByBoth++;
                continue;
            }

            Assert.True(ok, $"reference accepts, we rejected ({error}): {ctx}");
            var expected = res.EnumerateArray().Select(x => Convert.ToByte(x.GetString(), 16)).ToArray();
            Assert.True(expected.AsSpan().SequenceEqual(work), $"frame: expected {Convert.ToHexString(expected)} got {Convert.ToHexString(work)}: {ctx}");
            compared++;
        }

        Assert.True(compared > 3500, $"compared={compared}");
        Assert.True(rejectedByBoth > 20, $"rejectedByBoth={rejectedByBoth}");
        Assert.True(skipped < 400);
    }
}
