using System.Text.Json;
using Ddt4All.Core.Abstractions;
using Ddt4All.Core.Codec;
using Ddt4All.Core.Ecu;
using Ddt4All.Core.Loading;

namespace Ddt4All.Core.Tests;

public class RequestTests
{
    private static EcuFile Rich() => EcuXmlLoader.Load(TestPaths.Data("rich_ecu.xml"));

    private sealed class FakeTransport : IEcuTransport
    {
        public readonly List<byte[]> Sent = new();
        public Func<byte[], byte[]> Reply = _ => Array.Empty<byte>();
        public ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
        {
            Sent.Add(request.ToArray());
            return new ValueTask<byte[]>(Reply(request.ToArray()));
        }
    }

    [Fact]
    public void BuildRequest_matches_python_build_data_stream()
    {
        var f = Rich();
        using var doc = JsonDocument.Parse(TestPaths.Bytes("rich_ecu.requests.expected.json"));
        int n = 0;
        foreach (var b in doc.RootElement.GetProperty("build").EnumerateArray())
        {
            var req = f.Requests[b.GetProperty("request").GetString()!];
            var inputs = b.GetProperty("inputs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            string expected = b.GetProperty("stream").GetString()!;
            if (expected == "EXC")
            {
                // The reference crashes on a negative value (-3 for the signed scaled "Offset"); this port stores
                // it in two's complement: (-3*4-10)/2 = -11 = 0xF5, Mode 02, Bits 5 -> 0x28.
                Assert.Equal("2E0102F50028", Convert.ToHexString(req.BuildRequest(inputs)));
                continue;
            }

            Assert.Equal(expected, Convert.ToHexString(req.BuildRequest(inputs)));
            n++;
        }

        Assert.True(n >= 5);
    }

    [Fact]
    public void DecodeResponse_matches_python_get_values_from_stream()
    {
        var f = Rich();
        using var doc = JsonDocument.Parse(TestPaths.Bytes("rich_ecu.requests.expected.json"));
        foreach (var d in doc.RootElement.GetProperty("decode").EnumerateArray())
        {
            var req = f.Requests[d.GetProperty("request").GetString()!];
            var frame = Frames.Parse(d.GetProperty("frame").GetString()!);
            var resp = req.DecodeResponse(frame);
            Assert.True(resp.IsPositive);
            var expected = d.GetProperty("values");
            Assert.Equal(expected.EnumerateObject().Count(), resp.Values.Count);
            foreach (var p in expected.EnumerateObject())
            {
                Assert.True(resp.TryGetValue(p.Name, out var text));
                string? exp = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
                Assert.True(exp == text, $"{p.Name}: expected '{exp}' got '{text}' for {d.GetProperty("frame")}");
            }
        }
    }

    [Fact]
    public void Decode_order_follows_definition_and_lookup_by_name()
    {
        var resp = Rich().Requests["ReadData"].DecodeResponse(Frames.Parse("61 01 A0 12 34 01 05 3C 80 FF 56"));
        Assert.Equal(new[] { "Coolant", "Rpm", "Gear", "FlagBits", "Temp16", "VIN", "LeVal" }, resp.Values.Select(v => v.Name));
        Assert.Equal("-39", resp["Coolant"]);
        Assert.Null(resp["VIN"]);   // frame too short for 17 ASCII bytes -> null like the reference
        Assert.Null(resp["NoSuch"]);
    }

    [Fact]
    public void Negative_response_is_decoded_with_nrc_description()
    {
        var req = Rich().Requests["ReadData"];
        var r = req.DecodeResponse(new byte[] { 0x7F, 0x21, 0x31 });
        Assert.True(r.IsNegative);
        Assert.False(r.IsPositive);
        Assert.Equal((byte)0x21, r.RejectedServiceId);
        Assert.Equal((byte)0x31, r.NegativeResponseCode);
        Assert.Equal("NR: Request Out Of Range", r.NegativeResponseText);
        Assert.Empty(r.Values);
        Assert.False(r.IsResponsePending);
    }

    [Fact]
    public void Response_pending_unknown_and_truncated_nrc()
    {
        var req = Rich().Requests["ReadData"];
        Assert.True(req.DecodeResponse(new byte[] { 0x7F, 0x21, 0x78 }).IsResponsePending);
        var unk = req.DecodeResponse(new byte[] { 0x7F, 0x21, 0xEE });
        Assert.Equal("Unregistered error", unk.NegativeResponseText);
        var trunc = req.DecodeResponse(new byte[] { 0x7F, 0x21 });
        Assert.True(trunc.IsNegative);
        Assert.Null(trunc.NegativeResponseCode);
        var empty = req.DecodeResponse(Array.Empty<byte>());
        Assert.False(empty.IsNegative);
        Assert.False(empty.IsPositive);
    }

    [Theory]
    [InlineData(0x10, "NR: General Reject")]
    [InlineData(0x11, "NR: Service Not Supported")]
    [InlineData(0x22, "NR: Conditions Not Correct Or Request Sequence Error")]
    [InlineData(0x33, "NR: Security Access Denied- Security Access Requested  ")]
    [InlineData(0x78, "NR: Request Correctly Received-Response Pending")]
    [InlineData(0x93, "NR: Voltage Too Low")]
    [InlineData(0x00, "Unregistered error")]
    [InlineData(0x94, "Unregistered error")]
    public void Nrc_table(byte nrc, string text) => Assert.Equal(text, NegativeResponses.Describe(nrc));

    [Fact]
    public void Nrc_table_matches_python_negrsp_entries_count()
    {
        int known = Enumerable.Range(0, 256).Count(i => NegativeResponses.IsKnown((byte)i));
        Assert.Equal(52, known);
    }

    [Fact]
    public async Task SendAsync_builds_sends_and_decodes()
    {
        var f = Rich();
        var t = new FakeTransport { Reply = _ => Frames.Parse("61 01 A0 12 34 01 05 3C 80 FF 56 46 31 32 33 34 35 36 37 38 39 30 31 32 33 34 35 36 37") };
        var r = await f.SendAsync(t, "ReadData");
        Assert.Equal("2101", Convert.ToHexString(t.Sent.Single()));
        Assert.Equal("-39", r["Coolant"]);
        Assert.Equal("VF1234567890123", r["VIN"]);
    }

    [Fact]
    public async Task SendAsync_applies_inputs_and_reports_negative_response()
    {
        var f = Rich();
        var t = new FakeTransport { Reply = _ => new byte[] { 0x7F, 0x2E, 0x33 } };
        var r = await f.Requests["WriteConfig"].SendAsync(t, new Dictionary<string, string> { ["Mode"] = "OFF", ["Offset"] = "12.5" });
        Assert.Equal("2E0102140000", Convert.ToHexString(t.Sent.Single()));
        Assert.True(r.IsNegative);
        Assert.Contains("Security", r.NegativeResponseText);
    }

    [Fact]
    public async Task SendAsync_unknown_request_and_unknown_input_throw()
    {
        var f = Rich();
        var t = new FakeTransport();
        await Assert.ThrowsAsync<EcuRequestException>(async () => await f.SendAsync(t, "Nope"));
        await Assert.ThrowsAsync<EcuRequestException>(async () =>
            await f.Requests["WriteConfig"].SendAsync(t, new Dictionary<string, string> { ["Nope"] = "1" }));
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void TryBuildRequest_reports_errors_without_throwing()
    {
        var req = Rich().Requests["WriteConfig"];
        Assert.False(req.TryBuildRequest(new Dictionary<string, string> { ["Mode"] = "ZZ" }, out var bytes, out var err));
        Assert.Null(bytes);
        Assert.Contains("Mode", err ?? "ZZ");
        Assert.False(req.TryBuildRequest(new Dictionary<string, string> { ["Offset"] = "abc" }, out _, out err));
        Assert.NotNull(err);
        Assert.True(req.TryBuildRequest(null, out bytes, out err));
        Assert.Equal("2E0100000000", Convert.ToHexString(bytes!));
    }

    [Fact]
    public void List_text_is_resolved_to_value_even_for_scaled_data()
    {
        var f = new EcuFile();
        var d = new EcuData { Name = "S", Scaled = true, Step = 1, DivideBy = 1 };
        d.AddListItem(3, "Three");
        f.Data.Add(d);
        var r = new EcuRequest { Name = "R", SentBytes = "2E00" };
        r.SendItems.Add(new DataItem("S", 2));
        f.Requests.Add(r);
        f.ResolveReferences();
        Assert.Equal("2E03", Convert.ToHexString(r.BuildRequest(new Dictionary<string, string> { ["S"] = "Three" })));
    }

    [Fact]
    public void Template_with_invalid_hex_fails_cleanly()
    {
        var f = new EcuFile();
        var r = new EcuRequest { Name = "R", SentBytes = "2E0" };
        f.Requests.Add(r);
        f.ResolveReferences();
        Assert.False(r.TryBuildRequest(null, out _, out var err));
        Assert.Contains("SentBytes", err);
    }

    [Fact]
    public void Missing_data_definition_decodes_to_null_instead_of_throwing()
    {
        var f = new EcuFile();
        var r = new EcuRequest { Name = "R" };
        r.ReceiveItems.Add(new DataItem("Ghost", 1));
        f.Requests.Add(r);
        f.ResolveReferences();
        var resp = r.DecodeResponse(new byte[] { 1, 2 });
        Assert.Null(resp["Ghost"]);
        Assert.Single(resp.Values);
    }

    [Fact]
    public void Numeric_decode_is_allocation_free_path_and_matches_display()
    {
        var req = Rich().Requests["ReadData"];
        var frame = Frames.Parse("61 3C 20 00 FF 18 FF 38 00");
        Assert.True(req.TryDecodeNumeric(frame, req.ReceiveItems["Coolant"], out double c));
        Assert.Equal(20.0, c);
        Assert.True(req.TryDecodeNumeric(frame, req.ReceiveItems["Rpm"], out double rpm));
        Assert.Equal(0x2000 * 0.25, rpm);
        Assert.True(req.TryDecodeNumeric(frame, req.ReceiveItems["Temp16"], out double t));
        Assert.Equal(0xFF38 - 65536, t * 10);
        Assert.False(req.TryDecodeNumeric(frame, req.ReceiveItems["VIN"], out _));
    }
}
